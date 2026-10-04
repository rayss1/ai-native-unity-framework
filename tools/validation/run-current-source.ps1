[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $SourceRoot,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $ExpectedCommit,
    [Parameter(Mandatory)][string] $SdkPath,
    [Parameter(Mandatory)][string] $EvidenceDirectory,
    [string] $UnityEditorPath = $env:UNITY_EDITOR_PATH,
    [ValidateSet('Dotnet','Architecture','EditMode','WindowsLegacy','TopologyPublish','TopologyAcceptance','TopologyPlayMode')]
    [string[]] $Phases = @('Dotnet','Architecture','EditMode'),
    [ValidateSet('Baseline','Candidate')][string] $Profile = 'Baseline',
    [ValidateRange(1,1000000)][int] $ExpectedDotnetPassed = 333,
    [ValidateRange(1,1000000)][int] $ExpectedEditModePassed = 95,
    [string] $TopologyRunDirectory = 'artifacts/qualified-topology'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/nunit-report.ps1"
$SourceRoot = [IO.Path]::GetFullPath($SourceRoot)
$EvidenceDirectory = [IO.Path]::GetFullPath($EvidenceDirectory)
$SdkPath = [IO.Path]::GetFullPath($SdkPath)
function Invoke-Checked([string] $File, [string[]] $Arguments, [string] $Log) {
    & $File @Arguments *> $Log
    if ($LASTEXITCODE -ne 0) { throw "Validation failed: $File (exit $LASTEXITCODE). See $Log" }
}
function Assert-Source {
    $actual = (& git -C $SourceRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $actual -ne $ExpectedCommit) { throw "Source identity mismatch: expected $ExpectedCommit, found $actual" }
    & git -C $SourceRoot diff --quiet --ignore-submodules=none HEAD --
    if ($LASTEXITCODE -ne 0) { throw 'Source has tracked modifications' }
    $unknown = @(& git -C $SourceRoot ls-files --others --exclude-standard)
    if ($LASTEXITCODE -ne 0 -or $unknown.Count -ne 0) { throw 'Source contains untracked files' }
    $pin = (& git -C $SourceRoot rev-parse 'HEAD:server/vendor/Fantasy').Trim()
    $checkout = (& git -C (Join-Path $SourceRoot 'server/vendor/Fantasy') rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $pin -ne $checkout) { throw 'Fantasy checkout differs from its gitlink' }
    & git -C (Join-Path $SourceRoot 'server/vendor/Fantasy') diff --quiet HEAD --
    if ($LASTEXITCODE -ne 0) { throw 'Fantasy checkout has modifications' }
    $nestedUnknown = @(& git -C (Join-Path $SourceRoot 'server/vendor/Fantasy') ls-files --others --exclude-standard)
    if ($LASTEXITCODE -ne 0 -or $nestedUnknown.Count -ne 0) { throw 'Fantasy checkout contains untracked files' }
    return $pin
}
function Assert-TopologyPublication {
    $run = [IO.Path]::GetFullPath((Join-Path $SourceRoot $TopologyRunDirectory))
    if (-not $run.StartsWith((Join-Path $SourceRoot 'artifacts') + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Topology run must remain inside source artifacts' }
    $publicationPath = Join-Path $run 'publication.json'
    if (-not (Test-Path $publicationPath)) { throw 'TopologyPublish is required before topology validation' }
    $publication = Get-Content -LiteralPath $publicationPath -Raw | ConvertFrom-Json
    if ($publication.source -ne $ExpectedCommit -or $publication.fantasy -ne $fantasy) { throw 'Topology publication source mismatch' }
    if ((Get-FileHash -LiteralPath (Join-Path $SourceRoot 'infrastructure/topology/Fantasy.config')).Hash -ne $publication.fantasyConfigurationSha256) { throw 'Topology source configuration identity mismatch' }
    if (@($publication.files).Count -eq 0 -or @($publication.files.path | Select-Object -Unique).Count -ne @($publication.files).Count) { throw 'Topology publication file inventory is empty or duplicated' }
    $binaryPattern = '\.(dll|exe)$|\.(deps|runtimeconfig)\.json$'
    $publishedBinaries = @($publication.files | Where-Object path -match $binaryPattern)
    $actualBinaries = @(Get-ChildItem (Join-Path $run 'bin') -File -Recurse | Where-Object Name -match $binaryPattern)
    if ($publishedBinaries.Count -ne $actualBinaries.Count -or $actualBinaries.Count -lt 9) { throw 'Topology binary inventory mismatch' }
    foreach ($file in $publication.files) {
        $binary = [IO.Path]::GetFullPath((Join-Path $run $file.path))
        if (-not $binary.StartsWith((Join-Path $run 'bin') + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $binary) -or (Get-FileHash -LiteralPath $binary).Hash -ne $file.sha256) { throw 'Topology binary identity mismatch' }
    }
    foreach ($id in @('gate','player','lobby','match','coordinator','battle-1','battle-2')) {
        $effectiveConfiguration = Join-Path $run ('bin/'+$id+'/Fantasy.config')
        if (-not (Test-Path -LiteralPath $effectiveConfiguration) -or (Get-FileHash -LiteralPath $effectiveConfiguration).Hash -ne $publication.fantasyConfigurationSha256) { throw 'Topology effective configuration identity mismatch' }
        $record = Get-Content -LiteralPath (Join-Path $run ($id+'.pid.json')) -Raw | ConvertFrom-Json
        $process = Get-Process -Id $record.Pid -ErrorAction Stop
        $executable = [IO.Path]::GetFullPath($record.Executable)
        if (-not $executable.StartsWith((Join-Path $run ('bin/'+$id)) + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or
            $process.Path -ne $executable -or $process.StartTime.ToUniversalTime().Ticks -ne ([datetime]$record.StartUtc).ToUniversalTime().Ticks -or
            ([datetime]$record.StartUtc).ToUniversalTime() -lt ([datetime]$publication.publishedUtc).ToUniversalTime()) { throw 'Topology service process does not belong to this publication' }
    }
}
if ($null -eq $Phases -or $Phases.Count -eq 0) { throw 'At least one validation phase is required' }
if (@($Phases | Select-Object -Unique).Count -ne $Phases.Count) { throw 'Duplicate phases are not allowed' }
if ($Profile -eq 'Baseline') {
    if ($ExpectedCommit -ne 'c9098be7e2a44efc42182a87aca2551648993705' -or $ExpectedDotnetPassed -ne 333 -or $ExpectedEditModePassed -ne 95) { throw 'Baseline profile requires c9098be, 333 .NET and 95 EditMode tests' }
} else {
    if ($ExpectedCommit -eq 'c9098be7e2a44efc42182a87aca2551648993705') { throw 'Candidate profile cannot qualify the historical baseline' }
    if (-not $PSBoundParameters.ContainsKey('ExpectedDotnetPassed') -or -not $PSBoundParameters.ContainsKey('ExpectedEditModePassed') -or $ExpectedEditModePassed -ne 147) { throw 'Candidate requires explicit .NET count and the reviewed 147-test EditMode inventory' }
}
$fantasy = Assert-Source
if ((& $SdkPath --version).Trim() -ne '10.0.202' -or $LASTEXITCODE -ne 0) { throw 'Fixed .NET SDK 10.0.202 is required' }
if (Test-Path -LiteralPath $EvidenceDirectory) { throw 'Use a new evidence directory; old reports cannot qualify a new run' }
New-Item -ItemType Directory -Path $EvidenceDirectory | Out-Null
$report = [ordered]@{
    source = $ExpectedCommit; fantasy = $fantasy; sourceRoot = $SourceRoot
    startedUtc = [DateTime]::UtcNow.ToString('O'); status = 'Running'; phases = @()
    requestedPhases = $Phases
    profile = $Profile; expectedDotnetPassed = $ExpectedDotnetPassed; expectedEditModePassed = $ExpectedEditModePassed
    runnerSha256 = (Get-FileHash -LiteralPath $PSCommandPath).Hash.ToLowerInvariant()
    nunitGateSha256 = (Get-FileHash -LiteralPath "$PSScriptRoot/nunit-report.ps1").Hash.ToLowerInvariant()
    windowsRunnerSha256 = (Get-FileHash -LiteralPath "$PSScriptRoot/../run-unity-windows-validation.ps1").Hash.ToLowerInvariant()
    sdkSha256 = (Get-FileHash -LiteralPath $SdkPath).Hash.ToLowerInvariant()
}
$reportPath = Join-Path $EvidenceDirectory 'validation.json'
function Save-Report { $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding utf8 }
Save-Report
$previousAllUsersProfile = [Environment]::GetEnvironmentVariable('ALLUSERSPROFILE','Process')
Push-Location $SourceRoot
try {
    foreach ($phase in $Phases) {
        $phaseDirectory = Join-Path $EvidenceDirectory $phase
        New-Item -ItemType Directory -Path $phaseDirectory | Out-Null
        $result = [ordered]@{ phase = $phase; evidenceDirectory = $phaseDirectory; startedUtc = [DateTime]::UtcNow.ToString('O'); status = 'Running' }
        $report.phases += $result; Save-Report
        switch ($phase) {
            Dotnet {
                if (-not $env:AINATIVE_TEST_POSTGRES) { throw 'Dotnet requires an isolated real PostgreSQL via AINATIVE_TEST_POSTGRES' }
                Invoke-Checked $SdkPath @('build','AiNative.sln','-c','Release','--nologo') (Join-Path $phaseDirectory 'build.log')
                if ($Profile -eq 'Candidate') {
                    # A separate invocation creates a new testhost. Running this inside the
                    # solution testhost can inherit Gateway's vendor-global KCP settings.
                    $probeDirectory = Join-Path $phaseDirectory 'fresh-probe'
                    New-Item -ItemType Directory -Path $probeDirectory | Out-Null
                    $probeTest = 'AiNative.Server.Fantasy.Tests.FantasyProbeInitializationTests.IndependentProbeRuntimeConfiguresOuterKcpBeforeItsFirstNetwork'
                    Invoke-Checked $SdkPath @('test','server/tests/AiNative.Server.Fantasy.Tests/AiNative.Server.Fantasy.Tests.csproj','-c','Release','--no-build','--no-restore','--filter',('FullyQualifiedName='+$probeTest),'--logger','trx','--results-directory',$probeDirectory) (Join-Path $probeDirectory 'test.log')
                    $probeReports = @(Get-ChildItem -LiteralPath $probeDirectory -Filter '*.trx')
                    if ($probeReports.Count -ne 1) { throw 'Independent probe requires exactly one fresh-process TRX report' }
                    $result.freshProbe = Assert-AiNativeTrxReport -Path $probeReports[0].FullName -ExpectedPassed 1 -RequiredTest $probeTest
                    Save-Report
                }
                Invoke-Checked $SdkPath @('test','AiNative.sln','-c','Release','--no-build','--no-restore','--logger','trx','--results-directory',$phaseDirectory) (Join-Path $phaseDirectory 'test.log')
                $reports = @(Get-ChildItem -LiteralPath $phaseDirectory -Filter '*.trx')
                if ($reports.Count -ne 13) { throw "Expected 13 test assemblies, found $($reports.Count)" }
                $passed = 0; $total = 0
                $result.reports = @()
                foreach ($file in $reports) {
                    $assembly = Assert-AiNativeTrxReport -Path $file.FullName
                    $result.reports += $assembly
                    $passed += $assembly.Passed; $total += $assembly.Total
                }
                if ($passed -ne $ExpectedDotnetPassed) { throw "Expected $ExpectedDotnetPassed .NET tests, found $passed" }
                $result.passed = $passed; $result.total = $total; $result.assemblies = $reports.Count
            }
            Architecture {
                Invoke-Checked $SdkPath @('run','--project','tools/ArchitectureCheck','-c','Release','--','--root',$SourceRoot,'--format','text') (Join-Path $phaseDirectory 'architecture.log')
            }
            WindowsLegacy {
                if (-not (Test-Path -LiteralPath $UnityEditorPath -PathType Leaf)) { throw 'UnityEditorPath is required' }
                $windowsDirectory = Join-Path $phaseDirectory 'windows'
                & "$PSScriptRoot/../run-unity-windows-validation.ps1" -UnityEditorPath $UnityEditorPath -SdkPath $SdkPath -Profile $Profile -ExpectedEditModePassed $ExpectedEditModePassed -EvidenceDirectory $windowsDirectory
                $windowsReportPath = Join-Path $windowsDirectory 'reports.json'
                $windows = Get-Content -LiteralPath $windowsReportPath -Raw | ConvertFrom-Json
                if ($windows.source -ne $ExpectedCommit -or $windows.profile -ne $Profile) { throw 'Windows report source/profile mismatch' }
                foreach ($artifact in $windows.artifacts) {
                    $identity = Get-AiNativeEvidenceFile -Path $artifact.Path
                    if (-not $identity.Path.StartsWith($windowsDirectory + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or $identity.Sha256 -ne $artifact.Sha256) { throw 'Windows evidence identity mismatch' }
                }
                $result.tests = $windows.tests
                $result.windowsReport = Get-AiNativeEvidenceFile -Path $windowsReportPath
                $result.windowsArtifacts = $windows.artifacts
            }
            TopologyPublish {
                $run = [IO.Path]::GetFullPath((Join-Path $SourceRoot $TopologyRunDirectory))
                if (-not $run.StartsWith((Join-Path $SourceRoot 'artifacts') + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or (Test-Path $run)) { throw 'TopologyPublish requires a new run directory inside source artifacts' }
                Invoke-Checked (Get-Command pwsh).Source @('-NoProfile','-File',(Join-Path $SourceRoot 'tools/topology/build.ps1'),'-SdkPath',$SdkPath,'-RunDirectory',$TopologyRunDirectory) (Join-Path $phaseDirectory 'publish.log')
                [void](Assert-Source)
                # HostSettings.Load copies the canonical topology Fantasy.config at startup.
                # Bind immutable outputs and the source configuration separately.
                $files = @(Get-ChildItem (Join-Path $run 'bin') -File -Recurse | Where-Object Name -ne 'Fantasy.config' | ForEach-Object { @{path=[IO.Path]::GetRelativePath($run,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash} })
                if ($files.Count -eq 0) { throw 'TopologyPublish produced no binaries' }
                @{source=$ExpectedCommit;fantasy=$fantasy;publishedUtc=[DateTime]::UtcNow.ToString('O');files=$files;
                    fantasyConfigurationSha256=(Get-FileHash -LiteralPath (Join-Path $SourceRoot 'infrastructure/topology/Fantasy.config')).Hash;
                    mutableRuntimeFiles=@('Fantasy.config: canonical topology configuration copied by HostSettings.Load')} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $run 'publication.json')
                $result.publication = Get-AiNativeEvidenceFile -Path (Join-Path $run 'publication.json')
            }
            TopologyAcceptance {
                Assert-TopologyPublication
                # The caller owns the service/database lifecycle. No existing process is restarted here.
                $acceptancePath = Join-Path (Join-Path $SourceRoot $TopologyRunDirectory) 'acceptance.json'
                $phaseStart = [DateTime]::UtcNow
                Invoke-Checked (Get-Command pwsh).Source @('-NoProfile','-File',(Join-Path $SourceRoot 'tools/topology/acceptance.ps1'),'-SdkPath',$SdkPath,'-RunDirectory',$TopologyRunDirectory) (Join-Path $phaseDirectory 'acceptance.log')
                [void](Assert-AiNativeTopologyAcceptanceReport -Path $acceptancePath -StartedUtc $phaseStart)
                Copy-Item -LiteralPath $acceptancePath -Destination (Join-Path $phaseDirectory 'acceptance.json')
                $result.acceptance = Get-AiNativeEvidenceFile -Path (Join-Path $phaseDirectory 'acceptance.json')
            }
            default {
                if ($phase -eq 'TopologyPlayMode') { Assert-TopologyPublication }
                if (-not (Test-Path -LiteralPath $UnityEditorPath -PathType Leaf)) { throw 'UnityEditorPath is required' }
                $env:ALLUSERSPROFILE = $env:ProgramData
                $mode = if ($phase -eq 'EditMode') { 'EditMode' } else { 'PlayMode' }
                $filter = if ($phase -eq 'EditMode') { 'AiNative' } else { 'AiNative.Client.Application.PlayModeTests.TopologyClientFlowTests' }
                $xml = Join-Path $phaseDirectory 'tests.xml'
                Invoke-Checked (Get-Command unity).Source @('test',(Join-Path $SourceRoot 'client/UnityProject'),'--editor-path',$UnityEditorPath,'--mode',$mode,'--filter',$filter,'--output',$xml,'--timeout','900','--format','json') (Join-Path $phaseDirectory 'unity-cli.json')
                $expected = if ($phase -eq 'EditMode') { $ExpectedEditModePassed } else { 1 }
                $fixtures = if ($phase -eq 'TopologyPlayMode') { @{$filter=1} } else { Get-AiNativeEditModeFixtureCounts -Profile $Profile }
                $result.tests = Assert-AiNativeNUnitReport -Path $xml -ExpectedPassed $expected -FixtureCounts $fixtures
            }
        }
        [void](Assert-Source)
        $result.status = 'Passed'; $result.finishedUtc = [DateTime]::UtcNow.ToString('O'); Save-Report
    }
    $report.status = 'Passed'
} catch {
    $report.status = 'Failed'
    if ($report.phases.Count) { $report.phases[-1].status = 'Failed' }
    $report.error = $_.Exception.Message
    throw
} finally {
    # Rejected reports and failed native-command logs remain indexed as evidence,
    # without giving those files a passed test result.
    foreach ($phaseResult in $report.phases) {
        $phaseResult.logs = @(Get-ChildItem -LiteralPath $phaseResult.evidenceDirectory -File -Recurse | Where-Object { $_.Extension -eq '.log' -or $_.Name -eq 'unity-cli.json' } | ForEach-Object { Get-AiNativeEvidenceFile -Path $_.FullName })
        $phaseResult.rawReports = @(Get-ChildItem -LiteralPath $phaseResult.evidenceDirectory -File -Recurse | Where-Object { $_.Extension -in @('.trx','.xml') -or $_.Name -eq 'acceptance.json' } | ForEach-Object { Get-AiNativeEvidenceFile -Path $_.FullName })
    }
    $report.finishedUtc = [DateTime]::UtcNow.ToString('O'); Save-Report
    [Environment]::SetEnvironmentVariable('ALLUSERSPROFILE',$previousAllUsersProfile,'Process')
    Pop-Location
}
Write-Host "Validation passed. Evidence: $EvidenceDirectory"
