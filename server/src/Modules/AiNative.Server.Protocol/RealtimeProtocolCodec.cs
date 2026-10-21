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
    IMessage Message
);

internal static class RealtimeProtocolCodec
{
    public const int HeaderBytes = sizeof(ushort);
    public const int MaxDatagramBytes = 1200;

    private static readonly TransportChannel ControlChannel = new(
        0,
        TransportDelivery.Reliable,
        TransportOrdering.Ordered
    );

    private static readonly TransportChannel SnapshotChannel = new(
        1,
        TransportDelivery.Unreliable,
        TransportOrdering.Sequenced
    );

    private static readonly TransportChannel InputChannel = new(
        2,
        TransportDelivery.Unreliable,
        TransportOrdering.Sequenced
    );

    private static readonly TransportChannel EventChannel = new(
        3,
        TransportDelivery.Reliable,
        TransportOrdering.Ordered
    );

    public static bool TryEncode(
        MessageId messageId,
        IMessage message,
        Span<byte> destination,
        out TransportChannel channel,
        out int writtenBytes
    )
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
        out DecodedProtocolMessage decoded
    )
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
            case MessageId.C2SInputCommand:
            case MessageId.C2SInputBatch:
                channel = InputChannel;
                return true;
            case MessageId.S2CSnapshotPush:
                channel = SnapshotChannel;
                return true;
            case MessageId.S2CReliableEventPush:
                channel = EventChannel;
                return true;
            case MessageId.C2SLoginRequest:
            case MessageId.S2CLoginResponse:
            case MessageId.C2SJoinRoomRequest:
            case MessageId.S2CJoinRoomResponse:
            case MessageId.C2SReconnectRequest:
            case MessageId.S2CReconnectResponse:
                channel = ControlChannel;
                return true;
            default:
                channel = default;
                return false;
        }
    }

    private static bool IsExpectedType(MessageId messageId, IMessage message) =>
        messageId switch
        {
            MessageId.C2SLoginRequest => message is C2S_LoginRequest,
            MessageId.S2CLoginResponse => message is S2C_LoginResponse,
            MessageId.C2SJoinRoomRequest => message is C2S_JoinRoomRequest,
            MessageId.S2CJoinRoomResponse => message is S2C_JoinRoomResponse,
            MessageId.C2SInputCommand => message is C2S_InputCommand,
            MessageId.C2SInputBatch => message is C2S_InputBatch,
            MessageId.S2CSnapshotPush => message is S2C_SnapshotPush,
            MessageId.S2CReliableEventPush => message is S2C_ReliableEventPush,
            MessageId.C2SReconnectRequest => message is C2S_ReconnectRequest,
            MessageId.S2CReconnectResponse => message is S2C_ReconnectResponse,
            _ => false,
        };

    private static IMessage Parse(MessageId messageId, ReadOnlySpan<byte> payload) =>
        messageId switch
        {
            MessageId.C2SLoginRequest => C2S_LoginRequest.Parser.ParseFrom(payload),
            MessageId.S2CLoginResponse => S2C_LoginResponse.Parser.ParseFrom(payload),
            MessageId.C2SJoinRoomRequest => C2S_JoinRoomRequest.Parser.ParseFrom(payload),
            MessageId.S2CJoinRoomResponse => S2C_JoinRoomResponse.Parser.ParseFrom(payload),
            MessageId.C2SInputCommand => C2S_InputCommand.Parser.ParseFrom(payload),
            MessageId.C2SInputBatch => C2S_InputBatch.Parser.ParseFrom(payload),
            MessageId.S2CSnapshotPush => S2C_SnapshotPush.Parser.ParseFrom(payload),
            MessageId.S2CReliableEventPush => S2C_ReliableEventPush.Parser.ParseFrom(payload),
            MessageId.C2SReconnectRequest => C2S_ReconnectRequest.Parser.ParseFrom(payload),
            MessageId.S2CReconnectResponse => S2C_ReconnectResponse.Parser.ParseFrom(payload),
            _ => throw new InvalidOperationException($"Unknown message ID: {messageId}."),
        };
}
