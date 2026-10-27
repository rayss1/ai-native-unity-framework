#requires -Version 7.0
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/../nunit-report.ps1"
$bridge = [IO.Path]::GetFullPath("$PSScriptRoot/../unity-test-contract.ps1")
$directory = Join-Path ([IO.Path]::GetFullPath("$PSScriptRoot/../../..")) ('artifacts/unity-contract-tests/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $directory | Out-Null
$report = Join-Path $directory 'report with spaces.xml'
$pwsh = (Get-Process -Id $PID).Path

function Invoke-Bridge([string]$Mode, [bool]$Accept) {
    $output = & $pwsh -NoLogo -NoProfile -File $bridge -Mode $Mode -Path $report 2>&1
    if (($LASTEXITCODE -eq 0) -ne $Accept) {
        throw "Unexpected bridge exit for $Mode`: $output"
    }
    if ($Accept) {
        return ($output | ConvertFrom-Json)
    }
}
function Write-Report([hashtable]$Fixtures) {
    $total = [int](($Fixtures.Values | Measure-Object -Sum).Sum)
    $cases = foreach ($fixture in $Fixtures.Keys) {
        for ($index = 0; $index -lt $Fixtures[$fixture]; $index++) {
            "<test-case fullname='$fixture.Case$index' result='Passed'/>"
        }
    }
    "<test-run result='Passed' total='$total' passed='$total' failed='0' skipped='0'>$($cases -join '')</test-run>" |
        Set-Content -LiteralPath $report
}
$contract = Invoke-Bridge Contract $true
$profile = Get-AiNativeValidationProfile -Profile Development
if ($contract.Profile -ne 'Development' -or $contract.EditModePassed -ne $profile.EditModePassed -or
    $contract.FantasyCommit -ne $profile.FantasyCommit -or $contract.FantasyUnityVersion -ne $profile.FantasyUnityVersion) {
    throw 'Shell contract differs from shared profile'
}
$playFixtures = Get-AiNativeLegacyKcpFixtureCounts
if ($contract.PlayModeFilter -ne (($playFixtures.Keys | Sort-Object) -join ';') -or
    $contract.PlayModePassed -ne ($playFixtures.Values | Measure-Object -Sum).Sum) {
    throw 'PlayMode selection drift'
}

foreach ($mode in @('EditMode', 'PlayMode')) {
    $fixtures = if ($mode -eq 'EditMode') {
        $profile.FixtureCounts.Clone()
    } else {
        $playFixtures.Clone()
    }
    Write-Report $fixtures
    $result = Invoke-Bridge $mode $true
    if ($result.Passed -ne ($fixtures.Values | Measure-Object -Sum).Sum -or
        $result.Sha256 -ne (Get-FileHash -LiteralPath $report).Hash.ToLowerInvariant()) {
        throw 'Lost report provenance'
    }
    # A green total cannot hide a missing named fixture or an unrelated fixture.
    $name = @($fixtures.Keys)[0]
    $fixtures['Unexpected.Tests'] = $fixtures[$name]
    $fixtures.Remove($name)
    Write-Report $fixtures
    Invoke-Bridge $mode $false
}
Write-Report @{
    'Historical.Tests' = 51
}
Invoke-Bridge EditMode $false
Write-Report @{
    'AiNative.Client.Application.PlayModeTests.BattleClientKcpPlayModeTests' = 2
}
Invoke-Bridge PlayMode $false
Write-Report $playFixtures
$valid = Get-Content -LiteralPath $report -Raw
foreach ($bad in @(
        $valid.Replace("Case1'", "Case0'"),
        $valid.Replace("Case0' result='Passed'", "Case0' result='Skipped'"),
        $valid.Replace("Case0' result='Passed'", "Case0' result='Failed'"),
        $valid.Replace("total='3'", "total='4'"),
        '<test-run'
    )) {
    Set-Content -LiteralPath $report $bad
    Invoke-Bridge PlayMode $false
}
Remove-Item -LiteralPath $report
Invoke-Bridge PlayMode $false
Write-Host 'Unity shell contract: shared profiles, valid reports/provenance, historical counts, fixture substitution, duplicates, skipped/failed cases and missing/malformed reports passed.'
