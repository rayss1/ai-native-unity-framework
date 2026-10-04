[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ProjectPath,
    [Parameter(Mandatory)][string]$UnityEditorPath,
    [Parameter(Mandatory)][string]$EvidenceDirectory,
    [ValidateRange(60,7200)][int]$BuildTimeoutSeconds = 3600
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$project = [IO.Path]::GetFullPath($ProjectPath)
# This entry intentionally operates on a disposable validation copy. It must not
# change the owner's open Editor project or its active target.
if (-not $project.StartsWith((Join-Path $repo 'artifacts') + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Android validation requires an isolated project under workspace artifacts' }
$evidence = [IO.Path]::GetFullPath($EvidenceDirectory)
if (Test-Path -LiteralPath $evidence) { throw 'Use a fresh Android evidence directory' }
New-Item -ItemType Directory -Path $evidence | Out-Null
$settings = Join-Path $project 'ProjectSettings/ProjectSettings.asset'
$snapshot = [IO.File]::ReadAllBytes($settings)
$beforeHash = (Get-FileHash -LiteralPath $settings).Hash
$output = Join-Path $evidence 'AiNative.BattleClient.apk'
$sourceRoots = @(@{name='application';path=(Join-Path $project 'Assets')})
$manifestPath = Join-Path $project 'Packages/manifest.json'
$packageManifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
foreach ($dependency in $packageManifest.dependencies.GetEnumerator()) {
    if (([string]$dependency.Value).StartsWith('file:')) {
        $local = ([string]$dependency.Value).Substring(5)
        $resolved = if ([IO.Path]::IsPathRooted($local)) { $local } else { Join-Path (Join-Path $project 'Packages') $local }
        $sourceRoots += @{name=$dependency.Key;path=[IO.Path]::GetFullPath($resolved)}
    }
}
$sourceFiles = @($sourceRoots | ForEach-Object {
    $scope = $_
    Get-ChildItem -LiteralPath $scope.path -File -Recurse | Where-Object Extension -in @('.cs','.asmdef','.shader','.uxml','.uss','.proto','.csproj','.props','.targets') | ForEach-Object {
        @{scope=$scope.name;path=[IO.Path]::GetRelativePath($scope.path,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash;absolutePath=$_.FullName}
    }
})
$sourceFiles | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $evidence 'SOURCE-MANIFEST.json')
$validation = [ordered]@{status='Running';startedUtc=[DateTime]::UtcNow.ToString('O');
    sourceManifestSha256=(Get-FileHash (Join-Path $evidence 'SOURCE-MANIFEST.json')).Hash;
    manifestSha256=(Get-FileHash $manifestPath).Hash;packageLockSha256=(Get-FileHash (Join-Path $project 'Packages/packages-lock.json')).Hash;
    settingsSha256=$beforeHash;unityEditorSha256=(Get-FileHash $UnityEditorPath).Hash;deviceAcceptancePassed=$false}
$validationPath = Join-Path $evidence 'BUILD-VALIDATION.json'
$validation | ConvertTo-Json | Set-Content -LiteralPath $validationPath
$previousAllUsersProfile = [Environment]::GetEnvironmentVariable('ALLUSERSPROFILE','Process')
try {
    $env:ALLUSERSPROFILE = $env:ProgramData
    unity run $project --editor-path $UnityEditorPath --timeout 900 --log-file (Join-Path $evidence 'prepare.log') --no-tail --format json -- -buildTarget StandaloneWindows64 -nographics -executeMethod AiNative.Client.Editor.AndroidBattleClientBuild.PrepareAndroidBatchBuild > (Join-Path $evidence 'prepare-cli.json')
    if ($LASTEXITCODE -ne 0) { throw 'Android definition preparation failed' }
    # A new Editor process compiles Fantasy for Android before invoking the build.
    unity run $project --editor-path $UnityEditorPath --timeout $BuildTimeoutSeconds --log-file (Join-Path $evidence 'build.log') --no-tail --format json -- -buildTarget Android -nographics -executeMethod AiNative.Client.Editor.AndroidBattleClientBuild.BuildAndroidRelease --ainative-build-output $output > (Join-Path $evidence 'build-cli.json')
    if ($LASTEXITCODE -ne 0) { throw 'Android IL2CPP build failed' }
    foreach ($name in @('AiNative.BattleClient.apk','ANDROID-BUILD-INFO.json','THIRD-PARTY-NOTICES.md','Fantasy-LICENSE.txt')) {
        if (-not (Test-Path (Join-Path $evidence $name) -PathType Leaf)) { throw "Missing Android distribution artifact: $name" }
    }
    $info = Get-Content (Join-Path $evidence 'ANDROID-BUILD-INFO.json') -Raw | ConvertFrom-Json
    if ($info.backend -ne 'IL2CPP' -or $info.abi -ne 'arm64-v8a' -or $info.development -or $info.targetApi -ne 35 -or
        $info.apkSha256 -ne (Get-FileHash $output).Hash.ToLowerInvariant()) { throw 'Android build identity mismatch' }
    foreach ($file in $sourceFiles) {
        if ((Get-FileHash -LiteralPath $file.absolutePath).Hash -ne $file.sha256) { throw 'Android source changed during the build' }
    }
    $validation.status='Passed';$validation.apkSha256=$info.apkSha256
} catch {
    $validation.status='Failed';$validation.error=$_.Exception.Message
    throw
} finally {
    [Environment]::SetEnvironmentVariable('ALLUSERSPROFILE',$previousAllUsersProfile,'Process')
    [IO.File]::WriteAllBytes($settings,$snapshot)
    if ((Get-FileHash -LiteralPath $settings).Hash -ne $beforeHash) { throw 'Original project settings were not restored' }
    $validation.finishedUtc=[DateTime]::UtcNow.ToString('O')
    $validation | ConvertTo-Json | Set-Content -LiteralPath $validationPath
}
Write-Host "Android build passed. Device acceptance remains separate: $evidence"
