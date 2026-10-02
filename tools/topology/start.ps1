param([string]$SdkPath, [string]$RunDirectory='artifacts/topology-local', [int]$MatchTicks=1200)
. "$PSScriptRoot/common.ps1" -SdkPath $SdkPath -RunDirectory $RunDirectory
if (-not $env:AINATIVE_TEST_POSTGRES) { throw 'AINATIVE_TEST_POSTGRES required: use an isolated test database' }
if (-not (Test-Path (Join-Path $Run 'peers.json'))) { & "$PSScriptRoot/initialize.ps1" -SdkPath $SdkPath -RunDirectory $RunDirectory }
# This supervisor remains alive so it can drain every child's redirected output.
$children = @()
try {
 foreach ($service in @($Services | Where-Object Role -ne Client)) {
  $recordPath=Join-Path $Run ($service.Id+'.pid.json')
  if (Test-Path $recordPath) { $record=Get-Content $recordPath -Raw | ConvertFrom-Json; $existing=Get-Process -Id $record.Pid -ErrorAction SilentlyContinue; if ($existing -and $existing.Path -eq $record.Executable -and $existing.StartTime.ToUniversalTime().Ticks -eq ([datetime]$record.StartUtc).ToUniversalTime().Ticks) { throw ('Existing owned process: '+$service.Id) } }
  $children += Start-OwnedChild $service @{AINATIVE_MATCH_LENGTH_TICKS=[string]$MatchTicks}
 }
 while (@($children | Where-Object { -not $_.Process.HasExited }).Count -gt 0) { Start-Sleep -Milliseconds 500 }
} finally { foreach ($child in $children) { if (-not $child.Process.HasExited) { $child.Process.Kill() }; [void](Wait-Child $child) } }
