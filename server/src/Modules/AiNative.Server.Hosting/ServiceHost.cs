using System.Security.Cryptography;
using System.Text.Json;
using AiNative.Server.Backend;
using AiNative.Server.Control;
using AiNative.Server.Fantasy;
using AiNative.Server.Rooms;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OpenTelemetry.Logs;

namespace AiNative.Server.Hosting;

public sealed record HostSettings(string ServiceId, ServiceRole Role, int ProcessId, int SceneId, PeerAuthentication Authentication)
{
    public static HostSettings Load(ServiceRole role, string[] args)
    {
        string id = Required("AINATIVE_SERVICE_ID");
        PeerConfig[] peers = JsonSerializer.Deserialize<PeerConfig[]>(File.ReadAllText(Required("AINATIVE_PEERS_FILE")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("peers-required");
        PeerConfig self = peers.Single(x => x.Id == id);
        if (!Enum.TryParse(self.Role, out ServiceRole configured) || configured != role) throw new InvalidDataException("service-role-mismatch");
        int pidArg = Array.IndexOf(args, "--pid");
        if (pidArg < 0 || pidArg + 1 >= args.Length || args[pidArg + 1] != self.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) ||
            !args.Contains("Release", StringComparer.Ordinal) || args.Contains("Develop", StringComparer.Ordinal) || args.Contains("--StartupGroup", StringComparer.Ordinal) || args.Contains("-g", StringComparer.Ordinal))
            throw new InvalidDataException("launch-with-configured-pid-and-release-mode");
        string? configPath = Environment.GetEnvironmentVariable("AINATIVE_FANTASY_CONFIG_FILE");
        if (!string.IsNullOrWhiteSpace(configPath)) File.Copy(Path.GetFullPath(configPath), Path.Combine(AppContext.BaseDirectory, "Fantasy.config"), overwrite: true);
        ServicePeer[] principals = peers.Select(x => new ServicePeer(x.Id, Enum.Parse<ServiceRole>(x.Role), x.SceneId, File.ReadAllText(x.PublicKeyFile))).ToArray();
        PeerAuthentication auth = new(id, File.ReadAllText(Required("AINATIVE_SERVICE_PRIVATE_KEY_FILE")), principals, TimeProvider.System);
        return new(id, role, self.ProcessId, self.SceneId, auth);
    }
    public static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : throw new InvalidDataException(name + "-required");
    private sealed record PeerConfig(string Id, string Role, int ProcessId, int SceneId, string PublicKeyFile);
}

public static class ServiceHost
{
    public static WebApplicationBuilder CreateBuilder() => WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
    public static void RegisterRuntime(WebApplicationBuilder builder, FantasyServiceRuntime runtime)
    {
        builder.Logging.ClearProviders(); builder.Logging.AddJsonConsole();
        builder.Logging.AddOpenTelemetry(o =>
        {
            o.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(HostSettings.Required("AINATIVE_SERVICE_ID")));
            o.AddOtlpExporter(exporter => exporter.TimeoutMilliseconds = 2000);
        });
        builder.Services.AddOpenTelemetry().ConfigureResource(r => r.AddService(HostSettings.Required("AINATIVE_SERVICE_ID")))
            .WithTracing(t => t.AddSource(ServiceDiagnostics.Name).AddAspNetCoreInstrumentation().AddOtlpExporter(o =>
            { o.TimeoutMilliseconds = 2000; }))
            .WithMetrics(m => m.AddMeter(ServiceDiagnostics.Name).AddRuntimeInstrumentation().AddAspNetCoreInstrumentation().AddOtlpExporter(o =>
            { o.TimeoutMilliseconds = 2000; }));
        builder.Services.AddSingleton(runtime);
        builder.Services.AddHostedService<FantasyRuntimeHostedService>();
    }
    public static void MapHealth(WebApplication app, HostSettings settings, FantasyServiceRuntime runtime, Func<bool>? domainReady = null)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "live", role = settings.Role.ToString(), settings.ServiceId }));
        app.MapGet("/health/ready", () => runtime.IsReady && (domainReady?.Invoke() ?? true)
            ? Results.Ok(new { status = "ready", settings.ServiceId }) : Results.StatusCode(503));
    }
    public static async Task RunAsync(ServiceRole role, string[] args)
    {
        HostSettings settings = HostSettings.Load(role, args);
        await using FantasyServiceRuntime runtime = new(settings.SceneId, role, settings.Authentication);
        WebApplicationBuilder builder = CreateBuilder();
        RegisterRuntime(builder, runtime);
        IServiceHandler handler;
        IAsyncDisposable? store = null;
        RSA? signing = null;
        Func<CancellationToken, ValueTask>? pump = null;
        Func<bool>? ready = null;
        switch (role)
        {
            case ServiceRole.Gate:
                GateService gate = new(runtime, runtime, TimeProvider.System);
                handler = gate; pump = async ct => { await runtime.SweepDisconnectedAsync(ct); await gate.PumpAsync(ct); };
                break;
            case ServiceRole.Player:
                PostgresPlayerStore playerStore = new(HostSettings.Required("AINATIVE_POSTGRES_CONNECTION_STRING")); store = playerStore;
                await playerStore.InitializeAsync();
                signing = RSA.Create(); signing.ImportFromPem(File.ReadAllText(HostSettings.Required("AINATIVE_PLAYER_SIGNING_KEY_FILE")));
                handler = new PlayerService(playerStore, signing, TimeProvider.System, runtime);
                break;
            case ServiceRole.Lobby: handler = new LobbyService(runtime); break;
            case ServiceRole.Match:
                MatchService match = new(runtime, TimeProvider.System, builder.Configuration.GetValue("AINATIVE_PLAYERS_PER_MATCH", 2));
                handler = match; pump = match.PumpAsync; break;
            case ServiceRole.Coordinator:
                PostgresAllocationStore allocationStore = new(HostSettings.Required("AINATIVE_POSTGRES_CONNECTION_STRING")); store = allocationStore;
                await allocationStore.InitializeAsync();
                RoomCoordinator coordinator = new(allocationStore, runtime, TimeProvider.System);
                await coordinator.InitializeAsync();
                handler = coordinator; pump = coordinator.SweepAsync; ready = () => coordinator.IsAllocating && allocationStore.OwnsCoordinator; break;
            default: throw new ArgumentOutOfRangeException(nameof(role));
        }
        runtime.SetHandler(handler);
        if (pump is not null) builder.Services.AddSingleton<IHostedService>(new ServicePump(pump));
        WebApplication app = builder.Build(); MapHealth(app, settings, runtime, ready);
        try { await app.RunAsync(); }
        finally { if (store is not null) await store.DisposeAsync(); signing?.Dispose(); }
    }
    private sealed class FantasyRuntimeHostedService(FantasyServiceRuntime runtime) : BackgroundService
    { protected override Task ExecuteAsync(CancellationToken stoppingToken) => runtime.RunAsync(stoppingToken); }
}

public sealed class ServicePump(Func<CancellationToken, ValueTask> tick, int intervalMilliseconds = 250) : BackgroundService
{
    public long Failures { get; private set; }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await tick(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                Failures++; ServiceDiagnostics.RecordPumpFailure();
                // Throttle repeated outage messages and avoid exception text that may contain credentials or payloads.
                if (Failures == 1 || Failures % 100 == 0)
                    Console.Error.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { eventName = "service-pump-failed", count = Failures, errorType = exception.GetType().Name }));
            }
            await Task.Delay(intervalMilliseconds, stoppingToken);
        }
    }
}
