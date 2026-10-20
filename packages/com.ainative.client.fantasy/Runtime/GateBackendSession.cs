using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AiNative.Client.Protocol.Protobuf;

namespace AiNative.Client.Fantasy
{
    /// <summary>Authenticated public Gate operations. Session credentials remain private and are never formatted into diagnostics.</summary>
    public sealed class GateBackendSession : IDisposable
    {
        private readonly FantasyGateClient _client;
        private string _credential = "";
        public string PlayerId { get; private set; } = "";
        public long ExpiresUnixSeconds { get; private set; }
        public bool IsConnected => _client.IsConnected;
        public bool IsAuthenticated =>
            IsConnected
            && _credential.Length != 0
            && ExpiresUnixSeconds > DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        public GateBackendSession(FantasyGateClient client) =>
            _client = client ?? throw new ArgumentNullException(nameof(client));

        public Task RegisterAsync(
            string username,
            string password,
            CancellationToken ct = default
        ) => AuthenticateAsync("player.register", username, password, ct);

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
                throw new ArgumentException("Account input is empty or too long.");
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
                throw new InvalidDataException("gate-invalid-login");
            PlayerId = player;
            _credential = credential;
            ExpiresUnixSeconds = expiry;
        }

        public async Task<GateProfile> ProfileAsync(CancellationToken ct = default)
        {
            GateProfile profile = GateWire.Profile(
                await CallAsync("player.profile", GateProtocolCodec.ProfileRequest(PlayerId), ct)
                    .ConfigureAwait(false)
            );
            if (profile.PlayerId != PlayerId)
                throw new InvalidDataException("gate-profile-identity");
            return profile;
        }

        public async Task<GateSettlement> SettlementAsync(
            string matchId,
            CancellationToken ct = default
        )
        {
            if (string.IsNullOrEmpty(matchId) || matchId.Length > 128)
                throw new ArgumentException("Match identity required.", nameof(matchId));
            var receipt = GateProtocolCodec.Settlement(
                await CallAsync(
                        "player.settlement-status",
                        GateProtocolCodec.SettlementRequest(matchId),
                        ct
                    )
                    .ConfigureAwait(false)
            );
            if (receipt.Match != matchId)
                throw new InvalidDataException("gate-settlement-identity");
            return new GateSettlement { MatchId = matchId, Confirmed = receipt.Confirmed };
        }

        public async Task<GateParty> CreatePartyAsync(CancellationToken ct = default) =>
            GateWire.Party(
                await CallAsync("lobby.create", Array.Empty<byte>(), ct).ConfigureAwait(false)
            );

        public async Task<GateParty> GetPartyAsync(
            string partyId,
            CancellationToken ct = default
        ) =>
            GateWire.Party(
                await CallAsync("lobby.get", GateProtocolCodec.PartyRequest(partyId), ct)
                    .ConfigureAwait(false)
            );

        public async Task<GateParty> ReadyAsync(
            GateParty party,
            bool ready,
            CancellationToken ct = default
        ) =>
            GateWire.Party(
                await CallAsync("lobby.ready", PartyCommand(party, "", ready), ct)
                    .ConfigureAwait(false)
            );

        public async Task<GateParty> InviteAsync(
            GateParty party,
            string playerId,
            CancellationToken ct = default
        ) =>
            GateWire.Party(
                await CallAsync("lobby.invite", PartyCommand(party, playerId, false), ct)
                    .ConfigureAwait(false)
            );

        public async Task<GateParty> AcceptAsync(
            GateParty invitation,
            CancellationToken ct = default
        ) =>
            GateWire.Party(
                await CallAsync("lobby.accept", PartyCommand(invitation, "", false), ct)
                    .ConfigureAwait(false)
            );

        public async Task LeaveAsync(GateParty party, CancellationToken ct = default)
        {
            await CallAsync("lobby.leave", PartyCommand(party, "", false), ct)
                .ConfigureAwait(false);
        }

        public async Task<GateParty[]> InvitationsAsync(CancellationToken ct = default)
        {
            var decoded = GateProtocolCodec.Invitations(
                await CallAsync("lobby.invites", Array.Empty<byte>(), ct).ConfigureAwait(false)
            );
            var parties = new GateParty[decoded.Length];
            for (int i = 0; i < parties.Length; i++)
                parties[i] = GateWire.Party(decoded[i]);
            return parties;
        }

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

        public async Task CancelQueueAsync(string requestId, CancellationToken ct = default)
        {
            await CallAsync("match.cancel", GateProtocolCodec.MatchRequest(requestId), ct)
                .ConfigureAwait(false);
        }

        public async Task<GateMatch> MatchAsync(string requestId, CancellationToken ct = default) =>
            GateWire.Match(
                await CallAsync("match.status", GateProtocolCodec.MatchRequest(requestId), ct)
                    .ConfigureAwait(false)
            );

        private async Task<byte[]> CallAsync(string method, byte[] payload, CancellationToken ct)
        {
            if (_credential.Length == 0)
                throw new GateCallException("unauthorized");
            if (ExpiresUnixSeconds <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                throw new GateCallException("session-expired");
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
                // Preserve the player binding so recovery cannot replace an active battle's identity.
                _credential = "";
                throw;
            }
        }

        private static byte[] PartyCommand(GateParty party, string target, bool ready) =>
            GateProtocolCodec.PartyRequest(party.PartyId, target, ready, party.Version);

        public void Dispose()
        {
            _credential = "";
            _client.Dispose();
        }
    }
}
