using AiNative.Protocol.Backend.V1;
using AiNative.Server.Rooms;
using NUnit.Framework;

namespace AiNative.Server.Rooms.Tests;

public sealed class WorkerTerminationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    public async Task TerminalWorkerCompletesQueuedInstallationAndRelease(bool stop, bool cancel)
    {
        using ManualResetEventSlim entered = new(), exit = new();
        using CancellationTokenSource cancellation = new();
        TrackedRoom installed = new(entered, exit, fault: !stop), pending = new();
        await using BattleWorkerPool pool = new("node", "epoch", 1, 2, 8,
            allocation => allocation.RoomId == "a" ? installed : pending);
        pool.Start();
        Assert.That(pool.Reserve(Room("a")), Is.Empty);
        Assert.That(pool.Reserve(Room("b")), Is.Empty);
        try
        {
            Assert.That(await pool.CreateAsync("a", "a", "epoch").AsTask().WaitAsync(Timeout), Is.Empty);
            Assert.That(entered.Wait(Timeout), Is.True);
            Task<string> create = pool.CreateAsync("b", "b", "epoch", cancellation.Token).AsTask();
            Task<string> release = pool.ReleaseAsync("a", "a", "epoch").AsTask();
            Task<string> releasePending = pool.ReleaseAsync("b", "b", "epoch").AsTask();
            Task<string> duplicateRelease = pool.ReleaseAsync("b", "b", "epoch").AsTask();
            if (cancel)
            {
                cancellation.Cancel();
                Assert.ThrowsAsync<TaskCanceledException>(async () => await create.WaitAsync(Timeout));
            }
            Task? dispose = stop ? pool.DisposeAsync().AsTask() : null;
            exit.Set();
            if (!cancel) Assert.That(await create.WaitAsync(Timeout), Is.EqualTo("worker-unavailable"));
            Assert.That(await release.WaitAsync(Timeout), Is.Empty);
            Assert.That(await releasePending.WaitAsync(Timeout), Is.Empty);
            Assert.That(await duplicateRelease.WaitAsync(Timeout), Is.Empty);
            if (dispose is not null) await dispose.WaitAsync(Timeout);
            Assert.That(installed.Disposals, Is.EqualTo(1));
            Assert.That(pending.Disposals, Is.EqualTo(1));
            Assert.That(pending.Ticks, Is.Zero);
            Assert.That(pool.TryPost(0, () => { }), Is.False);
            Assert.That(pool.Reserve(Room("c")), Is.Not.Empty);
            Assert.That(pool.Inventory().Rooms, Is.Empty);
        }
        finally { exit.Set(); }
    }

    [Test]
    public async Task DisposeBeforeStartCompletesQueuedWorkAndDisposesOnlyOnce()
    {
        TrackedRoom room = new();
        BattleWorkerPool pool = new("node", "epoch", 1, 1, 8, _ => room);
        Assert.That(pool.Reserve(Room("a")), Is.Empty);
        Task<string> create = pool.CreateAsync("a", "a", "epoch").AsTask();
        Task<string> release = pool.ReleaseAsync("a", "a", "epoch").AsTask();
        await pool.DisposeAsync().AsTask().WaitAsync(Timeout);
        Assert.That(await create.WaitAsync(Timeout), Is.EqualTo("worker-unavailable"));
        Assert.That(await release.WaitAsync(Timeout), Is.Empty);
        await pool.DisposeAsync();
        Assert.That(room.Disposals, Is.EqualTo(1));
        Assert.That(room.Ticks, Is.Zero);
        Assert.That(pool.TryPost(0, () => { }), Is.False);
        Assert.Throws<ObjectDisposedException>(pool.Start);
    }

    [Test]
    public async Task ConcurrentDisposeBeforeStartWaitsForTheSameCleanup()
    {
        using ManualResetEventSlim entered = new(), exit = new();
        BattleWorkerPool pool = new("node", "epoch", 1, 1, 8, _ => new DisposalBlockedRoom(entered, exit));
        pool.Reserve(Room("a"));
        Task<string> create = pool.CreateAsync("a", "a", "epoch").AsTask();
        Task first = pool.DisposeAsync().AsTask();
        Task? second = null;
        try
        {
            Assert.That(entered.Wait(Timeout), Is.True);
            second = pool.DisposeAsync().AsTask();
            Assert.That(await Task.WhenAny(second, Task.Delay(200)), Is.Not.SameAs(second));
            exit.Set();
            await Task.WhenAll(first, second).WaitAsync(Timeout);
            Assert.That(await create.WaitAsync(Timeout), Is.EqualTo("worker-unavailable"));
        }
        finally
        {
            exit.Set();
            await first.WaitAsync(Timeout);
            if (second is not null) await second.WaitAsync(Timeout);
        }
    }

    [Test]
    public async Task FactoryInFlightCannotKeepDuplicateCreationWaitingAfterFault()
    {
        using ManualResetEventSlim tickEntered = new(), tickExit = new(), factoryEntered = new(), factoryExit = new();
        TrackedRoom installed = new(tickEntered, tickExit, fault: true), pending = new();
        await using BattleWorkerPool pool = new("node", "epoch", 1, 2, 8, allocation =>
        {
            if (allocation.RoomId == "a") return installed;
            factoryEntered.Set(); factoryExit.Wait(); return pending;
        });
        pool.Start(); pool.Reserve(Room("a")); pool.Reserve(Room("b"));
        Task<string>? first = null;
        try
        {
            await pool.CreateAsync("a", "a", "epoch").AsTask().WaitAsync(Timeout);
            Assert.That(tickEntered.Wait(Timeout), Is.True);
            first = Task.Run(async () => await pool.CreateAsync("b", "b", "epoch"));
            Assert.That(factoryEntered.Wait(Timeout), Is.True);
            Task<string> duplicate = pool.CreateAsync("b", "b", "epoch").AsTask();
            tickExit.Set();
            Assert.That(await duplicate.WaitAsync(Timeout), Is.EqualTo("worker-unavailable"));
            factoryExit.Set();
            Assert.That(await first.WaitAsync(Timeout), Is.EqualTo("worker-unavailable"));
            Assert.That(pending.Disposals, Is.EqualTo(1));
            Assert.That(pending.Ticks, Is.Zero);
        }
        finally
        {
            tickExit.Set(); factoryExit.Set();
            if (first is not null) await first.WaitAsync(Timeout);
        }
    }

    [Test]
    public async Task ThrowingRoomDisposalStillCompletesAllPendingWork()
    {
        using ManualResetEventSlim entered = new(), exit = new();
        TrackedRoom installed = new(entered, exit, fault: true, throwOnDispose: true), pending = new(throwOnDispose: true);
        await using BattleWorkerPool pool = new("node", "epoch", 1, 2, 8, allocation => allocation.RoomId == "a" ? installed : pending);
        pool.Start(); pool.Reserve(Room("a")); pool.Reserve(Room("b"));
        try
        {
            await pool.CreateAsync("a", "a", "epoch").AsTask().WaitAsync(Timeout);
            Assert.That(entered.Wait(Timeout), Is.True);
            Task<string> create = pool.CreateAsync("b", "b", "epoch").AsTask();
            Task<string> release = pool.ReleaseAsync("a", "a", "epoch").AsTask();
            exit.Set();
            Assert.That(await create.WaitAsync(Timeout), Is.EqualTo("worker-unavailable"));
            Assert.That(await release.WaitAsync(Timeout), Is.Empty);
            await pool.DisposeAsync().AsTask().WaitAsync(Timeout);
            Assert.That(installed.Disposals, Is.EqualTo(1));
            Assert.That(pending.Disposals, Is.EqualTo(1));
        }
        finally { exit.Set(); }
    }

    [Test]
    public async Task ReleaseCompletionRemovesReservationBeforeLaterCleanupCanBlock()
    {
        using ManualResetEventSlim entered = new(), exit = new(), disposing = new(), disposed = new();
        TrackedRoom installed = new(entered, exit, fault: true);
        DisposalBlockedRoom pending = new(disposing, disposed);
        await using BattleWorkerPool pool = new("node", "epoch", 2, 2, 8,
            allocation => allocation.RoomId == "a" ? installed : allocation.RoomId == "b" ? pending : new TrackedRoom());
        pool.Start(); pool.Reserve(Room("a")); pool.Reserve(Room("healthy")); pool.Reserve(Room("b"));
        try
        {
            await pool.CreateAsync("a", "a", "epoch").AsTask().WaitAsync(Timeout);
            Assert.That(entered.Wait(Timeout), Is.True);
            Task<string> release = pool.ReleaseAsync("a", "a", "epoch").AsTask();
            Task<string> create = pool.CreateAsync("b", "b", "epoch").AsTask();
            exit.Set();
            Assert.That(disposing.Wait(Timeout), Is.True);
            Assert.That(await release.WaitAsync(Timeout), Is.Empty);
            Assert.That(pool.Inventory().Rooms.Any(room => room.RoomId == "a"), Is.False);
            Assert.That(await pool.CreateAsync("healthy", "healthy", "epoch").AsTask().WaitAsync(Timeout), Is.Empty);
            Assert.That(pool.TryPost(0, () => { }), Is.False);
            disposed.Set();
            Assert.That(await create.WaitAsync(Timeout), Is.EqualTo("worker-unavailable"));
        }
        finally { exit.Set(); disposed.Set(); }
    }

    [Test]
    public async Task NormalReleaseDisposalFaultCannotDisposeRoomTwiceOrHangRelease()
    {
        TrackedRoom room = new(throwOnDispose: true);
        await using BattleWorkerPool pool = new("node", "epoch", 1, 1, 8, _ => room);
        pool.Start(); pool.Reserve(Room("a"));
        await pool.CreateAsync("a", "a", "epoch").AsTask().WaitAsync(Timeout);
        Assert.That(await pool.ReleaseAsync("a", "a", "epoch").AsTask().WaitAsync(Timeout), Is.Empty);
        await pool.DisposeAsync().AsTask().WaitAsync(Timeout);
        Assert.That(room.Disposals, Is.EqualTo(1));
        Assert.That(pool.Inventory().Rooms, Is.Empty);
    }

    [Test]
    public async Task CancelAndReleaseRaceDoesNotLeavePendingCompletionOrFreeCapacityTwice()
    {
        using ManualResetEventSlim entered = new(), exit = new();
        using CancellationTokenSource cancellation = new();
        TrackedRoom pending = new();
        await using BattleWorkerPool pool = new("node", "epoch", 1, 1, 8, _ => pending);
        pool.Start();
        Assert.That(pool.TryPost(0, () => { entered.Set(); exit.Wait(); }), Is.True);
        Assert.That(entered.Wait(Timeout), Is.True);
        pool.Reserve(Room("a"));
        try
        {
            Task<string> create = pool.CreateAsync("a", "a", "epoch", cancellation.Token).AsTask();
            Task cancel = Task.Run(cancellation.Cancel);
            Task<string> release = Task.Run(async () => await pool.ReleaseAsync("a", "a", "epoch"));
            await cancel.WaitAsync(Timeout);
            Assert.ThrowsAsync<TaskCanceledException>(async () => await create.WaitAsync(Timeout));
            exit.Set();
            Assert.That(await release.WaitAsync(Timeout), Is.Empty);
            using ManualResetEventSlim processed = new();
            Assert.That(pool.TryPost(0, processed.Set), Is.True);
            Assert.That(processed.Wait(Timeout), Is.True);
            Assert.That(pending.Disposals, Is.EqualTo(1));
            Assert.That(pending.Ticks, Is.Zero);
            Assert.That(pool.Reserve(Room("b")), Is.Empty);
            Assert.That(pool.Reserve(Room("c")), Is.EqualTo("capacity-unavailable"));
        }
        finally { exit.Set(); }
    }

    [Test]
    public async Task SteadyTicksDoNotAllocateManagedMemory()
    {
        await using BattleWorkerPool pool = new("node", "epoch", 1, 1, 8, _ => new TrackedRoom());
        pool.Start(); pool.Reserve(Room("a"));
        await pool.CreateAsync("a", "a", "epoch").AsTask().WaitAsync(Timeout);
        Assert.That(SpinWait.SpinUntil(() => pool.Performance()[0].TickCount >= 10, Timeout), Is.True);
        WorkerPerformance before = pool.Performance()[0];
        Assert.That(SpinWait.SpinUntil(() => pool.Performance()[0].TickCount >= before.TickCount + 10, Timeout), Is.True);
        Assert.That(pool.Performance()[0].AllocatedBytes - before.AllocatedBytes, Is.Zero);
    }

    private static RoomAllocation Room(string id) => new()
    { RoomId = id, MatchId = id, AllocationId = id, NodeId = "node", BootEpoch = "epoch", PlayerIds = { "p" + id } };

    private sealed class DisposalBlockedRoom(ManualResetEventSlim entered, ManualResetEventSlim exit) : IWorkerRoom
    {
        public void Tick() { }
        public void Dispose() { entered.Set(); exit.Wait(); }
    }

    private sealed class TrackedRoom(ManualResetEventSlim? entered = null, ManualResetEventSlim? exit = null,
        bool fault = false, bool throwOnDispose = false) : IWorkerRoom
    {
        private int _disposals, _ticks;
        public int Disposals => Volatile.Read(ref _disposals);
        public int Ticks => Volatile.Read(ref _ticks);
        public void Tick()
        {
            Interlocked.Increment(ref _ticks);
            entered?.Set(); exit?.Wait();
            if (fault) throw new InvalidOperationException("tick-fault");
        }
        public void Dispose()
        {
            Interlocked.Increment(ref _disposals);
            if (throwOnDispose) throw new InvalidOperationException("dispose-fault");
        }
    }
}
