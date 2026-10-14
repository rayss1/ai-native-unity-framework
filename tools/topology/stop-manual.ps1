#requires -Version 7.0
param([string]$RunDirectory = 'artifacts/manual-test')
. "$PSScriptRoot/common.ps1" -RunDirectory $RunDirectory
$recordPath = Join-Path $Run 'supervisor.json'
if (-not (Test-Path -LiteralPath $recordPath)) { throw 'No manual-test supervisor record exists.' }
$record = Get-Content -LiteralPath $recordPath -Raw | ConvertFrom-Json
$process = Get-Process -Id $record.Pid -ErrorAction SilentlyContinue
if (-not $process -or $process.StartTime.ToUniversalTime().Ticks -ne ([datetime]$record.StartUtc).ToUniversalTime().Ticks) {
    throw 'The recorded manual-test supervisor is no longer running.'
}
# Cooperative shutdown: the supervisor only cleans up its own children and database.
$record | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Run 'stop-request.json')
Write-Host '已请求停止。请等待启动窗口完成清理；VS 调试进程需在 VS 中自行停止。'
