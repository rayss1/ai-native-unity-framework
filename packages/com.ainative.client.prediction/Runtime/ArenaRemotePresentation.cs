using System;
using AiNative.Client.Protocol;
using AiNative.Gameplay;

namespace AiNative.Client.Prediction
{
    public readonly struct ArenaRemotePose
    {
        public ArenaRemotePose(uint entityId, double x, double y, double z, double yaw, bool alive)
            : this(
                entityId,
                x,
                y,
                z,
                yaw,
                new ArenaPlayerState(0, (int)x, (int)y, (int)z)
                {
                    YawMillidegrees = (int)yaw,
                    Alive = alive,
                }
            ) { }

        public ArenaRemotePose(
            uint entityId,
            double x,
            double y,
            double z,
            double yaw,
            ArenaPlayerState state
        )
        {
            EntityId = entityId;
            XMillimetres = x;
            YMillimetres = y;
            ZMillimetres = z;
            YawMillidegrees = yaw;
            State = state;
        }

        public uint EntityId { get; }
        public double XMillimetres { get; }
        public double YMillimetres { get; }
        public double ZMillimetres { get; }
        public double YawMillidegrees { get; }
        public bool Alive => State.Alive;

        /// <summary>仅为表现采样，插值状态不得反向写入模拟。</summary>
        public ArenaPlayerState State { get; }
    }

    /// <summary>仅限所属线程；有界远端渲染历史，不修改本地预测。</summary>
    public sealed class ArenaRemotePresentation
    {
        public const int HistoryCapacity = 16;
        public const int DelayTicks = 6;
        public const int TeleportMillimetres = 5000;
        private readonly uint[] _ids = new uint[BattlePacketBuffer.MaxArenaPlayers];
        private readonly int[] _counts = new int[BattlePacketBuffer.MaxArenaPlayers];
        private readonly int[] _heads = new int[BattlePacketBuffer.MaxArenaPlayers];
        private readonly ArenaPlayerState[] _history = new ArenaPlayerState[
            BattlePacketBuffer.MaxArenaPlayers * HistoryCapacity
        ];
        private long _latestTick = -1;
        private double _renderTick;
        private bool _rebuffering;

        public bool ApplySnapshot(
            ReadOnlySpan<ArenaSnapshotPlayer> players,
            long tick,
            uint localEntityId
        )
        {
            int count = players.Length;
            if (
                localEntityId == 0
                || count > BattlePacketBuffer.MaxArenaPlayers
                || tick <= _latestTick
            )
            {
                return false;
            }

            // 修改成员或历史前，必须完成所有验证。
            for (int slot = 0; slot < _ids.Length; slot++)
            {
                bool present = false;
                for (int i = 0; i < count; i++)
                {
                    if (players[i].EntityId != localEntityId && players[i].EntityId == _ids[slot])
                    {
                        present = true;
                    }
                }

                if (!present)
                {
                    _ids[slot] = 0;
                    _counts[slot] = 0;
                    _heads[slot] = 0;
                }
            }

            for (int i = 0; i < count; i++)
            {
                if (players[i].EntityId == localEntityId)
                {
                    continue;
                }

                int slot = Array.IndexOf(_ids, players[i].EntityId);
                if (slot < 0)
                {
                    slot = Array.IndexOf(_ids, 0u);
                    _ids[slot] = players[i].EntityId;
                }

                ArenaPlayerState next = players[i].State;
                if (_counts[slot] > 0)
                {
                    ArenaPlayerState last = At(slot, _counts[slot] - 1);
                    double dx = (double)next.PositionXMillimetres - last.PositionXMillimetres;
                    double dy = (double)next.PositionYMillimetres - last.PositionYMillimetres;
                    double dz = (double)next.PositionZMillimetres - last.PositionZMillimetres;
                    if (
                        next.Alive != last.Alive
                        || dx * dx + dy * dy + dz * dz
                            > (double)TeleportMillimetres * TeleportMillimetres
                    )
                    {
                        _counts[slot] = 0;
                        _heads[slot] = 0;
                    }
                }

                if (_counts[slot] == HistoryCapacity)
                {
                    _heads[slot] = (_heads[slot] + 1) % HistoryCapacity;
                    _counts[slot]--;
                }

                _history[
                    slot * HistoryCapacity + (_heads[slot] + _counts[slot]) % HistoryCapacity
                ] = next;
                _counts[slot]++;
            }

            _latestTick = tick;
            _renderTick = Math.Max(_renderTick, tick - DelayTicks);
            return true;
        }

        /// <summary>调用方提供八人容量；无效时间按零处理，断流时保持最近权威状态。</summary>
        public int Advance(float deltaSeconds, Span<ArenaRemotePose> output)
        {
            if (output.Length < BattlePacketBuffer.MaxArenaPlayers)
            {
                throw new ArgumentException("Eight pose slots are required.", nameof(output));
            }

            if (_latestTick < 0)
            {
                return 0;
            }

            if (float.IsNaN(deltaSeconds) || float.IsInfinity(deltaSeconds) || deltaSeconds < 0)
            {
                deltaSeconds = 0;
            }

            // 断流后保持单调游标，直到延迟窗口重新建立。
            // 否则新快照将一直被立即消费，无法恢复插值延迟。
            if (_rebuffering && _latestTick - _renderTick >= DelayTicks)
            {
                _rebuffering = false;
            }

            if (!_rebuffering)
            {
                double requestedTick = _renderTick + deltaSeconds * 60d;
                _renderTick = Math.Min(_latestTick, requestedTick);
                if (requestedTick >= _latestTick && deltaSeconds > 0)
                {
                    _rebuffering = true;
                }
            }

            int written = 0;
            for (int slot = 0; slot < _ids.Length; slot++)
            {
                if (_ids[slot] == 0)
                {
                    continue;
                }

                ArenaPlayerState before = At(slot, 0),
                    after = before;
                for (int i = 1; i < _counts[slot] && after.Tick < _renderTick; i++)
                {
                    before = after;
                    after = At(slot, i);
                }

                double t =
                    after.Tick == before.Tick
                        ? 0
                        : Math.Max(
                            0,
                            Math.Min(1, (_renderTick - before.Tick) / (after.Tick - before.Tick))
                        );
                double yawDelta = ((double)after.YawMillidegrees - before.YawMillidegrees) % 360000;
                if (yawDelta > 180000)
                {
                    yawDelta -= 360000;
                }

                if (yawDelta < -180000)
                {
                    yawDelta += 360000;
                }

                double yaw = (before.YawMillidegrees + yawDelta * t) % 360000;
                if (yaw < 0)
                {
                    yaw += 360000;
                }

                double x = Lerp(before.PositionXMillimetres, after.PositionXMillimetres, t);
                double y = Lerp(before.PositionYMillimetres, after.PositionYMillimetres, t);
                double z = Lerp(before.PositionZMillimetres, after.PositionZMillimetres, t);
                // 在对应渲染 Tick 前，不暴露未来的武器、生命值或落地状态变化。
                // 生命周期变化已重置历史，因此死亡和重生仍立即跳转。
                ArenaPlayerState sample = t >= 1 ? after : before;
                sample.PositionXMillimetres = (int)Math.Round(x);
                sample.PositionYMillimetres = (int)Math.Round(y);
                sample.PositionZMillimetres = (int)Math.Round(z);
                sample.YawMillidegrees = (int)Math.Round(yaw) % 360000;
                sample.PitchMillidegrees = (int)
                    Math.Round(Lerp(before.PitchMillidegrees, after.PitchMillidegrees, t));
                sample.VelocityXMillimetresPerSecond = (int)
                    Math.Round(
                        Lerp(
                            before.VelocityXMillimetresPerSecond,
                            after.VelocityXMillimetresPerSecond,
                            t
                        )
                    );
                sample.VelocityYMillimetresPerSecond = (int)
                    Math.Round(
                        Lerp(
                            before.VelocityYMillimetresPerSecond,
                            after.VelocityYMillimetresPerSecond,
                            t
                        )
                    );
                sample.VelocityZMillimetresPerSecond = (int)
                    Math.Round(
                        Lerp(
                            before.VelocityZMillimetresPerSecond,
                            after.VelocityZMillimetresPerSecond,
                            t
                        )
                    );
                output[written++] = new ArenaRemotePose(_ids[slot], x, y, z, yaw, sample);
            }

            return written;
        }

        public void Reset()
        {
            Array.Clear(_ids, 0, _ids.Length);
            Array.Clear(_counts, 0, _counts.Length);
            Array.Clear(_heads, 0, _heads.Length);
            _latestTick = -1;
            _renderTick = 0;
            _rebuffering = false;
        }

        private ArenaPlayerState At(int slot, int offset) =>
            _history[slot * HistoryCapacity + (_heads[slot] + offset) % HistoryCapacity];

        private static double Lerp(int a, int b, double t) => a + ((double)b - a) * t;
    }
}
