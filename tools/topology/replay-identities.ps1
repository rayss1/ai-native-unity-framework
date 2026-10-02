param([string]$RunDirectory='artifacts/topology-local')
$ErrorActionPreference='Stop'
$identityRepo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$identityRun=[IO.Path]::GetFullPath((Join-Path $identityRepo $RunDirectory))
if(-not $identityRun.StartsWith((Join-Path $identityRepo 'artifacts')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Identity output must remain inside workspace artifacts'}
$head=& git -C $identityRepo rev-parse HEAD
$fantasy=& git -C (Join-Path $identityRepo 'server/vendor/Fantasy') rev-parse HEAD
$files=@(& rg --files (Join-Path $identityRepo 'server/src') (Join-Path $identityRepo 'server/acceptance') (Join-Path $identityRepo 'shared') (Join-Path $identityRepo 'packages') -g '*.cs' -g '*.csproj' -g '*.proto' -g '*.props' -g '*.targets' -g '*.asmdef')
$manifest=($files | Sort-Object | ForEach-Object { $_.Substring($identityRepo.Length+1).Replace('\','/')+':'+(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash }) -join "`n"
$sourceHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($manifest)))
$protocol=(@(Get-ChildItem (Join-Path $identityRepo 'shared/schemas/ainative/v1') -Filter '*.proto' | Sort-Object Name | ForEach-Object {(Get-FileHash $_.FullName -Algorithm SHA256).Hash}) -join '|')
$protocolHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($protocol)))
New-Item -ItemType Directory -Force $identityRun | Out-Null
@{Source=($head+'+source-worktree-sha256:'+ $sourceHash);Fantasy=$fantasy;Protocol=('sha256:'+ $protocolHash);SourceManifestScope='server/src,server/acceptance,shared,packages source/project/schema files'} | ConvertTo-Json | Set-Content (Join-Path $identityRun 'replay-identities.json')
