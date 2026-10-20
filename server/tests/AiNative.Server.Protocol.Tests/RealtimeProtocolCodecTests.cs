using AiNative.Protocol.V1;
using AiNative.Realtime;
using AiNative.Server.Protocol;
using NUnit.Framework;

namespace AiNative.Server.Protocol.Tests;

public sealed class RealtimeProtocolCodecTests
{
    [Test]
    public void InputRoundTripUsesTheSequencedUnreliableChannel()
    {
        C2S_InputCommand input = new()
        {
            RoomTick = 42,
            Sequence = 7,
            MoveXMilli = -500,
            MoveYMilli = 250,
            YawMillidegrees = 90000,
            Buttons = 3,
        };
        Span<byte> frame = stackalloc byte[RealtimeProtocolCodec.MaxDatagramBytes];

        bool encoded = RealtimeProtocolCodec.TryEncode(
            MessageId.C2SInputCommand,
            input,
            frame,
            out TransportChannel channel,
            out int writtenBytes
        );
        ProtocolDecodeStatus status = RealtimeProtocolCodec.TryDecode(
            frame.Slice(0, writtenBytes),
            out DecodedProtocolMessage decoded
        );
        byte messageIdLowByte = frame[0];
        byte messageIdHighByte = frame[1];

        Assert.Multiple(() =>
        {
            Assert.That(encoded, Is.True);
            Assert.That(messageIdLowByte, Is.EqualTo(0x4c));
            Assert.That(messageIdHighByte, Is.EqualTo(0x04));
            Assert.That(channel.Delivery, Is.EqualTo(TransportDelivery.Unreliable));
            Assert.That(channel.Ordering, Is.EqualTo(TransportOrdering.Sequenced));
            Assert.That(status, Is.EqualTo(ProtocolDecodeStatus.Accepted));
            Assert.That(decoded.MessageId, Is.EqualTo(MessageId.C2SInputCommand));
            Assert.That((C2S_InputCommand)decoded.Message, Is.EqualTo(input));
        });
    }

    [Test]
    public void TwoFrameInputBatchUsesTheSequencedUnreliableChannelAndFitsOneDatagram()
    {
        C2S_InputBatch batch = new();
        batch.Commands.Add(
            new C2S_InputCommand
            {
                RoomTick = 42,
                Sequence = 7,
                MoveXMilli = -1000,
                MoveYMilli = 500,
            }
        );
        batch.Commands.Add(
            new C2S_InputCommand
            {
                RoomTick = 43,
                Sequence = 8,
                MoveXMilli = 1000,
                MoveYMilli = -500,
                Buttons = 1,
            }
        );
        Span<byte> frame = stackalloc byte[RealtimeProtocolCodec.MaxDatagramBytes];

        bool encoded = RealtimeProtocolCodec.TryEncode(
            MessageId.C2SInputBatch,
            batch,
            frame,
            out TransportChannel channel,
            out int writtenBytes
        );
        ProtocolDecodeStatus status = RealtimeProtocolCodec.TryDecode(
            frame[..writtenBytes],
            out DecodedProtocolMessage decoded
        );

        Assert.Multiple(() =>
        {
            Assert.That(encoded, Is.True);
            Assert.That(channel.Delivery, Is.EqualTo(TransportDelivery.Unreliable));
            Assert.That(channel.Ordering, Is.EqualTo(TransportOrdering.Sequenced));
            Assert.That(writtenBytes, Is.LessThan(96));
            Assert.That(status, Is.EqualTo(ProtocolDecodeStatus.Accepted));
            Assert.That(decoded.MessageId, Is.EqualTo(MessageId.C2SInputBatch));
            Assert.That((C2S_InputBatch)decoded.Message, Is.EqualTo(batch));
        });
    }

    [Test]
    public void SnapshotFrameForSixtyFourPlayersFitsTheDatagramBudget()
    {
        S2C_SnapshotPush snapshot = new()
        {
            ProtocolMajor = 1,
            RoomTick = 60,
            BaselineTick = 57,
            LastProcessedInputSequence = uint.MaxValue,
        };
        for (uint entityId = 1; entityId <= 64; entityId++)
        {
            snapshot.Players.Add(
                new PlayerState
                {
                    EntityId = entityId,
                    PositionXMilli = (int)entityId * 10,
                    PositionYMilli = 0,
                    PositionZMilli = (int)entityId * -10,
                    YawMillidegrees = (int)entityId * 100,
                    Health = 100,
                }
            );
        }

        Span<byte> frame = stackalloc byte[RealtimeProtocolCodec.MaxDatagramBytes];
        bool encoded = RealtimeProtocolCodec.TryEncode(
            MessageId.S2CSnapshotPush,
            snapshot,
            frame,
            out TransportChannel channel,
            out int writtenBytes
        );

        Assert.Multiple(() =>
        {
            Assert.That(encoded, Is.True);
            Assert.That(writtenBytes, Is.LessThanOrEqualTo(RealtimeProtocolCodec.MaxDatagramBytes));
            Assert.That(channel.Id, Is.EqualTo(1));
        });
    }

    [Test]
    public void WrongMessageTypeAndInvalidFramesAreRejected()
    {
        Span<byte> destination = stackalloc byte[64];
        bool wrongType = RealtimeProtocolCodec.TryEncode(
            MessageId.S2CSnapshotPush,
            new C2S_InputCommand(),
            destination,
            out _,
            out _
        );
        byte[] malformed = { 0x4c, 0x04, 0x80 };

        ProtocolDecodeStatus malformedStatus = RealtimeProtocolCodec.TryDecode(malformed, out _);
        ProtocolDecodeStatus unknownStatus = RealtimeProtocolCodec.TryDecode(
            new byte[] { 0xff, 0xff },
            out _
        );
        ProtocolDecodeStatus oversizedStatus = RealtimeProtocolCodec.TryDecode(
            new byte[RealtimeProtocolCodec.MaxDatagramBytes + 1],
            out _
        );

        Assert.That(wrongType, Is.False);
        Assert.That(malformedStatus, Is.EqualTo(ProtocolDecodeStatus.MalformedPayload));
        Assert.That(unknownStatus, Is.EqualTo(ProtocolDecodeStatus.UnknownMessage));
        Assert.That(oversizedStatus, Is.EqualTo(ProtocolDecodeStatus.FrameTooLarge));
    }
}
