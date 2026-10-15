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
$ErrorActionPreference = 'Stop'
$names = if ($IsWindows) { @('py', 'python', 'python3') } else { @('python3', 'python') }
$python = Get-Command -Name $names -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $python) { throw 'Python 3.11+ is required. See README-local-debug.md.' }
$arguments = @("$PSScriptRoot/manual.py", 'start', '--run-directory', $RunDirectory,
    '--postgres-port', [string]$PostgresPort, '--match-ticks', [string]$MatchTicks,
    '--players-per-room', [string]$PlayersPerRoom)
if ($SdkPath) { $arguments += @('--sdk-path', $SdkPath) }
if ($PostgresBinDirectory) { $arguments += @('--postgres-bin-directory', $PostgresBinDirectory) }
if ($DebugService) { $arguments += @('--debug-service', $DebugService) }
if ($python.Name -in @('py', 'py.exe')) { $arguments = @('-3') + $arguments }
& $python.Source @arguments
if ($LASTEXITCODE) { throw "Local topology exited with code $LASTEXITCODE." }
