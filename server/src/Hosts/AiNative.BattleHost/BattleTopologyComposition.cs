using System.Security.Cryptography;
using AiNative.Server.Backend;
using AiNative.Server.Battle;
using AiNative.Server.Control;
using AiNative.Server.Fantasy;
using AiNative.Server.Hosting;
using AiNative.Server.Rooms;

namespace AiNative.BattleHost;

internal static class BattleTopologyComposition
{
    public static async Task RunAsync(string[] args)
    {
        HostSettings settings = HostSettings.Load(ServiceRole.Battle, args);
        await using FantasyServiceRuntime runtime = new(settings.SceneId, ServiceRole.Battle, settings.Authentication);
        WebApplicationBuilder builder = ServiceHost.CreateBuilder();
        int workers = builder.Configuration.GetValue("AINATIVE_BATTLE_WORKERS", 2);
        int density = builder.Configuration.GetValue("AINATIVE_ROOMS_PER_WORKER", 2);
        if (workers < 1 || density < 1 || checked(workers * density) > 16)
            throw new InvalidOperationException("Battle topology supports at most 16 total rooms per process.");
        int mailbox = builder.Configuration.GetValue("AINATIVE_BATTLE_MAILBOX_CAPACITY", 256);
        int length = builder.Configuration.GetValue("AINATIVE_MATCH_LENGTH_TICKS", ArenaRoom.MatchLengthTicks);
        string address = HostSettings.Required("AINATIVE_BATTLE_ADDRESS");
        string epoch = Guid.NewGuid().ToString("N");
        int registered = 0;
        DurableResultOutbox outbox = new(HostSettings.Required("AINATIVE_OUTBOX_PATH"),
            builder.Configuration.GetValue("AINATIVE_OUTBOX_MAX_RESULTS", 128), builder.Configuration.GetValue("AINATIVE_OUTBOX_MAX_BYTES", 16_777_216L));
        await outbox.InitializeAsync();
        using RSA playerPublicKey = RSA.Create(); playerPublicKey.ImportFromPem(File.ReadAllText(HostSettings.Required("AINATIVE_PLAYER_PUBLIC_KEY_FILE")));
        await using FantasyKcpGateway gateway = new(maxConnections: checked(workers * density * ArenaRoom.MaxPlayers * 2));
        EntryTicketVerifier verifier = new(playerPublicKey, TimeProvider.System);
        string? replayDirectory = builder.Configuration["AINATIVE_ARENA_REPLAY_PATH"];
        if (string.IsNullOrWhiteSpace(replayDirectory)) replayDirectory = null;
        ArenaReplayIdentity? replayIdentity = replayDirectory == null ? null : new(
            HostSettings.Required("AINATIVE_SOURCE_COMMIT"), HostSettings.Required("AINATIVE_FANTASY_COMMIT"),
            HostSettings.Required("AINATIVE_PROTOCOL_IDENTITY"), HostSettings.Required("AINATIVE_CONFIGURATION_IDENTITY"));
        await using TopologyBattleEngine engine = new(gateway, verifier, outbox, length, replayDirectory, replayIdentity,
            builder.Configuration.GetValue("AINATIVE_ARENA_REPLAY_CAPACITY", 4096),
            builder.Configuration.GetValue("AINATIVE_ARENA_REPLAY_MAX_FILES", 1024),
            builder.Configuration.GetValue("AINATIVE_ARENA_REPLAY_MAX_BYTES", 2147483648L));
        await using BattleWorkerPool pool = new(settings.ServiceId, epoch, workers, density, mailbox, engine.CreateRoom);
        engine.Pool = pool;
        runtime.SetHandler(new BattleControlService(pool, outbox, address));
        ServiceHost.RegisterRuntime(builder, runtime);
        builder.Services.AddSingleton<IHostedService>(new ServicePump(engine.PumpAsync, 5));
        builder.Services.AddSingleton<IHostedService>(new ServicePump(engine.FlushResultsAsync, 250));
        builder.Services.AddSingleton<IHostedService>(new ServicePump(ct => outbox.RetryAsync(runtime, ct), 1000));
        builder.Services.AddSingleton<IHostedService>(new ServicePump(async ct =>
        {
            ServiceReply reported = await runtime.CallAsync(new(ServiceRole.Coordinator), ServiceMethods.NodeReport, pool.Inventory(address, outbox.CanAdmit && engine.ReplayCanAdmit), ct);
            Interlocked.Exchange(ref registered, reported.Success ? 1 : 0);
            if (reported.Error is "inventory-conflict" or "invalid-node-report" or "stale-epoch") pool.BeginDrain();
        }, 5000));
        WebApplication app = builder.Build();
        ServiceHost.MapHealth(app, settings, runtime, () => gateway.ListeningPort > 0 && Volatile.Read(ref registered) != 0 && outbox.CanAdmit && engine.ReplayCanAdmit);
        app.MapGet("/health/diagnostics", () =>
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            return Results.Ok(new
            {
                workers = pool.Performance(),
                gc = new { gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2), allocatedBytes = GC.GetTotalAllocatedBytes(false), heapSizeBytes = GC.GetGCMemoryInfo().HeapSizeBytes },
                process = new { privateMemoryBytes = process.PrivateMemorySize64, workingSetBytes = process.WorkingSet64,
                    peakWorkingSetBytes = process.PeakWorkingSet64, totalProcessorSeconds = process.TotalProcessorTime.TotalSeconds,
                    logicalProcessors = Environment.ProcessorCount, availableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes },
                queues = engine.Diagnostics(),
                replay = new { healthy = engine.ReplayHealthy, canAdmit = engine.ReplayCanAdmit, files = engine.ReplayFiles, accountedBytes = engine.ReplayBytes, incompleteCaptures = engine.IncompleteReplayCount }
            });
        });
        app.Lifetime.ApplicationStopping.Register(pool.BeginDrain);
        gateway.AttachToServiceRuntime(); pool.Start();
        try { await app.RunAsync(); }
        finally { gateway.DetachFromServiceRuntime(); }
    }
}
