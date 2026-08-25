using System.Buffers.Binary;
using AiNative.Protocol.V1;
using AiNative.Realtime;
using Google.Protobuf;

namespace AiNative.Server.Protocol;

internal enum ProtocolDecodeStatus : byte
{
    Accepted = 0,
    FrameTooShort = 1,
    FrameTooLarge = 2,
    UnknownMessage = 3,
    MalformedPayload = 4,
}

internal readonly record struct DecodedProtocolMessage(
    MessageId MessageId,
    TransportChannel Channel,
    IMessage Message);

internal static class RealtimeProtocolCodec
{
    public const int HeaderBytes = sizeof(ushort);
    public const int MaxDatagramBytes = 1200;

    private static readonly TransportChannel ControlChannel = new(
        0,
        TransportDelivery.Reliable,
        TransportOrdering.Ordered);

    private static readonly TransportChannel SnapshotChannel = new(
        1,
        TransportDelivery.Unreliable,
        TransportOrdering.Sequenced);

    private static readonly TransportChannel InputChannel = new(
        2,
        TransportDelivery.Unreliable,
        TransportOrdering.Sequenced);

    private static readonly TransportChannel EventChannel = new(
        3,
        TransportDelivery.Reliable,
        TransportOrdering.Ordered);

    public static bool TryEncode(
        MessageId messageId,
        IMessage message,
        Span<byte> destination,
        out TransportChannel channel,
        out int writtenBytes)
    {
        ArgumentNullException.ThrowIfNull(message);
        channel = default;
        writtenBytes = 0;

        if (!IsExpectedType(messageId, message) || !TryGetChannel(messageId, out channel))
        {
            return false;
        }

        int payloadBytes = message.CalculateSize();
        int frameBytes = HeaderBytes + payloadBytes;
        if (frameBytes > MaxDatagramBytes || frameBytes > destination.Length)
        {
            return false;
        }

        BinaryPrimitives.WriteUInt16LittleEndian(destination, checked((ushort)messageId));
        message.WriteTo(destination.Slice(HeaderBytes, payloadBytes));
        writtenBytes = frameBytes;
        return true;
    }

    public static ProtocolDecodeStatus TryDecode(
        ReadOnlySpan<byte> frame,
        out DecodedProtocolMessage decoded)
    {
        decoded = default;
        if (frame.Length < HeaderBytes)
        {
            return ProtocolDecodeStatus.FrameTooShort;
        }

        if (frame.Length > MaxDatagramBytes)
        {
            return ProtocolDecodeStatus.FrameTooLarge;
        }

        MessageId messageId = (MessageId)BinaryPrimitives.ReadUInt16LittleEndian(frame);
        if (!TryGetChannel(messageId, out TransportChannel channel))
        {
            return ProtocolDecodeStatus.UnknownMessage;
        }

        try
        {
            IMessage message = Parse(messageId, frame.Slice(HeaderBytes));
            decoded = new DecodedProtocolMessage(messageId, channel, message);
            return ProtocolDecodeStatus.Accepted;
        }
        catch (InvalidProtocolBufferException)
        {
            return ProtocolDecodeStatus.MalformedPayload;
        }
    }

    public static bool TryGetChannel(MessageId messageId, out TransportChannel channel)
    {
        switch (messageId)
        {
            case MessageId.InputCommand:
            case MessageId.InputBatch:
                channel = InputChannel;
                return true;
            case MessageId.Snapshot:
                channel = SnapshotChannel;
                return true;
            case MessageId.ReliableEvent:
                channel = EventChannel;
                return true;
            case MessageId.LoginRequest:
            case MessageId.LoginResponse:
            case MessageId.JoinRoomRequest:
            case MessageId.JoinRoomResponse:
            case MessageId.ReconnectRequest:
            case MessageId.ReconnectResponse:
                channel = ControlChannel;
                return true;
            default:
                channel = default;
                return false;
        }
    }

    private static bool IsExpectedType(MessageId messageId, IMessage message) => messageId switch
    {
        MessageId.LoginRequest => message is LoginRequest,
        MessageId.LoginResponse => message is LoginResponse,
        MessageId.JoinRoomRequest => message is JoinRoomRequest,
        MessageId.JoinRoomResponse => message is JoinRoomResponse,
        MessageId.InputCommand => message is InputCommand,
        MessageId.InputBatch => message is InputBatch,
        MessageId.Snapshot => message is Snapshot,
        MessageId.ReliableEvent => message is ReliableEvent,
        MessageId.ReconnectRequest => message is ReconnectRequest,
        MessageId.ReconnectResponse => message is ReconnectResponse,
        _ => false,
    };

    private static IMessage Parse(MessageId messageId, ReadOnlySpan<byte> payload) => messageId switch
    {
        MessageId.LoginRequest => LoginRequest.Parser.ParseFrom(payload),
        MessageId.LoginResponse => LoginResponse.Parser.ParseFrom(payload),
        MessageId.JoinRoomRequest => JoinRoomRequest.Parser.ParseFrom(payload),
        MessageId.JoinRoomResponse => JoinRoomResponse.Parser.ParseFrom(payload),
        MessageId.InputCommand => InputCommand.Parser.ParseFrom(payload),
        MessageId.InputBatch => InputBatch.Parser.ParseFrom(payload),
        MessageId.Snapshot => Snapshot.Parser.ParseFrom(payload),
        MessageId.ReliableEvent => ReliableEvent.Parser.ParseFrom(payload),
        MessageId.ReconnectRequest => ReconnectRequest.Parser.ParseFrom(payload),
        MessageId.ReconnectResponse => ReconnectResponse.Parser.ParseFrom(payload),
        _ => throw new InvalidOperationException($"Unknown message ID: {messageId}."),
    };
}
