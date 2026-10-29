using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AiNative.Client.Protocol.Protobuf;

namespace AiNative.Client.Fantasy
{
    /// <summary>经过认证的公开 Gate 操作；会话凭证保持私有，不得写入诊断内容。</summary>
    /// <remarks>
    /// 不依赖 Unity 主线程，也不捕获同步上下文；调用方须串行组织认证、业务调用和释放，勿并发修改会话状态。
    /// 所有异步操作沿用 FantasyGateClient 的排队、超时和取消语义：交换开始后的取消会关闭连接。
    /// 参数错误、网络错误、协议解析错误及 GateCallException 原样传播；取消不保证服务器未执行操作，恢复后应查询权威状态。
    /// 返回的 DTO 是调用方拥有的快照；本对象不负责其后续修改。不得记录用户名口令或私有凭证。
    /// </remarks>
    public sealed class GateBackendSession : IDisposable
    {
        private readonly FantasyGateClient _client;
        private string _credential = "";

        /// <summary>最近成功认证的玩家标识；凭证失效或释放不会清除此绑定。</summary>
        public string PlayerId { get; private set; } = "";

        /// <summary>最近成功认证凭证的 UTC Unix 到期时间，单位为秒。</summary>
        public long ExpiresUnixSeconds { get; private set; }

        /// <summary>本地传输尚未关闭；不代表已经探测到远端在线。</summary>
        public bool IsConnected => _client.IsConnected;

        /// <summary>本地连接、凭证和 UTC 到期时间满足认证前置条件；服务器仍可拒绝凭证。</summary>
        public bool IsAuthenticated =>
            IsConnected
            && _credential.Length != 0
            && ExpiresUnixSeconds > DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        /// <summary>接管传入客户端的释放责任；调用方不得再独立释放该客户端。</summary>
        public GateBackendSession(FantasyGateClient client) =>
            _client = client ?? throw new ArgumentNullException(nameof(client));

        /// <summary>注册并建立当前认证身份；成功后替换玩家绑定和私有凭证。</summary>
        public Task RegisterAsync(
            string username,
            string password,
            CancellationToken ct = default
        ) => AuthenticateAsync("player.register", username, password, ct);

        /// <summary>登录并建立当前认证身份；成功后替换玩家绑定和私有凭证。</summary>
        public Task LoginAsync(string username, string password, CancellationToken ct = default) =>
            AuthenticateAsync("player.login", username, password, ct);

        private async Task AuthenticateAsync(
            string method,
            string username,
            string password,
            CancellationToken ct
        )
        {
            if (
                string.IsNullOrWhiteSpace(username)
                || string.IsNullOrEmpty(password)
                || username.Length > 64
                || password.Length > 256
            )
            {
                throw new ArgumentException("Account input is empty or too long.");
            }

            var login = GateProtocolCodec.Login(
                await _client
                    .CallAsync(
                        method,
                        GateProtocolCodec.Account(username, password),
                        cancellationToken: ct
                    )
                    .ConfigureAwait(false)
            );
            string player = login.Player,
                credential = login.Credential;
            long expiry = login.Expiry;
            if (
                player.Length == 0
                || credential.Length == 0
                || expiry <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            )
            {
                throw new InvalidDataException("gate-invalid-login");
            }

            PlayerId = player;
            _credential = credential;
            ExpiresUnixSeconds = expiry;
        }

        /// <summary>查询当前玩家资料快照；响应身份不匹配时抛出 InvalidDataException。</summary>
        public async Task<GateProfile> ProfileAsync(CancellationToken ct = default)
        {
            GateProfile profile = GateWire.Profile(
                await CallAsync("player.profile", GateProtocolCodec.ProfileRequest(PlayerId), ct)
                    .ConfigureAwait(false)
            );
            if (profile.PlayerId != PlayerId)
            {
                throw new InvalidDataException("gate-profile-identity");
            }

            return profile;
        }

        /// <summary>查询指定对局的结算持久化确认；确认成功不代表房间或匹配队列已经释放。</summary>
        public async Task<GateSettlement> SettlementAsync(
            string matchId,
            CancellationToken ct = default
        )
        {
            if (string.IsNullOrEmpty(matchId) || matchId.Length > 128)
            {
                throw new ArgumentException("Match identity required.", nameof(matchId));
            }

            var receipt = GateProtocolCodec.Settlement(
                await CallAsync(
                        "player.settlement-status",
                        GateProtocolCodec.SettlementRequest(matchId),
                        ct
                    )
                    .ConfigureAwait(false)
            );
            if (receipt.Match != matchId)
            {
                throw new InvalidDataException("gate-settlement-identity");
            }

            return new GateSettlement { MatchId = matchId, Confirmed = receipt.Confirmed };
        }

        /// <summary>创建队伍并返回服务器快照。</summary>
        public async Task<GateParty> CreatePartyAsync(CancellationToken ct = default) =>
            GateWire.Party(
                await CallAsync("lobby.create", Array.Empty<byte>(), ct).ConfigureAwait(false)
            );

        /// <summary>查询指定队伍的当前权威快照。</summary>
        public async Task<GateParty> GetPartyAsync(
            string partyId,
            CancellationToken ct = default
        ) =>
            GateWire.Party(
                await CallAsync("lobby.get", GateProtocolCodec.PartyRequest(partyId), ct)
                    .ConfigureAwait(false)
            );

        /// <summary>按传入队伍快照的版本设置准备状态，返回更新后的快照。</summary>
        public async Task<GateParty> ReadyAsync(
            GateParty party,
            bool ready,
            CancellationToken ct = default
        ) =>
            GateWire.Party(
                await CallAsync("lobby.ready", PartyCommand(party, "", ready), ct)
                    .ConfigureAwait(false)
            );

        /// <summary>按队伍版本邀请指定玩家，返回服务器队伍快照。</summary>
        public async Task<GateParty> InviteAsync(
            GateParty party,
            string playerId,
            CancellationToken ct = default
        ) =>
            GateWire.Party(
                await CallAsync("lobby.invite", PartyCommand(party, playerId, false), ct)
                    .ConfigureAwait(false)
            );

        /// <summary>按邀请快照的队伍与版本接受邀请，返回当前队伍快照。</summary>
        public async Task<GateParty> AcceptAsync(
            GateParty invitation,
            CancellationToken ct = default
        ) =>
            GateWire.Party(
                await CallAsync("lobby.accept", PartyCommand(invitation, "", false), ct)
                    .ConfigureAwait(false)
            );

        /// <summary>按队伍快照的版本离开队伍；不修改调用方持有的快照。</summary>
        public async Task LeaveAsync(GateParty party, CancellationToken ct = default)
        {
            await CallAsync("lobby.leave", PartyCommand(party, "", false), ct)
                .ConfigureAwait(false);
        }

        /// <summary>查询邀请列表；返回数组及其中快照由调用方持有。</summary>
        public async Task<GateParty[]> InvitationsAsync(CancellationToken ct = default)
        {
            var decoded = GateProtocolCodec.Invitations(
                await CallAsync("lobby.invites", Array.Empty<byte>(), ct).ConfigureAwait(false)
            );
            var parties = new GateParty[decoded.Length];
            for (int i = 0; i < parties.Length; i++)
            {
                parties[i] = GateWire.Party(decoded[i]);
            }

            return parties;
        }

        /// <summary>使用调用方提供的请求标识及队伍版本发起匹配，返回权威匹配快照。</summary>
        public async Task<GateMatch> QueueAsync(
            GateParty party,
            string requestId,
            CancellationToken ct = default
        )
        {
            byte[] response = await CallAsync(
                    "lobby.queue",
                    GateProtocolCodec.QueueRequest(party.PartyId, party.Version, requestId),
                    ct
                )
                .ConfigureAwait(false);
            return GateWire.Match(response, queueResponse: true);
        }

        /// <summary>请求取消指定队列；活动分配能否取消由服务器决定，不强行释放对局。</summary>
        public async Task CancelQueueAsync(string requestId, CancellationToken ct = default)
        {
            await CallAsync("match.cancel", GateProtocolCodec.MatchRequest(requestId), ct)
                .ConfigureAwait(false);
        }

        /// <summary>查询指定请求的匹配状态与分配快照。</summary>
        public async Task<GateMatch> MatchAsync(string requestId, CancellationToken ct = default) =>
            GateWire.Match(
                await CallAsync("match.status", GateProtocolCodec.MatchRequest(requestId), ct)
                    .ConfigureAwait(false)
            );

        private async Task<byte[]> CallAsync(string method, byte[] payload, CancellationToken ct)
        {
            if (_credential.Length == 0)
            {
                throw new GateCallException("unauthorized");
            }

            if (ExpiresUnixSeconds <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            {
                throw new GateCallException("session-expired");
            }

            try
            {
                return await _client
                    .CallAsync(method, payload, _credential, ct)
                    .ConfigureAwait(false);
            }
            catch (GateCallException error)
                when (error.Code == "invalid_session"
                    || error.Code == "invalid-session"
                    || error.Code == "session-expired"
                    || error.Code == "unauthorized"
                )
            {
                // 保留玩家绑定，防止恢复流程替换正在进行的对局身份。
                _credential = "";
                throw;
            }
        }

        private static byte[] PartyCommand(GateParty party, string target, bool ready) =>
            GateProtocolCodec.PartyRequest(party.PartyId, target, ready, party.Version);

        /// <summary>清除私有凭证并幂等关闭拥有的传输；不等待在途调用，调用方仍须观察其完成或异常。</summary>
        public void Dispose()
        {
            _credential = "";
            _client.Dispose();
        }
    }
}
