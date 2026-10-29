using System;
using System.Threading;
using AiNative.Client.Fantasy;
using Cysharp.Threading.Tasks;

namespace AiNative.Client.Application
{
    /// <summary>
    /// 单个应用的账号和大厅状态拥有者。Flow 串行调度调用，服务不启动计时器或后台轮询。
    /// 候选登录完成身份与资料验证后才替换旧会话；失败必须保留原会话。
    /// </summary>
    internal sealed class TopologyAccountLobbyService : IDisposable
    {
        public GateBackendSession Backend { get; set; }
        public GateProfile Profile { get; set; }
        public GateParty Party { get; set; }
        public GateParty[] Invitations { get; set; } = Array.Empty<GateParty>();

        public async UniTask AuthenticateAsync(
            GateConnectionOptions options,
            string username,
            string password,
            bool register,
            string requiredPlayerId,
            CancellationToken cancellationToken
        )
        {
            // Gate 网络端口仍返回 Task；这里直接 await，不改变请求顺序或网络执行线程。
            var candidate = new GateBackendSession(
                await FantasyGateClient.ConnectAsync(options, cancellationToken)
            );
            try
            {
                if (register)
                {
                    await candidate.RegisterAsync(username, password, cancellationToken);
                }
                else
                {
                    await candidate.LoginAsync(username, password, cancellationToken);
                }
                if (requiredPlayerId != null && candidate.PlayerId != requiredPlayerId)
                {
                    throw new GateCallException("account-mismatch-active-battle");
                }
                GateProfile profile = await candidate.ProfileAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                Backend?.Dispose();
                Backend = candidate;
                Profile = profile;
            }
            catch
            {
                candidate.Dispose();
                throw;
            }
        }

        public async UniTask CreatePartyAsync(CancellationToken cancellationToken)
        {
            if (Party == null)
            {
                Party = await Backend.CreatePartyAsync(cancellationToken);
            }
        }

        public async UniTask InviteAsync(string playerId, CancellationToken cancellationToken)
        {
            if (Party != null)
            {
                Party = await Backend.InviteAsync(
                    Party,
                    (playerId ?? "").Trim(),
                    cancellationToken
                );
            }
        }

        public async UniTask AcceptInvitationAsync(
            string partyId,
            CancellationToken cancellationToken
        )
        {
            if (Party != null)
            {
                return;
            }
            foreach (GateParty invitation in Invitations)
            {
                if (invitation.PartyId == partyId)
                {
                    Party = await Backend.AcceptAsync(invitation, cancellationToken);
                    return;
                }
            }
            throw new GateCallException("invitation_expired");
        }

        public async UniTask SetReadyAsync(bool ready, CancellationToken cancellationToken)
        {
            if (Party != null)
            {
                Party = await Backend.ReadyAsync(Party, ready, cancellationToken);
            }
        }

        public async UniTask LeavePartyAsync(CancellationToken cancellationToken)
        {
            if (Party != null)
            {
                await Backend.LeaveAsync(Party, cancellationToken);
                Party = null;
            }
        }

        public async UniTask RefreshLobbyAsync(CancellationToken cancellationToken)
        {
            // 顺序保留：资料与邀请成功后才刷新已有队伍，不并发改变会话请求顺序。
            Profile = await Backend.ProfileAsync(cancellationToken);
            Invitations = await Backend.InvitationsAsync(cancellationToken);
            if (Party != null)
            {
                Party = await Backend.GetPartyAsync(Party.PartyId, cancellationToken);
            }
        }

        public void Dispose()
        {
            Backend?.Dispose();
        }
    }
}
