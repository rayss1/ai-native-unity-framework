using System;
using System.Buffers.Binary;
using AiNative.Gameplay;
using AiNative.Realtime;

namespace AiNative.Client.Prediction
{
    internal enum ClientProtocolDecodeStatus : byte
    {
        Accepted = 0,
        FrameTooShort = 1,
        FrameTooLarge = 2,
        UnknownMessage = 3,
        MalformedPayload = 4,
        ProtocolMismatch = 5,
        PlayerMissing = 6,
    }

    internal readonly struct DecodedAuthoritativeSnapshot
    {
        public DecodedAuthoritativeSnapshot(
            in KinematicState state,
            bool isReconnect,
            uint reconnectConnectionEpoch)
        {
            State = state;
            IsReconnect = isReconnect;
            ReconnectConnectionEpoch = reconnectConnectionEpoch;
        }

        public KinematicState State { get; }

        public bool IsReconnect { get; }

        public uint ReconnectConnectionEpoch { get; }
    }

    internal static class ClientPredictionProtocolV1
    {
        public const int HeaderBytes = sizeof(ushort);
        public const int MaxDatagramBytes = 1200;
        public const int MaxInputFrameBytes = 29;
        public const ushort InputCommandMessageId = 1100;
        public const ushort SnapshotMessageId = 1101;
        public const ushort ReconnectResponseMessageId = 1201;

        public static readonly TransportChannel ControlChannel = new TransportChannel(
            0,
            TransportDelivery.Reliable,
            TransportOrdering.Ordered);

        public static readonly TransportChannel SnapshotChannel = new TransportChannel(
            1,
            TransportDelivery.Unreliable,
            TransportOrdering.Sequenced);

        public static readonly TransportChannel InputChannel = new TransportChannel(
            2,
            TransportDelivery.Unreliable,
            TransportOrdering.Sequenced);

        public static bool TryEncodeInput(
            ulong roomTick,
            in KinematicInput input,
            Span<byte> destination,
            out int writtenBytes)
        {
            writtenBytes = 0;
            if (destination.Length < HeaderBytes)
            {
                return false;
            }

            BinaryPrimitives.WriteUInt16LittleEndian(destination, InputCommandMessageId);
            int offset = HeaderBytes;
            if (!TryWriteByte(destination, ref offset, 0x09) ||
                !TryWriteFixed64(destination, ref offset, roomTick) ||
                !TryWriteByte(destination, ref offset, 0x10) ||
                !TryWriteVarint(destination, ref offset, input.Sequence))
            {
                return false;
            }

            if (input.MoveXMilli != 0 &&
                (!TryWriteByte(destination, ref offset, 0x18) ||
                 !TryWriteVarint(destination, ref offset, ZigZag(input.MoveXMilli))))
            {
                return false;
            }

            if (input.MoveZMilli != 0 &&
                (!TryWriteByte(destination, ref offset, 0x20) ||
                 !TryWriteVarint(destination, ref offset, ZigZag(input.MoveZMilli))))
            {
                return false;
            }

            writtenBytes = offset;
            return true;
        }

        public static ClientProtocolDecodeStatus TryDecodeSnapshot(
            ReadOnlySpan<byte> frame,
            uint entityId,
            out TransportChannel expectedChannel,
            out DecodedAuthoritativeSnapshot decoded)
        {
            expectedChannel = default;
            decoded = default;
            if (frame.Length < HeaderBytes)
            {
                return ClientProtocolDecodeStatus.FrameTooShort;
            }

            if (frame.Length > MaxDatagramBytes)
            {
                return ClientProtocolDecodeStatus.FrameTooLarge;
            }

            ushort messageId = BinaryPrimitives.ReadUInt16LittleEndian(frame);
            ReadOnlySpan<byte> payload = frame.Slice(HeaderBytes);
            switch (messageId)
            {
                case SnapshotMessageId:
                    expectedChannel = SnapshotChannel;
                    return TryDecodeSnapshotPayload(payload, entityId, false, 0, out decoded);
                case ReconnectResponseMessageId:
                    expectedChannel = ControlChannel;
                    return TryDecodeReconnectPayload(payload, entityId, out decoded);
                default:
                    return ClientProtocolDecodeStatus.UnknownMessage;
            }
        }

        private static ClientProtocolDecodeStatus TryDecodeReconnectPayload(
            ReadOnlySpan<byte> payload,
            uint entityId,
            out DecodedAuthoritativeSnapshot decoded)
        {
            decoded = default;
            int offset = 0;
            uint connectionEpoch = 0;
            ulong resumeTick = 0;
            bool hasResumeTick = false;
            bool hasSnapshot = false;
            DecodedAuthoritativeSnapshot nested = default;

            while (offset < payload.Length)
            {
                if (!TryReadKey(payload, ref offset, out int fieldNumber, out int wireType))
                {
                    return ClientProtocolDecodeStatus.MalformedPayload;
                }

                switch (fieldNumber)
                {
                    case 1 when wireType == 0:
                        if (!TryReadUInt32(payload, ref offset, out connectionEpoch))
                        {
                            return ClientProtocolDecodeStatus.MalformedPayload;
                        }

                        break;
                    case 2 when wireType == 1:
                        if (!TryReadFixed64(payload, ref offset, out resumeTick))
                        {
                            return ClientProtocolDecodeStatus.MalformedPayload;
                        }

                        hasResumeTick = true;
                        break;
                    case 3 when wireType == 2:
                        if (hasSnapshot || !TryReadLengthDelimited(payload, ref offset, out ReadOnlySpan<byte> snapshot))
                        {
                            return ClientProtocolDecodeStatus.MalformedPayload;
                        }

                        ClientProtocolDecodeStatus status = TryDecodeSnapshotPayload(
                            snapshot,
                            entityId,
                            true,
                            connectionEpoch,
                            out nested);
                        if (status != ClientProtocolDecodeStatus.Accepted)
                        {
                            return status;
                        }

                        hasSnapshot = true;
                        break;
                    default:
                        if (!TrySkipField(payload, ref offset, wireType))
                        {
                            return ClientProtocolDecodeStatus.MalformedPayload;
                        }

                        break;
                }
            }

            if (connectionEpoch == 0 || !hasResumeTick || !hasSnapshot ||
                resumeTick > long.MaxValue || resumeTick < (ulong)nested.State.Tick)
            {
                return ClientProtocolDecodeStatus.MalformedPayload;
            }

            decoded = new DecodedAuthoritativeSnapshot(nested.State, true, connectionEpoch);
            return ClientProtocolDecodeStatus.Accepted;
        }

        private static ClientProtocolDecodeStatus TryDecodeSnapshotPayload(
            ReadOnlySpan<byte> payload,
            uint entityId,
            bool isReconnect,
            uint reconnectConnectionEpoch,
            out DecodedAuthoritativeSnapshot decoded)
        {
            decoded = default;
            int offset = 0;
            uint protocolMajor = 0;
            ulong roomTick = 0;
            uint acknowledgement = 0;
            bool hasRoomTick = false;
            bool foundPlayer = false;
            int positionX = 0;
            int positionZ = 0;

            while (offset < payload.Length)
            {
                if (!TryReadKey(payload, ref offset, out int fieldNumber, out int wireType))
                {
                    return ClientProtocolDecodeStatus.MalformedPayload;
                }

                switch (fieldNumber)
                {
                    case 1 when wireType == 0:
                        if (!TryReadUInt32(payload, ref offset, out protocolMajor))
                        {
                            return ClientProtocolDecodeStatus.MalformedPayload;
                        }

                        break;
                    case 2 when wireType == 1:
                        if (!TryReadFixed64(payload, ref offset, out roomTick))
                        {
                            return ClientProtocolDecodeStatus.MalformedPayload;
                        }

                        hasRoomTick = true;
                        break;
                    case 4 when wireType == 2:
                        if (!TryReadLengthDelimited(payload, ref offset, out ReadOnlySpan<byte> player) ||
                            !TryDecodePlayer(player, out uint candidateEntity, out int candidateX, out int candidateZ))
                        {
                            return ClientProtocolDecodeStatus.MalformedPayload;
                        }

                        if (candidateEntity == entityId)
                        {
                            if (foundPlayer)
                            {
                                return ClientProtocolDecodeStatus.MalformedPayload;
                            }

                            foundPlayer = true;
                            positionX = candidateX;
                            positionZ = candidateZ;
                        }

                        break;
                    case 6 when wireType == 0:
                        if (!TryReadUInt32(payload, ref offset, out acknowledgement))
                        {
                            return ClientProtocolDecodeStatus.MalformedPayload;
                        }

                        break;
                    default:
                        if (!TrySkipField(payload, ref offset, wireType))
                        {
                            return ClientProtocolDecodeStatus.MalformedPayload;
                        }

                        break;
                }
            }

            if (protocolMajor != 1)
            {
                return ClientProtocolDecodeStatus.ProtocolMismatch;
            }

            if (!hasRoomTick || roomTick > long.MaxValue)
            {
                return ClientProtocolDecodeStatus.MalformedPayload;
            }

            if (!foundPlayer)
            {
                return ClientProtocolDecodeStatus.PlayerMissing;
            }

            KinematicState state = new KinematicState(
                (long)roomTick,
                acknowledgement,
                positionX,
                positionZ);
            decoded = new DecodedAuthoritativeSnapshot(
                state,
                isReconnect,
                reconnectConnectionEpoch);
            return ClientProtocolDecodeStatus.Accepted;
        }

        private static bool TryDecodePlayer(
            ReadOnlySpan<byte> payload,
            out uint entityId,
            out int positionX,
            out int positionZ)
        {
            entityId = 0;
            positionX = 0;
            positionZ = 0;
            int offset = 0;
            while (offset < payload.Length)
            {
                if (!TryReadKey(payload, ref offset, out int fieldNumber, out int wireType))
                {
                    return false;
                }

                switch (fieldNumber)
                {
                    case 1 when wireType == 0:
                        if (!TryReadUInt32(payload, ref offset, out entityId))
                        {
                            return false;
                        }

                        break;
                    case 2 when wireType == 0:
                        if (!TryReadUInt32(payload, ref offset, out uint encodedX))
                        {
                            return false;
                        }

                        positionX = UnZigZag(encodedX);
                        break;
                    case 4 when wireType == 0:
                        if (!TryReadUInt32(payload, ref offset, out uint encodedZ))
                        {
                            return false;
                        }

                        positionZ = UnZigZag(encodedZ);
                        break;
                    default:
                        if (!TrySkipField(payload, ref offset, wireType))
                        {
                            return false;
                        }

                        break;
                }
            }

            return entityId != 0;
        }

        private static bool TryReadKey(
            ReadOnlySpan<byte> source,
            ref int offset,
            out int fieldNumber,
            out int wireType)
        {
            fieldNumber = 0;
            wireType = 0;
            if (!TryReadVarint(source, ref offset, out ulong key) || key == 0 || key > uint.MaxValue)
            {
                return false;
            }

            fieldNumber = (int)(key >> 3);
            wireType = (int)(key & 0x07);
            return fieldNumber > 0;
        }

        private static bool TryReadUInt32(
            ReadOnlySpan<byte> source,
            ref int offset,
            out uint value)
        {
            value = 0;
            if (!TryReadVarint(source, ref offset, out ulong wide) || wide > uint.MaxValue)
            {
                return false;
            }

            value = (uint)wide;
            return true;
        }

        private static bool TryReadVarint(
            ReadOnlySpan<byte> source,
            ref int offset,
            out ulong value)
        {
            value = 0;
            for (int shift = 0; shift < 70; shift += 7)
            {
                if (offset >= source.Length)
                {
                    return false;
                }

                byte current = source[offset++];
                if (shift == 63 && current > 1)
                {
                    return false;
                }

                value |= (ulong)(current & 0x7f) << shift;
                if ((current & 0x80) == 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryReadFixed64(
            ReadOnlySpan<byte> source,
            ref int offset,
            out ulong value)
        {
            value = 0;
            if (source.Length - offset < sizeof(ulong))
            {
                return false;
            }

            value = BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(offset, sizeof(ulong)));
            offset += sizeof(ulong);
            return true;
        }

        private static bool TryReadLengthDelimited(
            ReadOnlySpan<byte> source,
            ref int offset,
            out ReadOnlySpan<byte> value)
        {
            value = default;
            if (!TryReadVarint(source, ref offset, out ulong length) ||
                length > int.MaxValue ||
                (int)length > source.Length - offset)
            {
                return false;
            }

            value = source.Slice(offset, (int)length);
            offset += (int)length;
            return true;
        }

        private static bool TrySkipField(ReadOnlySpan<byte> source, ref int offset, int wireType)
        {
            switch (wireType)
            {
                case 0:
                    return TryReadVarint(source, ref offset, out _);
                case 1:
                    if (source.Length - offset < sizeof(ulong))
                    {
                        return false;
                    }

                    offset += sizeof(ulong);
                    return true;
                case 2:
                    return TryReadLengthDelimited(source, ref offset, out _);
                case 5:
                    if (source.Length - offset < sizeof(uint))
                    {
                        return false;
                    }

                    offset += sizeof(uint);
                    return true;
                default:
                    return false;
            }
        }

        private static bool TryWriteByte(Span<byte> destination, ref int offset, byte value)
        {
            if (offset >= destination.Length)
            {
                return false;
            }

            destination[offset++] = value;
            return true;
        }

        private static bool TryWriteFixed64(Span<byte> destination, ref int offset, ulong value)
        {
            if (destination.Length - offset < sizeof(ulong))
            {
                return false;
            }

            BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(offset, sizeof(ulong)), value);
            offset += sizeof(ulong);
            return true;
        }

        private static bool TryWriteVarint(Span<byte> destination, ref int offset, ulong value)
        {
            while (value >= 0x80)
            {
                if (!TryWriteByte(destination, ref offset, (byte)(value | 0x80)))
                {
                    return false;
                }

                value >>= 7;
            }

            return TryWriteByte(destination, ref offset, (byte)value);
        }

        private static uint ZigZag(int value)
        {
            unchecked
            {
                return (uint)((value << 1) ^ (value >> 31));
            }
        }

        private static int UnZigZag(uint value)
        {
            unchecked
            {
                return (int)(value >> 1) ^ -((int)value & 1);
            }
        }
    }
}
