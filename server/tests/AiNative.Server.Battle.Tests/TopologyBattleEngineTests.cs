using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using AiNative.Protocol.V1;
using AiNative.Protocol.Backend.V1;
using AiNative.Realtime;
using AiNative.Server.Backend;
using AiNative.Server.Battle;
using AiNative.Server.Fantasy;
using AiNative.Server.Rooms;
using AiNative.Server.Control;
using Google.Protobuf;
using NUnit.Framework;
namespace AiNative.Server.Battle.Tests;

public class TopologyBattleEngineTests
{
    [TestCase(true, "futureInput")]
    [TestCase(false, "staleInput")]
    public async Task DiagnosticDropsDistinguishRejectedInputAgeFromOtherPackets(bool future, string reason)
    {
        await using var f = await Fixture.Create(100000);
        var player = await JoinPlayer(f, "p1", 20);
        await f.Until(() => player.Latest<Snapshot>(MessageId.Snapshot).RoomTick >= 40);
        player.Push(MessageId.InputCommand, new InputCommand { Sequence = 1, RoomTick = future ? ulong.MaxValue : 0 });
        await f.Until(() => f.Engine.DroppedPackets == 1);
        AssertDropReasons(f, reason, 1);
        Assert.That(player.Latest<Snapshot>(MessageId.Snapshot).LastProcessedInputSequence, Is.Zero);
    }

    [Test]
    public async Task DiagnosticDropsSeparateTerminalTrafficFromRejectedLiveInputs()
    {
        await using var f = await Fixture.Create(30);
        var player = await JoinPlayer(f, "p1", 20);
        await JoinPlayer(f, "p2", 21);
        await f.Until(() => player.Latest<Snapshot>(MessageId.Snapshot).MatchPhase == AiNative.Protocol.V1.ArenaMatchPhase.ArenaMatchFinished);
        player.Push(MessageId.InputCommand, new InputCommand { Sequence = 1, RoomTick = 0 });
        await f.Engine.PumpAsync();
        AssertDropReasons(f, "finishedRoom", 1);
    }

    [Test]
    public async Task DiagnosticDropsIdentifyCommandQueueOverflowBeforeWorkerAdmission()
    {
        await using var f = await Fixture.Create(100000);
        var player = await JoinPlayer(f, "p1", 20);
        using var release = new ManualResetEventSlim();
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.That(f.Pool.TryPost(0, () => { blocked.SetResult(); release.Wait(TimeSpan.FromSeconds(5)); done.SetResult(); }), Is.True);
        try
        {
            await blocked.Task.WaitAsync(TimeSpan.FromSeconds(3));
            ulong tick = player.Latest<Snapshot>(MessageId.Snapshot).RoomTick;
            for (uint sequence = 1; sequence <= 257; sequence++)
                player.Push(MessageId.InputCommand, new InputCommand { Sequence = sequence, RoomTick = tick });
            for (int pump = 0; pump < 17; pump++) await f.Engine.PumpAsync();
            AssertDropReasons(f, "commandQueueFull", 1);
            Assert.That(player.Latest<Snapshot>(MessageId.Snapshot).LastProcessedInputSequence, Is.Zero);
        }
        finally { release.Set(); await done.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
    }

    [Test]
    public async Task DiagnosticDropsKeepMalformedPacketsOutsideInputAgeClassification()
    {
        await using var f = await Fixture.Create(100000);
        var player = await JoinPlayer(f, "p1", 20);
        player.Push(MessageId.InputCommand, new InputCommand { Sequence = 1, RoomTick = ulong.MaxValue, MoveXMilli = 1001 });
        await f.Engine.PumpAsync();
        AssertDropReasons(f, "other", 1);
    }

    private static async Task<FakeTransport> JoinPlayer(Fixture f, string id, uint epoch)
    {
        var player = f.Connect(epoch);
        player.Push(MessageId.LoginRequest, f.Login(id)); await f.Engine.PumpAsync();
        player.Push(MessageId.JoinRoomRequest, new JoinRoomRequest { SessionId = player.Latest<LoginResponse>(MessageId.LoginResponse).SessionId });
        await f.Until(() => player.Has(MessageId.JoinRoomResponse));
        return player;
    }

    [Test]
    public async Task DiagnosticDropsKeepDuplicateSequenceOutsideInputAgeClassification()
    {
        await using var f = await Fixture.Create(100000);
        var player = await JoinPlayer(f, "p1", 20);
        player.Push(MessageId.InputCommand, new InputCommand { Sequence = 1, RoomTick = player.Latest<Snapshot>(MessageId.Snapshot).RoomTick });
        await f.Until(() => player.Latest<Snapshot>(MessageId.Snapshot).LastProcessedInputSequence == 1);
        player.Push(MessageId.InputCommand, new InputCommand { Sequence = 1, RoomTick = player.Latest<Snapshot>(MessageId.Snapshot).RoomTick });
        await f.Until(() => f.Engine.DroppedPackets == 1);
        AssertDropReasons(f, "otherInput", 1);
    }

    [Test]
    public async Task DiagnosticDropsDoNotAllocateOnOwnerTickRejectionPath()
    {
        await using var f = await Fixture.Create(100000);
        var player = await JoinPlayer(f, "p1", 20);
        using var release = new ManualResetEventSlim();
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var done = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.That(f.Pool.TryPost(0, () =>
        {
            for (int warmup = 0; warmup < 100; warmup++) f.Room!.Tick();
            blocked.SetResult(); release.Wait(TimeSpan.FromSeconds(5));
            long before = GC.GetAllocatedBytesForCurrentThread();
            f.Room!.Tick();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            done.SetResult(allocated);
        }), Is.True);
        try
        {
            await blocked.Task.WaitAsync(TimeSpan.FromSeconds(3));
            for (uint sequence = 1; sequence <= 128; sequence++)
                player.Push(MessageId.InputCommand, new InputCommand { Sequence = sequence, RoomTick = ulong.MaxValue });
            for (int pump = 0; pump < 8; pump++) await f.Engine.PumpAsync();
            release.Set();
            Assert.That(await done.Task.WaitAsync(TimeSpan.FromSeconds(3)), Is.Zero);
            AssertDropReasons(f, "futureInput", 128);
        }
        finally { release.Set(); await done.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
    }

    private static void AssertDropReasons(Fixture f, string expected, long count)
    {
        var diagnostics = JsonSerializer.SerializeToElement(f.Engine.Diagnostics());
        Assert.That(diagnostics.TryGetProperty("droppedPacketReasons", out var reasons), Is.True,
            "Packet totals must expose the actual rejection boundary so terminal traffic cannot hide live input rejection.");
        foreach (var reason in reasons.EnumerateObject())
            Assert.That(reason.Value.GetInt64(), Is.EqualTo(reason.Name == expected ? count : 0), reason.Name);
        Assert.That(reasons.GetProperty(expected).GetInt64(), Is.EqualTo(count));
        Assert.That(diagnostics.GetProperty("droppedPackets").GetInt64(), Is.EqualTo(count));
    }

    [Test]
    public async Task DiagnosticSnapshotCountsActualRoomInstallationAndRelease()
    {
        await using var f = await Fixture.Create(600);
        var installed = JsonSerializer.SerializeToElement(f.Engine.Diagnostics());
        Assert.That(installed.GetProperty("liveRooms").GetInt32(), Is.EqualTo(1));
        Assert.That(installed.GetProperty("lifecycleCount").GetInt64(), Is.EqualTo(1));
        Assert.That(installed.GetProperty("commandDepth").GetInt32(), Is.Zero);
        Assert.That(installed.GetProperty("eventDepth").GetInt32(), Is.Zero);
        Assert.That(await f.Pool.ReleaseAsync("room", "alloc", "boot", coordinatorEpoch: "coord"), Is.Empty);
        var released = JsonSerializer.SerializeToElement(f.Engine.Diagnostics());
        Assert.That(released.GetProperty("liveRooms").GetInt32(), Is.Zero);
        Assert.That(released.GetProperty("lifecycleCount").GetInt64(), Is.EqualTo(2));
        Assert.That(released.GetProperty("rooms").GetArrayLength(), Is.Zero);
        // Duplicate release does not fabricate a lifecycle transition.
        await f.Pool.ReleaseAsync("room", "alloc", "boot", coordinatorEpoch: "coord");
        Assert.That(JsonSerializer.SerializeToElement(f.Engine.Diagnostics()).GetProperty("lifecycleCount").GetInt64(), Is.EqualTo(2));
    }
    [Test]
    public async Task RejectedAndUnjoinedConnectionsReturnGatewayCapacityWhileJoinedSessionsRemain()
    {
        Clock clock = new(); await using var f = await Fixture.Create(600, clock: clock);
        var bad = f.Connect(100); bad.Push(MessageId.LoginRequest, f.Login("outsider")); await f.Engine.PumpAsync(); await f.Engine.PumpAsync();
        Assert.That(bad.State, Is.EqualTo(TransportState.Closed)); Assert.That(f.ConnectionCount, Is.Zero);
        var unjoined = f.Connect(101); unjoined.Push(MessageId.LoginRequest, f.Login("p1")); await f.Engine.PumpAsync();
        Assert.That(unjoined.Has(MessageId.LoginResponse), Is.True); clock.Advance(11); await f.Engine.PumpAsync();
        Assert.That(unjoined.State, Is.EqualTo(TransportState.Closed)); Assert.That(f.ConnectionCount, Is.Zero);
        var silent = f.Connect(102); await f.Engine.PumpAsync(); clock.Advance(11); await f.Engine.PumpAsync();
        Assert.That(silent.State, Is.EqualTo(TransportState.Closed)); Assert.That(f.ConnectionCount, Is.Zero);
        var joined = f.Connect(103); joined.Push(MessageId.LoginRequest, f.Login("p1")); await f.Engine.PumpAsync();
        joined.Push(MessageId.JoinRoomRequest, new JoinRoomRequest { SessionId = joined.Latest<LoginResponse>(MessageId.LoginResponse).SessionId });
        await f.Until(() => joined.Has(MessageId.JoinRoomResponse)); clock.Advance(11); await f.Engine.PumpAsync();
        Assert.That(joined.State, Is.EqualTo(TransportState.Connected)); Assert.That(f.ConnectionCount, Is.EqualTo(1));
    }

    [Test]
    public async Task ReplayQuotaRaceBecomesTerminalAllocationWithoutLeakingWorkerOrResultCapacity()
    {
        await using var f = await Fixture.Create(600, replay: true, maxReplayFiles: 1, control: true);
        MemoryAllocations store = new(f.Pool.Inventory().Rooms.Single());
        RoomCoordinator coordinator = new(store, new LocalBattle(f.Control!), TimeProvider.System); await coordinator.InitializeAsync();
        var report = f.Pool.Inventory("127.0.0.1:22000", true); // A cached positive report races with the exhausted replay budget.
        Assert.That((await coordinator.HandleAsync(new(ServiceRole.Battle, "node"), ServiceMethods.NodeReport, report.ToByteArray())).Success, Is.True);
        var request = new AllocationRequest { MatchId = "second", PlayerIds = { "p3", "p4" } };
        var released = (await coordinator.HandleAsync(new(ServiceRole.Match, "match"), ServiceMethods.Allocate, request.ToByteArray())).Read(RoomAllocation.Parser);
        Assert.That(released.State, Is.EqualTo("Released"));
        var retry = (await coordinator.HandleAsync(new(ServiceRole.Match, "match"), ServiceMethods.Allocate, request.ToByteArray())).Read(RoomAllocation.Parser);
        Assert.That(retry.RoomId, Is.EqualTo(released.RoomId)); Assert.That(retry.State, Is.EqualTo("Released"));
        Assert.That(f.Pool.Inventory().Rooms.Count, Is.EqualTo(1)); Assert.That(f.Outbox.ReservedCount, Is.EqualTo(1));
    }
    private sealed class LocalBattle(BattleControlService control) : IServiceRpc
    {
        public ValueTask<ServiceReply> CallAsync(ServiceTarget target, string method, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default, string playerId = "") =>
            control.HandleAsync(new(ServiceRole.Coordinator, "coordinator"), method, payload, cancellationToken);
    }
    private sealed class MemoryAllocations(RoomAllocation first) : IAllocationStore
    {
        private readonly Dictionary<string, RoomAllocation> _rooms = new() { [first.RoomId] = first.Clone() };
        public ValueTask<IReadOnlyList<RoomAllocation>> LoadAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<RoomAllocation>>(_rooms.Values.Select(x => x.Clone()).ToArray());
        public ValueTask SaveAsync(RoomAllocation room, CancellationToken cancellationToken = default) { _rooms[room.RoomId] = room.Clone(); return ValueTask.CompletedTask; }
    }
    private sealed class Clock : TimeProvider
    { private long _timestamp; public override long GetTimestamp() => _timestamp; public void Advance(int seconds) => _timestamp += seconds * TimestampFrequency; }
    [Test]
    public async Task AuthAckReconnectAndDurableCompletion()
    {
        await using var f = await Fixture.Create(120, replay: true);
        var bad = f.Connect(1); bad.Push(MessageId.JoinRoomRequest, new JoinRoomRequest { SessionId = 123 }); await f.Engine.PumpAsync(); Assert.That(bad.Output, Is.Empty);
        bad.Push(MessageId.LoginRequest, new LoginRequest { ProtocolMajor = 1, GlobalRoomId = "room", EntryTicket = f.Ticket("p1", "wrong") }); await f.Engine.PumpAsync(); Assert.That(bad.Output, Is.Empty);
        bad.Push(MessageId.LoginRequest, f.Login("outsider")); await f.Engine.PumpAsync(); Assert.That(bad.Output, Is.Empty);
        var a = f.Connect(2); var b = f.Connect(3);
        a.Push(MessageId.LoginRequest, f.Login("p1")); b.Push(MessageId.LoginRequest, f.Login("p2")); await f.Engine.PumpAsync();
        var session = a.Latest<LoginResponse>(MessageId.LoginResponse).SessionId;
        a.Push(MessageId.JoinRoomRequest, new JoinRoomRequest { SessionId = session }); b.Push(MessageId.JoinRoomRequest, new JoinRoomRequest { SessionId = b.Latest<LoginResponse>(MessageId.LoginResponse).SessionId });
        await f.Until(() => a.Has(MessageId.JoinRoomResponse) && b.Has(MessageId.JoinRoomResponse));
        var entity = a.Latest<JoinRoomResponse>(MessageId.JoinRoomResponse).EntityId; var tick = a.Latest<Snapshot>(MessageId.Snapshot).RoomTick;
        a.PacketEpoch = 999; a.Push(MessageId.InputCommand, new InputCommand { Sequence = 1, RoomTick = tick + 1, MoveXMilli = 1000 }); await f.Engine.PumpAsync(); a.PacketEpoch = 2; Assert.That(a.Latest<Snapshot>(MessageId.Snapshot).LastProcessedInputSequence, Is.Zero);
        a.Push(MessageId.InputBatch, new InputBatch { Commands = { new InputCommand { Sequence = 1, RoomTick = tick + 1, MoveXMilli = 1000 }, new InputCommand { Sequence = 2, RoomTick = tick + 1, MoveXMilli = 1000 } } });
        await f.Until(() => a.Latest<Snapshot>(MessageId.Snapshot).LastProcessedInputSequence == 2);
        var next = f.Connect(4); next.Push(MessageId.LoginRequest, f.Login("p1")); await f.Engine.PumpAsync(); Assert.That(next.Latest<LoginResponse>(MessageId.LoginResponse).SessionId, Is.EqualTo(session));
        next.Push(MessageId.ReconnectRequest, new ReconnectRequest { SessionId = session }); await f.Until(() => next.Has(MessageId.ReconnectResponse));
        Assert.That(next.Latest<ReconnectResponse>(MessageId.ReconnectResponse).Snapshot.Players.Any(p => p.EntityId == entity), Is.True);
        await f.Engine.PumpAsync(); Assert.That(a.State, Is.EqualTo(TransportState.Closed));
        await f.Until(() => next.Latest<Snapshot>(MessageId.Snapshot).MatchPhase == AiNative.Protocol.V1.ArenaMatchPhase.ArenaMatchFinished);
        await f.Engine.FlushResultsAsync(); await f.Engine.FlushResultsAsync(); Assert.That(f.Outbox.Pending.Count, Is.EqualTo(1)); Assert.That(f.Outbox.Pending[0].Players.Select(p => p.PlayerId), Is.EquivalentTo(new[] { "p1", "p2" })); Assert.That(f.Pool.Inventory().Rooms, Is.Empty);
        await f.Engine.PumpAsync();
        string replay = Directory.GetFiles(Path.Combine(f.DirectoryPath, "replays"), "*.anar").Single();
        var verified = ArenaReplayVerifier.Verify(replay, new("source", "fantasy", "protocol", "config"));
        Assert.That(verified.RoomId, Is.EqualTo("room")); Assert.That(verified.FinalTick, Is.GreaterThanOrEqualTo(120));
        if (Environment.GetEnvironmentVariable("AINATIVE_REPLAY_TEST_OUTPUT") is { Length: > 0 } output) File.Copy(replay, output, overwrite: true);
    }
    [Test]
    public async Task RepresentativeOwnerTickHasNoManagedAllocationAfterWarmup()
    {
        await using var f = await Fixture.Create(100000, replay: true);
        var a = f.Connect(10); a.Push(MessageId.LoginRequest, f.Login("p1")); await f.Engine.PumpAsync(); a.Push(MessageId.JoinRoomRequest, new JoinRoomRequest { SessionId = a.Latest<LoginResponse>(MessageId.LoginResponse).SessionId }); await f.Until(() => a.Has(MessageId.JoinRoomResponse));
        var done = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.That(f.Pool.TryPost(0, () => { for (int i = 0; i < 100; i++) f.Room!.Tick(); long before = GC.GetAllocatedBytesForCurrentThread(); for (int i = 0; i < 1000; i++) f.Room!.Tick(); done.SetResult(GC.GetAllocatedBytesForCurrentThread() - before); }), Is.True);
        Assert.That(await done.Task.WaitAsync(TimeSpan.FromSeconds(3)), Is.Zero);
    }
    [Test]
    public void SpscRingPreservesValuesAcrossConcurrentWraparound()
    {
        var ring = new TopologyBattleEngine.SpscRing<(int Sequence, int Check)>(16); const int count = 50000;
        var producer = Task.Run(() => { for (int i = 0; i < count; i++) { while (!ring.TryWrite((i, ~i))) Thread.Yield(); } });
        for (int i = 0; i < count; i++) { (int Sequence, int Check) value; while (!ring.TryRead(out value)) Thread.Yield(); Assert.That(value.Sequence, Is.EqualTo(i)); Assert.That(value.Check, Is.EqualTo(~i)); }
        producer.GetAwaiter().GetResult();
    }
    [TestCase(false)]
    [TestCase(true)]
    public void TripleFramesDoNotAliasWhileReaderIsDelayed(bool writerCompletesBeforeRead)
    {
        var frames = new TopologyBattleEngine.Frame[] { new(), new(), new() }; int finished = 0;
        var writer = Task.Run(() => { for (int tick = 1; tick <= 100000; tick++) { var f = TopologyBattleEngine.ClaimWritable(frames); if (f == null) { Thread.Yield(); continue; } f.Tick = (ulong)tick; for (int p = 0; p < 8; p++) f.Players[p].PositionXMillimetres = tick; Volatile.Write(ref f.State, 2); } Volatile.Write(ref finished, 1); });
        if (writerCompletesBeforeRead) writer.GetAwaiter().GetResult();
        int observed = 0;
        while (true)
        {
            // Observe completion before claiming so the last publication is drained.
            bool writerFinished = Volatile.Read(ref finished) != 0;
            var f = TopologyBattleEngine.ClaimLatest(frames);
            if (f == null) { if (writerFinished) break; Thread.Yield(); continue; }
            try { Thread.SpinWait(100); for (int p = 0; p < 8; p++) Assert.That(f.Players[p].PositionXMillimetres, Is.EqualTo((int)f.Tick)); observed++; }
            finally { Volatile.Write(ref f.State, 0); }
        }
        writer.GetAwaiter().GetResult(); Assert.That(observed, Is.GreaterThan(0));
        Assert.That(frames.All(f => f.State == 0), Is.True);
    }
    [Test]
    public async Task FullOutboxRetainsFinishedRoomUntilDurableAcceptance()
    {
        await using var f = await Fixture.Create(30);
        var a = f.Connect(1); var b = f.Connect(2); a.Push(MessageId.LoginRequest, f.Login("p1")); b.Push(MessageId.LoginRequest, f.Login("p2")); await f.Engine.PumpAsync();
        a.Push(MessageId.JoinRoomRequest, new JoinRoomRequest { SessionId = a.Latest<LoginResponse>(MessageId.LoginResponse).SessionId }); b.Push(MessageId.JoinRoomRequest, new JoinRoomRequest { SessionId = b.Latest<LoginResponse>(MessageId.LoginResponse).SessionId }); await f.Until(() => a.Has(MessageId.JoinRoomResponse));
        for (int i = 0; i < 10; i++) Assert.That(await f.Outbox.StoreAsync(new MatchResult { MatchId = "prior" + i }), Is.True);
        await f.Until(() => a.Latest<Snapshot>(MessageId.Snapshot).MatchPhase == AiNative.Protocol.V1.ArenaMatchPhase.ArenaMatchFinished);
        await f.Engine.FlushResultsAsync(); Assert.That(f.Pool.Inventory().Rooms.Count, Is.EqualTo(1)); Assert.That(f.Outbox.Pending.Any(r => r.MatchId == "match"), Is.False);
        var prior = f.Outbox.Pending[0]; await f.Outbox.ConfirmAsync(prior.MatchId, DurableResultOutbox.Hash(prior)); await f.Engine.FlushResultsAsync(); Assert.That(f.Pool.Inventory().Rooms, Is.Empty); Assert.That(f.Outbox.Pending.Count(r => r.MatchId == "match"), Is.EqualTo(1));
    }
    [Test]
    public async Task RejectedFinalSnapshotDoesNotAuthorizeRoomRelease()
    {
        await using var f = await Fixture.Create(30);
        var a = f.Connect(1); var b = f.Connect(2);
        a.RejectFinalSnapshots = true;
        a.Push(MessageId.LoginRequest, f.Login("p1")); b.Push(MessageId.LoginRequest, f.Login("p2"));
        await f.Engine.PumpAsync();
        a.Push(MessageId.JoinRoomRequest, new JoinRoomRequest { SessionId = a.Latest<LoginResponse>(MessageId.LoginResponse).SessionId });
        b.Push(MessageId.JoinRoomRequest, new JoinRoomRequest { SessionId = b.Latest<LoginResponse>(MessageId.LoginResponse).SessionId });
        await f.Until(() => a.RejectedFinalSnapshots > 0);
        await f.Engine.FlushResultsAsync();
        await f.Engine.PumpAsync();
        Assert.That(f.Pool.Inventory().Rooms.Count, Is.EqualTo(1), "A rejected terminal frame must not be treated as delivered.");
        Assert.That(a.State, Is.EqualTo(TransportState.Connected));
    }
    [Test]
    public async Task DurableResultPrecedesDrainAndReleaseOccursExactlyOnceAfterAcknowledgement()
    {
        await using var f = await Fixture.Create(30);
        var a = await JoinPlayer(f, "p1", 20); var b = await JoinPlayer(f, "p2", 21);
        var barrier = new TaskCompletionSource<FantasySendDrainStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        a.DrainCheck = barrier.Task;
        await f.Until(() => a.Latest<Snapshot>(MessageId.Snapshot).MatchPhase == AiNative.Protocol.V1.ArenaMatchPhase.ArenaMatchFinished);
        await f.Engine.FlushResultsAsync();
        Assert.That(f.Outbox.Pending.Count, Is.EqualTo(1));
        Assert.That(f.Pool.Inventory().Rooms.Count, Is.EqualTo(1));
        Assert.That(a.DisposeCount, Is.Zero);
        barrier.SetResult(FantasySendDrainStatus.Drained);
        await f.Engine.PumpAsync(); await f.Engine.FlushResultsAsync(); await f.Engine.PumpAsync();
        await f.Engine.FlushResultsAsync(); await f.Engine.PumpAsync();
        Assert.That(f.Pool.Inventory().Rooms, Is.Empty);
        Assert.That(f.Outbox.Pending.Count, Is.EqualTo(1));
        Assert.That(a.DisposeCount, Is.EqualTo(1)); Assert.That(b.DisposeCount, Is.EqualTo(1));
    }
    [Test]
    public async Task TerminalOutcomeCannotReleaseBeforeItsFailureCounterIsPublished()
    {
        await using var f = await Fixture.Create(30);
        var a = await JoinPlayer(f, "p1", 20); await JoinPlayer(f, "p2", 21);
        a.DrainCheck = new TaskCompletionSource<FantasySendDrainStatus>().Task;
        await f.Until(() => a.Latest<Snapshot>(MessageId.Snapshot).MatchPhase == AiNative.Protocol.V1.ArenaMatchPhase.ArenaMatchFinished);
        var terminal = (TerminalRoomDelivery)f.Room!.GetType().GetField("Terminal")!.GetValue(f.Room)!;
        // Reproduce the interleaving after outcome publication but before PumpTerminal records it.
        terminal.Fail();
        await f.Engine.FlushResultsAsync();
        Assert.That(f.Outbox.Pending.Count, Is.EqualTo(1));
        Assert.That(f.Pool.Inventory().Rooms.Count, Is.EqualTo(1));
        await f.Engine.PumpAsync(); await f.Engine.FlushResultsAsync();
        Assert.That(f.Pool.Inventory().Rooms, Is.Empty);
        Assert.That(JsonSerializer.SerializeToElement(f.Engine.Diagnostics()).GetProperty("terminalDeliveryFailures").GetInt64(), Is.EqualTo(1));
    }
    [Test]
    public async Task AbsoluteDrainTimeoutPersistsSettlementThenReleasesAndRecordsFailure()
    {
        var clock = new Clock(); await using var f = await Fixture.Create(30, clock: clock);
        var a = await JoinPlayer(f, "p1", 20); await JoinPlayer(f, "p2", 21);
        a.DrainCheck = new TaskCompletionSource<FantasySendDrainStatus>().Task;
        await f.Until(() => a.Latest<Snapshot>(MessageId.Snapshot).MatchPhase == AiNative.Protocol.V1.ArenaMatchPhase.ArenaMatchFinished);
        await f.Engine.FlushResultsAsync(); clock.Advance(4); await f.Engine.PumpAsync(); await f.Engine.FlushResultsAsync();
        Assert.That(f.Pool.Inventory().Rooms.Count, Is.EqualTo(1));
        clock.Advance(1); await f.Engine.PumpAsync(); await f.Engine.FlushResultsAsync(); await f.Engine.PumpAsync();
        Assert.That(f.Outbox.Pending.Count, Is.EqualTo(1)); Assert.That(f.Pool.Inventory().Rooms, Is.Empty);
        var diagnostics = JsonSerializer.SerializeToElement(f.Engine.Diagnostics());
        Assert.That(diagnostics.GetProperty("terminalDeliveryFailures").GetInt64(), Is.EqualTo(1));
        Assert.That(diagnostics.GetProperty("terminalDeliveryTimeouts").GetInt64(), Is.EqualTo(1));
        Assert.That(diagnostics.GetProperty("terminalDelivered").GetInt64(), Is.Zero);
    }
    [Test]
    public void SupersessionDropsUnconsumedInputWithoutAdvancingAckOrSequenceFloor()
    {
        var room = new AiNative.BattleHost.ArenaRoom(600); room.TryJoin(out uint entity);
        Assert.That(room.SubmitInput(entity, new AiNative.Gameplay.ArenaInput(10, 1, 0, 0, 0, 0, 0, 0)), Is.True);
        room.ClearPendingInputs(entity); room.TickOnce(); room.TryGetPlayer(entity, out var state); Assert.That(state.LastProcessedInputSequence, Is.Zero);
        Assert.That(room.SubmitInput(entity, new AiNative.Gameplay.ArenaInput(1, 2, 0, 0, 0, 0, 0, 0)), Is.True);
        Assert.That(room.RemainingTicks, Is.EqualTo(600)); Assert.That(room.ComputeStateHash(), Is.Not.EqualTo(new AiNative.BattleHost.ArenaRoom(601).ComputeStateHash()));
    }
    [Test]
    public void StableGameplayInputHashAndEventDrainDoNotAllocate()
    {
        var room = new AiNative.BattleHost.ArenaRoom(100000); room.TryJoin(out uint player); room.TryJoin(out _); var events = new AiNative.BattleHost.ArenaCombatEventRecord[256];
        for (uint seq = 1; seq <= 100; seq++) { room.SubmitInput(player, new AiNative.Gameplay.ArenaInput(seq, room.Tick + 1, 100, 0, 0, 0, 0, 0)); room.TickOnce(); room.DrainEvents(events); room.ComputeStateHash(); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (uint seq = 101; seq <= 1100; seq++) { room.SubmitInput(player, new AiNative.Gameplay.ArenaInput(seq, room.Tick + 1, 100, 0, 0, 0, 0, 0)); room.TickOnce(); room.DrainEvents(events); room.ComputeStateHash(); }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before; Assert.That(allocated, Is.Zero); room.TryGetPlayer(player, out var state); Assert.That(state.LastProcessedInputSequence, Is.EqualTo(1100));
    }
    [Test]
    public async Task ReplayDiskFailureClosesNewAdmissionButKeepsExistingRoomTicking()
    {
        await using var f = await Fixture.Create(100000, replay: true, invalidReplayPath: true);
        await f.Engine.PumpAsync(); Assert.That(f.Engine.ReplayHealthy, Is.False);
        Assert.That(f.Pool.Inventory().Draining, Is.True);
        Assert.Throws<InvalidOperationException>(() => f.Engine.CreateRoom(new RoomAllocation { AllocationId = "other", RoomId = "other", MatchId = "other", NodeId = "node", BootEpoch = "boot", PlayerIds = { "p1" } }));
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.That(f.Pool.TryPost(0, () => { f.Room!.Tick(); done.SetResult(); }), Is.True);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Test]
    public async Task ReplayOverflowPublishesOnlyIncompleteEvidence()
    {
        await using var f = await Fixture.Create(100000, replay: true, replayCapacity: 1);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.That(f.Pool.TryPost(0, () => { for (int i = 0; i < 5; i++) f.Room!.Tick(); done.SetResult(); }), Is.True);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(3)); await f.Engine.PumpAsync();
        Assert.That(f.Engine.IncompleteReplayCount, Is.EqualTo(1));
        string replay = Directory.GetFiles(Path.Combine(f.DirectoryPath, "replays"), "*.incomplete").Single();
        Assert.Throws<InvalidDataException>(() => ArenaReplayVerifier.Verify(replay, new("source", "fantasy", "protocol", "config")));
        Assert.That(Directory.GetFiles(Path.Combine(f.DirectoryPath, "replays"), "*.anar"), Is.Empty);
    }

    [Test]
    public async Task ReplayCountQuotaRejectsNewRoomWithoutFaultingExistingRoom()
    {
        await using var f = await Fixture.Create(600, replay: true, maxReplayFiles: 1);
        Assert.That(f.Engine.ReplayCanAdmit, Is.False); Assert.That(f.Engine.ReplayHealthy, Is.True);
        Assert.Throws<InvalidOperationException>(() => f.Engine.CreateRoom(new RoomAllocation { AllocationId = "second", RoomId = "second", MatchId = "second", NodeId = "node", BootEpoch = "boot", PlayerIds = { "p1" } }));
        Assert.That(f.Engine.ReplayFiles, Is.EqualTo(1)); Assert.That(f.Pool.Inventory().Draining, Is.False);
    }

    sealed class Fixture : IAsyncDisposable
    {
        public readonly RSA Key = RSA.Create(2048); public readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), "battle-test-" + Guid.NewGuid().ToString("N"));
        public FantasyKcpGateway Gateway = null!; public TopologyBattleEngine Engine = null!; public DurableResultOutbox Outbox = null!; public BattleWorkerPool Pool = null!; public IWorkerRoom? Room; public BattleControlService? Control;
        private ConcurrentDictionary<long, FantasyKcpConnection> Connections => (ConcurrentDictionary<long, FantasyKcpConnection>)typeof(FantasyKcpGateway).GetField("_connections", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Gateway)!;
        public int ConnectionCount => Connections.Count;
        public static async Task<Fixture> Create(int length, bool replay = false, bool invalidReplayPath = false, int replayCapacity = 4096, int maxReplayFiles = 1024, long maxReplayBytes = 2147483648L, TimeProvider? clock = null, bool control = false)
        {
            var f = new Fixture(); f.Gateway = new FantasyKcpGateway(); f.Outbox = new DurableResultOutbox(f.DirectoryPath, 10, 100000); await f.Outbox.InitializeAsync(); string replayPath = Path.Combine(f.DirectoryPath, "replays"); f.Engine = new TopologyBattleEngine(f.Gateway, new EntryTicketVerifier(f.Key, TimeProvider.System), f.Outbox, length, replay ? replayPath : null, replay ? new("source", "fantasy", "protocol", "config") : null, replayCapacity, maxReplayFiles, maxReplayBytes, clock); f.Pool = new BattleWorkerPool("node", "boot", 1, 2, 32, a => f.Room = f.Engine.CreateRoom(a)); f.Engine.Pool = f.Pool; if (control) f.Control = new(f.Pool, f.Outbox, "127.0.0.1:22000"); f.Pool.Fence("coord", "", true); f.Pool.Start(); var a = new RoomAllocation { AllocationId = "alloc", RoomId = "room", MatchId = "match", NodeId = "node", BootEpoch = "boot", CoordinatorEpoch = "coord", PlayerIds = { "p1", "p2" } }; Assert.That(f.Pool.Reserve(a), Is.Empty); Assert.That(await f.Pool.CreateAsync("room", "alloc", "boot", coordinatorEpoch: "coord"), Is.Empty); if (invalidReplayPath) File.WriteAllText(replayPath, "blocks directory"); return f;
        }
        public string Ticket(string player, string epoch = "boot") { var bytes = JsonSerializer.SerializeToUtf8Bytes(new { Kind = "entry", Player = player, Room = "room", Node = "node", Epoch = epoch, Expiry = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds() }); return Convert.ToBase64String(bytes) + "." + Convert.ToBase64String(Key.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)); }
        public LoginRequest Login(string player) => new() { ProtocolMajor = 1, GlobalRoomId = "room", EntryTicket = Ticket(player) };
        public FakeTransport Connect(uint epoch) { var fake = new FakeTransport(epoch); var wire = new FantasyKcpConnection(epoch, epoch, fake); Connections.TryAdd(epoch, wire); var accepted = (ConcurrentQueue<FantasyKcpConnection>)typeof(FantasyKcpGateway).GetField("_accepted", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Gateway)!; accepted.Enqueue(wire); return fake; }
        public async Task Until(Func<bool> condition) { for (int i = 0; i < 600; i++) { await Engine.PumpAsync(); if (condition()) return; await Task.Delay(5); } Assert.Fail("runtime response timeout"); }
        public async ValueTask DisposeAsync() { await Pool.DisposeAsync(); await Engine.DisposeAsync(); Key.Dispose(); System.IO.Directory.Delete(DirectoryPath, true); }
    }
    sealed class FakeTransport(uint epoch) : IRealtimeTransport, IFantasySendDrain
    {
        public bool RejectFinalSnapshots;
        public int RejectedFinalSnapshots;
        public Task<FantasySendDrainStatus> DrainCheck = Task.FromResult(FantasySendDrainStatus.Drained);
        public Task<FantasySendDrainStatus> CheckSendDrainAsync() => DrainCheck;
        public int DisposeCount;
        public uint PacketEpoch = epoch;
        readonly Queue<(byte[] Bytes, TransportChannel Channel)> _input = new(); public readonly List<(MessageId Id, byte[] Payload)> Output = new(); public TransportState State { get; private set; } = TransportState.Connected;
        public void Push(MessageId id, IMessage m) { byte[] bytes = new byte[2 + m.CalculateSize()]; BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)id); m.WriteTo(bytes.AsSpan(2)); uint channel = id is MessageId.InputCommand or MessageId.InputBatch ? 2u : 0u; _input.Enqueue((bytes, new TransportChannel((byte)channel, channel == 2 ? TransportDelivery.Unreliable : TransportDelivery.Reliable, channel == 2 ? TransportOrdering.Sequenced : TransportOrdering.Ordered))); }
        public bool TryReceive(Span<byte> destination, out ReceivedPacket packet) { if (!_input.TryDequeue(out var item)) { packet = default; return false; } item.Bytes.CopyTo(destination); packet = new ReceivedPacket(item.Channel, item.Bytes.Length, item.Bytes.Length, 1, PacketEpoch); return true; }
        public ValueTask<SendResult> SendAsync(TransportChannel c, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
        {
            var id = (MessageId)BinaryPrimitives.ReadUInt16LittleEndian(payload.Span);
            if (RejectFinalSnapshots && id == MessageId.Snapshot && Snapshot.Parser.ParseFrom(payload.Span[2..]).MatchPhase == AiNative.Protocol.V1.ArenaMatchPhase.ArenaMatchFinished)
            {
                RejectedFinalSnapshots++;
                return ValueTask.FromResult(new SendResult(SendStatus.WouldBlock));
            }
            Output.Add((id, payload.Span[2..].ToArray()));
            return ValueTask.FromResult(new SendResult(SendStatus.Accepted, payload.Length));
        }
        public bool Has(MessageId id) => Output.Any(x => x.Id == id);
        public T Latest<T>(MessageId id) where T : IMessage<T>, new() => new MessageParser<T>(() => new T()).ParseFrom(Output.Last(x => x.Id == id).Payload);
        public ValueTask DisposeAsync() { DisposeCount++; State = TransportState.Closed; return ValueTask.CompletedTask; }
    }
}
