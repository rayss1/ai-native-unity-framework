param(
 [string]$SdkPath,[string]$RunDirectory='artifacts/topology-qualification',
 [ValidateSet('sweep','single','soak')][string]$Profile='sweep',
 [ValidateRange(1,3600)][int]$WarmupSeconds=60,[ValidateRange(1,7200)][int]$DurationSeconds=300,
 [ValidateRange(1,60)][int]$InputHz=60,[ValidateRange(1,[int]::MaxValue)][int]$Seed=20261002,
 [ValidateRange(60,216000)][int]$MatchTicks=36000,[ValidateRange(2,8)][int]$PlayersPerRoom=8,
 [ValidateRange(1,16)][int]$WorkerCount=2,[ValidateRange(1,16)][int]$RoomsPerWorker=2,
 [int]$RoomCount=0,[ValidateRange(1,[long]::MaxValue)][long]$ReplayMaxBytes=8589934592,
 [switch]$SkipBuild,[switch]$SkipAudit,[switch]$PlanOnly
)
$ErrorActionPreference='Stop'
if($WorkerCount*$RoomsPerWorker -gt 16){throw 'At most 16 rooms per node'}
if($RoomCount -eq 0){$RoomCount=2*$WorkerCount*$RoomsPerWorker}
if($RoomCount -ne 2*$WorkerCount*$RoomsPerWorker){throw 'RoomCount must fill the exact two-node configured capacity'}
if(-not $PlanOnly -and ($WarmupSeconds -lt 60 -or $InputHz -ne 60 -or $PlayersPerRoom -ne 8 -or $DurationSeconds -lt 300)) {
 throw 'Qualification requires at least 60s warmup, 300s measurement, 60Hz and eight players per room'
}
$plan=@()
if($Profile -eq 'sweep') {
 $plan+=@{Name='compatibility';Mode='bots';Workers=2;Density=2;Rooms=8;Players=2;InputHz=20;WarmupSeconds=3;DurationSeconds=10;MatchTicks=6000}
 foreach($density in @(1,2,4)) {
  $plan+=@{Name=('capacity-'+$density);Mode='qualification';Workers=$WorkerCount;Density=$density;Rooms=(2*$WorkerCount*$density);Players=$PlayersPerRoom;InputHz=$InputHz;WarmupSeconds=$WarmupSeconds;DurationSeconds=$DurationSeconds;MatchTicks=$MatchTicks}
 }
} else {
 $plan+=@{Name=$Profile;Mode='qualification';Workers=$WorkerCount;Density=$RoomsPerWorker;Rooms=$RoomCount;Players=$PlayersPerRoom;InputHz=$InputHz;WarmupSeconds=$WarmupSeconds;DurationSeconds=$(if($Profile -eq 'soak'){[Math]::Max(3600,$DurationSeconds)}else{$DurationSeconds});MatchTicks=$MatchTicks}
}
if($PlanOnly) {
 @{Stages=$plan;AfterSweep='Stop increasing density at first failure; soak highest passing configuration for 3600 seconds';ReplayMaxBytes=$ReplayMaxBytes;Seed=$Seed} | ConvertTo-Json -Depth 8
 return
}
. "$PSScriptRoot/common.ps1" -SdkPath $SdkPath -RunDirectory $RunDirectory
if(-not $env:AINATIVE_TEST_POSTGRES){throw 'Isolated AINATIVE_TEST_POSTGRES required'}
if(-not $SkipBuild){& "$PSScriptRoot/build.ps1" -SdkPath $SdkPath -RunDirectory $RunDirectory -SkipAudit:$SkipAudit}
if(-not (Test-Path -LiteralPath (Join-Path $Run 'bin'))){throw 'Qualification binaries missing'}
if(-not (Test-Path -LiteralPath (Join-Path $Run 'replay-identities.json'))){
 & "$PSScriptRoot/replay-identities.ps1" -RunDirectory $RunDirectory
}
$results=@();$highest=$null
function Invoke-Stage($stage) {
 $stageRun=Join-Path $Run $stage.Name
 if(Test-Path -LiteralPath $stageRun){throw ('Refusing to overwrite retained qualification stage: '+$stageRun)}
 New-Item -ItemType Directory -Path $stageRun | Out-Null
 Copy-Item -LiteralPath (Join-Path $Run 'bin') -Destination (Join-Path $stageRun 'bin') -Recurse
 $relativeStage=[IO.Path]::GetRelativePath($Repo,$stageRun)
 $stageError=$null;$exitCode=1;$replayReport=$null;$inputReport=$null;$inputAuditExit=$null
 try {
  & "$PSScriptRoot/faultserver.ps1" -Mode $stage.Mode -SdkPath $SdkPath -RunDirectory $relativeStage -SkipBuild -SkipAudit:$SkipAudit `
   -WorkerCount $stage.Workers -RoomsPerWorker $stage.Density -RoomCount $stage.Rooms -PlayersPerRoom $stage.Players `
   -InputHz $stage.InputHz -WarmupSeconds $stage.WarmupSeconds -DurationSeconds $stage.DurationSeconds -Seed $Seed `
   -MatchTicks $stage.MatchTicks -ReplayMaxBytes $ReplayMaxBytes | Out-Host
  $exitCode=$LASTEXITCODE
 } catch {$stageError=$_.Exception.GetType().Name}
 $reportPath=Join-Path $stageRun ($stage.Mode+'.json')
 $passed=$false
 if(Test-Path -LiteralPath $reportPath) {
  $report=Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
  $passed=$exitCode -eq 0 -and $report.exit -eq 0
  if($stage.Mode -eq 'qualification') {
   $detailPath=Join-Path $stageRun 'qualification-detail.json'
   $passed=$passed -and (Test-Path -LiteralPath $detailPath)
   if(Test-Path -LiteralPath $detailPath){
    $detail=Get-Content -LiteralPath $detailPath -Raw | ConvertFrom-Json
    $passed=$passed -and $detail.passed -and $detail.replay.Count -eq $detail.completedRooms
    # Complete offline evidence before increasing density; services have already stopped.
    $replayReport=Join-Path $stageRun 'independent-completed-replays.json'
    $inputReport=Join-Path $stageRun 'capacity-input-audit.json'
    try {
     $expected=Get-Content -LiteralPath (Join-Path $Run 'replay-identities.json') -Raw | ConvertFrom-Json
     & "$PSScriptRoot/verify-all-replays.ps1" -RunDirectory $stageRun -SdkPath $SdkPath `
      -ExpectedSource $expected.Source -ExpectedFantasy $expected.Fantasy -ReportPath $replayReport `
      -MinimumCompletedCaptures ([Math]::Max(1,[int]$detail.completedRooms)) | Out-Host
     & (Get-Command python -ErrorAction Stop).Source "$PSScriptRoot/capacity-input-audit.py" `
      --native-report $detailPath --independent-replays $replayReport --output $inputReport | Out-Host
     $inputAuditExit=$LASTEXITCODE
     if($inputAuditExit -ne 0){$passed=$false;$stageError='AcceptedInputCoverageRejected'}
     if(-not (Test-Path -LiteralPath $inputReport)){throw 'Accepted input report missing'}
     $inputAudit=Get-Content -LiteralPath $inputReport -Raw | ConvertFrom-Json
     if($inputAudit.accepted_input_audit_passed -isnot [bool] -or -not $inputAudit.accepted_input_audit_passed -or
        $inputAudit.native_report_sha256 -ne (Get-FileHash -LiteralPath $detailPath).Hash){
      $passed=$false;if(-not $stageError){$stageError='AcceptedInputEvidenceRejected'}
     }
    } catch { $passed=$false; if(-not $stageError){$stageError=$_.Exception.GetType().Name} }
   }
  }
 }
 return @{Stage=$stage;Passed=$passed;ExitCode=$exitCode;Report=$reportPath;FailureType=$stageError;
  IndependentReplayReport=$replayReport;InputAuditReport=$inputReport;InputAuditExitCode=$inputAuditExit}
}
foreach($stage in $plan) {
 $result=Invoke-Stage $stage;$results+=,$result
 if(-not $result.Passed) {break}
 if($stage.Mode -eq 'qualification'){$highest=$stage.Clone()}
}
$passed=$results.Count -gt 0 -and $results[-1].Passed
if($Profile -eq 'sweep' -and $highest) {
 $highest.Name='soak-highest-passing';$highest.DurationSeconds=3600
 $soak=Invoke-Stage $highest;$results+=,$soak;$passed=$soak.Passed
}
@{Passed=$passed;Profile=$Profile;Selected=$highest;Stages=$results;RecordedUtc=[datetime]::UtcNow.ToString('O');Qualification='Actual Arena topology only; Regional/Degraded wire and legacy 64-player budgets remain separate'} |
 ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $Run 'qualification-summary.json') -Encoding utf8
if(-not $passed){exit 1}
exit 0
