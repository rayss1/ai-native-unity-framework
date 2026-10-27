#requires -Version 7.0
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/../common.ps1" -RunDirectory ('artifacts/manual-child-tests/' + [guid]::NewGuid().ToString('N'))
$probe = @{
    Id = 'probe'
    Role = 'Client'
    ProcessId = 99
    Host = 'ManualChildProbe'
    Health = 24999
}
$outputDirectory = Join-Path $Run 'bin/probe'
$source = Join-Path $Run 'source'
New-Item -ItemType Directory -Force $source | Out-Null
'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><AssemblyName>AiNative.ManualChildProbe</AssemblyName></PropertyGroup></Project>' |
    Set-Content -LiteralPath (Join-Path $source 'probe.csproj')
'System.Threading.Thread.Sleep(60000);' | Set-Content -LiteralPath (Join-Path $source 'Program.cs')
& $SdkPath publish (Join-Path $source 'probe.csproj') -c Release -o $outputDirectory --nologo -v quiet
if ($LASTEXITCODE) {
    throw 'Probe build failed'
}
$probeExecutable = Join-Path $outputDirectory 'AiNative.ManualChildProbe.exe'
function Assert-NoProbe {
    $leaked = @(Get-Process -Name AiNative.ManualChildProbe -ErrorAction SilentlyContinue | Where-Object Path -eq $probeExecutable)
    if ($leaked.Count) {
        throw 'Failed startup leaked its child'
    }
}
$locked = [IO.File]::Open((Join-Path $Run 'probe.stderr.log'), [IO.FileMode]::Create, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
    $failed = $false
    try {
        $null = Start-OwnedChild $probe
    } catch {
        $failed = $true
    }
    if (-not $failed) {
        throw 'Expected locked-log failure'
    }
    Assert-NoProbe
} finally {
    $locked.Dispose()
}
# Fail after the process was started, before ownership could reach the caller.
New-Item -ItemType Directory (Join-Path $Run 'probe.pid.json') | Out-Null
$failed = $false
try {
    $null = Start-OwnedChild $probe
} catch {
    $failed = $true
}
if (-not $failed) {
    throw 'Expected PID record write failure'
}
Assert-NoProbe
Write-Host 'PASS: locked log and failed PID record leave no child process.'
