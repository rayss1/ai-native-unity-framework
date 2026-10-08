$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. "$PSScriptRoot/../nunit-report.ps1"
. "$PSScriptRoot/../source-identity.ps1"
function Must-Reject([scriptblock]$Action,[string]$Message) {
    try { & $Action | Out-Null } catch { if ($_.Exception.Message -notmatch [regex]::Escape($Message)) { throw }; return }
    throw "Accepted invalid source/profile: $Message"
}
$current='bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'
foreach ($item in @(@('Baseline',95),@('Candidate',147),@('TerminalDelivery',164),@('Current',166))) {
    $commit=if($item[0] -eq 'Baseline'){'c9098be7e2a44efc42182a87aca2551648993705'}else{$current}
    $profile=Assert-AiNativeEditModeProfile -Profile $item[0] -Commit $commit -ExpectedPassed $item[1]
    if($profile.EditModePassed -ne $item[1]){throw 'Profile total differs from named inventory'}
    Must-Reject { Assert-AiNativeEditModeProfile -Profile $item[0] -Commit $commit -ExpectedPassed ($item[1]+1) } 'reviewed'
}
Must-Reject { Assert-AiNativeEditModeProfile -Profile Candidate -Commit $current -ExpectedPassed 128 } 'reviewed'
Must-Reject { Assert-AiNativeEditModeProfile -Profile TerminalDelivery -Commit $current -ExpectedPassed 147 } 'reviewed'
Must-Reject { Assert-AiNativeEditModeProfile -Profile TerminalDelivery -Commit 'c9098be7e2a44efc42182a87aca2551648993705' -ExpectedPassed 164 } 'matching source'
# Exercise real file hashing and inventory changes with a controlled Git enumerator.
$root=Join-Path ([IO.Path]::GetFullPath("$PSScriptRoot/../../..")) ('artifacts/source-profile-tests/'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
Set-Content -LiteralPath (Join-Path $root 'one.cs') 'original source'
$sourcePaths=@('one.cs','server/vendor/Fantasy')
function git { $global:LASTEXITCODE=0; return $sourcePaths }
$manifest=Get-AiNativeSourceManifest -Root $root
[void](Assert-AiNativeSourceManifest -Root $root -ExpectedSha256 $manifest.sha256)
Set-Content -LiteralPath (Join-Path $root 'one.cs') 'modified source'
Must-Reject { Assert-AiNativeSourceManifest -Root $root -ExpectedSha256 $manifest.sha256 } 'manifest mismatch'
Set-Content -LiteralPath (Join-Path $root 'one.cs') 'original source'
Set-Content -LiteralPath (Join-Path $root 'two.cs') 'new source'
$sourcePaths+= 'two.cs'
Must-Reject { Assert-AiNativeSourceManifest -Root $root -ExpectedSha256 $manifest.sha256 } 'manifest mismatch'
$sourcePaths=@('missing.cs')
Must-Reject { Get-AiNativeSourceManifest -Root $root } 'file is missing'
$sourcePaths=@('../outside.cs')
Must-Reject { Get-AiNativeSourceManifest -Root $root } 'escapes repository'
Remove-Item Function:git
# Both executable entries must accept the current profile and use the same gate.
foreach ($path in @("$PSScriptRoot/../run-current-source.ps1","$PSScriptRoot/../../run-unity-windows-validation.ps1")) {
    $tokens=$null;$errors=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile($path,[ref]$tokens,[ref]$errors)
    if($errors.Count){throw 'Entry parsing failed'}
    $profileParameter=$ast.ParamBlock.Parameters | Where-Object {$_.Name.VariablePath.UserPath -eq 'Profile'}
    if($profileParameter.Extent.Text -notmatch "'Current'"){throw 'Entry cannot select current profile'}
    $gate=$ast.Find({param($node) $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Assert-AiNativeEditModeProfile'},$true)
    if(-not $gate){throw 'Entry bypasses shared profile gate'}
}
Write-Host 'Source profiles: historical/current inventories, wrong-count rejection, byte/inventory drift, and both entry contracts passed.'
