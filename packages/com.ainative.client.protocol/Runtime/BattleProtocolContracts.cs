using System;
using AiNative.Gameplay;
using AiNative.Realtime;

namespace AiNative.Client.Protocol
{
    public enum BattleMessageKind
    {
        Unknown,
        LoginResponse,
        JoinResponse,
        Snapshot,
        ReconnectResponse,
        CombatEvent,
    }

    public enum BattleDecodeStatus
    {
        Accepted,
        FrameTooShort,
        FrameTooLarge,
        UnknownMessage,
        MalformedPayload,
        ProtocolMismatch,
        PlayerMissing,
        Disposed,
    }

    public readonly struct BattleAdmission
    {
        public BattleAdmission(string roomId, string entryTicket)
        {
            RoomId = roomId;
            EntryTicket = entryTicket;
        }

        public string RoomId { get; }
        public string EntryTicket { get; }
    }

    public readonly struct ArenaSnapshotPlayer
    {
        public ArenaSnapshotPlayer(uint entityId, ArenaPlayerState state)
        {
            EntityId = entityId;
            State = state;
        }

        public uint EntityId { get; }
        public ArenaPlayerState State { get; }
    }

    public readonly struct DecodedArenaSnapshot
    {
        public DecodedArenaSnapshot(
            ArenaPlayerState state,
            ArenaMatchPhase phase,
            uint remainingTicks,
            uint leaderEntityId,
            uint acknowledgement,
            bool hasArenaData = true
        )
        {
            State = state;
            Phase = phase;
            RemainingTicks = remainingTicks;
            LeaderEntityId = leaderEntityId;
            Acknowledgement = acknowledgement;
            HasArenaData = hasArenaData;
        }

        public ArenaPlayerState State { get; }
        public ArenaMatchPhase Phase { get; }
        public uint RemainingTicks { get; }
        public uint LeaderEntityId { get; }
        public uint Acknowledgement { get; }
        public bool HasArenaData { get; }
    }

    public enum ArenaPresentationEventKind : byte
    {
        Fire,
        Hit,
        Kill,
        Respawn,
        Pickup,
        WeaponSwitch,
    }

    public readonly struct ArenaPresentationEvent
    {
        public ArenaPresentationEvent(
            ArenaPresentationEventKind kind,
            uint sequence,
            ulong tick,
            uint sourceEntityId,
            uint targetEntityId,
            ArenaWeaponId weapon,
            int damage,
            int positionXMillimetres,
            int positionYMillimetres,
            int positionZMillimetres
        )
        {
            Kind = kind;
            Sequence = sequence;
            Tick = tick;
            SourceEntityId = sourceEntityId;
            TargetEntityId = targetEntityId;
            Weapon = weapon;
            Damage = damage;
            PositionXMillimetres = positionXMillimetres;
            PositionYMillimetres = positionYMillimetres;
            PositionZMillimetres = positionZMillimetres;
        }

        public ArenaPresentationEventKind Kind { get; }
        public uint Sequence { get; }
        public ulong Tick { get; }
        public uint SourceEntityId { get; }
        public uint TargetEntityId { get; }
        public ArenaWeaponId Weapon { get; }
        public int Damage { get; }
        public int PositionXMillimetres { get; }
        public int PositionYMillimetres { get; }
        public int PositionZMillimetres { get; }
    }

    /// <summary>调用方持有的解码存储；视图在下一次成功解码时失效。
    /// 解码失败必须保持全部字段和数组不变；仅限所属线程。</summary>
    public sealed class BattlePacketBuffer
    {
        public const int MaxPlayers = 64;
        public const int MaxArenaPlayers = 8;
        public const int MaxPickups = 32;
        public readonly ArenaSnapshotPlayer[] Players = new ArenaSnapshotPlayer[MaxPlayers];
        public readonly ArenaPickupState[] Pickups = new ArenaPickupState[MaxPickups];
        public BattleMessageKind Kind;
        public int PlayerCount;
        public int PickupCount;
        public ulong Tick;
        public ulong SessionId;
        public uint Acknowledgement;
        public uint ConnectionEpoch;
        public uint RoomId;
        public uint EntityId;
        public uint TickRate;
        public uint RemainingTicks;
        public uint LeaderEntityId;
        public string GlobalRoomId = "";
        public string BootEpoch = "";
        public bool HasArenaData;
        public ArenaMatchPhase Phase;
        public ArenaPresentationEvent CombatEvent;
        public ReadOnlySpan<ArenaSnapshotPlayer> PlayerSpan => Players.AsSpan(0, PlayerCount);
        public ReadOnlySpan<ArenaPickupState> PickupSpan => Pickups.AsSpan(0, PickupCount);

        public bool TryGetPlayer(uint id, out DecodedArenaSnapshot snapshot)
        {
            for (int i = 0; i < PlayerCount; i++)
            {
                if (Players[i].EntityId == id)
                {
                    var state = Players[i].State;
                    state.LastProcessedInputSequence = Acknowledgement;
                    snapshot = new DecodedArenaSnapshot(
                        state,
                        Phase,
                        RemainingTicks,
                        LeaderEntityId,
                        Acknowledgement,
                        HasArenaData
                    );
                    return true;
                }
            }

            snapshot = default;
            return false;
        }
    }

    /// <summary>每个会话显式持有一个实例，仅限所属线程；不得保留调用方 Span。
    /// TryEncode 失败写入零字节且不推进玩法；Reset 丢弃临时连接状态，
    /// Dispose 幂等；实现不持有传输层所有权。</summary>
    public interface IBattleProtocolCodec : IDisposable
    {
        int MaximumFrameBytes { get; }

        int MaximumInputFrameBytes { get; }

        TransportChannel ControlChannel { get; }

        TransportChannel InputChannel { get; }

        TransportChannel SnapshotChannel { get; }

        TransportChannel EventChannel { get; }

        bool TryEncodeInput(in ArenaInput input, Span<byte> destination, out int writtenBytes);
        bool TryEncodeLegacyInput(
            ulong tick,
            in KinematicInput input,
            Span<byte> destination,
            out int writtenBytes
        );
        bool TryEncodeLogin(
            string clientBuild,
            in BattleAdmission admission,
            Span<byte> destination,
            out int writtenBytes
        );
        bool TryEncodeJoin(
            ulong sessionId,
            uint roomId,
            Span<byte> destination,
            out int writtenBytes
        );
        bool TryEncodeReconnect(
            ulong sessionId,
            uint epoch,
            ulong lastTick,
            Span<byte> destination,
            out int writtenBytes
        );
        BattleDecodeStatus TryDecode(ReadOnlySpan<byte> frame, BattlePacketBuffer destination);
        void Reset();
    }
}
