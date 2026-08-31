using System;
using System.Buffers.Binary;
using System.Text;
using AiNative.Realtime;

namespace AiNative.Client.Application
{
    internal static class BattleClientProtocolV1
    {
        internal const ushort LoginRequestMessageId = 1000;
        internal const ushort LoginResponseMessageId = 1001;
        internal const ushort JoinRoomRequestMessageId = 1010;
        internal const ushort JoinRoomResponseMessageId = 1011;
        internal const ushort SnapshotMessageId = 1101;
        internal const ushort ReconnectRequestMessageId = 1200;
        internal const ushort ReconnectResponseMessageId = 1201;
        internal const int MaxFrameBytes = 1200;

        internal static readonly TransportChannel ControlChannel = new TransportChannel(
            0,
            TransportDelivery.Reliable,
            TransportOrdering.Ordered);

        internal static readonly TransportChannel SnapshotChannel = new TransportChannel(
            1,
            TransportDelivery.Unreliable,
            TransportOrdering.Sequenced);

        internal static readonly TransportChannel InputChannel = new TransportChannel(
            2,
            TransportDelivery.Unreliable,
            TransportOrdering.Sequenced);

        internal static bool TryEncodeLogin(
            string clientBuild,
            Span<byte> destination,
            out int writtenBytes)
        {
            writtenBytes = 0;
            clientBuild ??= string.Empty;
            int buildBytes = Encoding.UTF8.GetByteCount(clientBuild);
            if (buildBytes > 128 || destination.Length < 2)
            {
                return false;
            }

            BinaryPrimitives.WriteUInt16LittleEndian(destination, LoginRequestMessageId);
            int offset = 2;
            if (!TryWriteByte(destination, ref offset, 0x08) ||
                !TryWriteVarint(destination, ref offset, 1) ||
                (buildBytes != 0 &&
                 (!TryWriteByte(destination, ref offset, 0x12) ||
                  !TryWriteVarint(destination, ref offset, (ulong)buildBytes) ||
                  destination.Length - offset < buildBytes)))
            {
                return false;
            }

            if (buildBytes != 0)
            {
                offset += Encoding.UTF8.GetBytes(clientBuild, destination.Slice(offset, buildBytes));
            }

            writtenBytes = offset;
            return true;
        }

        internal static bool TryDecodeLoginResponse(
            ReadOnlySpan<byte> frame,
            out ulong sessionId,
            out uint connectionEpoch)
        {
            sessionId = 0;
            connectionEpoch = 0;
            if (!TryBegin(frame, LoginResponseMessageId, out int offset))
            {
                return false;
            }

            while (offset < frame.Length)
            {
                if (!TryReadKey(frame, ref offset, out int field, out int wire))
                {
                    return false;
                }

                if (field == 1 && wire == 1)
                {
                    if (!TryReadFixed64(frame, ref offset, out sessionId))
                    {
                        return false;
                    }
                }
                else if (field == 2 && wire == 0)
                {
                    if (!TryReadUInt32(frame, ref offset, out connectionEpoch))
                    {
                        return false;
                    }
                }
                else if (!TrySkip(frame, ref offset, wire))
                {
                    return false;
                }
            }

            return sessionId != 0 && connectionEpoch != 0;
        }

        internal static bool TryEncodeJoin(
            ulong sessionId,
            uint roomId,
            Span<byte> destination,
            out int writtenBytes)
        {
            writtenBytes = 0;
            if (sessionId == 0 || roomId == 0 || destination.Length < 2)
            {
                return false;
            }

            BinaryPrimitives.WriteUInt16LittleEndian(destination, JoinRoomRequestMessageId);
            int offset = 2;
            if (!TryWriteByte(destination, ref offset, 0x09) ||
                !TryWriteFixed64(destination, ref offset, sessionId) ||
                !TryWriteByte(destination, ref offset, 0x10) ||
                !TryWriteVarint(destination, ref offset, roomId))
            {
                return false;
            }

            writtenBytes = offset;
            return true;
        }

        internal static bool TryDecodeJoinResponse(
            ReadOnlySpan<byte> frame,
            out uint roomId,
            out uint entityId,
            out uint tickRate)
        {
            roomId = 0;
            entityId = 0;
            tickRate = 0;
            if (!TryBegin(frame, JoinRoomResponseMessageId, out int offset))
            {
                return false;
            }

            while (offset < frame.Length)
            {
                if (!TryReadKey(frame, ref offset, out int field, out int wire))
                {
                    return false;
                }

                if (wire == 0 && field is >= 1 and <= 3)
                {
                    if (!TryReadUInt32(frame, ref offset, out uint value))
                    {
                        return false;
                    }

                    if (field == 1) roomId = value;
                    else if (field == 2) entityId = value;
                    else tickRate = value;
                }
                else if (!TrySkip(frame, ref offset, wire))
                {
                    return false;
                }
            }

            return roomId != 0 && entityId != 0 && tickRate != 0;
        }

        internal static bool TryEncodeReconnect(
            ulong sessionId,
            uint previousConnectionEpoch,
            ulong lastReceivedTick,
            Span<byte> destination,
            out int writtenBytes)
        {
            writtenBytes = 0;
            if (sessionId == 0 || previousConnectionEpoch == 0 || destination.Length < 2)
            {
                return false;
            }

            BinaryPrimitives.WriteUInt16LittleEndian(destination, ReconnectRequestMessageId);
            int offset = 2;
            if (!TryWriteByte(destination, ref offset, 0x09) ||
                !TryWriteFixed64(destination, ref offset, sessionId) ||
                !TryWriteByte(destination, ref offset, 0x10) ||
                !TryWriteVarint(destination, ref offset, previousConnectionEpoch) ||
                !TryWriteByte(destination, ref offset, 0x19) ||
                !TryWriteFixed64(destination, ref offset, lastReceivedTick))
            {
                return false;
            }

            writtenBytes = offset;
            return true;
        }

        internal static bool TryDecodeReconnectResponse(
            ReadOnlySpan<byte> frame,
            out uint connectionEpoch,
            out ulong resumeTick)
        {
            connectionEpoch = 0;
            resumeTick = 0;
            bool hasTick = false;
            bool hasSnapshot = false;
            if (!TryBegin(frame, ReconnectResponseMessageId, out int offset))
            {
                return false;
            }

            while (offset < frame.Length)
            {
                if (!TryReadKey(frame, ref offset, out int field, out int wire))
                {
                    return false;
                }

                if (field == 1 && wire == 0)
                {
                    if (!TryReadUInt32(frame, ref offset, out connectionEpoch)) return false;
                }
                else if (field == 2 && wire == 1)
                {
                    if (!TryReadFixed64(frame, ref offset, out resumeTick)) return false;
                    hasTick = true;
                }
                else if (field == 3 && wire == 2)
                {
                    if (!TryReadLength(frame, ref offset, out _)) return false;
                    hasSnapshot = true;
                }
                else if (!TrySkip(frame, ref offset, wire))
                {
                    return false;
                }
            }

            return connectionEpoch != 0 && hasTick && hasSnapshot;
        }

        internal static bool TryReadSnapshotMetadata(
            ReadOnlySpan<byte> frame,
            out ulong roomTick,
            out uint acknowledgement)
        {
            roomTick = 0;
            acknowledgement = 0;
            bool hasTick = false;
            if (!TryBegin(frame, SnapshotMessageId, out int offset))
            {
                return false;
            }

            while (offset < frame.Length)
            {
                if (!TryReadKey(frame, ref offset, out int field, out int wire)) return false;
                if (field == 2 && wire == 1)
                {
                    if (!TryReadFixed64(frame, ref offset, out roomTick)) return false;
                    hasTick = true;
                }
                else if (field == 6 && wire == 0)
                {
                    if (!TryReadUInt32(frame, ref offset, out acknowledgement)) return false;
                }
                else if (!TrySkip(frame, ref offset, wire)) return false;
            }

            return hasTick;
        }

        internal static ushort ReadMessageId(ReadOnlySpan<byte> frame) =>
            frame.Length < 2 ? (ushort)0 : BinaryPrimitives.ReadUInt16LittleEndian(frame);

        private static bool TryBegin(ReadOnlySpan<byte> frame, ushort expected, out int offset)
        {
            offset = 2;
            return frame.Length is >= 2 and <= MaxFrameBytes &&
                   BinaryPrimitives.ReadUInt16LittleEndian(frame) == expected;
        }

        private static bool TryReadKey(
            ReadOnlySpan<byte> source,
            ref int offset,
            out int field,
            out int wire)
        {
            field = 0;
            wire = 0;
            if (!TryReadVarint(source, ref offset, out ulong key) || key == 0 || key > uint.MaxValue)
            {
                return false;
            }

            field = (int)(key >> 3);
            wire = (int)(key & 7);
            return field > 0;
        }

        private static bool TryReadUInt32(ReadOnlySpan<byte> source, ref int offset, out uint value)
        {
            value = 0;
            if (!TryReadVarint(source, ref offset, out ulong wide) || wide > uint.MaxValue)
            {
                return false;
            }

            value = (uint)wide;
            return true;
        }

        private static bool TryReadVarint(ReadOnlySpan<byte> source, ref int offset, out ulong value)
        {
            value = 0;
            for (int shift = 0; shift < 70; shift += 7)
            {
                if (offset >= source.Length) return false;
                byte current = source[offset++];
                if (shift == 63 && current > 1) return false;
                value |= (ulong)(current & 0x7f) << shift;
                if ((current & 0x80) == 0) return true;
            }

            return false;
        }

        private static bool TryReadFixed64(ReadOnlySpan<byte> source, ref int offset, out ulong value)
        {
            value = 0;
            if (source.Length - offset < 8) return false;
            value = BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(offset, 8));
            offset += 8;
            return true;
        }

        private static bool TryReadLength(
            ReadOnlySpan<byte> source,
            ref int offset,
            out ReadOnlySpan<byte> value)
        {
            value = default;
            if (!TryReadVarint(source, ref offset, out ulong length) ||
                length > int.MaxValue || (int)length > source.Length - offset)
            {
                return false;
            }

            value = source.Slice(offset, (int)length);
            offset += (int)length;
            return true;
        }

        private static bool TrySkip(ReadOnlySpan<byte> source, ref int offset, int wire)
        {
            switch (wire)
            {
                case 0: return TryReadVarint(source, ref offset, out _);
                case 1:
                    if (source.Length - offset < 8) return false;
                    offset += 8;
                    return true;
                case 2: return TryReadLength(source, ref offset, out _);
                case 5:
                    if (source.Length - offset < 4) return false;
                    offset += 4;
                    return true;
                default: return false;
            }
        }

        private static bool TryWriteByte(Span<byte> destination, ref int offset, byte value)
        {
            if ((uint)offset >= (uint)destination.Length) return false;
            destination[offset++] = value;
            return true;
        }

        private static bool TryWriteVarint(Span<byte> destination, ref int offset, ulong value)
        {
            do
            {
                byte current = (byte)(value & 0x7f);
                value >>= 7;
                if (value != 0) current |= 0x80;
                if (!TryWriteByte(destination, ref offset, current)) return false;
            }
            while (value != 0);

            return true;
        }

        private static bool TryWriteFixed64(Span<byte> destination, ref int offset, ulong value)
        {
            if (destination.Length - offset < 8) return false;
            BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(offset, 8), value);
            offset += 8;
            return true;
        }
    }
}
