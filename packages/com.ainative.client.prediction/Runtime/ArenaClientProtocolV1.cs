using System;
using System.Buffers.Binary;
using AiNative.Gameplay;
using AiNative.Realtime;

namespace AiNative.Client.Prediction
{
    internal readonly struct DecodedArenaSnapshot
    {
        public DecodedArenaSnapshot(
            ArenaPlayerState state,
            ArenaMatchPhase phase,
            uint remainingTicks,
            uint leaderEntityId,
            uint acknowledgement)
        {
            State = state;
            Phase = phase;
            RemainingTicks = remainingTicks;
            LeaderEntityId = leaderEntityId;
            Acknowledgement = acknowledgement;
        }

        public ArenaPlayerState State { get; }
        public ArenaMatchPhase Phase { get; }
        public uint RemainingTicks { get; }
        public uint LeaderEntityId { get; }
        public uint Acknowledgement { get; }
    }

    internal static class ArenaClientProtocolV1
    {
        public const int HeaderBytes = sizeof(ushort);
        public const int MaxDatagramBytes = 1200;
        public const int MaxInputFrameBytes = 64;
        public const ushort InputCommandMessageId = 1100;
        public const ushort SnapshotMessageId = 1101;

        public static readonly TransportChannel InputChannel = new TransportChannel(
            2,
            TransportDelivery.Unreliable,
            TransportOrdering.Sequenced);

        public static readonly TransportChannel SnapshotChannel = new TransportChannel(
            1,
            TransportDelivery.Unreliable,
            TransportOrdering.Sequenced);

        public static bool TryEncodeInput(
            in ArenaInput input,
            Span<byte> destination,
            out int writtenBytes)
        {
            writtenBytes = 0;
            if (destination.Length < HeaderBytes || input.Sequence == 0)
            {
                return false;
            }

            BinaryPrimitives.WriteUInt16LittleEndian(destination, InputCommandMessageId);
            int offset = HeaderBytes;
            if (!TryWriteByte(destination, ref offset, 0x09) ||
                !TryWriteFixed64(destination, ref offset, input.ClientTick) ||
                !TryWriteByte(destination, ref offset, 0x10) ||
                !TryWriteVarint(destination, ref offset, input.Sequence))
            {
                return false;
            }

            if (input.MoveXMilli != 0 && (!TryWriteByte(destination, ref offset, 0x18) ||
                !TryWriteVarint(destination, ref offset, ZigZag(input.MoveXMilli)))) return false;
            if (input.MoveZMilli != 0 && (!TryWriteByte(destination, ref offset, 0x20) ||
                !TryWriteVarint(destination, ref offset, ZigZag(input.MoveZMilli)))) return false;
            if (input.Buttons != ArenaButtons.None && (!TryWriteByte(destination, ref offset, 0x30) ||
                !TryWriteVarint(destination, ref offset, (uint)input.Buttons))) return false;
            if (input.Weapon != ArenaWeaponId.None && (!TryWriteByte(destination, ref offset, 0x38) ||
                !TryWriteVarint(destination, ref offset, (uint)input.Weapon))) return false;
            if (input.LookYawMilli != 0 && (!TryWriteByte(destination, ref offset, 0x40) ||
                !TryWriteVarint(destination, ref offset, ZigZag(input.LookYawMilli)))) return false;
            if (input.LookPitchMilli != 0 && (!TryWriteByte(destination, ref offset, 0x48) ||
                !TryWriteVarint(destination, ref offset, ZigZag(input.LookPitchMilli)))) return false;

            writtenBytes = offset;
            return writtenBytes <= MaxInputFrameBytes;
        }

        public static bool TryDecodeSnapshot(
            ReadOnlySpan<byte> frame,
            uint entityId,
            out DecodedArenaSnapshot decoded)
        {
            decoded = default;
            if (frame.Length < HeaderBytes || frame.Length > MaxDatagramBytes ||
                BinaryPrimitives.ReadUInt16LittleEndian(frame) != SnapshotMessageId)
            {
                return false;
            }

            ReadOnlySpan<byte> payload = frame.Slice(HeaderBytes);
            int offset = 0;
            uint protocolMajor = 0;
            ulong roomTick = 0;
            uint acknowledgement = 0;
            uint remainingTicks = 0;
            uint leaderEntityId = 0;
            ArenaMatchPhase phase = ArenaMatchPhase.Waiting;
            bool hasTick = false;
            bool found = false;
            ArenaPlayerState player = default;

            while (offset < payload.Length)
            {
                if (!TryReadKey(payload, ref offset, out int field, out int wire)) return false;
                switch (field)
                {
                    case 1 when wire == 0:
                        if (!TryReadUInt32(payload, ref offset, out protocolMajor)) return false;
                        break;
                    case 2 when wire == 1:
                        if (!TryReadFixed64(payload, ref offset, out roomTick)) return false;
                        hasTick = true;
                        break;
                    case 4 when wire == 2:
                        if (!TryReadLength(payload, ref offset, out ReadOnlySpan<byte> playerPayload) ||
                            !TryReadPlayer(playerPayload, entityId, ref found, ref player)) return false;
                        break;
                    case 6 when wire == 0:
                        if (!TryReadUInt32(payload, ref offset, out acknowledgement)) return false;
                        break;
                    case 7 when wire == 0:
                        if (!TryReadUInt32(payload, ref offset, out uint phaseValue) || phaseValue > 2) return false;
                        phase = (ArenaMatchPhase)phaseValue;
                        break;
                    case 8 when wire == 0:
                        if (!TryReadUInt32(payload, ref offset, out remainingTicks)) return false;
                        break;
                    case 9 when wire == 0:
                        if (!TryReadUInt32(payload, ref offset, out leaderEntityId)) return false;
                        break;
                    default:
                        if (!TrySkip(payload, ref offset, wire)) return false;
                        break;
                }
            }

            if (protocolMajor != 1 || !hasTick || roomTick > long.MaxValue || !found)
            {
                return false;
            }

            player.Tick = checked((long)roomTick);
            player.LastProcessedInputSequence = acknowledgement;
            decoded = new DecodedArenaSnapshot(player, phase, remainingTicks, leaderEntityId, acknowledgement);
            return true;
        }

        private static bool TryReadPlayer(
            ReadOnlySpan<byte> payload,
            uint expectedEntityId,
            ref bool found,
            ref ArenaPlayerState player)
        {
            int offset = 0;
            uint entityId = 0;
            int x = 0, y = 0, z = 0, vx = 0, vy = 0, vz = 0, yaw = 0, pitch = 0;
            uint health = 0, armor = 0, weapon = 0, kills = 0;
            bool alive = true;
            while (offset < payload.Length)
            {
                if (!TryReadKey(payload, ref offset, out int field, out int wire)) return false;
                switch (field)
                {
                    case 1 when wire == 0: if (!TryReadUInt32(payload, ref offset, out entityId)) return false; break;
                    case 2 when wire == 0: if (!TryReadUInt32(payload, ref offset, out uint valueX)) return false; x = UnZigZag(valueX); break;
                    case 3 when wire == 0: if (!TryReadUInt32(payload, ref offset, out uint valueY)) return false; y = UnZigZag(valueY); break;
                    case 4 when wire == 0: if (!TryReadUInt32(payload, ref offset, out uint valueZ)) return false; z = UnZigZag(valueZ); break;
                    case 5 when wire == 0: if (!TryReadUInt32(payload, ref offset, out uint valueYaw)) return false; yaw = UnZigZag(valueYaw); break;
                    case 6 when wire == 0: if (!TryReadUInt32(payload, ref offset, out health)) return false; break;
                    case 14 when wire == 0: if (!TryReadUInt32(payload, ref offset, out uint valuePitch)) return false; pitch = UnZigZag(valuePitch); break;
                    case 7 when wire == 0: if (!TryReadUInt32(payload, ref offset, out uint valueVx)) return false; vx = UnZigZag(valueVx); break;
                    case 8 when wire == 0: if (!TryReadUInt32(payload, ref offset, out uint valueVy)) return false; vy = UnZigZag(valueVy); break;
                    case 9 when wire == 0: if (!TryReadUInt32(payload, ref offset, out uint valueVz)) return false; vz = UnZigZag(valueVz); break;
                    case 10 when wire == 0: if (!TryReadUInt32(payload, ref offset, out weapon)) return false; break;
                    case 11 when wire == 0: if (!TryReadUInt32(payload, ref offset, out armor)) return false; break;
                    case 12 when wire == 0: if (!TryReadUInt32(payload, ref offset, out uint aliveValue)) return false; alive = aliveValue != 0; break;
                    case 13 when wire == 0: if (!TryReadUInt32(payload, ref offset, out kills)) return false; break;
                    default: if (!TrySkip(payload, ref offset, wire)) return false; break;
                }
            }

            if (entityId != expectedEntityId) return true;
            if (found) return false;
            player = new ArenaPlayerState(0, x, y, z)
            {
                VelocityXMillimetresPerSecond = vx,
                VelocityYMillimetresPerSecond = vy,
                VelocityZMillimetresPerSecond = vz,
                YawMillidegrees = yaw,
                PitchMillidegrees = pitch,
                Health = (int)Math.Min(health, int.MaxValue),
                Armor = (int)Math.Min(armor, int.MaxValue),
                Weapon = weapon is >= 1 and <= 3 ? (ArenaWeaponId)weapon : ArenaWeaponId.Machinegun,
                Alive = alive,
                Grounded = y == 0,
                Kills = kills,
            };
            found = true;
            return true;
        }

        private static bool TryReadKey(ReadOnlySpan<byte> source, ref int offset, out int field, out int wire)
        {
            field = 0; wire = 0;
            if (!TryReadVarint(source, ref offset, out ulong key) || key == 0 || key > uint.MaxValue) return false;
            field = (int)(key >> 3); wire = (int)(key & 7);
            return field > 0;
        }

        private static bool TryReadUInt32(ReadOnlySpan<byte> source, ref int offset, out uint value)
        {
            value = 0;
            if (!TryReadVarint(source, ref offset, out ulong wide) || wide > uint.MaxValue) return false;
            value = (uint)wide; return true;
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
            value = BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(offset, 8)); offset += 8; return true;
        }

        private static bool TryReadLength(ReadOnlySpan<byte> source, ref int offset, out ReadOnlySpan<byte> value)
        {
            value = default;
            if (!TryReadVarint(source, ref offset, out ulong length) || length > int.MaxValue || (int)length > source.Length - offset) return false;
            value = source.Slice(offset, (int)length); offset += (int)length; return true;
        }

        private static bool TrySkip(ReadOnlySpan<byte> source, ref int offset, int wire)
        {
            switch (wire)
            {
                case 0: return TryReadVarint(source, ref offset, out _);
                case 1: if (source.Length - offset < 8) return false; offset += 8; return true;
                case 2: return TryReadLength(source, ref offset, out _);
                case 5: if (source.Length - offset < 4) return false; offset += 4; return true;
                default: return false;
            }
        }

        private static bool TryWriteByte(Span<byte> destination, ref int offset, byte value)
        {
            if ((uint)offset >= (uint)destination.Length) return false;
            destination[offset++] = value; return true;
        }

        private static bool TryWriteFixed64(Span<byte> destination, ref int offset, ulong value)
        {
            if (destination.Length - offset < 8) return false;
            BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(offset, 8), value); offset += 8; return true;
        }

        private static bool TryWriteVarint(Span<byte> destination, ref int offset, ulong value)
        {
            do
            {
                byte current = (byte)(value & 0x7f); value >>= 7;
                if (value != 0) current |= 0x80;
                if (!TryWriteByte(destination, ref offset, current)) return false;
            } while (value != 0);
            return true;
        }

        private static ulong ZigZag(int value) => (ulong)((value << 1) ^ (value >> 31));
        private static int UnZigZag(uint value) => (int)(value >> 1) ^ -((int)value & 1);
    }
}
