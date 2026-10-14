# Helpers shared by the interactive supervisor and its regression tests.
function Write-LocalDebugProfile($Service, [hashtable]$Extra, $Supervisor) {
    $variables = Get-ServiceEnvironment $Service $Extra
    $files = @{}
    $paths = @($variables.AINATIVE_PEERS_FILE, $variables.AINATIVE_FANTASY_CONFIG_FILE,
        $variables.AINATIVE_SERVICE_PRIVATE_KEY_FILE, $variables.AINATIVE_PLAYER_SIGNING_KEY_FILE,
        $variables.AINATIVE_PLAYER_PUBLIC_KEY_FILE)
    $peers = Get-Content -LiteralPath $variables.AINATIVE_PEERS_FILE -Raw | ConvertFrom-Json
    $paths += @($peers | ForEach-Object PublicKeyFile)
    foreach ($path in ($paths | Select-Object -Unique)) {
        $absolute = (Resolve-Path -LiteralPath $path).Path
        $files[$absolute] = (Get-FileHash -LiteralPath $absolute -Algorithm SHA256).Hash
    }
    $profile = @{
        Version = 1; ServiceId = $Service.Id; ProcessId = $Service.ProcessId; Host = $Service.Host
        Environment = $variables; RequiredFiles = $files
        SupervisorPid = $Supervisor.Id; SupervisorStartUtc = $Supervisor.StartTime.ToUniversalTime().ToString('O')
    }
    $profile | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Run 'vs-debug.json') -Encoding utf8
}

function Get-ManualReadiness($ServerServices) {
    $pending = @()
    foreach ($service in $ServerServices) {
        try {
            $response = Invoke-WebRequest "http://127.0.0.1:$($service.Health)/health/ready" -TimeoutSec 1
            if ($response.StatusCode -ne 200) { $pending += $service.Id }
        } catch { $pending += $service.Id }
    }
    return $pending
}

function Test-ManualStopRequest($Supervisor) {
    $path = Join-Path $Run 'stop-request.json'
    if (-not (Test-Path -LiteralPath $path)) { return $false }
    try {
        $request = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        return $request.Pid -eq $Supervisor.Id -and
            ([datetime]$request.StartUtc).ToUniversalTime().Ticks -eq $Supervisor.StartTime.ToUniversalTime().Ticks
    } catch { return $false }
}
