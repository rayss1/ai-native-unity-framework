param([string]$SdkPath, [string]$RunDirectory='artifacts/topology-local', [switch]$SkipAudit)
. "$PSScriptRoot/common.ps1" -SdkPath $SdkPath -RunDirectory $RunDirectory
New-Item -ItemType Directory -Force $Run | Out-Null
if (Test-Path (Join-Path $Repo 'artifacts/topology-bootstrap/environment.ps1')) { . (Join-Path $Repo 'artifacts/topology-bootstrap/environment.ps1') }
foreach ($service in $Services) {
 $project = if ($service.Role -eq 'Client') { 'server/acceptance/AiNative.TopologyAcceptance/AiNative.TopologyAcceptance.csproj' } else { 'server/src/Hosts/AiNative.'+$service.Host+'/AiNative.'+$service.Host+'.csproj' }
 [string[]]$auditArgs=@(); if($SkipAudit){$auditArgs+= "-p:NuGetAudit=false"}
 & $SdkPath publish (Join-Path $Repo $project) -c Release -o (Join-Path $Run ('bin/'+$service.Id)) --nologo -v quiet @auditArgs
 if ($LASTEXITCODE) { throw "Publish failed: $project" }
}
& $SdkPath publish (Join-Path $Repo 'server/acceptance/AiNative.ArenaReplay/AiNative.ArenaReplay.csproj') -c Release -o (Join-Path $Run 'bin/replay-verifier') --nologo -v quiet @auditArgs
if($LASTEXITCODE){throw 'Replay verifier publish failed'}
