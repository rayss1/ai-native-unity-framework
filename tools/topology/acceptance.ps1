param([string]$SdkPath, [string]$RunDirectory='artifacts/topology-local', [switch]$BackendOnly)
. "$PSScriptRoot/common.ps1" -SdkPath $SdkPath -RunDirectory $RunDirectory
$extra=@{}; if ($BackendOnly) { $extra.AINATIVE_ACCEPTANCE_BACKEND_ONLY='true' }
$child=Start-OwnedChild ($Services | Where-Object Id -eq acceptance) $extra
$code=Wait-Child $child
Get-Content (Join-Path $Run 'acceptance.json')
exit $code
