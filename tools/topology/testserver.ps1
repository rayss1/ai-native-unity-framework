param([string]$SdkPath, [string]$RunDirectory='artifacts/topology-local', [switch]$SkipBuild, [switch]$GateRestart, [switch]$DuplicateSettlement, [switch]$SkipAudit, [switch]$BackendOnly, [ValidateRange(120,360000)][int]$MatchTicks=1200, [ValidateRange(1,86400)][int]$DeadlineSeconds=240, [string]$GateAddress='127.0.0.1:23001')
. "$PSScriptRoot/common.ps1" -SdkPath $SdkPath -RunDirectory $RunDirectory
if (-not $env:AINATIVE_TEST_POSTGRES) { throw 'AINATIVE_TEST_POSTGRES must name an isolated test database' }
New-Item -ItemType Directory -Force $Run | Out-Null
if (-not $SkipBuild) { & "$PSScriptRoot/build.ps1" -SdkPath $SdkPath -RunDirectory $RunDirectory -SkipAudit:$SkipAudit }
& "$PSScriptRoot/initialize.ps1" -SdkPath $SdkPath -RunDirectory $RunDirectory
# All arguments are trusted local absolute paths, quoted for PowerShell's command-line parser.
$launchArgs=@('-NoProfile','-File',('"'+(Join-Path $PSScriptRoot 'start.ps1')+'"'),'-SdkPath',('"'+$SdkPath+'"'),'-RunDirectory',('"'+$RunDirectory+'"'),'-MatchTicks',[string]$MatchTicks)
$supervisor=Start-Process (Get-Command pwsh).Source -ArgumentList $launchArgs -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $Run 'supervisor.stdout.log') -RedirectStandardError (Join-Path $Run 'supervisor.stderr.log')
$supervisorStart=$supervisor.StartTime.ToUniversalTime().Ticks
@{Pid=$supervisor.Id; StartUtc=$supervisor.StartTime.ToUniversalTime().ToString('O'); Executable=$supervisor.Path; Script=(Join-Path $PSScriptRoot 'start.ps1')} | ConvertTo-Json | Set-Content (Join-Path $Run 'supervisor.pid.json')
$code=1
$captureSinceUtc=[datetime]::UtcNow
try {
 $deadline=[datetime]::UtcNow.AddSeconds(30)
 foreach($service in @($Services | Where-Object Role -ne Client)) {
  $live=$false
  while([datetime]::UtcNow -lt $deadline) {
   if($supervisor.HasExited) { throw 'Supervisor exited: inspect its error log' }
   try { $response=Invoke-WebRequest ('http://127.0.0.1:'+$service.Health+'/health/ready') -TimeoutSec 1; if($response.StatusCode -eq 200) {$live=$true;break} } catch { }
   Start-Sleep -Milliseconds 200
  }
  if(-not $live) { throw ('Health unavailable: '+$service.Id) }
 }
  Start-Sleep -Seconds 6 # Initial Battle heartbeat can precede scene readiness; allow next 5-second inventory report.
 $extra=@{AINATIVE_ACCEPTANCE_GATE_ADDRESS=$GateAddress;AINATIVE_ACCEPTANCE_DEADLINE_SECONDS=[string]$DeadlineSeconds}; $restartedGate=$null; $admin=$null; $restoredBattle=$null
 if($BackendOnly){$extra.AINATIVE_ACCEPTANCE_BACKEND_ONLY='true'}
 if($DuplicateSettlement) { $resultFile=Join-Path $Run ([guid]::NewGuid().ToString("N")+".result.pb"); $extra.AINATIVE_ACCEPTANCE_RESULT_FILE=$resultFile }
 if($GateRestart) {
  $signal=Join-Path $Run ('gate-restart-'+[guid]::NewGuid().ToString('N')+'.signal')
  $extra.AINATIVE_GATE_RESTART_SIGNAL=$signal
 }
 $client=Start-OwnedChild ($Services | Where-Object Id -eq acceptance) $extra
 while(-not $client.Process.HasExited) {
  if($GateRestart -and -not $restartedGate -and (Test-Path $signal)) {
   & "$PSScriptRoot/stop.ps1" -SdkPath $SdkPath -RunDirectory $RunDirectory -ServiceId gate
   $restartedGate=Start-OwnedChild ($Services | Where-Object Id -eq gate)
   $readyDeadline=[datetime]::UtcNow.AddSeconds(10)
   $ready=$false
   while([datetime]::UtcNow -lt $readyDeadline) {
    try { $response=Invoke-WebRequest 'http://127.0.0.1:24101/health/ready' -TimeoutSec 1; if($response.StatusCode -eq 200) {$ready=$true;break} } catch {}
    Start-Sleep -Milliseconds 100
   }
   if(-not $ready) { throw 'Gate restart readiness failed' }
   'restarted' | Set-Content ($signal+'.done')
  }
  Start-Sleep -Milliseconds 100
 }
 $code=Wait-Child $client
 if(Test-Path (Join-Path $Run 'acceptance.json')) { Get-Content (Join-Path $Run 'acceptance.json') }
 if($code -eq 0 -and $DuplicateSettlement) {
  $node=Get-Content ($resultFile+'.node') -Raw
  $target=@{}; foreach($key in ($Services | Where-Object Id -eq $node).Keys){ $target[$key]=($Services | Where-Object Id -eq $node)[$key] }
  & "$PSScriptRoot/stop.ps1" -SdkPath $SdkPath -RunDirectory $RunDirectory -ServiceId $node
  # Publish acceptance was copied to this dedicated directory; do not overwrite the stopped Battle executable.
  $adminDirectoryId=$node+'-acceptance-'+[guid]::NewGuid().ToString('N'); $adminBin=Join-Path $Run ('bin/'+$adminDirectoryId)
  Copy-Item (Join-Path $Run 'bin/acceptance') $adminBin -Recurse -Force
  $target.Host='TopologyAcceptance'; $target.DirectoryId=$adminDirectoryId
  $admin=Start-OwnedChild $target @{AINATIVE_ACCEPTANCE_ROLE='Battle';AINATIVE_ACCEPTANCE_RESULT_FILE=$resultFile;AINATIVE_ACCEPTANCE_REPORT=(Join-Path $Run 'duplicate-settlement.json')}
  $code=Wait-Child $admin
  Get-Content (Join-Path $Run 'duplicate-settlement.json')
  $restoredBattle=Start-OwnedChild ($Services | Where-Object Id -eq $node) @{AINATIVE_MATCH_LENGTH_TICKS=[string]$MatchTicks}
  $restoreDeadline=[datetime]::UtcNow.AddSeconds(15)
  $restored=$false
  while([datetime]::UtcNow -lt $restoreDeadline) {
   try { $healthPort=($Services | Where-Object Id -eq $node).Health; $response=Invoke-WebRequest ('http://127.0.0.1:'+$healthPort+'/health/ready') -TimeoutSec 1; if($response.StatusCode -eq 200) {$restored=$true;break} } catch {}
   Start-Sleep -Milliseconds 200
  }
  if(-not $restored) { throw 'Original Battle node failed to restore readiness' }
 }
 if($code -eq 0 -and -not $BackendOnly){& "$PSScriptRoot/verify-replay.ps1" -SdkPath $SdkPath -RunDirectory $RunDirectory -SinceUtc $captureSinceUtc -ReportName 'acceptance-replay.json'}
} finally {
 & "$PSScriptRoot/stop.ps1" -SdkPath $SdkPath -RunDirectory $RunDirectory
 if($restartedGate) { [void](Wait-Child $restartedGate) }
 if($restoredBattle) { [void](Wait-Child $restoredBattle) }
 if(-not $supervisor.WaitForExit(5000)) {
  $current=Get-Process -Id $supervisor.Id -ErrorAction SilentlyContinue
  if($current -and $current.Path -eq $supervisor.Path -and $current.StartTime.ToUniversalTime().Ticks -eq $supervisorStart) { Stop-Process -Id $supervisor.Id }
 }
}
exit $code
