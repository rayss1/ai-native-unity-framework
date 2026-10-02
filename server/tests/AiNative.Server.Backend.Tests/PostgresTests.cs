using AiNative.Protocol.Backend.V1;
using AiNative.Server.Backend;
using AiNative.Server.Control;
using NUnit.Framework;

namespace AiNative.Server.Backend.Tests;

public sealed class PostgresTests
{
    [Test]
    public void Unavailable_database_never_falls_back_to_memory()
    {
        var store = new PostgresPlayerStore("Host=127.0.0.1;Port=1;Database=unavailable;Username=test;Timeout=1;Pooling=false");
        Assert.ThrowsAsync<Npgsql.NpgsqlException>(async () => await store.InitializeAsync());
        store.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
    [Test]
    public async Task Settlement_transaction_survives_store_restart_and_updates_once()
    {
        var connection = Environment.GetEnvironmentVariable("AINATIVE_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connection)) Assert.Ignore("AINATIVE_TEST_POSTGRES is not configured: real PostgreSQL duplicate/restart test skipped.");
        var store = new PostgresPlayerStore(connection!);
        await store.InitializeAsync();
        var id = Guid.NewGuid().ToString("N");
        Assert.That(await store.CreateAsync(new(id, id, new byte[32], new byte[32])), Is.True);
        Assert.That(await store.CreateAsync(new(Guid.NewGuid().ToString("N"), id, new byte[32], new byte[32])), Is.False);
        var result = new MatchResult { MatchId = Guid.NewGuid().ToString("N"), RoomId = "room", NodeId = "node", BootEpoch = "epoch", Completion = "completed" };
        result.Players.Add(new PlayerResult { PlayerId = id, Kills = 4, Won = true });
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.SettleAsync(result, "same-hash").AsTask()));
        await store.DisposeAsync();
        await using var restarted = new PostgresPlayerStore(connection!);
        await restarted.InitializeAsync();
        Assert.That((await restarted.SettleAsync(result, "same-hash")).Confirmed, Is.True);
        var profile = await restarted.ProfileAsync(id);
        Assert.That((profile!.Played, profile.Won, profile.Kills), Is.EqualTo((1L, 1L, 4L)));
        Assert.ThrowsAsync<ServiceException>(async () => await restarted.SettleAsync(result, "conflict"));
        var invalid = result.Clone(); invalid.MatchId = Guid.NewGuid().ToString("N"); invalid.Players.Add(new PlayerResult { PlayerId = "unknown" });
        Assert.ThrowsAsync<ServiceException>(async () => await restarted.SettleAsync(invalid, "bad-hash"));
        Assert.That(await restarted.SettlementAsync(invalid.MatchId), Is.Null);
        Assert.That((await restarted.ProfileAsync(id))!.Played, Is.EqualTo(1));
    }
}
