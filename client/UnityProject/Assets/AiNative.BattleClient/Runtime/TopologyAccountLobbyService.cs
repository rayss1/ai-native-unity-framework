using System;
using System.Threading;
using System.Threading.Tasks;
using AiNative.Client.Fantasy;

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

        public async Task AuthenticateAsync(
            GateConnectionOptions options,
            string username,
            string password,
            bool register,
            string requiredPlayerId,
            CancellationToken cancellationToken
        )
        {
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

        public async Task CreatePartyAsync(CancellationToken cancellationToken)
        {
            if (Party == null)
            {
                Party = await Backend.CreatePartyAsync(cancellationToken);
            }
        }

        public async Task InviteAsync(string playerId, CancellationToken cancellationToken)
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

        public async Task AcceptInvitationAsync(string partyId, CancellationToken cancellationToken)
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

        public async Task SetReadyAsync(bool ready, CancellationToken cancellationToken)
        {
            if (Party != null)
            {
                Party = await Backend.ReadyAsync(Party, ready, cancellationToken);
            }
        }

        public async Task LeavePartyAsync(CancellationToken cancellationToken)
        {
            if (Party != null)
            {
                await Backend.LeaveAsync(Party, cancellationToken);
                Party = null;
            }
        }

        public async Task RefreshLobbyAsync(CancellationToken cancellationToken)
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
