$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. "$PSScriptRoot/../nunit-report.ps1"
$directory = Join-Path ([IO.Path]::GetFullPath("$PSScriptRoot/../../..")) ('artifacts/validation-contracts/'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $directory | Out-Null
function Must-Reject([scriptblock]$Action, [string]$Message) {
    try { & $Action | Out-Null } catch {
        if ($_.Exception.Message -notmatch [regex]::Escape($Message)) { throw }
        return
    }
    throw "Accepted invalid fixture: $Message"
}
$balanced = Join-Path $directory 'balanced.xml'
Set-Content -LiteralPath $balanced '<test-run result="Passed" total="2" passed="2" failed="0" skipped="0"><test-case fullname="AiNative.ClockTests.A" result="Passed"/><test-case fullname="AiNative.OtherTests.B" result="Passed"/></test-run>'
Must-Reject { Assert-AiNativeNUnitReport -Path $balanced -ExpectedPassed 2 -FixtureCounts @{'AiNative.ClockTests'=2} } 'fixture inventory'
$valid = Assert-AiNativeNUnitReport -Path $balanced -ExpectedPassed 2 -FixtureCounts @{'AiNative.ClockTests'=1;'AiNative.OtherTests'=1}
if ($valid.Fixtures.Count -ne 2) { throw 'Fixture discovery was not retained' }
$profile = Get-AiNativeEditModeFixtureCounts -Profile Baseline
if (($profile.Values | Measure-Object -Sum).Sum -ne 95) { throw 'Baseline inventory mismatch' }
$profile = Get-AiNativeEditModeFixtureCounts -Profile Candidate
if (($profile.Values | Measure-Object -Sum).Sum -ne 147) { throw 'Candidate inventory mismatch' }
$trx = Join-Path $directory 'probe.trx'
$probe = 'AiNative.Server.Fantasy.Tests.FantasyProbeInitializationTests.IndependentProbeRuntimeConfiguresOuterKcpBeforeItsFirstNetwork'
function Write-Trx([string]$Name, [string]$Outcome='Passed', [int]$Count=1) {
    Set-Content -LiteralPath $trx "<TestRun xmlns='http://microsoft.com/schemas/VisualStudio/TeamTest/2010'><Results><UnitTestResult testId='one' outcome='$Outcome'/></Results><TestDefinitions><UnitTest id='one'><TestMethod className='AiNative.Server.Fantasy.Tests.FantasyProbeInitializationTests' name='$Name'/></UnitTest></TestDefinitions><ResultSummary outcome='Completed'><Counters total='$Count' executed='$Count' passed='$Count' failed='0' notExecuted='0'/></ResultSummary></TestRun>"
}
Write-Trx IndependentProbeRuntimeConfiguresOuterKcpBeforeItsFirstNetwork
$summary = Assert-AiNativeTrxReport -Path $trx -ExpectedPassed 1 -RequiredTest $probe
if (-not $summary.Sha256 -or $summary.Passed -ne 1) { throw 'TRX identity not retained' }
$counterFailures=@()
foreach($counter in @(@('notExecuted','1'),@('executed','0'))) {
    Write-Trx IndependentProbeRuntimeConfiguresOuterKcpBeforeItsFirstNetwork
    [xml]$report=Get-Content -LiteralPath $trx -Raw
    $report.TestRun.ResultSummary.Counters.SetAttribute($counter[0],$counter[1]);$report.Save($trx)
    try { Assert-AiNativeTrxReport -Path $trx -ExpectedPassed 1 -RequiredTest $probe | Out-Null; $counterFailures += ($counter[0]+'='+$counter[1]) }
    catch { if($_.Exception.Message -notmatch 'TRX gate'){throw} }
}
if($counterFailures.Count){throw ('Accepted contradictory TRX counters: '+($counterFailures -join ', '))}
foreach($name in @('total','executed','passed','failed','notExecuted')) {
    Write-Trx IndependentProbeRuntimeConfiguresOuterKcpBeforeItsFirstNetwork
    [xml]$report=Get-Content -LiteralPath $trx -Raw
    $report.TestRun.ResultSummary.Counters.RemoveAttribute($name);$report.Save($trx)
    Must-Reject { Assert-AiNativeTrxReport -Path $trx -ExpectedPassed 1 -RequiredTest $probe } 'TRX gate missing or invalid counter'
}
foreach($name in @('error','timeout','aborted','inconclusive','passedButRunAborted','notRunnable','disconnected','warning','inProgress','pending')) {
    Write-Trx IndependentProbeRuntimeConfiguresOuterKcpBeforeItsFirstNetwork
    [xml]$report=Get-Content -LiteralPath $trx -Raw
    $report.TestRun.ResultSummary.Counters.SetAttribute($name,'1');$report.Save($trx)
    Must-Reject { Assert-AiNativeTrxReport -Path $trx -ExpectedPassed 1 -RequiredTest $probe } 'TRX gate nonzero or invalid counter'
}
foreach($value in @('invalid','-1','1.0')) {
    Write-Trx IndependentProbeRuntimeConfiguresOuterKcpBeforeItsFirstNetwork
    [xml]$report=Get-Content -LiteralPath $trx -Raw
    $report.TestRun.ResultSummary.Counters.SetAttribute('executed',$value);$report.Save($trx)
    Must-Reject { Assert-AiNativeTrxReport -Path $trx -ExpectedPassed 1 -RequiredTest $probe } 'TRX gate missing or invalid counter'
}
Write-Trx AnotherTest
Must-Reject { Assert-AiNativeTrxReport -Path $trx -ExpectedPassed 1 -RequiredTest $probe } 'required test'
Write-Trx IndependentProbeRuntimeConfiguresOuterKcpBeforeItsFirstNetwork NotExecuted
Must-Reject { Assert-AiNativeTrxReport -Path $trx -ExpectedPassed 1 -RequiredTest $probe } 'TRX gate'
Write-Trx IndependentProbeRuntimeConfiguresOuterKcpBeforeItsFirstNetwork Passed 2
Must-Reject { Assert-AiNativeTrxReport -Path $trx -ExpectedPassed 1 -RequiredTest $probe } 'TRX gate'
Must-Reject { Assert-AiNativeTrxReport -Path (Join-Path $directory 'missing.trx') } 'Missing TRX report'
Must-Reject { Assert-AiNativeTopologyAcceptanceReport -Path (Join-Path $directory 'missing.json') -StartedUtc ([datetime]::UtcNow) } 'Missing or stale topology report'
$acceptance = Join-Path $directory 'acceptance.json'
@{exit=0;evidence=@(1..8 | ForEach-Object { @{passed=$true} })} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $acceptance
[void](Assert-AiNativeTopologyAcceptanceReport -Path $acceptance -StartedUtc ([datetime]::UtcNow.AddMinutes(-1)))
Must-Reject { Assert-AiNativeTopologyAcceptanceReport -Path $acceptance -StartedUtc ([datetime]::UtcNow.AddMinutes(1)) } 'Missing or stale topology report'
@{exit=1;evidence=@(1..8 | ForEach-Object { @{passed=$true} })} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $acceptance
Must-Reject { Assert-AiNativeTopologyAcceptanceReport -Path $acceptance -StartedUtc ([datetime]::UtcNow.AddMinutes(-1)) } 'Topology acceptance gate failed'
foreach($exit in @($false,$null,'0')) {
    @{exit=$exit;evidence=@(1..8 | ForEach-Object { @{passed=$true} })} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $acceptance
    Must-Reject { Assert-AiNativeTopologyAcceptanceReport -Path $acceptance -StartedUtc ([datetime]::UtcNow.AddMinutes(-1)) } 'Topology acceptance gate failed'
}
Write-Host 'Validation contracts: named inventories, independent probe identity, missing/stale/failed reports passed.'
