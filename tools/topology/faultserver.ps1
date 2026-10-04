param([ValidateSet('backend-restarts','player-outage','battle-crash','capacity','bots','qualification','expired-ticket','party-notifications')][string]$Mode, [string]$SdkPath, [string]$RunDirectory='artifacts/topology-local', [switch]$SkipBuild, [switch]$SkipAudit, [int]$MatchTicks=1200, [ValidateRange(1,86400)][int]$DeadlineSeconds=240,
 [ValidateRange(1,16)][int]$WorkerCount=2,[ValidateRange(1,16)][int]$RoomsPerWorker=2,[ValidateRange(2,8)][int]$PlayersPerRoom=2,
 [ValidateRange(1,32)][int]$RoomCount=8,[ValidateRange(1,60)][int]$InputHz=60,[ValidateRange(1,3600)][int]$WarmupSeconds=60,
 [ValidateRange(1,7200)][int]$DurationSeconds=300,[ValidateRange(1,[int]::MaxValue)][int]$Seed=20261002,
 [ValidateRange(1,[long]::MaxValue)][long]$ReplayMaxBytes=2147483648)
if($Mode -eq 'qualification' -and ($RoomCount -ne 2*$WorkerCount*$RoomsPerWorker -or $WorkerCount*$RoomsPerWorker -gt 16)){throw 'Qualification must fill the exact two-node room capacity'}
if($Mode -eq 'qualification' -and $MatchTicks -eq 1200){$MatchTicks=36000}
if($Mode -eq 'qualification'){$DeadlineSeconds=[Math]::Max($DeadlineSeconds,$WarmupSeconds+$DurationSeconds+[int][Math]::Ceiling($MatchTicks/60.0)+300)}
. "$PSScriptRoot/common.ps1" -SdkPath $SdkPath -RunDirectory $RunDirectory
if(-not $env:AINATIVE_TEST_POSTGRES){throw 'Isolated AINATIVE_TEST_POSTGRES required'}
if($Mode -in @('capacity','bots') -and $MatchTicks -eq 1200){$MatchTicks=6000}
if($Mode -eq 'backend-restarts' -and $MatchTicks -eq 1200){$MatchTicks=2400}
if($Mode -eq 'expired-ticket' -and $MatchTicks -eq 1200){$MatchTicks=9000}
if(-not $SkipBuild){& "$PSScriptRoot/build.ps1" -SdkPath $SdkPath -RunDirectory $RunDirectory -SkipAudit:$SkipAudit}
& "$PSScriptRoot/initialize.ps1" -SdkPath $SdkPath -RunDirectory $RunDirectory
$signalDirectory=Join-Path $Run ('fault-signals-'+[guid]::NewGuid().ToString('N'));New-Item -ItemType Directory $signalDirectory | Out-Null
$launch=@('-NoProfile','-File',('"'+(Join-Path $PSScriptRoot 'start.ps1')+'"'),'-SdkPath',('"'+$SdkPath+'"'),'-RunDirectory',('"'+$RunDirectory+'"'),'-MatchTicks',[string]$MatchTicks,
 '-WorkerCount',[string]$WorkerCount,'-RoomsPerWorker',[string]$RoomsPerWorker,'-PlayersPerRoom',[string]$PlayersPerRoom,'-ReplayMaxBytes',[string]$ReplayMaxBytes)
$supervisor=Start-Process (Get-Command pwsh).Source -ArgumentList $launch -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $Run 'supervisor.stdout.log') -RedirectStandardError (Join-Path $Run 'supervisor.stderr.log')
$supervisorTicks=$supervisor.StartTime.ToUniversalTime().Ticks
$runStartUtc=[datetime]::UtcNow
@{Pid=$supervisor.Id;StartUtc=$supervisor.StartTime.ToUniversalTime().ToString('O');Executable=$supervisor.Path;Script=(Join-Path $PSScriptRoot 'start.ps1')} | ConvertTo-Json | Set-Content (Join-Path $Run 'supervisor.pid.json')
$replacements=@();$actions=@();$handled=@{};$code=1
function Wait-Ready($service,[int]$Seconds=30){
 $end=[datetime]::UtcNow.AddSeconds($Seconds)
 while([datetime]::UtcNow -lt $end){try{$r=Invoke-WebRequest ('http://127.0.0.1:'+$service.Health+'/health/ready') -TimeoutSec 1;if($r.StatusCode -eq 200){return}}catch{};Start-Sleep -Milliseconds 100}
 throw ('Readiness timeout: '+$service.Id)
}
try{
 foreach($service in @($Services | Where-Object Role -ne Client)){Wait-Ready $service}
 $client=Start-OwnedChild ($Services | Where-Object Id -eq acceptance) @{AINATIVE_ACCEPTANCE_MODE=$Mode;AINATIVE_FAULT_SIGNALS=$signalDirectory;AINATIVE_ACCEPTANCE_RUN_DIRECTORY=$Run;AINATIVE_ACCEPTANCE_REPORT=(Join-Path $Run ($Mode+'.json'));AINATIVE_MATCH_LENGTH_TICKS=[string]$MatchTicks;AINATIVE_ACCEPTANCE_DEADLINE_SECONDS=[string]$DeadlineSeconds;
  AINATIVE_QUALIFICATION_ROOM_COUNT=[string]$RoomCount;AINATIVE_QUALIFICATION_PLAYERS_PER_ROOM=[string]$PlayersPerRoom;AINATIVE_QUALIFICATION_WORKER_COUNT=[string]$WorkerCount;AINATIVE_QUALIFICATION_ROOMS_PER_WORKER=[string]$RoomsPerWorker;
  AINATIVE_QUALIFICATION_INPUT_HZ=[string]$InputHz;AINATIVE_QUALIFICATION_WARMUP_SECONDS=[string]$WarmupSeconds;AINATIVE_QUALIFICATION_DURATION_SECONDS=[string]$DurationSeconds;AINATIVE_QUALIFICATION_SEED=[string]$Seed;AINATIVE_QUALIFICATION_MATCH_TICKS=[string]$MatchTicks}
 while(-not $client.Process.HasExited){
  foreach($request in Get-ChildItem $signalDirectory -Filter '*.request.json'){
   if($handled.ContainsKey($request.Name)){continue}
   try{$command=Get-Content $request.FullName -Raw | ConvertFrom-Json}catch{continue}
   if($command.action -notin @('stop','start','restart')){throw 'Invalid fault action'}
   foreach($id in $command.services){
    $service=$Services | Where-Object Id -eq $id
    if(-not $service -or $id -eq 'acceptance'){throw 'Fault target outside owned service list'}
    if($command.action -in @('stop','restart')){& "$PSScriptRoot/stop.ps1" -SdkPath $SdkPath -RunDirectory $RunDirectory -ServiceId $id;Start-Sleep -Milliseconds 200}
    if($command.action -in @('start','restart')){
     $record=Get-Content (Join-Path $Run ($id+'.pid.json')) -Raw | ConvertFrom-Json
     $existing=Get-Process -Id $record.Pid -ErrorAction SilentlyContinue
     if($existing -and $existing.Path -eq $record.Executable -and $existing.StartTime.ToUniversalTime().Ticks -eq ([datetime]$record.StartUtc).ToUniversalTime().Ticks){throw 'Refusing duplicate owned process launch'}
     $child=Start-OwnedChild $service @{AINATIVE_MATCH_LENGTH_TICKS=[string]$MatchTicks;AINATIVE_BATTLE_WORKERS=[string]$WorkerCount;AINATIVE_ROOMS_PER_WORKER=[string]$RoomsPerWorker;AINATIVE_PLAYERS_PER_MATCH=[string]$PlayersPerRoom;AINATIVE_ARENA_REPLAY_MAX_BYTES=[string]$ReplayMaxBytes};$replacements+=$child;Wait-Ready $service
    }
    $actions+=@{Action=$command.action;Service=$id;Utc=[datetime]::UtcNow.ToString('O')}
   }
   $handled[$request.Name]=$true
   @{status='done'} | ConvertTo-Json | Set-Content (Join-Path $signalDirectory ($request.Name.Replace('.request.json','.done.json')))
  }
  Start-Sleep -Milliseconds 50
 }
 $code=Wait-Child $client
 Get-Content (Join-Path $Run ($Mode+'.json'))
 if($code -eq 0 -and $Mode -notin @('capacity','party-notifications')) { & "$PSScriptRoot/verify-replay.ps1" -SdkPath $SdkPath -RunDirectory $RunDirectory -SinceUtc $runStartUtc -ReportName ($Mode+'-replay.json') }
}finally{
 $actions | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $Run ($Mode+'-actions.json'))
 & "$PSScriptRoot/stop.ps1" -SdkPath $SdkPath -RunDirectory $RunDirectory
 foreach($child in $replacements){[void](Wait-Child $child)}
 if(-not $supervisor.WaitForExit(5000)){$current=Get-Process -Id $supervisor.Id -ErrorAction SilentlyContinue;if($current -and $current.Path -eq $supervisor.Path -and $current.StartTime.ToUniversalTime().Ticks -eq $supervisorTicks){Stop-Process -Id $supervisor.Id}}
}
exit $code
