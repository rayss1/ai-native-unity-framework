using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using AiNative.Server.Control;
using AiNative.Server.Hosting;
using NUnit.Framework;

namespace AiNative.BattleHost.Tests;

[TestFixture, NonParallelizable]
public sealed class LocalDebugBootstrapTests
{
    private string root = null!;
    private string directory = null!;
    private string priorDirectory = null!;
    private Dictionary<string, string?> saved = null!;

    [SetUp]
    public void SetUp()
    {
        DirectoryInfo? current = new(TestContext.CurrentContext.TestDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "AiNative.sln"))) current = current.Parent;
        root = current!.FullName;
        priorDirectory = Environment.CurrentDirectory;
        Environment.CurrentDirectory = root;
        directory = Path.Combine(root, "artifacts", "local-debug-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        saved = new();
        foreach (string key in new[] { LocalDebugBootstrap.ProfileVariable, LocalDebugBootstrap.ServiceVariable })
        { saved[key] = Environment.GetEnvironmentVariable(key); Environment.SetEnvironmentVariable(key, null); }
    }

    [TearDown]
    public void TearDown()
    {
        foreach ((string key, string? value) in saved) Environment.SetEnvironmentVariable(key, value);
        Environment.CurrentDirectory = priorDirectory;
        Directory.Delete(directory, true);
    }

    private string[] CreateProfile(string id, ServiceRole role, int pid, string host, Action<Dictionary<string, object>>? change = null)
    {
        string keyFile = Path.Combine(directory, "key.pem");
        File.WriteAllText(keyFile, "test fixture, not real key material");
        string peersFile = Path.Combine(directory, "peers.json");
        File.WriteAllText(peersFile, JsonSerializer.Serialize(new[] { new { Id = id, Role = role.ToString(), ProcessId = pid, PublicKeyFile = keyFile } }));
        Dictionary<string, string> env = new()
        {
            ["AINATIVE_SERVICE_ID"] = id, ["AINATIVE_SERVER_TOPOLOGY"] = "true",
            ["ASPNETCORE_URLS"] = "http://127.0.0.1:24102", ["AINATIVE_POSTGRES_CONNECTION_STRING"] = "Host=127.0.0.1;Username=local",
            ["AINATIVE_PLAYERS_PER_MATCH"] = "2", ["AINATIVE_MATCH_LENGTH_TICKS"] = "7200",
            ["AINATIVE_PEERS_FILE"] = peersFile, ["AINATIVE_SERVICE_PRIVATE_KEY_FILE"] = keyFile,
            ["AINATIVE_PLAYER_SIGNING_KEY_FILE"] = keyFile, ["AINATIVE_PLAYER_PUBLIC_KEY_FILE"] = keyFile,
            ["AINATIVE_FANTASY_CONFIG_FILE"] = keyFile, ["AINATIVE_BATTLE_ADDRESS"] = "127.0.0.1:22000"
        };
        foreach (string key in env.Keys) saved.TryAdd(key, Environment.GetEnvironmentVariable(key));
        using Process supervisor = Process.GetCurrentProcess();
        Dictionary<string, object> profile = new()
        {
            ["Version"] = 1, ["ServiceId"] = id, ["ProcessId"] = pid, ["Host"] = host,
            ["Environment"] = env, ["RequiredFiles"] = new Dictionary<string, string>
            {
                [keyFile] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(keyFile))),
                [peersFile] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(peersFile)))
            },
            ["SupervisorPid"] = supervisor.Id, ["SupervisorStartUtc"] = supervisor.StartTime.ToUniversalTime()
        };
        change?.Invoke(profile);
        string path = Path.Combine(directory, "profile.json");
        File.WriteAllText(path, JsonSerializer.Serialize(profile));
        Environment.SetEnvironmentVariable(LocalDebugBootstrap.ProfileVariable, path);
        Environment.SetEnvironmentVariable(LocalDebugBootstrap.ServiceVariable, id);
        return ["--pid", pid.ToString(), "-m", "Release"];
    }

    [TestCase("gate", ServiceRole.Gate, 1, "GateHost")]
    [TestCase("player", ServiceRole.Player, 2, "PlayerHost")]
    [TestCase("lobby", ServiceRole.Lobby, 3, "LobbyHost")]
    [TestCase("match", ServiceRole.Match, 4, "MatchHost")]
    [TestCase("coordinator", ServiceRole.Coordinator, 5, "RoomCoordinatorHost")]
    [TestCase("battle-1", ServiceRole.Battle, 100, "BattleHost")]
    [TestCase("battle-2", ServiceRole.Battle, 101, "BattleHost")]
    public void CorrectProfileOverridesInheritedEnvironmentAndStripsArguments(string id, ServiceRole role, int pid, string host)
    {
        string[] args = CreateProfile(id, role, pid, host);
        Environment.SetEnvironmentVariable("AINATIVE_SERVICE_ID", "stale-value");
#if DEBUG
        Assert.That(LocalDebugBootstrap.Apply(role, args), Is.EqualTo(new[] { "--pid", pid.ToString(), "-m", "Release" }));
        Assert.That(Environment.GetEnvironmentVariable("AINATIVE_SERVICE_ID"), Is.EqualTo(id));
#else
        Assert.Throws<InvalidOperationException>(() => LocalDebugBootstrap.Apply(role, args));
        Assert.That(Environment.GetEnvironmentVariable("AINATIVE_SERVICE_ID"), Is.EqualTo("stale-value"));
#endif
    }

#if DEBUG
    [TestCase("host")]
    [TestCase("pid")]
    [TestCase("version")]
    [TestCase("supervisor")]
    [TestCase("hash")]
    [TestCase("password")]
    [TestCase("missing-setting")]
    public void InvalidProfileDoesNotMutateEnvironment(string defect)
    {
        string[] args = CreateProfile("player", ServiceRole.Player, 2, "PlayerHost", p =>
        {
            Dictionary<string, string> env = (Dictionary<string, string>)p["Environment"];
            switch (defect)
            {
                case "host": p["Host"] = "BattleHost"; break;
                case "pid": p["ProcessId"] = 100; break;
                case "version": p["Version"] = 2; break;
                case "supervisor": p["SupervisorStartUtc"] = DateTime.UnixEpoch; break;
                case "hash": ((Dictionary<string, string>)p["RequiredFiles"])[env["AINATIVE_PEERS_FILE"]] = "00"; break;
                case "password": env["AINATIVE_POSTGRES_CONNECTION_STRING"] += ";Password = disallowed"; break;
                case "missing-setting": env.Remove("ASPNETCORE_URLS"); break;
            }
        });
        Environment.SetEnvironmentVariable("AINATIVE_SERVICE_ID", "unchanged");
        Assert.Throws<InvalidDataException>(() => LocalDebugBootstrap.Apply(ServiceRole.Player, args));
        Assert.That(Environment.GetEnvironmentVariable("AINATIVE_SERVICE_ID"), Is.EqualTo("unchanged"));
    }

    [Test]
    public void WrongHostRoleAndMissingProfileFailBeforeMutation()
    {
        string[] args = CreateProfile("player", ServiceRole.Player, 2, "PlayerHost");
        Assert.Throws<InvalidDataException>(() => LocalDebugBootstrap.Apply(ServiceRole.Battle, args));
        File.Delete(Environment.GetEnvironmentVariable(LocalDebugBootstrap.ProfileVariable)!);
        Assert.Throws<FileNotFoundException>(() => LocalDebugBootstrap.Apply(ServiceRole.Player, args));
    }
#endif

    [Test]
    public void OrdinaryLaunchArgumentsAreUntouched()
    {
        string[] args = ["--pid", "2", "-m", "Release"];
        Assert.That(LocalDebugBootstrap.Apply(ServiceRole.Player, args), Is.SameAs(args));
    }
}
