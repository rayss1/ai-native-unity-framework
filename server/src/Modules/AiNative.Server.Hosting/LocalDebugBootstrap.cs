using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using AiNative.Server.Control;

namespace AiNative.Server.Hosting;

/// <summary>Explicit, Debug-only handoff from the local supervisor to a VS-owned process.</summary>
public static class LocalDebugBootstrap
{
    public const string ProfileVariable = "AINATIVE_LOCAL_DEBUG_PROFILE";
    public const string ServiceVariable = "AINATIVE_LOCAL_DEBUG_SERVICE";

    public static string[] Apply(ServiceRole role, string[] args)
    {
        string? path = Environment.GetEnvironmentVariable(ProfileVariable);
        string? service = Environment.GetEnvironmentVariable(ServiceVariable);
        if (path is null && service is null) return args;
#if !DEBUG
        throw new InvalidOperationException("Local debug profiles require a Debug build.");
#else
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(service)) throw Invalid("selectors");
        (ServiceRole expectedRole, int pid, string host) = service switch
        {
            "gate" => (ServiceRole.Gate, 1, "GateHost"),
            "player" => (ServiceRole.Player, 2, "PlayerHost"),
            "lobby" => (ServiceRole.Lobby, 3, "LobbyHost"),
            "match" => (ServiceRole.Match, 4, "MatchHost"),
            "coordinator" => (ServiceRole.Coordinator, 5, "RoomCoordinatorHost"),
            "battle-1" => (ServiceRole.Battle, 100, "BattleHost"),
            "battle-2" => (ServiceRole.Battle, 101, "BattleHost"),
            _ => throw Invalid("service")
        };
        if (role != expectedRole) throw Invalid("role-mismatch");
        if (!args.SequenceEqual(new[] { "--pid", pid.ToString(CultureInfo.InvariantCulture), "-m", "Release" })) throw Invalid("fantasy-arguments");
        DirectoryInfo? root = new(Environment.CurrentDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AiNative.sln"))) root = root.Parent;
        if (root is null) throw Invalid("repository-working-directory-required");
        string fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(Path.Combine(root.FullName, "artifacts") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw Invalid("profile-outside-artifacts");
        Profile profile = JsonSerializer.Deserialize<Profile>(File.ReadAllText(fullPath)) ?? throw Invalid("profile");
        if (profile.Version != 1 || profile.ServiceId != service || profile.ProcessId != pid || profile.Host != host) throw Invalid("identity-mismatch");
        using (Process supervisor = Process.GetProcessById(profile.SupervisorPid))
        {
            if (supervisor.HasExited || supervisor.StartTime.ToUniversalTime() != profile.SupervisorStartUtc.ToUniversalTime()) throw Invalid("supervisor-stale");
        }
        Dictionary<string, string> env = profile.Environment ?? throw Invalid("environment");
        Dictionary<string, string> files = profile.RequiredFiles ?? throw Invalid("required-files");
        foreach ((string key, string value) in env)
        {
            if (!(key.StartsWith("AINATIVE_", StringComparison.Ordinal) || key is "ASPNETCORE_URLS" or "DOTNET_ROOT") ||
                key is ProfileVariable or ServiceVariable || key.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_')) || string.IsNullOrEmpty(value) || value.Contains('\0') || value.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase) || key.Contains("SECRET", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("Password=", StringComparison.OrdinalIgnoreCase) || value.Contains("Pwd=", StringComparison.OrdinalIgnoreCase)) throw Invalid("unsafe-environment");
        }
        string Required(string key) => env.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : throw Invalid("missing-" + key);
        if (Required("AINATIVE_SERVICE_ID") != service || Required("AINATIVE_SERVER_TOPOLOGY") != "true") throw Invalid("environment-identity");
        Required("ASPNETCORE_URLS");
        System.Data.Common.DbConnectionStringBuilder database = new() { ConnectionString = Required("AINATIVE_POSTGRES_CONNECTION_STRING") };
        if (database.ContainsKey("Password") || database.ContainsKey("Pwd")) throw Invalid("database-password-not-allowed");
        Required("AINATIVE_PLAYERS_PER_MATCH");
        Required("AINATIVE_MATCH_LENGTH_TICKS");
        foreach (string key in new[] { "AINATIVE_PEERS_FILE", "AINATIVE_SERVICE_PRIVATE_KEY_FILE", "AINATIVE_PLAYER_SIGNING_KEY_FILE", "AINATIVE_PLAYER_PUBLIC_KEY_FILE", "AINATIVE_FANTASY_CONFIG_FILE" })
        {
            string input = Required(key);
            if (!Path.IsPathFullyQualified(input) || !files.ContainsKey(input)) throw Invalid("unverified-input-" + key);
        }
        if (role == ServiceRole.Battle) Required("AINATIVE_BATTLE_ADDRESS");
        foreach ((string input, string hash) in files)
        {
            if (!Path.IsPathFullyQualified(input) || !File.Exists(input) || !string.Equals(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(input))), hash, StringComparison.OrdinalIgnoreCase)) throw Invalid("input-changed");
        }
        // Validate the peer identity before touching this process's environment.
        using JsonDocument peers = JsonDocument.Parse(File.ReadAllText(Required("AINATIVE_PEERS_FILE")));
        bool found = false;
        foreach (JsonElement peer in peers.RootElement.EnumerateArray())
        {
            string publicKey = peer.GetProperty("PublicKeyFile").GetString() ?? throw Invalid("peer-key");
            if (!files.ContainsKey(publicKey)) throw Invalid("unverified-peer-key");
            if (peer.GetProperty("Id").GetString() != service) continue;
            if (found || peer.GetProperty("Role").GetString() != role.ToString() || peer.GetProperty("ProcessId").GetInt32() != pid) throw Invalid("peer-identity");
            found = true;
        }
        if (!found) throw Invalid("peer-missing");
        foreach ((string key, string value) in env) System.Environment.SetEnvironmentVariable(key, value);
        System.Environment.SetEnvironmentVariable(ProfileVariable, null);
        System.Environment.SetEnvironmentVariable(ServiceVariable, null);
        return args;
#endif
    }

#if DEBUG
    private static InvalidDataException Invalid(string reason) => new("local-debug-" + reason + "; rerun start-manual.ps1 -DebugService and choose the matching VS profile.");
    private sealed record Profile(int Version, string ServiceId, int ProcessId, string Host,
        Dictionary<string, string>? Environment, Dictionary<string, string>? RequiredFiles, int SupervisorPid, DateTime SupervisorStartUtc);
#endif
}
