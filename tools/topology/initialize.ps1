param([string]$SdkPath, [string]$RunDirectory='artifacts/topology-local')
. "$PSScriptRoot/common.ps1" -SdkPath $SdkPath -RunDirectory $RunDirectory
& "$PSScriptRoot/replay-identities.ps1" -RunDirectory $RunDirectory
New-Item -ItemType Directory -Force $Run | Out-Null
foreach ($service in $Services) {
 $private = Join-Path $Run ($service.Id+'.private.pem'); $public = Join-Path $Run ($service.Id+'.public.pem')
 if (-not (Test-Path $private)) { $rsa=[Security.Cryptography.RSA]::Create(2048); $rsa.ExportPkcs8PrivateKeyPem() | Set-Content $private; $rsa.ExportSubjectPublicKeyInfoPem() | Set-Content $public; $rsa.Dispose() }
}
if (-not (Test-Path (Join-Path $Run 'player-ticket.private.pem'))) { $rsa=[Security.Cryptography.RSA]::Create(2048); $rsa.ExportPkcs8PrivateKeyPem() | Set-Content (Join-Path $Run 'player-ticket.private.pem'); $rsa.ExportSubjectPublicKeyInfoPem() | Set-Content (Join-Path $Run 'player-ticket.public.pem'); $rsa.Dispose() }
@($Services | ForEach-Object { @{Id=$_.Id;Role=$_.Role;ProcessId=$_.ProcessId;SceneId=$_.SceneId;PublicKeyFile=(Join-Path $Run ($_.Id+'.public.pem'))} }) | ConvertTo-Json | Set-Content (Join-Path $Run 'peers.json')
