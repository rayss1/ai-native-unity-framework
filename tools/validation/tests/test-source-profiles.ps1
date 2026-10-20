$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. "$PSScriptRoot/../nunit-report.ps1"
. "$PSScriptRoot/../source-identity.ps1"
function Must-Reject([scriptblock]$Action,[string]$Message) {
    try { & $Action | Out-Null } catch { if ($_.Exception.Message -notmatch [regex]::Escape($Message)) { throw }; return }
    throw "Accepted invalid source/profile: $Message"
}
$current='bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'
foreach ($item in @(@('Baseline',95),@('Candidate',147),@('TerminalDelivery',164),@('Current',166),@('Development',294))) {
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
$deletedPaths=@()
function git { $global:LASTEXITCODE=0; if ($args -contains '--deleted') { return $deletedPaths }; return $sourcePaths }
$manifest=Get-AiNativeSourceManifest -Root $root
[void](Assert-AiNativeSourceManifest -Root $root -ExpectedSha256 $manifest.sha256)
Set-Content -LiteralPath (Join-Path $root 'one.cs') 'modified source'
Must-Reject { Assert-AiNativeSourceManifest -Root $root -ExpectedSha256 $manifest.sha256 } 'manifest mismatch'
Set-Content -LiteralPath (Join-Path $root 'one.cs') 'original source'
Set-Content -LiteralPath (Join-Path $root 'two.cs') 'new source'
$sourcePaths+= 'two.cs'
Must-Reject { Assert-AiNativeSourceManifest -Root $root -ExpectedSha256 $manifest.sha256 } 'manifest mismatch'
$sourcePaths=@('one.cs','removed.cs'); $deletedPaths=@('removed.cs')
$withDeletion=Get-AiNativeSourceManifest -Root $root
if (@($withDeletion.files | Where-Object { $_.path -eq 'removed.cs' -and $_.sha256 -eq 'deleted' }).Count -ne 1) { throw 'Tracked deletion not bound to source identity' }
Set-Content -LiteralPath (Join-Path $root 'removed.cs') 'restored source'
$deletedPaths=@()
Must-Reject { Assert-AiNativeSourceManifest -Root $root -ExpectedSha256 $withDeletion.sha256 } 'manifest mismatch'
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
    if($profileParameter.Extent.Text -notmatch "'Development'"){throw 'Entry cannot select development profile'}
    $gate=$ast.Find({param($node) $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Assert-AiNativeEditModeProfile'},$true)
    if(-not $gate){throw 'Entry bypasses shared profile gate'}
}
Write-Host 'Source profiles: historical/current inventories, wrong-count rejection, byte/inventory drift, and both entry contracts passed.'
