using System;
using System.Buffers.Binary;
using System.Text;
using AiNative.Client.Protocol;
using AiNative.Gameplay;
using AiNative.Realtime;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Wire = AiNative.Protocol.V1;

namespace AiNative.Client.Protocol.Protobuf
{
    /// <summary>Session-owned, single-threaded codec. Generated messages never escape this boundary.</summary>
    public sealed class ProtobufBattleProtocolCodec : IBattleProtocolCodec
    {
        private readonly Wire.InputCommand input = new Wire.InputCommand();
        private readonly byte[] receive = new byte[1200];
        private readonly BattlePacketBuffer scratch = new BattlePacketBuffer();
        private bool disposed;
        public int MaximumFrameBytes => 1200;
        public int MaximumInputFrameBytes => 64;
        public TransportChannel ControlChannel => new TransportChannel(0, TransportDelivery.Reliable, TransportOrdering.Ordered);
        public TransportChannel InputChannel => new TransportChannel(2, TransportDelivery.Unreliable, TransportOrdering.Sequenced);
        public TransportChannel SnapshotChannel => new TransportChannel(1, TransportDelivery.Unreliable, TransportOrdering.Sequenced);
        public TransportChannel EventChannel => new TransportChannel(3, TransportDelivery.Reliable, TransportOrdering.Ordered);

        public bool TryEncodeInput(in ArenaInput value, Span<byte> destination, out int writtenBytes)
        {
            writtenBytes = 0;
            if (disposed || value.Sequence == 0) return false;
            input.RoomTick = value.ClientTick; input.Sequence = value.Sequence;
            input.MoveXMilli = value.MoveXMilli; input.MoveYMilli = value.MoveZMilli;
            input.YawMillidegrees = 0; input.Buttons = (uint)value.Buttons; input.WeaponId = (uint)value.Weapon;
            input.LookYawMilli = value.LookYawMilli; input.LookPitchMilli = value.LookPitchMilli;
            return Encode(1100, input, destination, MaximumInputFrameBytes, out writtenBytes);
        }
        public bool TryEncodeLegacyInput(ulong tick, in KinematicInput value, Span<byte> destination, out int writtenBytes)
        {
            if (value.Sequence == 0) { writtenBytes = 0; return false; }
            var arena = new ArenaInput(value.Sequence, tick, value.MoveXMilli, value.MoveZMilli, 0, 0, ArenaButtons.None, ArenaWeaponId.None);
            return TryEncodeInput(arena, destination, out writtenBytes);
        }
        public bool TryEncodeLogin(string clientBuild, in BattleAdmission admission, Span<byte> destination, out int writtenBytes)
        {
            writtenBytes = 0;
            if (disposed || Encoding.UTF8.GetByteCount(clientBuild ?? "") > 128) return false;
            return Encode(1000, new Wire.LoginRequest { ProtocolMajor = 1, ClientBuild = clientBuild ?? "",
                EntryTicket = admission.EntryTicket ?? "", GlobalRoomId = admission.RoomId ?? "" }, destination, MaximumFrameBytes, out writtenBytes);
        }
        public bool TryEncodeJoin(ulong sessionId, uint roomId, Span<byte> destination, out int writtenBytes)
        {
            writtenBytes = 0;
            return !disposed && sessionId != 0 && roomId != 0 && Encode(1010,
                new Wire.JoinRoomRequest { SessionId = sessionId, RequestedRoom = roomId }, destination, MaximumFrameBytes, out writtenBytes);
        }
        public bool TryEncodeReconnect(ulong sessionId, uint epoch, ulong lastTick, Span<byte> destination, out int writtenBytes)
        {
            writtenBytes = 0;
            return !disposed && sessionId != 0 && epoch != 0 && lastTick <= long.MaxValue && Encode(1200,
                new Wire.ReconnectRequest { SessionId = sessionId, ConnectionEpoch = epoch, LastReceivedTick = lastTick }, destination, MaximumFrameBytes, out writtenBytes);
        }
        private static bool Encode(ushort id, IMessage message, Span<byte> destination, int maximum, out int written)
        {
            written = 0;
            int length = message.CalculateSize() + 2;
            if (length > maximum || length > destination.Length) return false;
            message.WriteTo(destination.Slice(2, length - 2));
            BinaryPrimitives.WriteUInt16LittleEndian(destination, id);
            written = length; return true;
        }
        public BattleDecodeStatus TryDecode(ReadOnlySpan<byte> frame, BattlePacketBuffer destination)
        {
            if (disposed) return BattleDecodeStatus.Disposed;
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (frame.Length < 2) return BattleDecodeStatus.FrameTooShort;
            if (frame.Length > MaximumFrameBytes) return BattleDecodeStatus.FrameTooLarge;
            ushort id = BinaryPrimitives.ReadUInt16LittleEndian(frame);
            MessageDescriptor descriptor;
            switch (id)
            {
                case 1001: descriptor = Wire.LoginResponse.Descriptor; break;
                case 1011: descriptor = Wire.JoinRoomResponse.Descriptor; break;
                case 1101: descriptor = Wire.Snapshot.Descriptor; break;
                case 1102: descriptor = Wire.ReliableEvent.Descriptor; break;
                case 1201: descriptor = Wire.ReconnectResponse.Descriptor; break;
                default: return BattleDecodeStatus.UnknownMessage;
            }
            int length = frame.Length - 2;
            frame.Slice(2).CopyTo(receive);
            Clear(scratch);
            try
            {
                bool arena = false;
                using (var validation = new CodedInputStream(receive, 0, length))
                    if (!Validate(validation, length, descriptor, ref arena, 0)) return BattleDecodeStatus.MalformedPayload;
                switch (id)
                {
                    case 1001:
                        var login = Wire.LoginResponse.Parser.ParseFrom(receive, 0, length);
                        if (login.SessionId == 0 || login.ConnectionEpoch == 0 || login.RoomTick > long.MaxValue) return BattleDecodeStatus.MalformedPayload;
                        scratch.Kind = BattleMessageKind.LoginResponse; scratch.SessionId = login.SessionId;
                        scratch.ConnectionEpoch = login.ConnectionEpoch; scratch.Tick = login.RoomTick;
                        scratch.GlobalRoomId = login.GlobalRoomId; scratch.BootEpoch = login.BootEpoch;
                        break;
                    case 1011:
                        var join = Wire.JoinRoomResponse.Parser.ParseFrom(receive, 0, length);
                        if (join.RoomId == 0 || join.EntityId == 0 || join.TickRate == 0) return BattleDecodeStatus.MalformedPayload;
                        scratch.Kind = BattleMessageKind.JoinResponse; scratch.RoomId = join.RoomId;
                        scratch.EntityId = join.EntityId; scratch.TickRate = join.TickRate;
                        break;
                    case 1101:
                        var snapshot = Wire.Snapshot.Parser.ParseFrom(receive, 0, length);
                        if (snapshot.ProtocolMajor != 1) return BattleDecodeStatus.ProtocolMismatch;
                        if (!MapSnapshot(snapshot, arena)) return BattleDecodeStatus.MalformedPayload;
                        scratch.Kind = BattleMessageKind.Snapshot;
                        break;
                    case 1201:
                        var reconnect = Wire.ReconnectResponse.Parser.ParseFrom(receive, 0, length);
                        if (reconnect.Snapshot == null || reconnect.ConnectionEpoch == 0 || reconnect.ResumeTick != reconnect.Snapshot.RoomTick) return BattleDecodeStatus.MalformedPayload;
                        if (reconnect.Snapshot.ProtocolMajor != 1) return BattleDecodeStatus.ProtocolMismatch;
                        if (!MapSnapshot(reconnect.Snapshot, arena)) return BattleDecodeStatus.MalformedPayload;
                        scratch.ConnectionEpoch = reconnect.ConnectionEpoch; scratch.Kind = BattleMessageKind.ReconnectResponse;
                        break;
                    case 1102:
                        var reliable = Wire.ReliableEvent.Parser.ParseFrom(receive, 0, length);
                        if (!MapEvent(reliable)) return BattleDecodeStatus.MalformedPayload;
                        break;
                }
                Copy(scratch, destination);
                return BattleDecodeStatus.Accepted;
            }
            catch (InvalidProtocolBufferException) { return BattleDecodeStatus.MalformedPayload; }
            catch (ArgumentException) { return BattleDecodeStatus.MalformedPayload; }
        }
        // Validate wire shape with the library, not a second custom Protobuf decoder. This preserves
        // strict singular-field and boolean checks, and proto3 field presence for legacy discrimination.
        private static bool Validate(CodedInputStream stream, long end, MessageDescriptor descriptor, ref bool arena, int depth)
        {
            if (depth > 8) return false;
            ulong seen = 0;
            while (stream.Position < end)
            {
                uint tag = stream.ReadTag();
                if (tag == 0) return false;
                int number = (int)(tag >> 3), wire = (int)(tag & 7);
                var field = descriptor.FindFieldByNumber(number);
                if (field == null)
                {
                    if (wire == 3 || wire == 4) return false;
                    stream.SkipLastField();
                }
                else
                {
                    int expected = field.FieldType == FieldType.Fixed64 ? 1 :
                        field.FieldType == FieldType.String || field.FieldType == FieldType.Bytes || field.FieldType == FieldType.Message ? 2 : 0;
                    if (wire != expected) return false;
                    ulong bit = 1UL << number;
                    if (!field.IsRepeated && (seen & bit) != 0) return false;
                    seen |= bit;
                    if (descriptor == Wire.PlayerState.Descriptor && number >= 7 && number <= 14) arena = true;
                    if (field.FieldType == FieldType.Message)
                    {
                        int size = stream.ReadLength();
                        long childEnd = stream.Position + size;
                        if (size < 0 || childEnd > end || !Validate(stream, childEnd, field.MessageType, ref arena, depth + 1)) return false;
                    }
                    else if (wire == 0)
                    {
                        ulong value = stream.ReadUInt64();
                        if (value > uint.MaxValue || (field.FieldType == FieldType.Bool && value > 1)) return false;
                    }
                    else stream.SkipLastField();
                }
                if (stream.Position > end) return false;
            }
            return stream.Position == end;
        }
        private bool MapSnapshot(Wire.Snapshot value, bool arena)
        {
            if (value.RoomTick > long.MaxValue || value.BaselineTick > value.RoomTick ||
                (int)value.MatchPhase < 0 || (int)value.MatchPhase > 2 || value.Players.Count > (arena ? BattlePacketBuffer.MaxArenaPlayers : BattlePacketBuffer.MaxPlayers) ||
                value.Pickups.Count > BattlePacketBuffer.MaxPickups) return false;
            for (int i = 0; i < value.Players.Count; i++)
            {
                var p = value.Players[i];
                if (p.EntityId == 0 || p.Health > int.MaxValue || p.Armor > int.MaxValue || p.WeaponId > 3 || (arena && p.EntityId > 8)) return false;
                for (int j = 0; j < i; j++) if (scratch.Players[j].EntityId == p.EntityId) return false;
                var state = new ArenaPlayerState((long)value.RoomTick, p.PositionXMilli, p.PositionYMilli, p.PositionZMilli)
                {
                    VelocityXMillimetresPerSecond = p.VelocityXMilliPerSecond, VelocityYMillimetresPerSecond = p.VelocityYMilliPerSecond,
                    VelocityZMillimetresPerSecond = p.VelocityZMilliPerSecond, YawMillidegrees = p.YawMillidegrees, PitchMillidegrees = p.PitchMillidegrees,
                    Health = (int)p.Health, Armor = (int)p.Armor, Weapon = p.WeaponId == 0 ? ArenaWeaponId.Machinegun : (ArenaWeaponId)p.WeaponId,
                    Alive = p.Alive, Grounded = p.PositionYMilli == 0, Kills = p.Kills, LastProcessedInputSequence = value.LastProcessedInputSequence
                };
                scratch.Players[i] = new ArenaSnapshotPlayer(p.EntityId, state);
            }
            for (int i = 0; i < value.Pickups.Count; i++)
            {
                var p = value.Pickups[i];
                if (p.PickupId == 0 || p.PickupId > int.MaxValue || (int)p.PickupType < 1 || (int)p.PickupType > 2 || p.RespawnTick > long.MaxValue) return false;
                for (int j = 0; j < i; j++) if (scratch.Pickups[j].Id == (int)p.PickupId) return false;
                scratch.Pickups[i] = new ArenaPickupState((int)p.PickupId, (ArenaPickupType)p.PickupType, p.PositionXMilli, p.PositionYMilli, p.PositionZMilli, p.Active, p.RespawnTick);
            }
            scratch.PlayerCount = value.Players.Count; scratch.PickupCount = value.Pickups.Count; scratch.Tick = value.RoomTick;
            scratch.Acknowledgement = value.LastProcessedInputSequence; scratch.Phase = (ArenaMatchPhase)value.MatchPhase;
            scratch.RemainingTicks = value.RemainingTicks; scratch.LeaderEntityId = value.LeaderEntityId; scratch.HasArenaData = arena;
            return true;
        }
        private bool MapEvent(Wire.ReliableEvent value)
        {
            var e = value.CombatEvent;
            if (e == null || value.Sequence == 0 || value.RoomTick > long.MaxValue || value.EventType > 5 ||
                (uint)e.EventType != value.EventType || e.EventTick != value.RoomTick || e.SourceEntityId == 0 || e.SourceEntityId > 8 ||
                (int)e.WeaponId < 0 || (int)e.WeaponId > 3 || e.Damage > int.MaxValue) return false;
            bool playerTarget = value.EventType == 1 || value.EventType == 2;
            if (playerTarget && (e.TargetEntityId == 0 || e.TargetEntityId > 8)) return false;
            if (value.EventType == 4 ? e.TargetEntityId == 0 || (int)e.WeaponId != 0 : (int)e.WeaponId == 0) return false;
            if (!playerTarget && value.EventType != 4 && e.TargetEntityId != 0) return false;
            scratch.Kind = BattleMessageKind.CombatEvent; scratch.Tick = value.RoomTick;
            scratch.CombatEvent = new ArenaPresentationEvent((ArenaPresentationEventKind)value.EventType, value.Sequence, value.RoomTick,
                e.SourceEntityId, e.TargetEntityId, (ArenaWeaponId)e.WeaponId, (int)e.Damage, e.PositionXMilli, e.PositionYMilli, e.PositionZMilli);
            return true;
        }
        private static void Clear(BattlePacketBuffer value)
        {
            value.Kind = BattleMessageKind.Unknown; value.PlayerCount = value.PickupCount = 0;
            value.Tick = value.SessionId = 0; value.Acknowledgement = value.ConnectionEpoch = value.RoomId = value.EntityId = value.TickRate = value.RemainingTicks = value.LeaderEntityId = 0;
            value.GlobalRoomId = value.BootEpoch = ""; value.HasArenaData = false; value.Phase = default; value.CombatEvent = default;
        }
        private static void Copy(BattlePacketBuffer source, BattlePacketBuffer target)
        {
            source.PlayerSpan.CopyTo(target.Players); source.PickupSpan.CopyTo(target.Pickups);
            target.Kind = source.Kind; target.PlayerCount = source.PlayerCount; target.PickupCount = source.PickupCount;
            target.Tick = source.Tick; target.SessionId = source.SessionId; target.Acknowledgement = source.Acknowledgement; target.ConnectionEpoch = source.ConnectionEpoch;
            target.RoomId = source.RoomId; target.EntityId = source.EntityId; target.TickRate = source.TickRate; target.RemainingTicks = source.RemainingTicks; target.LeaderEntityId = source.LeaderEntityId;
            target.GlobalRoomId = source.GlobalRoomId; target.BootEpoch = source.BootEpoch; target.HasArenaData = source.HasArenaData; target.Phase = source.Phase; target.CombatEvent = source.CombatEvent;
        }
        public void Reset() { Array.Clear(receive, 0, receive.Length); Clear(scratch); }
        public void Dispose() { if (disposed) return; Reset(); disposed = true; }
    }
}
