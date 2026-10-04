using System;
using AiNative.Gameplay;
namespace AiNative.Client.Prediction
{
    public readonly struct ArenaSnapshotPlayer
    {
        public ArenaSnapshotPlayer(uint entityId, ArenaPlayerState state) { EntityId = entityId; State = state; }
        public uint EntityId { get; }
        public ArenaPlayerState State { get; }
    }
    public readonly struct ArenaRemotePose
    {
        public ArenaRemotePose(uint entityId, double x, double y, double z, double yaw, bool alive)
        { EntityId = entityId; XMillimetres = x; YMillimetres = y; ZMillimetres = z; YawMillidegrees = yaw; Alive = alive; }
        public uint EntityId { get; }
        public double XMillimetres { get; }
        public double YMillimetres { get; }
        public double ZMillimetres { get; }
        public double YawMillidegrees { get; }
        public bool Alive { get; }
    }
    /// <summary>Owner-thread only. Bounded remote render history; never changes local prediction.</summary>
    public sealed class ArenaRemotePresentation
    {
        public const int HistoryCapacity = 16;
        public const int DelayTicks = 6;
        public const int TeleportMillimetres = 5000;
        private readonly uint[] _ids = new uint[ArenaClientProtocolV1.MaxSnapshotPlayers];
        private readonly int[] _counts = new int[ArenaClientProtocolV1.MaxSnapshotPlayers];
        private readonly int[] _heads = new int[ArenaClientProtocolV1.MaxSnapshotPlayers];
        private readonly ArenaPlayerState[] _history = new ArenaPlayerState[ArenaClientProtocolV1.MaxSnapshotPlayers * HistoryCapacity];
        private long _latestTick = -1;
        private double _renderTick;
        private bool _rebuffering;

        public bool ApplySnapshot(ReadOnlySpan<byte> frame, uint localEntityId)
        {
            Span<ArenaSnapshotPlayer> players = stackalloc ArenaSnapshotPlayer[ArenaClientProtocolV1.MaxSnapshotPlayers];
            if (localEntityId == 0 || !ArenaClientProtocolV1.TryDecodePlayers(frame, players, out int count, out long tick) || tick <= _latestTick) return false;
            // All validation completes before membership or history changes.
            for (int slot = 0; slot < _ids.Length; slot++)
            {
                bool present = false;
                for (int i = 0; i < count; i++) if (players[i].EntityId != localEntityId && players[i].EntityId == _ids[slot]) present = true;
                if (!present) { _ids[slot] = 0; _counts[slot] = 0; _heads[slot] = 0; }
            }
            for (int i = 0; i < count; i++)
            {
                if (players[i].EntityId == localEntityId) continue;
                int slot = Array.IndexOf(_ids, players[i].EntityId);
                if (slot < 0) { slot = Array.IndexOf(_ids, 0u); _ids[slot] = players[i].EntityId; }
                ArenaPlayerState next = players[i].State;
                if (_counts[slot] > 0)
                {
                    ArenaPlayerState last = At(slot, _counts[slot] - 1);
                    double dx = (double)next.PositionXMillimetres - last.PositionXMillimetres;
                    double dy = (double)next.PositionYMillimetres - last.PositionYMillimetres;
                    double dz = (double)next.PositionZMillimetres - last.PositionZMillimetres;
                    if (next.Alive != last.Alive || dx * dx + dy * dy + dz * dz > (double)TeleportMillimetres * TeleportMillimetres)
                    { _counts[slot] = 0; _heads[slot] = 0; }
                }
                if (_counts[slot] == HistoryCapacity) { _heads[slot] = (_heads[slot] + 1) % HistoryCapacity; _counts[slot]--; }
                _history[slot * HistoryCapacity + (_heads[slot] + _counts[slot]) % HistoryCapacity] = next;
                _counts[slot]++;
            }
            _latestTick = tick;
            _renderTick = Math.Max(_renderTick, tick - DelayTicks);
            return true;
        }

        /// <summary>Supply capacity for eight players. Invalid time is treated as zero; starvation holds latest authority.</summary>
        public int Advance(float deltaSeconds, Span<ArenaRemotePose> output)
        {
            if (output.Length < ArenaClientProtocolV1.MaxSnapshotPlayers) throw new ArgumentException("Eight pose slots are required.", nameof(output));
            if (_latestTick < 0) return 0;
            if (float.IsNaN(deltaSeconds) || float.IsInfinity(deltaSeconds) || deltaSeconds < 0) deltaSeconds = 0;
            // After starvation hold the monotonic cursor until the delayed window has rebuilt.
            // Without this pause each new arrival would be consumed immediately forever.
            if (_rebuffering && _latestTick - _renderTick >= DelayTicks) _rebuffering = false;
            if (!_rebuffering)
            {
                double requestedTick = _renderTick + deltaSeconds * 60d;
                _renderTick = Math.Min(_latestTick, requestedTick);
                if (requestedTick >= _latestTick && deltaSeconds > 0) _rebuffering = true;
            }
            int written = 0;
            for (int slot = 0; slot < _ids.Length; slot++)
            {
                if (_ids[slot] == 0) continue;
                ArenaPlayerState before = At(slot, 0), after = before;
                for (int i = 1; i < _counts[slot] && after.Tick < _renderTick; i++) { before = after; after = At(slot, i); }
                double t = after.Tick == before.Tick ? 0 : Math.Max(0, Math.Min(1, (_renderTick - before.Tick) / (after.Tick - before.Tick)));
                double yawDelta = ((double)after.YawMillidegrees - before.YawMillidegrees) % 360000;
                if (yawDelta > 180000) yawDelta -= 360000;
                if (yawDelta < -180000) yawDelta += 360000;
                double yaw = (before.YawMillidegrees + yawDelta * t) % 360000;
                if (yaw < 0) yaw += 360000;
                output[written++] = new ArenaRemotePose(_ids[slot],
                    Lerp(before.PositionXMillimetres, after.PositionXMillimetres, t),
                    Lerp(before.PositionYMillimetres, after.PositionYMillimetres, t),
                    Lerp(before.PositionZMillimetres, after.PositionZMillimetres, t), yaw, after.Alive);
            }
            return written;
        }
        public void Reset()
        {
            Array.Clear(_ids, 0, _ids.Length); Array.Clear(_counts, 0, _counts.Length); Array.Clear(_heads, 0, _heads.Length);
            _latestTick = -1; _renderTick = 0; _rebuffering = false;
        }
        private ArenaPlayerState At(int slot, int offset) => _history[slot * HistoryCapacity + (_heads[slot] + offset) % HistoryCapacity];
        private static double Lerp(int a, int b, double t) => a + ((double)b - a) * t;
    }
}
