#requires -Version 7.0
# JSON bridge for the macOS shell entry; the shared report/profile code owns the rules.
[CmdletBinding()]
param(
    [ValidateSet('Contract', 'EditMode', 'PlayMode')][string]$Mode = 'Contract',
    [string]$Path
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/nunit-report.ps1"
$profile = Get-AiNativeValidationProfile -Profile Development
$playFixtures = Get-AiNativeLegacyKcpFixtureCounts
if ($Mode -eq 'Contract') {
    [pscustomobject]@{
        Profile = 'Development'
        EditModePassed = $profile.EditModePassed
        EditModeFixtures = $profile.FixtureCounts
        PlayModePassed = [int](($playFixtures.Values | Measure-Object -Sum).Sum)
        PlayModeFixtures = $playFixtures
        PlayModeFilter = ($playFixtures.Keys | Sort-Object) -join ';'
        FantasyCommit = $profile.FantasyCommit
        FantasyUnityVersion = $profile.FantasyUnityVersion
    } | ConvertTo-Json -Depth 5
} else {
    if (-not $Path) {
        throw 'A report path is required for validation'
    }
    $fixtures = if ($Mode -eq 'EditMode') {
        $profile.FixtureCounts
    } else {
        $playFixtures
    }
    $expected = [int](($fixtures.Values | Measure-Object -Sum).Sum)
    Assert-AiNativeNUnitReport -Path $Path -ExpectedPassed $expected -FixtureCounts $fixtures |
        ConvertTo-Json -Depth 5
}
