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

    private sealed class FakeSender : IFantasySessionSender
    {
        public bool IsClosed { get; private set; }

        public int SendCount { get; private set; }

        public void Send(TransportChannel channel, ReadOnlySpan<byte> payload) => SendCount++;

        public void Dispose() => IsClosed = true;
    }
}
