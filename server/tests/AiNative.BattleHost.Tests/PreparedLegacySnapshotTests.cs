using AiNative.Protocol.V1;
using AiNative.Server.Protocol;
using Google.Protobuf;
using NUnit.Framework;

namespace AiNative.BattleHost.Tests;

public sealed class PreparedLegacySnapshotTests
{
    [Test]
    public void RecipientsRetainIdenticalRoomStateAndDistinctFullRangeAcknowledgements()
    {
        SyntheticRoom room = new(64);
        for (int index = 0; index < 64; index++) room.ApplyInput(index, index * 17, -index * 29);
        Snapshot snapshot = room.CreateSnapshot(1234);
        PreparedLegacySnapshot frame = new();
        Assert.That(frame.TryPrepare(snapshot), Is.True);
        byte[] destination = new byte[RealtimeProtocolCodec.MaxDatagramBytes];
        foreach (uint sequence in new uint[] { 0, 1, 127, 128, 16383, 16384, 2097151, 2097152, 268435455, 268435456, uint.MaxValue })
        {
            Assert.That(frame.TryWriteRecipient(sequence, destination, out int length), Is.True);
            Assert.That(RealtimeProtocolCodec.TryDecode(destination.AsSpan(0, length), out var decoded), Is.EqualTo(ProtocolDecodeStatus.Accepted));
            Snapshot expected = snapshot.Clone(); expected.LastProcessedInputSequence = sequence;
            Assert.That(decoded.Message, Is.EqualTo(expected));
            Assert.That(decoded.Channel, Is.EqualTo(frame.Channel));
        }
        Assert.That(snapshot.LastProcessedInputSequence, Is.Zero);
        Snapshot nextRoom = new SyntheticRoom(2).CreateSnapshot(5678);
        Assert.That(frame.TryPrepare(nextRoom), Is.True);
        Assert.That(frame.TryWriteRecipient(10, destination, out int nextLength), Is.True);
        Assert.That(RealtimeProtocolCodec.TryDecode(destination.AsSpan(0, nextLength), out var next), Is.EqualTo(ProtocolDecodeStatus.Accepted));
        nextRoom.LastProcessedInputSequence = 10;
        Assert.That(next.Message, Is.EqualTo(nextRoom));
    }

    [Test]
    public void BoundsAndFailedPreparationNeverSendStaleRoomDataOrPartiallyWriteDestination()
    {
        PreparedLegacySnapshot frame = new();
        Snapshot snapshot = new SyntheticRoom(2).CreateSnapshot(1);
        Assert.That(frame.TryPrepare(snapshot), Is.True);
        byte[] destination = Enumerable.Repeat((byte)0x5a, RealtimeProtocolCodec.MaxDatagramBytes).ToArray();
        Assert.That(frame.TryWriteRecipient(uint.MaxValue, destination.AsSpan(0, 2), out int shortLength), Is.False);
        Assert.That(shortLength, Is.Zero);
        Assert.That(destination.All(value => value == 0x5a), Is.True);
        // Unknown additive field 20 fills the common frame to 1197 bytes; a six-byte ACK cannot fit.
        byte[] payload = new byte[1195]; payload[0] = 0xa2; payload[1] = 1; payload[2] = 0xa7; payload[3] = 9;
        Assert.That(frame.TryPrepare(Snapshot.Parser.ParseFrom(payload)), Is.True);
        Assert.That(frame.TryWriteRecipient(uint.MaxValue, destination, out int oversizedLength), Is.False);
        Assert.That(oversizedLength, Is.Zero);
        Assert.That(destination.All(value => value == 0x5a), Is.True);
        snapshot.LastProcessedInputSequence = 1;
        Assert.That(frame.TryPrepare(snapshot), Is.False);
        Assert.That(frame.TryWriteRecipient(2, destination, out _), Is.False);
        snapshot.LastProcessedInputSequence = 0;
        for (int index = 0; index < 400; index++) snapshot.Players.Add(new PlayerState { EntityId = (uint)index + 100 });
        Assert.That(frame.TryPrepare(snapshot), Is.False);
        Assert.That(frame.TryWriteRecipient(2, destination, out _), Is.False);
        Assert.That(destination.All(value => value == 0x5a), Is.True);
    }

    [Test]
    public void PreparedRecipientFanoutHasZeroSteadyStateManagedAllocation()
    {
        PreparedLegacySnapshot frame = new();
        Snapshot snapshot = new SyntheticRoom(64).CreateSnapshot(100);
        byte[] destination = new byte[RealtimeProtocolCodec.MaxDatagramBytes];
        Assert.That(frame.TryPrepare(snapshot), Is.True);
        for (int iteration = 0; iteration < 10000; iteration++) frame.TryWriteRecipient((uint)iteration, destination, out _);
        long started = GC.GetAllocatedBytesForCurrentThread();
        bool passed = true;
        for (int iteration = 0; iteration < 10000; iteration++) passed &= frame.TryWriteRecipient((uint)iteration, destination, out _);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - started;
        Assert.That(passed, Is.True);
        Assert.That(allocated, Is.Zero);
    }
}
