#requires -Version 7.0
param([string]$RunDirectory = 'artifacts/manual-test')
$ErrorActionPreference = 'Stop'
$names = if ($IsWindows) { @('py', 'python', 'python3') } else { @('python3', 'python') }
$python = Get-Command -Name $names -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $python) { throw 'Python 3.11+ is required. See README-local-debug.md.' }
$arguments = @("$PSScriptRoot/manual.py", 'stop', '--run-directory', $RunDirectory)
if ($python.Name -in @('py', 'py.exe')) { $arguments = @('-3') + $arguments }
& $python.Source @arguments
if ($LASTEXITCODE) { throw "Local topology exited with code $LASTEXITCODE." }
