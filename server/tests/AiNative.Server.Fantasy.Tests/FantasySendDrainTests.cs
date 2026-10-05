using AiNative.Realtime;
using AiNative.Server.Fantasy;
using NUnit.Framework;

namespace AiNative.Server.Fantasy.Tests;

public sealed class FantasySendDrainTests
{
    private static readonly TransportChannel Snapshot = new(1, TransportDelivery.Unreliable, TransportOrdering.Sequenced);

    [Test]
    public async Task CheckRunsAfterSceneBarrierAndWaitsForKcpAcknowledgement()
    {
        Dispatcher dispatcher = new();
        using FantasySessionSender sender = new(dispatcher);
        Assert.That(sender.Send(Snapshot, new byte[] { 1 }), Is.EqualTo(SendStatus.Accepted));
        Task<FantasySendDrainStatus> check = sender.CheckSendDrainAsync();
        Assert.That(check.IsCompleted, Is.False);
        Assert.That(dispatcher.Reads, Is.Zero);
        dispatcher.RunOne(); // Adapter drain hands the accepted packet to KCP.
        Assert.That(sender.PendingOutboundPackets, Is.Zero);
        Assert.That(check.IsCompleted, Is.False);
        dispatcher.RunOne();
        Assert.That(await check, Is.EqualTo(FantasySendDrainStatus.Pending));
        dispatcher.KcpPending = 0; // Explicit peer ACK, not merely adapter queue removal.
        check = sender.CheckSendDrainAsync();
        dispatcher.RunOne();
        Assert.That(await check, Is.EqualTo(FantasySendDrainStatus.Drained));
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task PacketQueuedBehindBarrierPreventsFalseDrained(bool snapshot)
    {
        Dispatcher dispatcher = new();
        using FantasySessionSender sender = new(dispatcher);
        Task<FantasySendDrainStatus> check = sender.CheckSendDrainAsync();
        sender.Send(snapshot ? Snapshot : new TransportChannel(2, TransportDelivery.Reliable, TransportOrdering.Ordered), new byte[] { 2 });
        dispatcher.RunOne();
        Assert.That(await check, Is.EqualTo(FantasySendDrainStatus.Pending));
        Assert.That(dispatcher.Reads, Is.Zero);
    }

    [Test]
    public async Task DisposeCompletesOutstandingCheckWithoutSceneProgress()
    {
        Dispatcher dispatcher = new();
        FantasySessionSender sender = new(dispatcher);
        Task<FantasySendDrainStatus> check = sender.CheckSendDrainAsync();
        sender.Dispose();
        Assert.That(await check, Is.EqualTo(FantasySendDrainStatus.Failed));
        dispatcher.RunAll();
        Assert.That(await sender.CheckSendDrainAsync(), Is.EqualTo(FantasySendDrainStatus.Failed));
    }

    [TestCase(true, false, false)]
    [TestCase(false, true, false)]
    [TestCase(false, false, true)]
    public async Task ClosedContractFailureAndReadExceptionFailClosed(bool closed, bool invalid, bool throws)
    {
        Dispatcher dispatcher = new() { InvalidContract = invalid, ThrowOnRead = throws };
        using FantasySessionSender sender = new(dispatcher);
        var check = sender.CheckSendDrainAsync();
        dispatcher.IsClosed = closed;
        dispatcher.RunAll();
        Assert.That(await check, Is.EqualTo(FantasySendDrainStatus.Failed));
    }

    [Test]
    public async Task RejectedScenePostFailsWithoutHanging()
    {
        Dispatcher dispatcher = new() { RejectPost = true };
        using FantasySessionSender sender = new(dispatcher);
        Assert.That(await sender.CheckSendDrainAsync(), Is.EqualTo(FantasySendDrainStatus.Failed));
    }

    [Test]
    public async Task ThrowingScenePostFailsWithoutHanging()
    {
        Dispatcher dispatcher = new() { ThrowOnPost = true };
        using FantasySessionSender sender = new(dispatcher);
        Assert.That(await sender.CheckSendDrainAsync(), Is.EqualTo(FantasySendDrainStatus.Failed));
    }

    [Test]
    public async Task RepeatedChecksShareOneOutstandingBarrier()
    {
        Dispatcher dispatcher = new();
        using FantasySessionSender sender = new(dispatcher);
        var first = sender.CheckSendDrainAsync();
        Assert.That(sender.CheckSendDrainAsync(), Is.SameAs(first));
        dispatcher.RunOne();
        Assert.That(await first, Is.EqualTo(FantasySendDrainStatus.Drained));
        Assert.That(dispatcher.Reads, Is.EqualTo(1));
    }

    [Test]
    public async Task UnsupportedDispatcherFailsWithoutPosting()
    {
        using FantasySessionSender sender = new(new UnsupportedDispatcher());
        Assert.That(await sender.CheckSendDrainAsync(), Is.EqualTo(FantasySendDrainStatus.Failed));
    }

    [Test]
    public async Task TransportDelegatesAndUnsupportedSenderFails()
    {
        await using var unsupported = new FantasyRealtimeTransport(new UnsupportedSender());
        Assert.That(await unsupported.CheckSendDrainAsync(), Is.EqualTo(FantasySendDrainStatus.Failed));
        Dispatcher dispatcher = new();
        await using var transport = new FantasyRealtimeTransport(new FantasySessionSender(dispatcher));
        var check = transport.CheckSendDrainAsync();
        Assert.That(check.IsCompleted, Is.False);
        dispatcher.RunAll();
        Assert.That(await check, Is.EqualTo(FantasySendDrainStatus.Drained));
    }

    [Test]
    public void PinnedVendorReflectionContractIsAvailable() =>
        Assert.That(FantasyKcpSendDrainObservation.IsContractAvailable, Is.True);

    private sealed class UnsupportedSender : IFantasySessionSender
    {
        public bool IsClosed => false;
        public SendStatus Send(TransportChannel channel, ReadOnlySpan<byte> payload) => SendStatus.Accepted;
        public void Dispose() { }
    }

    private sealed class UnsupportedDispatcher : IFantasyOutboundDispatcher
    {
        public bool IsClosed => false;
        public void Post(Action action) { }
        public void Send(FantasyRealtimeEnvelope envelope) => envelope.Dispose();
        public void DisposeSession() { }
    }

    private sealed class Dispatcher : IFantasyOutboundDispatcher, IFantasySendDrainDispatcher
    {
        private readonly Queue<Action> _actions = new();
        public bool IsClosed { get; set; }
        public bool InvalidContract, ThrowOnRead, RejectPost, ThrowOnPost;
        public uint KcpPending;
        public int Reads;
        private bool _onScene;
        public void Post(Action action) => _actions.Enqueue(action);
        public bool TryPostSendDrainCheck(Action action)
        {
            if (ThrowOnPost) throw new InvalidOperationException("post-failed");
            if (RejectPost) return false;
            Post(action);
            return true;
        }
        public bool TryGetPendingKcpSends(out uint count)
        {
            Assert.That(_onScene, Is.True);
            Reads++;
            if (ThrowOnRead) throw new InvalidOperationException("read-failed");
            count = KcpPending;
            return !InvalidContract;
        }
        public void Send(FantasyRealtimeEnvelope envelope) { KcpPending++; envelope.Dispose(); }
        public void DisposeSession() => IsClosed = true;
        public void RunOne()
        {
            _onScene = true;
            try { _actions.Dequeue()(); }
            finally { _onScene = false; }
        }
        public void RunAll() { while (_actions.Count != 0) RunOne(); }
    }
}
