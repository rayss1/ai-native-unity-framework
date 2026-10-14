#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$SdkPath,
    [string]$RunDirectory = 'artifacts/manual-test',
    [string]$PostgresBinDirectory,
    [ValidateRange(1024, 65535)][int]$PostgresPort = 25439,
    [ValidateRange(120, 360000)][int]$MatchTicks = 36000,
    [ValidateRange(2, 8)][int]$PlayersPerRoom = 2,
    [ValidateSet('gate', 'player', 'lobby', 'match', 'coordinator', 'battle-1', 'battle-2')]
    [string]$DebugService
)

if (-not $IsWindows) { throw 'This entry requires Windows and PowerShell 7.' }
. "$PSScriptRoot/common.ps1" -SdkPath $SdkPath -RunDirectory $RunDirectory
. "$PSScriptRoot/manual-session.ps1"
if ($DebugService -and $Run -ne [IO.Path]::GetFullPath((Join-Path $Repo 'artifacts/manual-test'))) {
    throw 'VS launch profiles use artifacts/manual-test. Omit -RunDirectory when using -DebugService.'
}
$PSNativeCommandUseErrorActionPreference = $false
$serverServices = @($Services | Where-Object Role -ne Client)

if (-not $PostgresBinDirectory) {
    $pgCommand = Get-Command pg_ctl.exe -ErrorAction SilentlyContinue
    $candidates = @(
        (Join-Path $Repo 'artifacts/status-sync-20261008/postgresql/runtime/pgsql/bin'),
        (Join-Path $env:ProgramFiles 'PostgreSQL/17/bin')
    )
    if ($pgCommand) { $candidates += Split-Path $pgCommand.Source }
    foreach ($candidate in $candidates) {
        if ((Test-Path (Join-Path $candidate 'pg_ctl.exe')) -and
            ((& (Join-Path $candidate 'pg_ctl.exe') --version | Out-String).Trim() -eq 'pg_ctl (PostgreSQL) 17.11')) {
            $PostgresBinDirectory = $candidate
            break
        }
    }
}
if (-not $PostgresBinDirectory) {
    throw 'PostgreSQL 17.11 not found. Supply -PostgresBinDirectory with its bin directory.'
}
$PostgresBinDirectory = (Resolve-Path -LiteralPath $PostgresBinDirectory).Path
foreach ($tool in @('pg_ctl', 'initdb', 'psql', 'createdb')) {
    if (-not (Test-Path (Join-Path $PostgresBinDirectory ($tool + '.exe')))) { throw "Missing PostgreSQL tool: $tool" }
}
$pgCtl = Join-Path $PostgresBinDirectory 'pg_ctl.exe'
if ((& $pgCtl --version | Out-String).Trim() -ne 'pg_ctl (PostgreSQL) 17.11') {
    throw 'This local profile requires PostgreSQL 17.11.'
}

function Assert-FreePort([int]$Port, [switch]$Udp) {
    $socket = $null
    try {
        if ($Udp) {
            $socket = [Net.Sockets.UdpClient]::new()
            $socket.ExclusiveAddressUse = $true
            $socket.Client.Bind([Net.IPEndPoint]::new([Net.IPAddress]::Loopback, $Port))
        } else {
            $socket = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $Port)
            $socket.Server.ExclusiveAddressUse = $true
            $socket.Start()
        }
    } catch { throw "Local port $Port is occupied. Stop that service before starting manual tests." }
    finally {
        if ($socket -is [Net.Sockets.TcpListener]) { $socket.Stop() }
        elseif ($socket) { $socket.Dispose() }
    }
}

# Wait only for pg_ctl itself; its daemon must not hold a PowerShell output pipe open.
function Invoke-PostgresControl([string[]]$Arguments) {
    $controlInfo = [Diagnostics.ProcessStartInfo]::new($pgCtl)
    $controlInfo.UseShellExecute = $false
    $controlInfo.CreateNoWindow = $true
    foreach ($argument in $Arguments) { $controlInfo.ArgumentList.Add($argument) }
    $control = [Diagnostics.Process]::Start($controlInfo)
    try {
        if (-not $control.WaitForExit(45000)) {
            $control.Kill()
            throw 'PostgreSQL control timed out; inspect postgres/server.log.'
        }
        return $control.ExitCode
    } finally { $control.Dispose() }
}

New-Item -ItemType Directory -Force $Run | Out-Null
try {
    $runLock = [IO.FileStream]::new((Join-Path $Run 'manual-start.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
} catch { throw "本机测试启动器已运行，或无法锁定目录 $Run。请先停止原启动器；不要重复启动。" }
$children = @()
$supervisor = Get-Process -Id $PID
$debugProfileWritten = $false
$databaseProcess = $null
$databaseStartAttempted = $false
$priorPostgres = [Environment]::GetEnvironmentVariable('AINATIVE_TEST_POSTGRES', 'Process')
$dataDirectory = Join-Path $Run 'postgres/data'
$databaseLog = Join-Path $Run 'postgres/server.log'
$locationPushed = $false
$ProgressPreference = 'SilentlyContinue'
function Get-StartedDatabaseProcess {
    $pidPath = Join-Path $dataDirectory 'postmaster.pid'
    if (Test-Path $pidPath) {
        $candidateProcess = Get-Process -Id ([int](Get-Content -LiteralPath $pidPath -TotalCount 1)) -ErrorAction SilentlyContinue
        if ($candidateProcess -and $candidateProcess.Path -eq (Join-Path $PostgresBinDirectory 'postgres.exe') -and
            $candidateProcess.StartTime.ToUniversalTime() -ge $databaseStartUtc) {
            return $candidateProcess
        }
    }
}
try {
    Push-Location -LiteralPath $Repo
    $locationPushed = $true
    # A new invocation in the same PowerShell process has the same PID/start time.
    $stopRequest = Join-Path $Run 'stop-request.json'
    if (Test-Path -LiteralPath $stopRequest) { Remove-Item -LiteralPath $stopRequest }
    @{ Pid = $supervisor.Id; StartUtc = $supervisor.StartTime.ToUniversalTime().ToString('O') } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Run 'supervisor.json')
    # Never adopt or stop an existing database or service process.
    if (Test-Path (Join-Path $dataDirectory 'PG_VERSION')) {
        & $pgCtl status -D $dataDirectory *> $null
        if ($LASTEXITCODE -eq 0) { throw 'The manual-test database is already running.' }
    }
    foreach ($service in $serverServices) {
        $recordPath = Join-Path $Run ($service.Id + '.pid.json')
        if (Test-Path $recordPath) {
            $record = Get-Content -LiteralPath $recordPath -Raw | ConvertFrom-Json
            $existing = Get-Process -Id $record.Pid -ErrorAction SilentlyContinue
            if ($existing -and $existing.Path -eq $record.Executable -and
                $existing.StartTime.ToUniversalTime().Ticks -eq ([datetime]$record.StartUtc).ToUniversalTime().Ticks) {
                throw "Manual-test service already running: $($service.Id)"
            }
        }
    }
    $tcpPorts = @(23001) + @($serverServices.Health) + @(23101..23107) + @($PostgresPort)
    foreach ($port in $tcpPorts) { Assert-FreePort $port }
    foreach ($port in @(22000, 22001)) { Assert-FreePort $port -Udp }

    Write-Host '[1/4] 编译服务器（首次运行可能需要几分钟）...'
    try { & "$PSScriptRoot/build.ps1" -SdkPath $SdkPath -RunDirectory $RunDirectory *> (Join-Path $Run 'build.log') }
    catch { throw "Build failed; inspect $(Join-Path $Run 'build.log'). $($_.Exception.Message)" }

    Write-Host '[2/4] 准备独立的本机测试数据库...'
    New-Item -ItemType Directory -Force (Split-Path $dataDirectory) | Out-Null
    if (-not (Test-Path (Join-Path $dataDirectory 'PG_VERSION'))) {
        & (Join-Path $PostgresBinDirectory 'initdb.exe') -D $dataDirectory -U ainative_manual --auth=trust --encoding=UTF8 --locale=C *> (Join-Path $Run 'postgres/initdb.log')
        if ($LASTEXITCODE) { throw 'Database initialization failed; see postgres/initdb.log.' }
    }
    $databaseStartUtc = [datetime]::UtcNow
    $databaseStartAttempted = $true
    $databaseStartExit = Invoke-PostgresControl @('start', '-D', $dataDirectory, '-o', "-h 127.0.0.1 -p $PostgresPort", '-l', $databaseLog, '-w', '-t', '30')
    $databaseProcess = Get-StartedDatabaseProcess
    if ($databaseStartExit -ne 0 -or -not $databaseProcess) { throw 'Database startup failed; see postgres/server.log.' }
    $pgArguments = @('-h', '127.0.0.1', '-p', [string]$PostgresPort, '-U', 'ainative_manual', '-w')
    $databaseExists = & (Join-Path $PostgresBinDirectory 'psql.exe') @pgArguments -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname = 'ainative_manual'"
    if ($LASTEXITCODE) { throw 'Cannot connect to the manual-test database.' }
    if ($databaseExists -ne '1') {
        & (Join-Path $PostgresBinDirectory 'createdb.exe') @pgArguments ainative_manual
        if ($LASTEXITCODE) { throw 'Cannot create the manual-test database.' }
    }
    [Environment]::SetEnvironmentVariable('AINATIVE_TEST_POSTGRES', "Host=127.0.0.1;Port=$PostgresPort;Database=ainative_manual;Username=ainative_manual;Pooling=true", 'Process')

    Write-Host '[3/4] 初始化服务配置并启动脚本负责的进程...'
    & "$PSScriptRoot/initialize.ps1" -SdkPath $SdkPath -RunDirectory $RunDirectory
    $extra = @{
        AINATIVE_MATCH_LENGTH_TICKS = [string]$MatchTicks
        AINATIVE_PLAYERS_PER_MATCH = [string]$PlayersPerRoom
    }
    if ($DebugService) {
        $debugTarget = $serverServices | Where-Object Id -eq $DebugService
        Write-LocalDebugProfile $debugTarget $extra $supervisor
        $debugProfileWritten = $true
        Write-Host "VS：打开 AiNative.sln，选择 Debug 编译、AiNative.$($debugTarget.Host)、Local Debug $DebugService，按 F5。"
        Write-Host '等待 VS 启动；可反复停止/F5。退出本窗口前请先停止 VS 调试。'
    }
    foreach ($service in $serverServices) {
        if ($service.Id -eq $DebugService) { continue }
        $children += Start-OwnedChild $service $extra
    }
    Write-Host '[4/4] 等待所有服务就绪...'
    $deadline = [datetime]::UtcNow.AddSeconds(60)
    Write-Host "每局 $($MatchTicks / 60) 秒，$PlayersPerRoom 名玩家。日志：$Run"
    Write-Host '保持窗口打开；按 Ctrl+C 停止。数据库和账号会保留，供下次启动使用。'
    $wasReady = $false
    $everReady = $false
    $lastPending = $null
    while (-not (Test-ManualStopRequest $supervisor)) {
        if (@($children | Where-Object { $_.Process.HasExited }).Count) {
            throw "A server process exited; inspect logs in $Run."
        }
        $pending = @(Get-ManualReadiness $serverServices)
        if ($pending.Count -eq 0) {
            if (-not $wasReady) { Write-Host '服务器已就绪：Gate 127.0.0.1:23001；Unity 本机测试请关闭 TLS。' }
            $wasReady = $true
            $everReady = $true
        } else {
            if (-not $DebugService -and -not $everReady -and [datetime]::UtcNow -ge $deadline) {
                throw "Services not ready: $($pending -join ', '). Inspect logs in $Run."
            }
            if (($pending -join ',') -ne $lastPending) {
                Write-Host "等待服务就绪：$($pending -join ', ')$(if ($DebugService) { '；请检查 VS 目标是否正在运行或停在断点。' })"
            }
            $wasReady = $false
        }
        $lastPending = $pending -join ','
        Start-Sleep -Seconds 2
    }
} finally {
    if ($DebugService) { Write-Host '不会终止 VS 启动的进程；请在 VS 中停止调试。' }
    if ($debugProfileWritten) { Remove-Item -LiteralPath (Join-Path $Run 'vs-debug.json') -ErrorAction SilentlyContinue }
    foreach ($child in $children) {
        if (-not $child.Process.HasExited) { $child.Process.Kill() }
        [void](Wait-Child $child)
    }
    # Only the database started here, with the same PID/path/start time, may be stopped.
    if ($databaseStartAttempted -and -not $databaseProcess) { $databaseProcess = Get-StartedDatabaseProcess }
    if ($databaseStartAttempted -and $databaseProcess) {
        $current = Get-Process -Id $databaseProcess.Id -ErrorAction SilentlyContinue
        if ($current -and $current.Path -eq $databaseProcess.Path -and
            $current.StartTime.ToUniversalTime().Ticks -eq $databaseProcess.StartTime.ToUniversalTime().Ticks) {
            $databaseStopExit = Invoke-PostgresControl @('stop', '-D', $dataDirectory, '-m', 'fast', '-w', '-t', '30')
            if ($databaseStopExit) { Write-Warning "Database shutdown failed; inspect $databaseLog." }
        }
    }
    [Environment]::SetEnvironmentVariable('AINATIVE_TEST_POSTGRES', $priorPostgres, 'Process')
    if ($locationPushed) { Pop-Location }
    $runLock.Dispose()
}
