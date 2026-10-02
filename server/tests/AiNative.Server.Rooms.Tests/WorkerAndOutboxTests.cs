using AiNative.Protocol.Backend.V1;
using AiNative.Server.Rooms;
using NUnit.Framework;

namespace AiNative.Server.Rooms.Tests;

public sealed class WorkerAndOutboxTests
{
    [Test]
    public async Task ConcurrentReleaseIsIdempotentAndCannotFreeCapacityTwice()
    {
        await using BattleWorkerPool pool = new("node", "epoch", 1, 1, 8, _ => new CountingRoom());
        pool.Start();
        Assert.That(pool.Reserve(Room("a")), Is.Empty);
        await pool.CreateAsync("a", "a", "epoch");
        Task<string> first = pool.ReleaseAsync("a", "a", "epoch").AsTask();
        Task<string> second = pool.ReleaseAsync("a", "a", "epoch").AsTask();
        await Task.WhenAll(first, second);
        Assert.That(pool.Reserve(Room("b")), Is.Empty);
        Assert.That(pool.Reserve(Room("c")), Is.EqualTo("capacity-unavailable"));
    }

    [Test]
    public async Task FaultedWorkerStopsAdvertisingReadyRooms()
    {
        await using BattleWorkerPool pool = new("node", "epoch", 1, 1, 8, _ => new BrokenRoom());
        pool.Start(); pool.Reserve(Room("a"));
        await pool.CreateAsync("a", "a", "epoch");
        await Task.Delay(60);
        Assert.That(pool.Inventory().Rooms.Single().State, Is.EqualTo("Lost"));
        Assert.That(pool.Inventory().Workers.Single().AvailableRooms, Is.Zero);
    }
    [Test]
    public async Task RoomStaysOnItsWorkerAndRejectsOldEpochOrOverCapacity()
    {
        await using BattleWorkerPool pool = new("node", "epoch", 2, 1, 8, _ => new CountingRoom());
        pool.Start();
        RoomAllocation a = Room("a");
        RoomAllocation b = Room("b");
        Assert.That(pool.Reserve(a), Is.EqualTo(""));
        Assert.That(pool.Reserve(a), Is.EqualTo(""));
        Assert.That(pool.Reserve(b), Is.EqualTo(""));
        Assert.That(pool.Reserve(Room("c")), Is.EqualTo("capacity-unavailable"));
        RoomAllocation old = Room("old"); old.BootEpoch = "previous";
        Assert.That(pool.Reserve(old), Is.EqualTo("stale-epoch"));
        Assert.That(await pool.CreateAsync("a", "a", "epoch"), Is.EqualTo(""));
        int owner = pool.GetWorkerId("a");
        await Task.Delay(70);
        Assert.That(pool.GetWorkerId("a"), Is.EqualTo(owner));
        Assert.That(pool.Inventory().Rooms.Single(x => x.RoomId == "a").State, Is.EqualTo("Ready"));
    }

    [Test]
    public async Task BlockedWorkerAndFullMailboxDoNotStopAnotherWorker()
    {
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        CountingRoom healthy = new();
        await using BattleWorkerPool pool = new("node", "epoch", 2, 1, 2,
            room => room.RoomId == "slow" ? new BlockedRoom(entered, release) : healthy);
        pool.Start();
        try
        {
            Assert.That(pool.Reserve(Room("slow")), Is.Empty);
            Assert.That(pool.Reserve(Room("healthy")), Is.Empty);
            Assert.That(await pool.CreateAsync("healthy", "healthy", "epoch"), Is.Empty);
            Assert.That(await pool.CreateAsync("slow", "slow", "epoch"), Is.Empty);
            Assert.That(entered.Wait(TimeSpan.FromSeconds(2)), Is.True);
            int worker = pool.GetWorkerId("slow");
            Assert.That(pool.TryPost(worker, () => { }), Is.True);
            Assert.That(pool.TryPost(worker, () => { }), Is.True);
            Assert.That(pool.TryPost(worker, () => { }), Is.False);
            long before = healthy.Ticks;
            await Task.Delay(100);
            Assert.That(healthy.Ticks, Is.GreaterThan(before + 2));
        }
        finally { release.Set(); }
    }

    [Test]
    public async Task UnconfirmedResultsSurviveRestartAndConflictingDuplicateIsRejected()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ainative-outbox", Guid.NewGuid().ToString("N"));
        try
        {
            DurableResultOutbox first = new(dir, 1, 16384);
            MatchResult result = new() { MatchId = "m1", RoomId = "r1", NodeId = "node", BootEpoch = "epoch", Players = { new PlayerResult { PlayerId = "p", Kills = 3 } } };
            await first.InitializeAsync();
            Assert.That(await first.StoreAsync(result), Is.True);
            Assert.That(await first.StoreAsync(result), Is.True);
            MatchResult conflicting = result.Clone(); conflicting.Players[0].Kills = 4;
            Assert.ThrowsAsync<InvalidDataException>(async () => await first.StoreAsync(conflicting));
            DurableResultOutbox restarted = new(dir, 1, 16384);
            await restarted.InitializeAsync();
            Assert.That(restarted.Pending.Single().Players[0].Kills, Is.EqualTo(3));
            Assert.That(restarted.CanAdmit, Is.False);
            Assert.That(await restarted.StoreAsync(new MatchResult { MatchId = "m2" }), Is.False);
            await restarted.ConfirmAsync("m1", DurableResultOutbox.Hash(result));
            DurableResultOutbox confirmed = new(dir, 1, 16384);
            await confirmed.InitializeAsync();
            Assert.That(confirmed.Pending, Is.Empty);
            Assert.That(confirmed.CanAdmit, Is.True);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    private static RoomAllocation Room(string id) => new()
    { RoomId = id, MatchId = id, AllocationId = id, NodeId = "node", BootEpoch = "epoch", State = "Reserved", PlayerIds = { "p" + id } };
    private class CountingRoom : IWorkerRoom
    {
        private long _ticks;
        public long Ticks => Interlocked.Read(ref _ticks);
        public virtual void Tick() => Interlocked.Increment(ref _ticks);
        public void Dispose() { }
    }
    private sealed class BlockedRoom(ManualResetEventSlim entered, ManualResetEventSlim release) : CountingRoom
    {
        public override void Tick() { entered.Set(); release.Wait(); base.Tick(); }
    }
    private sealed class BrokenRoom : CountingRoom { public override void Tick() => throw new InvalidOperationException("broken"); }
}
