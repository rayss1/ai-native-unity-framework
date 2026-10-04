using System.Reflection;
using System.Text.Json;
using NUnit.Framework;
using AiNative.Protocol.V1;
using AiNative.Realtime;
using AiNative.Protocol.Backend.V1;

namespace AiNative.Topology.ToolTests;

public class CapacityQualificationTests
{
    [TestCase(1, true)]
    [TestCase(2, false)]
    public async Task SettlementOpensFreshConnectionPreservesOriginalIdentityAndDisposesEvenOnDuplicate(int played, bool accepted)
    {
        var expired = new ProfileConnection { Disposed = true }; var fresh = new ProfileConnection();
        int opened = 0;
        Func<CancellationToken, Task<IAsyncDisposable>> open = _ => { opened++; return Task.FromResult<IAsyncDisposable>(fresh); };
        Func<IAsyncDisposable, string, string, CancellationToken, ValueTask<PlayerProfile?>> read = (connection, player, token, _) =>
        {
            Assert.That(connection, Is.SameAs(fresh)); Assert.That(player, Is.EqualTo("original-player")); Assert.That(token, Is.EqualTo("original-session"));
            var session = (ProfileConnection)connection; if (session.Disposed) throw new InvalidOperationException("expired-session");
            session.Reads++; return ValueTask.FromResult<PlayerProfile?>(new PlayerProfile { PlayerId = player, Played = played });
        };
        Exception? error = null;
        try { await (Task)Feature("QualificationSettlement").GetMethod("VerifyOnceAsync")!.Invoke(null, ["original-player", "original-session", open, read, CancellationToken.None])!; }
        catch (Exception failure) { error = failure; }
        Assert.That(error is null, Is.EqualTo(accepted));
        if (!accepted) Assert.That(error!.Message, Is.EqualTo("settlement-count-exceeds-one"));
        Assert.That(opened, Is.EqualTo(1)); Assert.That(fresh.Reads, Is.GreaterThan(0));
        Assert.That(fresh.Disposals, Is.EqualTo(1)); Assert.That(expired.Reads, Is.Zero);
    }

    sealed class ProfileConnection : IAsyncDisposable
    {
        public bool Disposed; public int Reads, Disposals;
        public ValueTask DisposeAsync() { Disposed = true; Disposals++; return ValueTask.CompletedTask; }
    }
    [TestCase(SendStatus.WouldBlock, TransportState.Connected)]
    [TestCase(SendStatus.Closed, TransportState.Closed)]
    public async Task InputSendFailureRetainsExactStatusAndBoundedQueueDiagnostics(SendStatus status, TransportState state)
    {
        using CancellationTokenSource stop = new();
        var transport = new DiagnosticTransport(status, state, stop);
        MethodInfo send = Feature("QualificationInputSend").GetMethod("SendAsync")!;
        Exception? error = null;
        try { await (ValueTask<SendResult>)send.Invoke(null, [transport, new TransportChannel(0, TransportDelivery.Unreliable, TransportOrdering.Sequenced), new ReadOnlyMemory<byte>(new byte[40]), stop.Token])!; }
        catch (Exception failure) { error = failure; }
        Assert.That(error, Is.Not.Null);
        JsonElement details = (JsonElement)error!.GetType().GetProperty("Details")!.GetValue(error)!;
        Assert.That(details.GetProperty("sendStatus").GetString(), Is.EqualTo(status.ToString()));
        Assert.That(details.GetProperty("transportState").GetString(), Is.EqualTo(state.ToString()));
        Assert.That(details.GetProperty("pendingOutboundPackets").GetInt32(), Is.EqualTo(17));
        Assert.That(details.GetProperty("pendingOutboundBytes").GetInt32(), Is.EqualTo(680));
        Assert.That(details.GetProperty("payloadBytes").GetInt32(), Is.EqualTo(40));
    }

    [Test]
    public void CancellationDuringSendRemainsCancellationInsteadOfGenericLoadFailure()
    {
        using CancellationTokenSource stop = new();
        var transport = new DiagnosticTransport(SendStatus.DroppedByPolicy, TransportState.Connected, stop, cancel: true);
        MethodInfo send = Feature("QualificationInputSend").GetMethod("SendAsync")!;
        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await (ValueTask<SendResult>)send.Invoke(null, [transport, new TransportChannel(0, TransportDelivery.Unreliable, TransportOrdering.Sequenced), new ReadOnlyMemory<byte>(new byte[40]), stop.Token])!);
    }

    sealed class DiagnosticTransport(SendStatus status, TransportState state, CancellationTokenSource stop, bool cancel = false) : IRealtimeTransport
    {
        readonly DiagnosticSender _sender = new();
        public TransportState State => state;
        public ValueTask<SendResult> SendAsync(TransportChannel channel, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        { GC.KeepAlive(_sender); if (cancel) stop.Cancel(); return ValueTask.FromResult(new SendResult(status)); }
        public bool TryReceive(Span<byte> destination, out ReceivedPacket packet) { packet = default; return false; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    sealed class DiagnosticSender
    { public int PendingOutboundPackets => 17; public int PendingOutboundBytes => 680; public bool IsClosed => false; }
    static Type Feature(string name)
    {
        Type? type = Assembly.Load("AiNative.TopologyAcceptance").GetType(name);
        Assert.That(type, Is.Not.Null, $"Missing capacity behavior: {name}");
        return type!;
    }

    [Test]
    public void QualificationOptionsUseActualDensityAndEightPlayerSixtyHertzLoad()
    {
        Func<string, string?> env = key => key switch
        {
            "AINATIVE_QUALIFICATION_ROOM_COUNT" => "16",
            "AINATIVE_QUALIFICATION_ROOMS_PER_WORKER" => "4",
            "AINATIVE_QUALIFICATION_DURATION_SECONDS" => "3600",
            _ => null
        };
        dynamic options = Feature("ArenaCapacityOptions").GetMethod("Load")!.Invoke(null, [env])!;
        Assert.Multiple(() =>
        {
            Assert.That((int)options.RoomCount, Is.EqualTo(16));
            Assert.That((int)options.PlayersPerRoom, Is.EqualTo(8));
            Assert.That((int)options.InputHz, Is.EqualTo(60));
            Assert.That((int)options.WarmupSeconds, Is.EqualTo(60));
            Assert.That((int)options.DurationSeconds, Is.EqualTo(3600));
            Assert.That((int)options.RoomsPerWorker, Is.EqualTo(4));
        });
    }

    [TestCase("AINATIVE_QUALIFICATION_INPUT_HZ", "0")]
    [TestCase("AINATIVE_QUALIFICATION_INPUT_HZ", "61")]
    [TestCase("AINATIVE_QUALIFICATION_PLAYERS_PER_ROOM", "9")]
    [TestCase("AINATIVE_QUALIFICATION_ROOM_COUNT", "7")]
    [TestCase("AINATIVE_QUALIFICATION_WARMUP_SECONDS", "0")]
    public void InvalidQualificationCannotStartRuntime(string key, string value)
    {
        Func<string, string?> env = k => k == key ? value : null;
        var error = Assert.Throws<TargetInvocationException>(() => Feature("ArenaCapacityOptions").GetMethod("Load")!.Invoke(null, [env]));
        Assert.That(error!.InnerException, Is.TypeOf<ArgumentException>());
    }

    static JsonElement Worker(long tick, long[] ids, long[] micros, long allocated = 0, long[]? roomBytes = null,
        long[]? mailboxBytes = null, long[]? transitions = null) => JsonSerializer.SerializeToElement(new
    { tickCount = tick, sampleTickIds = ids, recentTickMicros = micros, allocatedBytes = allocated, overBudgetTicks = 0,
        sampleRoomAllocatedBytes = roomBytes ?? new long[ids.Length], sampleMailboxAllocatedBytes = mailboxBytes ?? new long[ids.Length],
        sampleLifecycleOperations = transitions ?? new long[ids.Length], sampleRoomMicros = micros });

    [Test]
    public void WorkerWindowMergesExactIdsAndExcludesWarmupSamples()
    {
        dynamic window = Activator.CreateInstance(Feature("QualificationWorkerWindow"))!;
        window.Observe(Worker(10, [9, 10], [99999, 99999]), false);
        window.Observe(Worker(13, [10, 11, 12, 13], [99999, 10, 20, 30]), false);
        window.Observe(Worker(15, [12, 13, 14, 15], [20, 30, 40, 50]), false);
        JsonElement report = window.Report();
        Assert.That(report.GetProperty("complete").GetBoolean(), Is.True);
        Assert.That(report.GetProperty("sampleCount").GetInt32(), Is.EqualTo(5));
        Assert.That(report.GetProperty("p99Micros").GetInt64(), Is.EqualTo(50));
    }

    [Test]
    public void MissingTickWindowCannotPassCapacityGate()
    {
        dynamic window = Activator.CreateInstance(Feature("QualificationWorkerWindow"))!;
        window.Observe(Worker(10, [10], [1]), false);
        window.Observe(Worker(14, [11, 13, 14], [1, 1, 1]), false);
        JsonElement report = window.Report();
        Assert.That(report.GetProperty("complete").GetBoolean(), Is.False);
        Assert.That(report.GetProperty("passed").GetBoolean(), Is.False);
    }

    [Test]
    public void StableAllocationFailsWhileLifecycleAllocationIsReportedSeparately()
    {
        dynamic window = Activator.CreateInstance(Feature("QualificationWorkerWindow"))!;
        window.Observe(Worker(10, [10], [1], 100), false);
        window.Observe(Worker(12, [11, 12], [1, 1], 200, mailboxBytes: [100, 0], transitions: [1, 0]), true);
        window.Observe(Worker(14, [13, 14], [1, 1], 201, roomBytes: [0, 1]), false);
        JsonElement report = window.Report();
        Assert.That(report.GetProperty("lifecycleAllocatedBytes").GetInt64(), Is.EqualTo(100));
        Assert.That(report.GetProperty("steadyAllocatedBytes").GetInt64(), Is.EqualTo(1));
        Assert.That(report.GetProperty("passed").GetBoolean(), Is.False);
    }

    [Test]
    public void LifecycleMarkerCannotHideBusinessTickAllocation()
    {
        dynamic window = Activator.CreateInstance(Feature("QualificationWorkerWindow"))!;
        window.Observe(Worker(10, [10], [1]), false);
        window.Observe(Worker(12, [11, 12], [1, 1], 64, roomBytes: [64, 0], transitions: [1, 0]), true);
        window.Observe(Worker(14, [13, 14], [1, 1], 64), false);
        JsonElement report = window.Report();
        Assert.That(report.GetProperty("passed").GetBoolean(), Is.False);
        Assert.That(report.GetProperty("businessAllocatedBytes").GetInt64(), Is.EqualTo(64));
    }

    [Test]
    public void BusinessTickMustKeepGameplayBudgetHeadroomEvenWhenFullTickFits()
    {
        dynamic window = Activator.CreateInstance(Feature("QualificationWorkerWindow"))!;
        window.Observe(Worker(10, [10], [1]), false);
        window.Observe(Worker(12, [11, 12], [6500, 6500]), false);
        Assert.That(((JsonElement)window.Report()).GetProperty("passed").GetBoolean(), Is.False);
    }

    [Test]
    public void CpuAndMemoryMustRetainTwentyPercentHeadroom()
    {
        MethodInfo gate = Feature("QualificationResourceGate").GetMethod("Passes")!;
        Assert.That((bool)gate.Invoke(null, [80d, 800L, 1000L])!, Is.True);
        Assert.That((bool)gate.Invoke(null, [80.01d, 800L, 1000L])!, Is.False);
        Assert.That((bool)gate.Invoke(null, [80d, 801L, 1000L])!, Is.False);
        Assert.That((bool)gate.Invoke(null, [0d, 0L, 0L])!, Is.False);
    }

    [Test]
    public void AimUsesActualArenaLookDeltaIncludingElevationAndYawWrap()
    {
        var method = Feature("QualificationAim").GetMethod("LookDelta")!;
        var self = new PlayerState { YawMillidegrees = 350000 };
        var enemy = new PlayerState { PositionXMilli = 1000, PositionYMilli = 1000 };
        var delta = ((int Yaw, int Pitch))method.Invoke(null, [self, enemy])!;
        Assert.That(delta.Yaw, Is.EqualTo(100000));
        Assert.That(delta.Pitch, Is.EqualTo(45000));
    }

    [Test]
    public void CombatCountsRealFireAndAmmunitionExhaustionThenRespawnResetsIt()
    {
        dynamic counters = Activator.CreateInstance(Feature("QualificationCombatEvidence"), new object[] { new uint[] { 1 } })!;
        for (uint seq = 1; seq <= 40; seq++)
            counters.Observe(new ReliableEvent { Sequence = seq, EventType = 0,
                CombatEvent = new ArenaCombatEvent { SourceEntityId = 1, WeaponId = ArenaWeaponId.ArenaWeaponMachinegun } }, true);
        Assert.That((bool)counters.AmmoExhausted(1u, 1u), Is.True);
        JsonElement report = counters.Report();
        Assert.That(report.GetProperty("measuredFire").GetInt64(), Is.EqualTo(40));
        counters.Observe(new ReliableEvent { Sequence = 41, EventType = 3,
            CombatEvent = new ArenaCombatEvent { EventType = ArenaCombatEventType.ArenaEventRespawn, SourceEntityId = 1 } }, true);
        Assert.That((bool)counters.AmmoExhausted(1u, 1u), Is.False);
    }

    [Test]
    public void EightPlayerQualificationKeepsRealCombatAfterWarmupWithFiniteAmmo()
    {
        Type type = Assembly.Load("AiNative.Server.Battle").GetType("AiNative.BattleHost.ArenaRoom")!;
        object room = Activator.CreateInstance(type, [36000])!;
        var join = type.GetMethod("TryJoin")!; var submit = type.GetMethod("SubmitInput")!;
        var tick = type.GetMethod("TickOnce")!; var aim = Feature("QualificationAim").GetMethod("LookDelta")!;
        var move = Feature("QualificationAim").GetMethod("MoveToward")!;
        uint[] entities = new uint[8];
        for (int i = 0; i < 8; i++) { object?[] args = [0u]; Assert.That(join.Invoke(room, args), Is.True); entities[i] = (uint)args[0]!; }
        var states = (AiNative.Gameplay.ArenaPlayerState[])type.GetField("_players", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(room)!;
        var events = (System.Collections.IList)type.GetField("_events", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(room)!;
        var random = new Random(20261002); long[] counts = new long[6]; long[] playerFire = new long[8];
        uint sequence = 0; int rotations = 0;
        PlayerState State(int i) => new() { EntityId = entities[i], Alive = states[i].Alive, PositionXMilli = states[i].PositionXMillimetres,
            PositionYMilli = states[i].PositionYMillimetres, PositionZMilli = states[i].PositionZMillimetres,
            YawMillidegrees = states[i].YawMillidegrees, PitchMillidegrees = states[i].PitchMillidegrees };
        for (uint elapsed = 1; elapsed <= 72000; elapsed++)
        {
            if (type.GetProperty("Phase")!.GetValue(room)!.ToString() == "Finished")
            {
                room = Activator.CreateInstance(type, [36000])!; rotations++; sequence = 0;
                for (int i = 0; i < 8; i++) { object?[] args = [0u]; join.Invoke(room, args); entities[i] = (uint)args[0]!; }
                states = (AiNative.Gameplay.ArenaPlayerState[])type.GetField("_players", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(room)!;
                events = (System.Collections.IList)type.GetField("_events", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(room)!;
                random = new Random(unchecked(20261002 + rotations * 104729));
            }
            sequence++;
            for (int i = 0; i < 8; i++)
            {
                var self = State(i); var enemy = Enumerable.Range(0, 8).Where(n => n != i && states[n].Alive)
                    .Select(State).OrderBy(p => Math.Abs((long)p.PositionXMilli-self.PositionXMilli)+Math.Abs((long)p.PositionZMilli-self.PositionZMilli)).FirstOrDefault();
                var look = enemy is null ? (0, 0) : ((int, int))aim.Invoke(null, [self, enemy])!;
                var movement = enemy is null ? (0, 0) : ((int, int))move.Invoke(null, [self, enemy, random.Next(2) == 0 ? 250 : -250])!;
                var input = new AiNative.Gameplay.ArenaInput(sequence, sequence, movement.Item1,
                    movement.Item2, look.Item1, look.Item2, AiNative.Gameplay.ArenaButtons.Fire, (AiNative.Gameplay.ArenaWeaponId)(1 + sequence / 240 % 3));
                submit.Invoke(room, [entities[i], input]);
            }
            tick.Invoke(room, null);
            foreach (object e in events)
            {
                int kind = Convert.ToInt32(e.GetType().GetProperty("Kind")!.GetValue(e));
                if (elapsed > 3600) { counts[kind]++; if (kind == 0) playerFire[Array.IndexOf(entities, (uint)e.GetType().GetProperty("SourceEntityId")!.GetValue(e)!)]++; }
            }
            events.Clear();
        }
        TestContext.WriteLine("Measured combat after 60s warmup: " + string.Join(",", counts) + "; player fire=" + string.Join(",", playerFire) + "; score/time rotations=" + rotations);
        Assert.That(rotations, Is.GreaterThan(0));
        Assert.That(counts[0], Is.GreaterThan(0), "Finite ammunition must not leave measurement idle");
        Assert.That(counts[1], Is.GreaterThan(0)); Assert.That(counts[2], Is.GreaterThan(0)); Assert.That(counts[3], Is.GreaterThan(0));
        Assert.That(playerFire, Has.All.GreaterThan(0));
    }
}
