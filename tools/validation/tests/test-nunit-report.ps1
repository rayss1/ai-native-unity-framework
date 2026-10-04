$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/../nunit-report.ps1"
$fixtureDirectory = Join-Path ([IO.Path]::GetFullPath("$PSScriptRoot/../../..")) 'artifacts/release-validation-20261003/report-fixtures'
New-Item -ItemType Directory -Path $fixtureDirectory -Force | Out-Null
function Write-Fixture([string]$Name, [string]$Contents) {
    $path = Join-Path $fixtureDirectory "$Name.xml"
    Set-Content -LiteralPath $path -Value $Contents
    return $path
}
function Must-Reject([scriptblock]$Action, [string]$Label) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw "Accepted invalid report: $Label" }
}
$good = Write-Fixture good '<test-run result="Passed" total="2" passed="2" failed="0" skipped="0"><test-case fullname="AiNative.ClockTests.FixedClock" result="Passed"/><test-case fullname="AiNative.TransportTests.Disposes" result="Passed"/></test-run>'
$summary = Assert-AiNativeNUnitReport -Path $good -ExpectedPassed 2 -RequiredFixtures @('AiNative.ClockTests', 'AiNative.TransportTests')
if ($summary.Passed -ne 2) { throw 'Valid report count was lost' }
Must-Reject { Assert-AiNativeNUnitReport -Path $good -ExpectedPassed 3 } 'stale count'
Must-Reject { Assert-AiNativeNUnitReport -Path $good -ExpectedPassed 2 -RequiredFixtures @('AiNative.MissingTests') } 'missing mandatory fixture'
$failed = Write-Fixture failed '<test-run result="Failed" total="2" passed="1" failed="1" skipped="0"><test-case fullname="AiNative.Tests.A" result="Passed"/><test-case fullname="AiNative.Tests.B" result="Failed"/></test-run>'
Must-Reject { Assert-AiNativeNUnitReport -Path $failed -ExpectedPassed 2 } 'failed test'
$skipped = Write-Fixture skipped '<test-run result="Passed" total="2" passed="1" failed="0" skipped="1"><test-case fullname="AiNative.Tests.A" result="Passed"/><test-case fullname="AiNative.Tests.B" result="Skipped"/></test-run>'
Must-Reject { Assert-AiNativeNUnitReport -Path $skipped -ExpectedPassed 2 } 'skipped live-network test'
$partial = Write-Fixture partial '<test-run result="Passed" total="2" passed="2" failed="0" skipped="0"><test-case fullname="AiNative.Tests.A" result="Passed"/></test-run>'
Must-Reject { Assert-AiNativeNUnitReport -Path $partial -ExpectedPassed 2 } 'incomplete case inventory'
Must-Reject { Assert-AiNativeNUnitReport -Path (Join-Path $fixtureDirectory 'missing.xml') -ExpectedPassed 2 } 'missing output'
Write-Output 'NUnit report validation: 1 valid report and 6 rejection cases passed.'
