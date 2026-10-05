using AiNative.Realtime;
using AiNative.Server.Battle;
using AiNative.Server.Fantasy;
using NUnit.Framework;

namespace AiNative.Server.Battle.Tests;

public sealed class TerminalRoomDeliveryTests
{
    [Test]
    public void ActiveFrameCannotConsumeEventsEnqueuedByANewerTerminalTick()
    {
        var events = new TopologyBattleEngine.SpscRing<AiNative.BattleHost.ArenaCombatEventRecord>(256);
        Assert.That(events.TryWrite(default(AiNative.BattleHost.ArenaCombatEventRecord) with { Tick = 10 }), Is.True);
        Assert.That(events.TryWrite(default(AiNative.BattleHost.ArenaCombatEventRecord) with { Tick = 11 }), Is.True);
        Assert.That(TopologyBattleEngine.TryReadPublishedEvent(events, 10, out var active), Is.True);
        Assert.That(active.Tick, Is.EqualTo(10));
        Assert.That(TopologyBattleEngine.TryReadPublishedEvent(events, 10, out _), Is.False);
        Assert.That(events.Count, Is.EqualTo(1));
        Assert.That(events.TryRead(out var terminal), Is.True); Assert.That(terminal.Tick, Is.EqualTo(11));
    }
    static readonly TransportChannel Channel = new(0, TransportDelivery.Reliable, TransportOrdering.Ordered);
    static TerminalRoomDelivery Create(Clock clock, params Wire[] wires) => new(clock,
        wires.Select(wire => new TerminalRoomDelivery.Peer(wire, [new(Channel, [1]), new(Channel, [2])])).ToArray());

    [Test]
    public void AdmissionDoesNotAuthorizeReleaseBeforeTheSceneBarrierAndKcpAcknowledgement()
    {
        var clock = new Clock(); var wire = new Wire(); var delivery = Create(clock, wire);
        delivery.Pump();
        Assert.That(wire.Accepted, Is.EqualTo(new byte[] { 1, 2 }));
        Assert.That(delivery.State, Is.EqualTo(TerminalDeliveryState.Pending));
        wire.Check.SetResult(FantasySendDrainStatus.Pending); delivery.Pump();
        wire.Check = new(TaskCreationOptions.RunContinuationsAsynchronously); delivery.Pump();
        Assert.That(delivery.State, Is.EqualTo(TerminalDeliveryState.Pending));
        wire.Check.SetResult(FantasySendDrainStatus.Drained); delivery.Pump();
        Assert.That(delivery.State, Is.EqualTo(TerminalDeliveryState.Drained));
        Assert.That(wire.Accepted, Is.EqualTo(new byte[] { 1, 2 }), "Do not resend accepted terminal packets while waiting for ACK.");
    }

    [Test]
    public void BackpressureRetriesOnlyTheUnacceptedPacketAndDoesNotStartDrainEarly()
    {
        var wire = new Wire { BlockPacket = 2 }; var delivery = Create(new(), wire);
        delivery.Pump(); delivery.Pump();
        Assert.That(wire.Accepted, Is.EqualTo(new byte[] { 1 }));
        Assert.That(wire.CheckCount, Is.Zero);
        wire.BlockPacket = 0; wire.Check.SetResult(FantasySendDrainStatus.Drained); delivery.Pump();
        Assert.That(delivery.State, Is.EqualTo(TerminalDeliveryState.Drained));
        Assert.That(wire.Accepted, Is.EqualTo(new byte[] { 1, 2 }));
    }

    [Test]
    public void EveryCurrentPeerMustDrainBeforeSuccess()
    {
        var a = new Wire(); var b = new Wire(); var delivery = Create(new(), a, b);
        a.Check.SetResult(FantasySendDrainStatus.Drained); delivery.Pump();
        Assert.That(delivery.State, Is.EqualTo(TerminalDeliveryState.Pending));
        b.Check.SetResult(FantasySendDrainStatus.Drained); delivery.Pump();
        Assert.That(delivery.State, Is.EqualTo(TerminalDeliveryState.Drained));
    }

    [Test]
    public void FiveSecondDeadlineIsAbsoluteAndDoesNotWaitForAnUnresponsiveScene()
    {
        var clock = new Clock(); var wire = new Wire(); var delivery = Create(clock, wire);
        delivery.Pump(); clock.Advance(4.999); delivery.Pump();
        Assert.That(delivery.State, Is.EqualTo(TerminalDeliveryState.Pending));
        Assert.That(wire.CheckCount, Is.EqualTo(1), "Only one outstanding barrier is owned per peer.");
        clock.Advance(.001); delivery.Pump();
        Assert.That(delivery.State, Is.EqualTo(TerminalDeliveryState.TimedOut));
        wire.Check.SetResult(FantasySendDrainStatus.Drained); delivery.Pump();
        Assert.That(delivery.State, Is.EqualTo(TerminalDeliveryState.TimedOut));
    }

    [Test]
    public void RejectedSendRetriesDoNotRenewTheDeadline()
    {
        var clock = new Clock(); var wire = new Wire { BlockPacket = 1 }; var delivery = Create(clock, wire);
        for (int i = 0; i < 5; i++) { delivery.Pump(); clock.Advance(1); }
        delivery.Pump();
        Assert.That(delivery.State, Is.EqualTo(TerminalDeliveryState.TimedOut));
        Assert.That(wire.Accepted, Is.Empty); Assert.That(wire.CheckCount, Is.Zero);
    }

    [TestCase(TransportState.Closed)]
    [TestCase(TransportState.Faulted)]
    public void DisconnectedPeerFailsInsteadOfBeingCountedAsDrained(TransportState state)
    {
        var wire = new Wire { State = state }; var delivery = Create(new(), wire); delivery.Pump();
        Assert.That(delivery.State, Is.EqualTo(TerminalDeliveryState.Failed)); Assert.That(wire.Accepted, Is.Empty);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void FailedOrFaultedBarrierCannotBecomeSuccessful(bool faulted)
    {
        var wire = new Wire(); var delivery = Create(new(), wire); delivery.Pump();
        if (faulted) wire.Check.SetException(new InvalidOperationException()); else wire.Check.SetResult(FantasySendDrainStatus.Failed);
        delivery.Pump(); delivery.Pump();
        Assert.That(delivery.State, Is.EqualTo(TerminalDeliveryState.Failed));
    }

    [Test]
    public void CompletedDeliveryDoesNotSendOrCheckAgain()
    {
        var wire = new Wire(); wire.Check.SetResult(FantasySendDrainStatus.Drained);
        var delivery = Create(new(), wire); delivery.Pump(); delivery.Pump();
        Assert.That(delivery.State, Is.EqualTo(TerminalDeliveryState.Drained));
        Assert.That(wire.CheckCount, Is.EqualTo(1)); Assert.That(wire.Accepted, Has.Count.EqualTo(2));
    }

    sealed class Clock : TimeProvider
    {
        long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public void Advance(double seconds) => ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }
    sealed class Wire : IRealtimeTransport, IFantasySendDrain
    {
        public TransportState State { get; set; } = TransportState.Connected;
        public byte BlockPacket;
        public List<byte> Accepted = [];
        public int CheckCount;
        public TaskCompletionSource<FantasySendDrainStatus> Check = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<FantasySendDrainStatus> CheckSendDrainAsync() { CheckCount++; return Check.Task; }
        public ValueTask<SendResult> SendAsync(TransportChannel channel, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            if (payload.Span[0] == BlockPacket) return ValueTask.FromResult(new SendResult(SendStatus.WouldBlock));
            Accepted.Add(payload.Span[0]); return ValueTask.FromResult(new SendResult(SendStatus.Accepted, payload.Length));
        }
        public bool TryReceive(Span<byte> destination, out ReceivedPacket packet) { packet = default; return false; }
        public ValueTask DisposeAsync() { State = TransportState.Closed; return ValueTask.CompletedTask; }
    }
}
