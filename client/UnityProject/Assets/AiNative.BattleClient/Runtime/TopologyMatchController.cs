using System.IO;
using System.Threading.Tasks;
using AiNative.Client.Fantasy;
using AiNative.Client.Protocol.Protobuf;

namespace AiNative.Client.Application
{
    /// <summary>
    /// 单个应用的对局分配和 Battle 会话拥有者；网络 Pump 与预测 Tick 仍由 Flow 在原阶段驱动。
    /// 恢复只能使用原分配身份，缺失字段仅在显式允许的结算查询路径中接受。
    /// </summary>
    internal sealed class TopologyMatchController
    {
        public GateMatch Match { get; set; }
        public string RequestId { get; set; } = "";
        public BattleClientSession Battle { get; set; }

        public void StartAllocatedBattle(string playerId)
        {
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
            Battle.Start();
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

        public async ValueTask StopAsync()
        {
            if (Battle != null)
            {
                await Battle.DisposeAsync();
            }
        }

        private static bool Mismatch(string candidate, string original, bool allowMissing) =>
            candidate != original && !(allowMissing && candidate.Length == 0);
    }
}
