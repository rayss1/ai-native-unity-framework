param([string]$SdkPath, [string]$RunDirectory='artifacts/topology-local', [switch]$BackendOnly, [string]$GateAddress='127.0.0.1:23001', [ValidateRange(1,86400)][int]$DeadlineSeconds=240)
. "$PSScriptRoot/common.ps1" -SdkPath $SdkPath -RunDirectory $RunDirectory
$extra=@{AINATIVE_ACCEPTANCE_GATE_ADDRESS=$GateAddress;AINATIVE_ACCEPTANCE_DEADLINE_SECONDS=[string]$DeadlineSeconds}; if ($BackendOnly) { $extra.AINATIVE_ACCEPTANCE_BACKEND_ONLY='true' }
$child=Start-OwnedChild ($Services | Where-Object Id -eq acceptance) $extra
$code=Wait-Child $child
Get-Content (Join-Path $Run 'acceptance.json')
exit $code
