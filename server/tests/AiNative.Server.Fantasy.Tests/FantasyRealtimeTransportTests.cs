using AiNative.Realtime;
using AiNative.Server.Fantasy;
using NUnit.Framework;

namespace AiNative.Server.Fantasy.Tests;

public sealed class FantasyRealtimeTransportTests
{
    private static readonly TransportChannel SnapshotChannel = new(
        1,
        TransportDelivery.Unreliable,
        TransportOrdering.Sequenced);

    [Test]
    public async Task SendCopiesOnlyDatagramsWithinTheTransportMtu()
    {
        FakeSender sender = new();
        await using FantasyRealtimeTransport transport = new(sender);

        SendResult accepted = await transport.SendAsync(SnapshotChannel, new byte[1200]);
        SendResult rejected = await transport.SendAsync(SnapshotChannel, new byte[1201]);

        Assert.That(accepted.Status, Is.EqualTo(SendStatus.Accepted));
        Assert.That(accepted.AcceptedBytes, Is.EqualTo(1200));
        Assert.That(rejected.Status, Is.EqualTo(SendStatus.PayloadTooLarge));
        Assert.That(sender.SendCount, Is.EqualTo(1));
    }

    [Test]
    public async Task InboundQueueIsBoundedAndReportsTruncation()
    {
        FakeSender sender = new();
        await using FantasyRealtimeTransport transport = new(sender, maxInboundBytes: 8);

        Assert.That(transport.TryEnqueueReceived(SnapshotChannel, new byte[] { 1, 2, 3, 4, 5 }, 9, 2), Is.True);
        Assert.That(transport.TryEnqueueReceived(SnapshotChannel, new byte[] { 6, 7, 8, 9 }, 10, 2), Is.False);

        Span<byte> destination = stackalloc byte[3];
        Assert.That(transport.TryReceive(destination, out ReceivedPacket packet), Is.True);
        Assert.That(destination.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3 }));
        Assert.That(packet.WrittenBytes, Is.EqualTo(3));
        Assert.That(packet.RequiredBytes, Is.EqualTo(5));
        Assert.That(packet.IsComplete, Is.False);
    }

    [Test]
    public async Task SenderBackpressureIsReportedWithoutAcceptingBytes()
    {
        FakeSender sender = new() { NextStatus = SendStatus.WouldBlock };
        await using FantasyRealtimeTransport transport = new(sender);

        SendResult result = await transport.SendAsync(SnapshotChannel, new byte[32]);

        Assert.That(result.Status, Is.EqualTo(SendStatus.WouldBlock));
        Assert.That(result.AcceptedBytes, Is.Zero);
    }

    [Test]
    public async Task InboundQueueIsBoundedByPacketCountForEmptyPayloads()
    {
        await using FantasyRealtimeTransport transport = new(
            new FakeSender(),
            maxInboundBytes: 256,
            maxInboundPackets: 2);

        Assert.That(transport.TryEnqueueReceived(SnapshotChannel, [], 1, 1), Is.True);
        Assert.That(transport.TryEnqueueReceived(SnapshotChannel, [], 2, 1), Is.True);
        Assert.That(transport.TryEnqueueReceived(SnapshotChannel, [], 3, 1), Is.False);

        Assert.That(transport.TryReceive([], out _), Is.True);
        Assert.That(transport.TryEnqueueReceived(SnapshotChannel, [], 4, 1), Is.True);
    }

    [Test]
    public void ReplaceableSnapshotBacklogRetainsOnlyTheNewestFrame()
    {
        FakeDispatcher dispatcher = new();
        using FantasySessionSender sender = new(
            dispatcher,
            maxOutboundBytes: 4096,
            maxOutboundPackets: 8);

        for (int index = 0; index < 1000; index++)
        {
            Assert.That(sender.Send(SnapshotChannel, BitConverter.GetBytes(index)), Is.EqualTo(SendStatus.Accepted));
        }

        Assert.That(sender.PendingOutboundPackets, Is.EqualTo(1));
        Assert.That(sender.PendingOutboundBytes, Is.EqualTo(sizeof(int)));
        Assert.That(sender.SnapshotReplacementCount, Is.EqualTo(999));

        dispatcher.RunPostedActions();

        Assert.That(dispatcher.SentPayloads, Has.Count.EqualTo(1));
        Assert.That(BitConverter.ToInt32(dispatcher.SentPayloads[0]), Is.EqualTo(999));
        Assert.That(sender.PendingOutboundPackets, Is.Zero);
        Assert.That(sender.PendingOutboundBytes, Is.Zero);
    }

    private sealed class FakeSender : IFantasySessionSender
    {
        public bool IsClosed { get; private set; }

        public int SendCount { get; private set; }

        public SendStatus NextStatus { get; init; } = SendStatus.Accepted;

        public SendStatus Send(TransportChannel channel, ReadOnlySpan<byte> payload)
        {
            SendCount++;
            return NextStatus;
        }

        public void Dispose() => IsClosed = true;
    }

    private sealed class FakeDispatcher : IFantasyOutboundDispatcher
    {
        private readonly Queue<Action> _posted = new();

        public bool IsClosed { get; private set; }

        public List<byte[]> SentPayloads { get; } = new();

        public void Post(Action action) => _posted.Enqueue(action);

        public void Send(FantasyRealtimeEnvelope envelope)
        {
            SentPayloads.Add(envelope.Payload.ToArray());
            envelope.Dispose();
        }

        public void DisposeSession() => IsClosed = true;

        public void RunPostedActions()
        {
            while (_posted.TryDequeue(out Action? action))
            {
                action();
            }
        }
    }
}
