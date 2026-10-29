using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AiNative.Client.Core;
using AiNative.Client.Fantasy;
using AiNative.Client.Protocol.Protobuf;
using Cysharp.Threading.Tasks;

namespace AiNative.Client.Application
{
    /// <summary>
    /// 单个应用的对局分配和 Battle 会话拥有者；网络 Pump 与预测 Tick 仍由 Flow 在原阶段驱动。
    /// 恢复只能使用原分配身份，缺失字段仅在显式允许的结算查询路径中接受。
    /// </summary>
    internal sealed class TopologyMatchController
    {
        private sealed class BattleModule : IClientModule
        {
            private readonly BattleClientSession _battle;

            internal BattleModule(BattleClientSession battle)
            {
                _battle = battle;
            }

            public ValueTask StartAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _battle.Start();
                return default;
            }

            public ValueTask StopAsync(CancellationToken cancellationToken)
            {
                // 生命周期停止不可被调用方取消；部分启动也必须释放会话。
                return _battle.DisposeAsync();
            }
        }

        private ClientApplication _battleLifetime;
        private BattleClientSession _battle;
        private BattleClientSession _lifetimeBattle;
        private Task _stopTask;

        public GateMatch Match { get; set; }
        public string RequestId { get; set; } = "";

        /// <summary>保留测试与旧入口注入；已有会话必须先 Stop，不能静默覆盖其所有权。</summary>
        public BattleClientSession Battle
        {
            get => _battle;
            set
            {
                if (ReferenceEquals(_battle, value))
                {
                    return;
                }
                if (_battle != null || (_stopTask != null && !_stopTask.IsCompleted))
                {
                    throw new InvalidOperationException("battle-owner-must-stop-before-replace");
                }
                _battle = value;
                if (value != null)
                {
                    _stopTask = null;
                }
            }
        }

        /// <summary>为分配创建一次独立子生命周期；旧对局未停止时拒绝覆盖，取消与失败清理部分启动。</summary>
        internal async UniTask StartAllocatedBattleAsync(string playerId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (
                _battle != null
                || _battleLifetime != null
                || (_stopTask != null && !_stopTask.IsCompleted)
            )
            {
                throw new InvalidOperationException("battle-owner-must-stop-before-replace");
            }
            if (Match == null)
            {
                throw new InvalidOperationException("battle-allocation-required");
            }
            int split = Match.Address.LastIndexOf(':');
            if (split < 1 || !int.TryParse(Match.Address.Substring(split + 1), out int port))
            {
                throw new InvalidDataException("invalid-battle-address");
            }
            Battle = new BattleClientSession(
                new ProtobufBattleProtocolCodec(),
                Match.Address.Substring(0, split),
                port,
                new BattleAdmissionInfo(
                    Match.RoomId,
                    Match.BootEpoch,
                    Match.EntryTicket,
                    Match.NodeId,
                    playerId
                )
            );
            _lifetimeBattle = Battle;
            _battleLifetime = new ClientApplication(
                new IClientModule[] { new BattleModule(Battle) }
            );
            try
            {
                await _battleLifetime.StartAsync(token);
            }
            catch
            {
                // Core 已逆序释放部分启动；保留启动异常，后续 Stop 仍可幂等收尾。
                _battle = null;
                throw;
            }
        }

        public void ValidateOriginalMatch(GateMatch candidate, bool allowMissing)
        {
            if (
                Match == null
                || Mismatch(candidate.MatchId, Match.MatchId, allowMissing)
                || Mismatch(candidate.RoomId, Match.RoomId, allowMissing)
                || Mismatch(candidate.NodeId, Match.NodeId, allowMissing)
                || Mismatch(candidate.BootEpoch, Match.BootEpoch, allowMissing)
            )
            {
                throw new GateCallException("allocation-mismatch-active-battle");
            }
        }

        /// <summary>幂等停止本次子生命周期和手动注入会话；同一次停止的调用共享完成或失败。</summary>
        public ValueTask StopAsync()
        {
            if (_stopTask == null)
            {
                var completion = new UniTaskCompletionSource();
                _stopTask = completion.Task.AsTask();
                StopOwnedBattleAsync(completion).Forget();
            }
            return new ValueTask(_stopTask);
        }

        private async UniTask StopOwnedBattleAsync(UniTaskCompletionSource completion)
        {
            BattleClientSession battle = _battle;
            Exception failure = null;
            try
            {
                if (_battleLifetime != null)
                {
                    await _battleLifetime.StopAsync();
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            try
            {
                if (battle != null && !ReferenceEquals(battle, _lifetimeBattle))
                {
                    // 兼容手动注入会话：它没有启动模块，但所有权仍归当前控制器。
                    await battle.DisposeAsync();
                }
            }
            catch (Exception exception)
            {
                failure = failure == null ? exception : new AggregateException(failure, exception);
            }
            // 完成源可能同步恢复 Flow；必须先清空 Battle，返回大厅才看不到旧会话。
            _battle = null;
            if (failure == null)
            {
                _battleLifetime = null;
                _lifetimeBattle = null;
                completion.TrySetResult();
            }
            else
            {
                completion.TrySetException(failure);
            }
        }

        private static bool Mismatch(string candidate, string original, bool allowMissing) =>
            candidate != original && !(allowMissing && candidate.Length == 0);
    }
}
