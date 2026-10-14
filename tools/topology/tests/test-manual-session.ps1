#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$testRun = 'artifacts/manual-session-tests/' + [guid]::NewGuid().ToString('N')
. "$PSScriptRoot/../common.ps1" -RunDirectory $testRun
. "$PSScriptRoot/../manual-session.ps1"
New-Item -ItemType Directory -Path $Run -Force | Out-Null
function Assert($Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
$priorDatabase = $env:AINATIVE_TEST_POSTGRES
try {
    # Deliberately fake file bodies: exercise handoff identity without creating secrets.
    $peers = @()
    foreach ($service in $Services) {
        $public = Join-Path $Run ($service.Id + '.public.pem')
        'test-public-file' | Set-Content -LiteralPath $public
        'test-private-file' | Set-Content -LiteralPath (Join-Path $Run ($service.Id + '.private.pem'))
        $peers += @{ Id=$service.Id; Role=$service.Role; ProcessId=$service.ProcessId; SceneId=$service.SceneId; PublicKeyFile=$public }
    }
    $peers | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Run 'peers.json')
    foreach ($name in @('player-ticket.private.pem', 'player-ticket.public.pem')) {
        'test-file' | Set-Content -LiteralPath (Join-Path $Run $name)
    }
    @{Source='test-source'; Fantasy='test-fantasy'; Protocol='test-protocol'} | ConvertTo-Json |
        Set-Content -LiteralPath (Join-Path $Run 'replay-identities.json')
    $env:AINATIVE_TEST_POSTGRES = 'Host=127.0.0.1;Port=25439;Database=ainative_manual;Username=ainative_manual;Pooling=true'
    $supervisor = Get-Process -Id $PID
    foreach ($service in ($Services | Where-Object Role -ne Client)) {
        $extra = @{AINATIVE_MATCH_LENGTH_TICKS='7200'; AINATIVE_PLAYERS_PER_MATCH='4'}
        $expected = Get-ServiceEnvironment $service $extra
        Write-LocalDebugProfile $service $extra $supervisor
        $actual = Get-Content -LiteralPath (Join-Path $Run 'vs-debug.json') -Raw | ConvertFrom-Json
        Assert ($actual.ServiceId -eq $service.Id -and $actual.ProcessId -eq $service.ProcessId) 'Wrong service identity'
        foreach ($key in $expected.Keys) { Assert ($actual.Environment.$key -eq $expected[$key]) "Environment mismatch: $key" }
        foreach ($file in $actual.RequiredFiles.PSObject.Properties) {
            Assert ((Get-FileHash -LiteralPath $file.Name).Hash -eq $file.Value) 'Required file hash differs'
        }
        Assert ($actual.RequiredFiles.PSObject.Properties.Name.Count -eq 13) 'Incomplete file inventory'
        $profilePath = Join-Path $Repo "server/src/Hosts/AiNative.$($service.Host)/Properties/launchSettings.json"
        $launch = Get-Content -LiteralPath $profilePath -Raw | ConvertFrom-Json
        $profile = $launch.profiles."Local Debug $($service.Id)"
        Assert ($null -ne $profile -and -not $profile.launchBrowser) 'Missing VS profile'
        Assert ($profile.commandLineArgs.Contains("--pid $($service.ProcessId) -m Release")) 'Incorrect Fantasy arguments'
    }
    Assert (-not (Test-ManualStopRequest $supervisor)) 'Absent stop request matched'
    @{Pid=$PID;StartUtc=$supervisor.StartTime.ToUniversalTime().AddSeconds(-1).ToString('O')} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Run 'stop-request.json')
    Assert (-not (Test-ManualStopRequest $supervisor)) 'Stale process identity matched'
    @{Pid=$PID;StartUtc=$supervisor.StartTime.ToUniversalTime().ToString('O')} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Run 'stop-request.json')
    Assert (Test-ManualStopRequest $supervisor) 'Current stop request did not match'
    function Invoke-WebRequest { param($Uri,$TimeoutSec) if ($Uri -match ':24102/') { throw 'offline' }; return @{StatusCode=200} }
    $pending = @(Get-ManualReadiness ($Services | Where-Object Role -ne Client))
    Assert ($pending.Count -eq 1 -and $pending[0] -eq 'player') 'Readiness did not identify excluded service'
    Write-Host 'PASS: seven VS profiles and environment parity; file hashes; stop ownership; partial readiness.'
} finally { $env:AINATIVE_TEST_POSTGRES = $priorDatabase }
