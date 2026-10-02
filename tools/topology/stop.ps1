param([string]$SdkPath, [string]$RunDirectory='artifacts/topology-local', [string]$ServiceId)
. "$PSScriptRoot/common.ps1" -SdkPath $SdkPath -RunDirectory $RunDirectory
foreach ($service in @($Services | Where-Object { -not $ServiceId -or $_.Id -eq $ServiceId })) {
 $recordPath=Join-Path $Run ($service.Id+'.pid.json')
 if (-not (Test-Path $recordPath)) { continue }
 $record=Get-Content $recordPath -Raw | ConvertFrom-Json
 $process=Get-Process -Id $record.Pid -ErrorAction SilentlyContinue
 if (-not $process) { continue }
 $expected=[IO.Path]::GetFullPath($record.Executable)
 if (-not $expected.StartsWith($Run+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Recorded executable outside run directory' }
 if ($process.Path -ne $expected -or $process.StartTime.ToUniversalTime().Ticks -ne ([datetime]$record.StartUtc).ToUniversalTime().Ticks) { Write-Verbose 'Stale PID record; unrelated reused PID was left untouched'; continue }
 Stop-Process -Id $record.Pid
}
