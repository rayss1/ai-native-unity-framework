using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

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
        public GateBackendSession(FantasyGateClient client) => _client = client ?? throw new ArgumentNullException(nameof(client));
        public Task RegisterAsync(string username, string password, CancellationToken ct = default) => AuthenticateAsync("player.register", username, password, ct);
        public Task LoginAsync(string username, string password, CancellationToken ct = default) => AuthenticateAsync("player.login", username, password, ct);
        private async Task AuthenticateAsync(string method, string username, string password, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password) || username.Length > 64 || password.Length > 256) throw new ArgumentException("Account input is empty or too long.");
            var login = GateWire.Read(await _client.CallAsync(method, GateWire.Join(GateWire.Text(1, username), GateWire.Text(2, password)), cancellationToken: ct).ConfigureAwait(false));
            string player = GateWire.String(login, 1), credential = GateWire.String(login, 2); long expiry = checked((long)GateWire.Value(login, 3));
            if (player.Length == 0 || credential.Length == 0 || expiry <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) throw new InvalidDataException("gate-invalid-login");
            PlayerId = player; _credential = credential; ExpiresUnixSeconds = expiry;
        }
        public async Task<GateProfile> ProfileAsync(CancellationToken ct = default)
        {
            GateProfile profile = GateWire.Profile(await CallAsync("player.profile", GateWire.Text(1, PlayerId), ct).ConfigureAwait(false));
            if (profile.PlayerId != PlayerId) throw new InvalidDataException("gate-profile-identity"); return profile;
        }
        public async Task<GateSettlement> SettlementAsync(string matchId, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(matchId) || matchId.Length > 128) throw new ArgumentException("Match identity required.", nameof(matchId));
            var receipt = GateWire.Read(await CallAsync("player.settlement-status", GateWire.Text(1, matchId), ct).ConfigureAwait(false));
            if (GateWire.String(receipt, 1) != matchId) throw new InvalidDataException("gate-settlement-identity");
            return new GateSettlement { MatchId = matchId, Confirmed = GateWire.Value(receipt, 3) != 0 };
        }
        public async Task<GateParty> CreatePartyAsync(CancellationToken ct = default) => GateWire.Party(await CallAsync("lobby.create", Array.Empty<byte>(), ct).ConfigureAwait(false));
        public async Task<GateParty> GetPartyAsync(string partyId, CancellationToken ct = default) => GateWire.Party(await CallAsync("lobby.get", GateWire.Text(1, partyId), ct).ConfigureAwait(false));
        public async Task<GateParty> ReadyAsync(GateParty party, bool ready, CancellationToken ct = default) => GateWire.Party(await CallAsync("lobby.ready", PartyCommand(party, "", ready), ct).ConfigureAwait(false));
        public async Task<GateParty> InviteAsync(GateParty party, string playerId, CancellationToken ct = default) => GateWire.Party(await CallAsync("lobby.invite", PartyCommand(party, playerId, false), ct).ConfigureAwait(false));
        public async Task<GateParty> AcceptAsync(GateParty invitation, CancellationToken ct = default) => GateWire.Party(await CallAsync("lobby.accept", PartyCommand(invitation, "", false), ct).ConfigureAwait(false));
        public async Task LeaveAsync(GateParty party, CancellationToken ct = default) { await CallAsync("lobby.leave", PartyCommand(party, "", false), ct).ConfigureAwait(false); }
        public async Task<GateParty[]> InvitationsAsync(CancellationToken ct = default)
        {
            var fields = GateWire.Read(await CallAsync("lobby.invites", Array.Empty<byte>(), ct).ConfigureAwait(false)); var parties = new List<GateParty>();
            foreach (var field in fields) if (field.Id == 1) { if (field.Wire != 2 || parties.Count >= 64) throw new InvalidDataException("gate-invitation-limit"); parties.Add(GateWire.Party(field.Data)); }
            return parties.ToArray();
        }
        public async Task<GateMatch> QueueAsync(GateParty party, string requestId, CancellationToken ct = default)
        {
            byte[] response = await CallAsync("lobby.queue", GateWire.Join(GateWire.Text(1, party.PartyId), GateWire.Number(2, party.Version), GateWire.Text(3, requestId)), ct).ConfigureAwait(false);
            return GateWire.Match(GateWire.Bytes(1, response));
        }
        public async Task CancelQueueAsync(string requestId, CancellationToken ct = default) { await CallAsync("match.cancel", GateWire.Text(1, requestId), ct).ConfigureAwait(false); }
        public async Task<GateMatch> MatchAsync(string requestId, CancellationToken ct = default) => GateWire.Match(await CallAsync("match.status", GateWire.Text(1, requestId), ct).ConfigureAwait(false));
        private Task<byte[]> CallAsync(string method, byte[] payload, CancellationToken ct)
        {
            if (_credential.Length == 0) throw new GateCallException("unauthorized");
            if (ExpiresUnixSeconds <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) throw new GateCallException("session-expired");
            return _client.CallAsync(method, payload, _credential, ct);
        }
        private static byte[] PartyCommand(GateParty party, string target, bool ready) => GateWire.Join(GateWire.Text(1, party.PartyId), GateWire.Text(2, target), GateWire.Number(3, ready ? 1UL : 0UL), GateWire.Number(4, party.Version));
        public void Dispose() { _credential = ""; _client.Dispose(); }
    }
}
