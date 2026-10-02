param([string]$SdkPath, [string]$RunDirectory='artifacts/topology-local', [datetime]$SinceUtc, [string]$ReportName='replay-verification.json')
. "$PSScriptRoot/common.ps1" -SdkPath $SdkPath -RunDirectory $RunDirectory
$capture=Get-ChildItem (Join-Path $Run 'replay') -Filter '*.anar' -Recurse | Where-Object {$_.LastWriteTimeUtc -ge $SinceUtc} | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
if(-not $capture){throw 'No actual completed ANAR capture from this run'}
$node=$capture.Directory.Name
$identity=Get-Content (Join-Path $Run ($node+'.replay-identity.json')) -Raw | ConvertFrom-Json
$verified=& $SdkPath (Join-Path $Run 'bin/replay-verifier/AiNative.ArenaReplay.dll') $capture.FullName $identity.Source $identity.Fantasy $identity.Protocol $identity.Configuration
if($LASTEXITCODE){throw 'Actual replay CLI verification failed'}
@{Capture=$capture.FullName;Identity=$identity;Result=($verified | ConvertFrom-Json);VerifiedUtc=[datetime]::UtcNow.ToString('O')} | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $Run $ReportName)
Get-Content (Join-Path $Run $ReportName)
