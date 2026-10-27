param([string]$SdkPath, [string]$RunDirectory = 'artifacts/topology-local')
$ErrorActionPreference = 'Stop'
$Repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$Run = [IO.Path]::GetFullPath((Join-Path $Repo $RunDirectory))
if (-not $Run.StartsWith((Join-Path $Repo 'artifacts') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'RunDirectory must be inside workspace artifacts'
}
if (-not $SdkPath) {
    $SdkPath = if (Test-Path (Join-Path $Repo 'artifacts/dotnet/dotnet.exe')) {
        Join-Path $Repo 'artifacts/dotnet/dotnet.exe'
    } else {
        (Get-Command dotnet).Source
    }
}
$SdkPath = [IO.Path]::GetFullPath($SdkPath)
# Shared tool inventory; deployment-specific paths and overrides stay in this adapter.
$Topology = Get-Content (Join-Path $PSScriptRoot 'topology-manifest.json') -Raw | ConvertFrom-Json -AsHashtable
$Services = @($Topology.Services)
function Get-ServiceEnvironment($Service, [hashtable]$Extra = @{}) {
    $variables = @{
        AINATIVE_SERVICE_ID = $Service.Id
        AINATIVE_PEERS_FILE = (Join-Path $Run 'peers.json')
        AINATIVE_SERVICE_PRIVATE_KEY_FILE = (Join-Path $Run ($Service.Id + '.private.pem'))

        AINATIVE_PLAYER_SIGNING_KEY_FILE = (Join-Path $Run 'player-ticket.private.pem')
        AINATIVE_PLAYER_PUBLIC_KEY_FILE = (Join-Path $Run 'player-ticket.public.pem')

        AINATIVE_FANTASY_CONFIG_FILE = (Join-Path $Repo 'infrastructure/topology/Fantasy.config')
        ASPNETCORE_URLS = ('http://127.0.0.1:' + $Service.Health)

        AINATIVE_POSTGRES_CONNECTION_STRING = $env:AINATIVE_TEST_POSTGRES

        AINATIVE_OUTBOX_PATH = (Join-Path $Run ('outbox/' + $Service.Id))

        AINATIVE_ACCEPTANCE_REPORT = (Join-Path $Run 'acceptance.json')
        DOTNET_ROOT = (Split-Path $SdkPath)
    }
    foreach ($key in $Topology.EnvironmentDefaults.Keys) {
        $variables[$key] = $Topology.EnvironmentDefaults[$key]
    }
    if ($Service.Address) {
        $variables.AINATIVE_BATTLE_ADDRESS = $Service.Address
    }
    foreach ($key in $Extra.Keys) {
        $variables[$key] = $Extra[$key]
    }
    if ($Service.Role -eq 'Battle' -and $Service.Host -eq 'BattleHost') {
        $identity = Get-Content (Join-Path $Run 'replay-identities.json') -Raw | ConvertFrom-Json
        $configuration = (Get-FileHash (Join-Path $Repo 'infrastructure/topology/Fantasy.config')).Hash
        foreach ($setting in $Topology.ReplayIdentitySettings) {
            $configuration += '|' + $setting + '=' + $variables[$setting]
        }
        $configurationHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($configuration)))
        $variables.AINATIVE_ARENA_REPLAY_PATH = Join-Path $Run ('replay/' + $Service.Id)
        $variables.AINATIVE_SOURCE_COMMIT = $identity.Source
        $variables.AINATIVE_FANTASY_COMMIT = $identity.Fantasy
        $variables.AINATIVE_PROTOCOL_IDENTITY = $identity.Protocol
        $variables.AINATIVE_CONFIGURATION_IDENTITY = 'sha256:' + $configurationHash
        @{
            Source = $identity.Source
            Fantasy = $identity.Fantasy
            Protocol = $identity.Protocol
            Configuration = ('sha256:' + $configurationHash)
        } | ConvertTo-Json | Set-Content (Join-Path $Run ($Service.Id + '.replay-identity.json'))
    }
    return $variables
}
function Start-OwnedChild($Service, [hashtable]$Extra = @{}) {
    $directoryId = if ($Service.DirectoryId) {
        $Service.DirectoryId
    } else {
        $Service.Id
    }
    $directory = Join-Path $Run ('bin/' + $directoryId)
    $exe = Join-Path $directory ('AiNative.' + $Service.Host + '.exe')
    if (-not (Test-Path $exe)) {
        throw "Build missing: $exe"
    }
    $psi = [Diagnostics.ProcessStartInfo]::new($exe)
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.WorkingDirectory = $directory
    $psi.ArgumentList.Add('--pid')
    $psi.ArgumentList.Add([string]$Service.ProcessId)
    $psi.ArgumentList.Add('-m')
    $psi.ArgumentList.Add('Release')
    $variables = Get-ServiceEnvironment $Service $Extra
    foreach ($key in $variables.Keys) {
        if ($null -ne $variables[$key]) {
            $psi.Environment[$key] = [string]$variables[$key]
        }
    }
    # Child owns its own log files; no shell quoting or inherited global environment mutation.
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $process = $null
    $stdout = $null
    $stderr = $null
    $outCopy = $null
    $errCopy = $null
    try {
        $stdout = [IO.FileStream]::new((Join-Path $Run ($Service.Id + '.stdout.log')),
            [IO.FileMode]::Create,
            [IO.FileAccess]::Write,
            [IO.FileShare]::ReadWrite)
        $stderr = [IO.FileStream]::new((Join-Path $Run ($Service.Id + '.stderr.log')),
            [IO.FileMode]::Create,
            [IO.FileAccess]::Write,
            [IO.FileShare]::ReadWrite)
        $process = [Diagnostics.Process]::Start($psi)
        $outCopy = $process.StandardOutput.BaseStream.CopyToAsync($stdout)
        $errCopy = $process.StandardError.BaseStream.CopyToAsync($stderr)
        $record = @{
            Id = $Service.Id
            Pid = $process.Id
            StartUtc = $process.StartTime.ToUniversalTime().ToString('O')
            Executable = $exe
        }
        $record | ConvertTo-Json | Set-Content (Join-Path $Run ($Service.Id + '.pid.json'))
        return @{
            Process = $process
            Stdout = $stdout
            Stderr = $stderr
            OutCopy = $outCopy
            ErrCopy = $errCopy
        }
    } catch {
        # Until the caller receives the handle, this function owns failed-start cleanup.
        if ($process) {
            if (-not $process.HasExited) {
                $process.Kill()
            }
            $process.WaitForExit()
        }
        foreach ($copy in @($outCopy, $errCopy)) {
            if ($copy) {
                try {
                    [void]$copy.GetAwaiter().GetResult()
                } catch {}
            }
        }
        if ($stdout) {
            $stdout.Dispose()
        }
        if ($stderr) {
            $stderr.Dispose()
        }
        if ($process) {
            $process.Dispose()
        }
        throw
    }
}
function Wait-Child($child) {
    $child.Process.WaitForExit()
    [void]$child.OutCopy.GetAwaiter().GetResult()
    [void]$child.ErrCopy.GetAwaiter().GetResult()
    $child.Stdout.Dispose()
    $child.Stderr.Dispose()
    return $child.Process.ExitCode
}
