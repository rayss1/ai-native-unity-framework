[CmdletBinding()]
param(
    [string] $UnityEditorPath = $(
        if ($env:UNITY_EDITOR_PATH) { $env:UNITY_EDITOR_PATH }
        else { 'C:\Program Files\Unity\Hub\Editor\6000.3.9f1\Editor\Unity.exe' }
    ),
    [string] $EvidenceDirectory,
    [string] $HostAddress = '127.0.0.1',
    [ValidateRange(1, 65535)]
    [int] $KcpPort = 22000,
    [ValidateRange(1, 65535)]
    [int] $HealthPort = 22080,
    [ValidateRange(10, 600)]
    [int] $StartupTimeoutSeconds = 90,
    [ValidateRange(10, 600)]
    [int] $SmokeTimeoutSeconds = 120
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$unityAllUsersProfile = [Environment]::GetEnvironmentVariable('ALLUSERSPROFILE')
if ([string]::IsNullOrWhiteSpace($unityAllUsersProfile)) {
    $unityAllUsersProfile = [Environment]::GetEnvironmentVariable('ProgramData')
}
if ([string]::IsNullOrWhiteSpace($unityAllUsersProfile)) {
    throw 'Unity Package Manager requires ALLUSERSPROFILE or ProgramData on Windows.'
}

function Invoke-CheckedNative {
    param(
        [Parameter(Mandatory)]
        [string] $FilePath,
        [Parameter(Mandatory)]
        [string[]] $ArgumentList,
        [Parameter(Mandatory)]
        [string] $Description
    )

    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

function Get-Sha256 {
    param([Parameter(Mandatory)][string] $Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-NUnitResult {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][int] $ExpectedPassed,
        [Parameter(Mandatory)][string] $Label
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Unity did not produce $Label NUnit results at: $Path"
    }

    [xml] $document = Get-Content -LiteralPath $Path -Raw
    $run = $document.'test-run'
    $passed = [int] $run.passed
    $failed = [int] $run.failed
    $skipped = [int] $run.skipped
    $result = [string] $run.result
    if ($result -ne 'Passed' -or $passed -ne $ExpectedPassed -or $failed -ne 0 -or $skipped -ne 0) {
        throw "Expected exactly $ExpectedPassed passed, 0 failed, and 0 skipped $Label tests; got result=$result passed=$passed failed=$failed skipped=$skipped."
    }

    return [pscustomobject]@{
        Result = $result
        Passed = $passed
        Failed = $failed
        Skipped = $skipped
    }
}

function Invoke-Unity {
    param(
        [Parameter(Mandatory)][string[]] $ArgumentList,
        [Parameter(Mandatory)][string] $Description
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $UnityEditorPath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.Environment['ALLUSERSPROFILE'] = $unityAllUsersProfile
    foreach ($argument in $ArgumentList) {
        [void] $startInfo.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    [void] $process.Start()
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) {
        throw "$Description failed with Unity exit code $($process.ExitCode)."
    }
}

function Get-UnityVersionOutput {
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $UnityEditorPath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.Environment['ALLUSERSPROFILE'] = $unityAllUsersProfile
    $startInfo.Arguments = '-version'

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    [void] $process.Start()
    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) {
        throw "Unity -version failed with exit code $($process.ExitCode): $stderr"
    }
    return ($stdout + [Environment]::NewLine + $stderr).Trim()
}

$repositoryRoot = (& git rev-parse --show-toplevel).Trim()
if ($LASTEXITCODE -ne 0 -or -not $repositoryRoot) {
    throw 'The repository root could not be resolved.'
}
$repositoryRoot = [System.IO.Path]::GetFullPath($repositoryRoot)

if ($KcpPort -ne 22000) {
    throw 'The checked-in Battle Host Fantasy.config fixes the outer KCP port at 22000; KcpPort must remain 22000.'
}
if ($HostAddress -ne '127.0.0.1') {
    throw 'The WS-26 local validation profile requires HostAddress 127.0.0.1.'
}

if (-not (Test-Path -LiteralPath $UnityEditorPath -PathType Leaf)) {
    throw "Unity Editor was not found at: $UnityEditorPath"
}

$dirty = & git -C $repositoryRoot status --porcelain --untracked-files=all
if ($LASTEXITCODE -ne 0) {
    throw 'git status failed.'
}
if ($dirty) {
    throw 'The worktree is dirty. Commit, stash, or remove changes so the evidence identifies an exact revision.'
}

$commit = (& git -C $repositoryRoot rev-parse HEAD).Trim()
$fantasyCommit = (& git -C $repositoryRoot rev-parse 'HEAD:server/vendor/Fantasy').Trim()
if ($LASTEXITCODE -ne 0) {
    throw 'The pinned Fantasy gitlink could not be resolved.'
}
if ($fantasyCommit -ne 'f8bed0d464924f159d46498f1311206ea0694be8') {
    throw "Expected Fantasy f8bed0d464924f159d46498f1311206ea0694be8, found: $fantasyCommit"
}

$projectVersionPath = Join-Path $repositoryRoot 'client/UnityProject/ProjectSettings/ProjectVersion.txt'
$projectVersion = Get-Content -LiteralPath $projectVersionPath -Raw
if ($projectVersion -notmatch '(?m)^m_EditorVersion: 6000\.3\.9f1\s*$' -or
    $projectVersion -notmatch '(?m)^m_EditorVersionWithRevision: 6000\.3\.9f1 \(7a9955a4f2fa\)\s*$') {
    throw 'The Unity project is not pinned to 6000.3.9f1 revision 7a9955a4f2fa.'
}

$editorVersionOutput = Get-UnityVersionOutput
if ($editorVersionOutput -notmatch '6000\.3\.9f1') {
    throw "Expected Unity Editor 6000.3.9f1, found: $editorVersionOutput"
}

if (-not $EvidenceDirectory) {
    $EvidenceDirectory = Join-Path $repositoryRoot "artifacts/unity-windows/$commit"
}
$EvidenceDirectory = [System.IO.Path]::GetFullPath($EvidenceDirectory)
New-Item -ItemType Directory -Path $EvidenceDirectory -Force | Out-Null

$manifestPath = Join-Path $repositoryRoot 'client/UnityProject/Packages/manifest.json'
$lockPath = Join-Path $repositoryRoot 'client/UnityProject/Packages/packages-lock.json'
$fantasyPackagePath = Join-Path $repositoryRoot 'packages/com.ainative.client.fantasy/package.json'
$fantasyNoticeSource = Join-Path $repositoryRoot 'packages/com.ainative.client.fantasy/THIRD-PARTY-NOTICES.md'
$fantasyLicenseSource = Join-Path $repositoryRoot 'server/vendor/Fantasy/Fantasy.Packages/Fantasy.Unity/LICENSE'
$expectedFantasyGitUrl = 'https://github.com/rayss1/Fantasy.git?path=/Fantasy.Packages/Fantasy.Unity#f8bed0d464924f159d46498f1311206ea0694be8'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$packageLock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
$clientFantasyPackage = Get-Content -LiteralPath $fantasyPackagePath -Raw | ConvertFrom-Json
if ($manifest.dependencies.'com.fantasy.unity' -ne $expectedFantasyGitUrl -or
    $packageLock.dependencies.'com.fantasy.unity'.version -ne $expectedFantasyGitUrl -or
    $packageLock.dependencies.'com.fantasy.unity'.source -ne 'git' -or
    $packageLock.dependencies.'com.fantasy.unity'.hash -ne $fantasyCommit -or
    $clientFantasyPackage.dependencies.'com.fantasy.unity' -ne '2026.1.1001') {
    throw 'The Fantasy.Unity manifest, package declaration, or UPM lock does not match the approved version and commit.'
}
$metadataPath = Join-Path $EvidenceDirectory 'metadata.txt'
$metadata = [System.Collections.Generic.List[string]]::new()
$metadata.Add("commit=$commit")
$metadata.Add("fantasy_commit=$fantasyCommit")
$metadata.Add("validated_at_utc=$([DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ'))")
$metadata.Add("host=$([System.Environment]::OSVersion.VersionString)")
$dotnetSdk = (& dotnet --version).Trim()
if ($dotnetSdk -ne '10.0.202') {
    throw "Expected .NET SDK 10.0.202, found: $dotnetSdk"
}
$metadata.Add("dotnet_sdk=$dotnetSdk")
$metadata.Add("editor_version_output=$editorVersionOutput")
$metadata.Add('editor_revision=7a9955a4f2fa')
$metadata.Add("editor_executable_sha256=$(Get-Sha256 $UnityEditorPath)")
$metadata.Add("manifest_sha256=$(Get-Sha256 $manifestPath)")
$metadata.Add("packages_lock_sha256=$(Get-Sha256 $lockPath)")
if (Test-Path -LiteralPath $fantasyPackagePath -PathType Leaf) {
    $metadata.Add("fantasy_client_package_sha256=$(Get-Sha256 $fantasyPackagePath)")
}
$metadata.Add("fantasy_client_notice_sha256=$(Get-Sha256 $fantasyNoticeSource)")
$metadata.Add("fantasy_unity_license_sha256=$(Get-Sha256 $fantasyLicenseSource)")
$metadata.Add("kcp_endpoint=$HostAddress`:$KcpPort")
$metadata.Add("health_endpoint=http://127.0.0.1:$HealthPort/health/ready")
$metadata | Set-Content -LiteralPath $metadataPath -Encoding utf8
(& git -C $repositoryRoot submodule status --recursive) | Add-Content -LiteralPath $metadataPath -Encoding utf8

$unityProject = Join-Path $repositoryRoot 'client/UnityProject'
$editModeXml = Join-Path $EvidenceDirectory 'editmode.xml'
$editModeLog = Join-Path $EvidenceDirectory 'editmode.log'
$playModeXml = Join-Path $EvidenceDirectory 'playmode.xml'
$playModeLog = Join-Path $EvidenceDirectory 'playmode.log'
$buildLog = Join-Path $EvidenceDirectory 'player-build.log'
$playerDirectory = Join-Path $EvidenceDirectory 'player'
$playerPath = Join-Path $playerDirectory 'AiNative.BattleClient.exe'
$smokeJson = Join-Path $EvidenceDirectory 'smoke.json'
$hostPublishDirectory = Join-Path $EvidenceDirectory 'battle-host'
$hostStdout = Join-Path $EvidenceDirectory 'battle-host.stdout.log'
$hostStderr = Join-Path $EvidenceDirectory 'battle-host.stderr.log'
$playerStdout = Join-Path $EvidenceDirectory 'player.stdout.log'
$playerStderr = Join-Path $EvidenceDirectory 'player.stderr.log'

$hostProcess = $null
$playerProcess = $null
try {
    Invoke-CheckedNative -FilePath 'dotnet' -Description 'Battle Host publish' -ArgumentList @(
        'publish',
        (Join-Path $repositoryRoot 'server/src/Hosts/AiNative.BattleHost/AiNative.BattleHost.csproj'),
        '-c', 'Release',
        '-f', 'net10.0',
        '-o', $hostPublishDirectory
    )

    $hostExecutable = Join-Path $hostPublishDirectory 'AiNative.BattleHost.exe'
    if (-not (Test-Path -LiteralPath $hostExecutable -PathType Leaf)) {
        throw "Battle Host publish did not produce: $hostExecutable"
    }

    $hostEnvironment = @{
        ASPNETCORE_URLS = "http://127.0.0.1:$HealthPort"
        AINATIVE_FANTASY_ENABLED = 'true'
        AINATIVE_FANTASY_OUTER_KCP_MTU = '1150'
        AINATIVE_SOURCE_COMMIT = $commit
        AINATIVE_FANTASY_COMMIT = $fantasyCommit
    }
    $previousHostEnvironment = @{}
    try {
        foreach ($name in $hostEnvironment.Keys) {
            $previousHostEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
            [Environment]::SetEnvironmentVariable($name, $hostEnvironment[$name], 'Process')
        }
    $hostProcess = Start-Process -FilePath $hostExecutable `
        -ArgumentList @('--pid', '1', '-m', 'Release') `
        -WorkingDirectory $hostPublishDirectory `
            -RedirectStandardOutput $hostStdout `
            -RedirectStandardError $hostStderr `
            -WindowStyle Hidden `
            -PassThru
    }
    finally {
        foreach ($name in $hostEnvironment.Keys) {
            [Environment]::SetEnvironmentVariable($name, $previousHostEnvironment[$name], 'Process')
        }
    }

    $readyUri = "http://127.0.0.1:$HealthPort/health/ready"
    $deadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    $ready = $false
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($hostProcess.HasExited) {
            throw "Battle Host exited before readiness with code $($hostProcess.ExitCode)."
        }
        try {
            $response = Invoke-RestMethod -Uri $readyUri -Method Get -TimeoutSec 2
            if ($response.status -eq 'ready') {
                $ready = $true
                break
            }
        }
        catch {
            # Readiness is expected to refuse connections while the Host starts.
        }
        Start-Sleep -Milliseconds 250
    }
    if (-not $ready) {
        throw "Battle Host did not become ready within $StartupTimeoutSeconds seconds."
    }

    Invoke-Unity -Description 'Unity EditMode validation' -ArgumentList @(
        '-batchmode', '-nographics',
        '-projectPath', $unityProject,
        '-runTests', '-testPlatform', 'EditMode',
        '-testResults', $editModeXml,
        '-logFile', $editModeLog
    )
    $editMode = Assert-NUnitResult -Path $editModeXml -ExpectedPassed 38 -Label 'EditMode'

    Invoke-Unity -Description 'Unity PlayMode validation' -ArgumentList @(
        '-batchmode', '-nographics',
        '-projectPath', $unityProject,
        '-runTests', '-testPlatform', 'PlayMode',
        '-testResults', $playModeXml,
        '-logFile', $playModeLog
    )
    $playMode = Assert-NUnitResult -Path $playModeXml -ExpectedPassed 2 -Label 'PlayMode'

    New-Item -ItemType Directory -Path $playerDirectory -Force | Out-Null
    Invoke-Unity -Description 'Windows x64 Mono Player build' -ArgumentList @(
        '-batchmode', '-nographics', '-quit',
        '-projectPath', $unityProject,
        '-executeMethod', 'AiNative.Client.Editor.BattleClientBuild.BuildWindowsSmoke',
        '--ainative-build-output', $playerPath,
        '-logFile', $buildLog
    )
    if (-not (Test-Path -LiteralPath $playerPath -PathType Leaf)) {
        throw "Unity did not produce the Windows smoke Player at: $playerPath"
    }
    $playerNotice = Join-Path $playerDirectory 'THIRD-PARTY-NOTICES.md'
    $playerFantasyLicense = Join-Path $playerDirectory 'Fantasy-LICENSE.txt'
    if (-not (Test-Path -LiteralPath $playerNotice -PathType Leaf) -or
        -not (Test-Path -LiteralPath $playerFantasyLicense -PathType Leaf)) {
        throw 'The Windows Player distribution is missing THIRD-PARTY-NOTICES.md or Fantasy-LICENSE.txt.'
    }
    if ((Get-Sha256 $playerNotice) -ne (Get-Sha256 $fantasyNoticeSource) -or
        (Get-Sha256 $playerFantasyLicense) -ne (Get-Sha256 $fantasyLicenseSource)) {
        throw 'The Windows Player Fantasy notice/license does not match the repository-approved source.'
    }

    $playerArguments = @(
            '--ainative-smoke',
            '--ainative-host', $HostAddress,
            '--ainative-port', $KcpPort.ToString([Globalization.CultureInfo]::InvariantCulture),
            '--ainative-result', $smokeJson,
            '-batchmode', '-nographics'
        ) | ForEach-Object {
            if ($_ -match '[\s"]') { '"' + $_.Replace('"', '\"') + '"' } else { $_ }
        }
    $playerProcess = Start-Process -FilePath $playerPath `
        -ArgumentList $playerArguments `
        -WorkingDirectory $playerDirectory `
        -RedirectStandardOutput $playerStdout `
        -RedirectStandardError $playerStderr `
        -WindowStyle Hidden `
        -PassThru

    if (-not $playerProcess.WaitForExit($SmokeTimeoutSeconds * 1000)) {
        Stop-Process -Id $playerProcess.Id -Force
        $playerProcess.WaitForExit()
        throw "Windows smoke Player timed out after $SmokeTimeoutSeconds seconds."
    }
    $playerProcess.WaitForExit()
    if ($playerProcess.ExitCode -ne 0) {
        throw "Windows smoke Player exited with code $($playerProcess.ExitCode)."
    }
    if (-not (Test-Path -LiteralPath $smokeJson -PathType Leaf)) {
        throw "Windows smoke Player did not produce: $smokeJson"
    }
    $smoke = Get-Content -LiteralPath $smokeJson -Raw | ConvertFrom-Json
    if ($smoke.success -ne $true) {
        throw 'Windows smoke result did not report success=true.'
    }
    if ([UInt64]$smoke.sessionId -eq 0 -or
        [UInt32]$smoke.initialEpoch -eq 0 -or
        [UInt32]$smoke.reconnectedEpoch -le [UInt32]$smoke.initialEpoch -or
        [UInt32]$smoke.preReconnectAcknowledgedSequence -eq 0 -or
        [UInt32]$smoke.lastAcknowledgedSequence -le [UInt32]$smoke.preReconnectAcknowledgedSequence) {
        throw 'Windows smoke JSON did not prove login, reconnect epoch growth, and post-reconnect acknowledgement growth.'
    }

    @(
        "editmode_result=$($editMode.Result)",
        "editmode_passed=$($editMode.Passed)",
        "editmode_failed=$($editMode.Failed)",
        "editmode_skipped=$($editMode.Skipped)",
        "playmode_result=$($playMode.Result)",
        "playmode_passed=$($playMode.Passed)",
        "playmode_failed=$($playMode.Failed)",
        "playmode_skipped=$($playMode.Skipped)",
        'player_target=StandaloneWindows64',
        'player_scripting_backend=Mono',
        'smoke_exit_code=0',
        'smoke_success=true',
        "smoke_session_id=$($smoke.sessionId)",
        "smoke_initial_epoch=$($smoke.initialEpoch)",
        "smoke_reconnected_epoch=$($smoke.reconnectedEpoch)",
        "smoke_pre_reconnect_ack=$($smoke.preReconnectAcknowledgedSequence)",
        "smoke_post_reconnect_ack=$($smoke.lastAcknowledgedSequence)"
    ) | Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'summary.txt') -Encoding utf8

}
finally {
    if ($null -ne $playerProcess -and -not $playerProcess.HasExited) {
        Stop-Process -Id $playerProcess.Id -Force
        $playerProcess.WaitForExit()
    }
    if ($null -ne $hostProcess -and -not $hostProcess.HasExited) {
        Stop-Process -Id $hostProcess.Id
        if (-not $hostProcess.WaitForExit(10000)) {
            Stop-Process -Id $hostProcess.Id -Force
            $hostProcess.WaitForExit()
        }
    }
}

$postValidationDirty = & git -C $repositoryRoot status --porcelain --untracked-files=all
if ($LASTEXITCODE -ne 0) {
    throw 'The post-validation git status check failed.'
}
if ($postValidationDirty) {
    throw 'Validation completed, but the worktree is no longer clean. Inspect Unity-generated changes before accepting the evidence.'
}

Write-Host "Windows Unity validation passed. Evidence: $EvidenceDirectory"
