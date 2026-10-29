using System;
using System.Buffers.Binary;
using AiNative.Client.Protocol;
using AiNative.Gameplay;
using AiNative.Realtime;

namespace AiNative.Client.Prediction
{
    public static class ArenaCombatEventProtocolV1
    {
        public const ushort MessageId = 1102;
        public static readonly TransportChannel EventChannel = new TransportChannel(
            3,
            TransportDelivery.Reliable,
            TransportOrdering.Ordered
        );

        public static bool TryDecode(ReadOnlySpan<byte> frame, out ArenaPresentationEvent result)
        {
            result = default;
            if (
                frame.Length < 2
                || frame.Length > ArenaClientProtocolV1.MaxDatagramBytes
                || BinaryPrimitives.ReadUInt16LittleEndian(frame) != MessageId
            )
            {
                return false;
            }

            int offset = 2,
                seen = 0;
            ulong tick = 0;
            uint sequence = 0,
                kind = 0;
            ReadOnlySpan<byte> combat = default;
            while (offset < frame.Length)
            {
                if (!TryReadKey(frame, ref offset, out int field, out int wire))
                {
                    return false;
                }

                if (field <= 5)
                {
                    int expected;
                    if (field == 1)
                    {
                        expected = 1;
                    }
                    else
                    {
                        expected = field >= 4 ? 2 : 0;
                    }
                    int bit = 1 << field;
                    if (wire != expected || (seen & bit) != 0)
                    {
                        return false;
                    }

                    seen |= bit;
                }

                switch (field)
                {
                    case 1:
                        if (!TryReadFixed64(frame, ref offset, out tick))
                        {
                            return false;
                        }

                        break;
                    case 2:
                        if (!TryReadUInt32(frame, ref offset, out sequence))
                        {
                            return false;
                        }

                        break;
                    case 3:
                        if (!TryReadUInt32(frame, ref offset, out kind))
                        {
                            return false;
                        }

                        break;
                    case 5:
                        if (!TryReadLength(frame, ref offset, out var eventBytes))
                        {
                            return false;
                        }

                        combat = eventBytes.ToArray();
                        break;
                    default:
                        if (!TrySkip(frame, ref offset, wire))
                        {
                            return false;
                        }

                        break;
                }
            }

            if (sequence == 0 || tick > long.MaxValue || kind > 5 || (seen & (1 << 5)) == 0)
            {
                return false;
            }

            offset = 0;
            seen = 0;
            ulong eventTick = 0;
            Span<uint> values = stackalloc uint[10];
            values.Clear();
            while (offset < combat.Length)
            {
                if (!TryReadKey(combat, ref offset, out int field, out int wire))
                {
                    return false;
                }

                if (field <= 9)
                {
                    int bit = 1 << field;
                    if (wire != (field == 2 ? 1 : 0) || (seen & bit) != 0)
                    {
                        return false;
                    }

                    seen |= bit;
                    if (field == 2)
                    {
                        if (!TryReadFixed64(combat, ref offset, out eventTick))
                        {
                            return false;
                        }
                    }
                    else if (!TryReadUInt32(combat, ref offset, out values[field]))
                    {
                        return false;
                    }
                }
                else if (!TrySkip(combat, ref offset, wire))
                {
                    return false;
                }
            }

            if (
                eventTick != tick
                || values[1] != kind
                || values[3] == 0
                || values[3] > ArenaClientProtocolV1.MaxSnapshotPlayers
                || values[5] > 3
                || values[6] > int.MaxValue
            )
            {
                return false;
            }

            bool targetPlayer = kind == 1 || kind == 2;
            if (
                targetPlayer
                && (values[4] == 0 || values[4] > ArenaClientProtocolV1.MaxSnapshotPlayers)
            )
            {
                return false;
            }

            if (kind == 4 ? values[4] == 0 || values[5] != 0 : values[5] == 0)
            {
                return false;
            }

            if (!targetPlayer && kind != 4 && values[4] != 0)
            {
                return false;
            }

            result = new ArenaPresentationEvent(
                (ArenaPresentationEventKind)kind,
                sequence,
                tick,
                values[3],
                values[4],
                (ArenaWeaponId)values[5],
                (int)values[6],
                DecodeSigned(values[7]),
                DecodeSigned(values[8]),
                DecodeSigned(values[9])
            );
            return true;
        }

        private static int DecodeSigned(uint value) => (int)(value >> 1) ^ -((int)value & 1);

        private static bool TryReadKey(
            ReadOnlySpan<byte> source,
            ref int offset,
            out int field,
            out int wire
        )
        {
            field = 0;
            wire = 0;
            if (
                !TryReadVarint(source, ref offset, out ulong key)
                || key == 0
                || key > uint.MaxValue
            )
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

        private static bool TryReadVarint(
            ReadOnlySpan<byte> source,
            ref int offset,
            out ulong value
        )
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
            out ulong value
        )
        {
            value = 0;
            if (source.Length - offset < 8)
            {
                return false;
            }

            value = BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(offset, 8));
            offset += 8;
            return true;
        }

        private static bool TryReadLength(
            ReadOnlySpan<byte> source,
            ref int offset,
            out ReadOnlySpan<byte> value
        )
        {
            value = default;
            if (
                !TryReadVarint(source, ref offset, out ulong length)
                || length > int.MaxValue
                || (int)length > source.Length - offset
            )
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
                case 0:
                    return TryReadVarint(source, ref offset, out _);
                case 1:
                    if (source.Length - offset < 8)
                    {
                        return false;
                    }

                    offset += 8;
                    return true;
                case 2:
                    return TryReadLength(source, ref offset, out _);
                case 5:
                    if (source.Length - offset < 4)
                    {
                        return false;
                    }

                    offset += 4;
                    return true;
                default:
                    return false;
            }
        }
    }
}
