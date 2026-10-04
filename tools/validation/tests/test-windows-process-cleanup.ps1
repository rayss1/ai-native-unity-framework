param([string]$SourceRevision)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$script = Join-Path $repo 'tools/run-unity-windows-validation.ps1'
$text = if ($SourceRevision) { (& git -C $repo show ($SourceRevision+':tools/run-unity-windows-validation.ps1')) -join "`n" } else { Get-Content -LiteralPath $script -Raw }
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseInput($text,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Windows runner parsing failed'}
$function=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-Unity'},$true)
if(-not $function){throw 'Missing actual Unity process function'}
Invoke-Expression $function.Extent.Text
$UnityEditorPath=(Get-Command pwsh).Source
$unityAllUsersProfile=$env:ProgramData
$UnityTimeoutSeconds=1
$record=Join-Path $repo ('artifacts/release-validation-20261003/timeout-child-'+[guid]::NewGuid().ToString('N')+'.json')
$oldRecord=$env:AINATIVE_TEST_UNITY_PID_RECORD
$env:AINATIVE_TEST_UNITY_PID_RECORD=$record
$observed=$false
try {
    try {
        Invoke-Unity -Description 'Fixture Editor' -ArgumentList @('-NoProfile','-Command','@{pid=$PID;startUtc=(Get-Process -Id $PID).StartTime.ToUniversalTime().ToString("O")}|ConvertTo-Json|Set-Content -LiteralPath $env:AINATIVE_TEST_UNITY_PID_RECORD;Start-Sleep -Seconds 3')
    } catch {
        if($_.Exception.Message -notmatch 'timed out'){throw}
        $observed=$true
    }
    if(-not $observed){throw 'A hung owned Editor was accepted without a timeout'}
    $identity=Get-Content -LiteralPath $record -Raw|ConvertFrom-Json
    $survivor=Get-Process -Id $identity.pid -ErrorAction SilentlyContinue
    if($survivor -and $survivor.StartTime.ToUniversalTime().Ticks -eq ([datetime]$identity.startUtc).ToUniversalTime().Ticks){throw 'The timed-out owned child survived cleanup'}
} finally { $env:AINATIVE_TEST_UNITY_PID_RECORD=$oldRecord }
Write-Host 'Actual Windows runner process function: timeout rejected and its owned child cleaned up.'
