using System.Security.Cryptography;
using System.Text.Json;
using AiNative.Protocol.Backend.V1;
using AiNative.Server.Control;
using Google.Protobuf;
using Npgsql;

namespace AiNative.Server.Backend;

public sealed class PlayerService : IServiceHandler
{
    readonly IPlayerStore store;
    readonly SignedTokens tokens;
    readonly TimeProvider clock;
    readonly IServiceRpc? roomRpc;
    readonly TimeSpan sessionLifetime, entryLifetime;
    public PlayerService(IPlayerStore store, RSA signingKey, TimeProvider clock, IServiceRpc? roomRpc = null, TimeSpan? sessionLifetime = null, TimeSpan? entryLifetime = null)
    {
        this.store = store; this.clock = clock; this.roomRpc = roomRpc; tokens = new(signingKey, clock);
        this.sessionLifetime = sessionLifetime ?? TimeSpan.FromHours(8);
        this.entryLifetime = entryLifetime ?? TimeSpan.FromMinutes(2);
        if (this.sessionLifetime <= TimeSpan.Zero || this.entryLifetime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(sessionLifetime));
    }
    public async ValueTask<ServiceReply> HandleAsync(ServiceCallContext context, string method, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        try
        {
            if (payload.Length > 65536) return ServiceReply.Reject("request_too_large");
            switch (method)
            {
                case ServiceMethods.RegisterAccount:
                case ServiceMethods.Login:
                {
                    if (context.Caller != ServiceRole.Gate) return ServiceReply.Reject("forbidden");
                    var request = AccountRequest.Parser.ParseFrom(payload.Span);
                    var username = request.Username.Trim().ToLowerInvariant();
                    if (username.Length is < 3 or > 32 || !username.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') || request.Password.Length is < 12 or > 128) return ServiceReply.Reject("invalid_credentials");
                    PlayerAccount? account;
                    if (method == ServiceMethods.RegisterAccount)
                    {
                        var salt = RandomNumberGenerator.GetBytes(32);
                        account = new(Guid.NewGuid().ToString("N"), username, salt, HashPassword(request.Password, salt));
                        if (!await store.CreateAsync(account, cancellationToken)) return ServiceReply.Reject("username_exists");
                    }
                    else
                    {
                        account = await store.FindAsync(username, cancellationToken);
                        // Always run the KDF, including unknown accounts, to reduce username timing disclosure.
                        var actual = HashPassword(request.Password, account?.Salt ?? new byte[32]);
                        if (account is null || !CryptographicOperations.FixedTimeEquals(actual, account.PasswordHash)) return ServiceReply.Reject("invalid_credentials");
                    }
                    var expiry = clock.GetUtcNow().Add(sessionLifetime).ToUnixTimeSeconds();
                    return ServiceReply.From(new LoginResult { PlayerId = account.PlayerId, ExpiresUnixSeconds = expiry, SessionToken = tokens.Sign(new("session", account.PlayerId, "", "", "", expiry)) });
                }
                case ServiceMethods.ValidateSession:
                {
                    if (context.Caller != ServiceRole.Gate) return ServiceReply.Reject("forbidden");
                    var request = SessionRequest.Parser.ParseFrom(payload.Span);
                    if (!tokens.TryRead(request.SessionToken, "session", out var claims)) return ServiceReply.Reject("invalid_session");
                    return ServiceReply.From(new PlayerIdentity { PlayerId = claims!.Player, ExpiresUnixSeconds = claims.Expiry });
                }
                case ServiceMethods.Profile:
                {
                    var request = ProfileRequest.Parser.ParseFrom(payload.Span);
                    if (context.Caller != ServiceRole.Gate || context.PlayerId.Length == 0 || context.PlayerId != request.PlayerId) return ServiceReply.Reject("forbidden");
                    var profile = await store.ProfileAsync(request.PlayerId, cancellationToken);
                    return profile is null ? ServiceReply.Reject("unknown_player") : ServiceReply.From(profile);
                }
                case ServiceMethods.IssueTicket:
                {
                    var request = TicketRequest.Parser.ParseFrom(payload.Span);
                    if (context.Caller is not (ServiceRole.Gate or ServiceRole.Match) || (context.Caller == ServiceRole.Gate && context.PlayerId != request.PlayerId)) return ServiceReply.Reject("forbidden");
                    var room = request.Allocation;
                    if (room is null || room.State != "Ready" || !Identifiers.Valid(request.PlayerId, room.MatchId, room.RoomId, room.NodeId, room.BootEpoch) || !room.PlayerIds.Contains(request.PlayerId) || room.PlayerIds.Distinct().Count() != room.PlayerIds.Count) return ServiceReply.Reject("invalid_allocation");
                    var authoritative = await GetRoomAsync(room.RoomId, room.MatchId, cancellationToken);
                    if (authoritative.State != "Ready" || authoritative.AllocationId != room.AllocationId || !SameRoom(authoritative, room.MatchId, room.RoomId, room.NodeId, room.BootEpoch, room.PlayerIds)) return ServiceReply.Reject("invalid_allocation");
                    if (await store.ProfileAsync(request.PlayerId, cancellationToken) is null) return ServiceReply.Reject("unknown_player");
                    var expiry = clock.GetUtcNow().Add(entryLifetime).ToUnixTimeSeconds();
                    return ServiceReply.From(new EntryTicket { ExpiresUnixSeconds = expiry, Ticket = tokens.Sign(new("entry", request.PlayerId, room.RoomId, room.NodeId, room.BootEpoch, expiry)) });
                }
                case ServiceMethods.Settle:
                {
                    var result = MatchResult.Parser.ParseFrom(payload.Span);
                    if (context.Caller != ServiceRole.Battle || context.PeerId != result.NodeId) return ServiceReply.Reject("forbidden");
                    if (!Identifiers.Valid(result.MatchId, result.RoomId, result.NodeId, result.BootEpoch) || result.Completion.Length is 0 or > 64 || result.Players.Count is 0 or > 128 || result.Players.Any(p => !Identifiers.Valid(p.PlayerId) || p.Kills is < 0 or > 1000000) || result.Players.Select(p => p.PlayerId).Distinct().Count() != result.Players.Count) return ServiceReply.Reject("invalid_result");
                    var authoritative = await GetRoomAsync(result.RoomId, result.MatchId, cancellationToken);
                    if (!SameRoom(authoritative, result.MatchId, result.RoomId, result.NodeId, result.BootEpoch, result.Players.Select(p => p.PlayerId))) return ServiceReply.Reject("invalid_result");
                    // Canonical roster ordering makes exact semantic retries independent of serialization order.
                    var canonical = ResultPayload.Canonicalize(result);
                    var hash = ResultPayload.Hash(canonical);
                    return ServiceReply.From(await store.SettleAsync(canonical, hash, cancellationToken));
                }
                case ServiceMethods.SettlementStatus:
                {
                    if (context.Caller != ServiceRole.Battle) return ServiceReply.Reject("forbidden");
                    var query = SettlementQuery.Parser.ParseFrom(payload.Span);
                    if (!Identifiers.Valid(query.MatchId)) return ServiceReply.Reject("invalid_request");
                    var authoritative = await GetRoomAsync("", query.MatchId, cancellationToken);
                    if (authoritative.MatchId != query.MatchId || authoritative.NodeId != context.PeerId || !Identifiers.Valid(authoritative.RoomId, authoritative.NodeId, authoritative.BootEpoch)) return ServiceReply.Reject("forbidden");
                    var receipt = await store.SettlementAsync(query.MatchId, cancellationToken);
                    return ServiceReply.From(receipt ?? new SettlementReceipt { MatchId = query.MatchId });
                }
                default: return ServiceReply.Reject("unknown_method");
            }
        }
        catch (ServiceException ex) { return ServiceReply.Reject(ex.Code); }
        catch (InvalidProtocolBufferException) { return ServiceReply.Reject("invalid_request"); }
        catch (NpgsqlException) { return ServiceReply.Reject("unavailable"); }
        catch (TimeoutException) { return ServiceReply.Reject("unavailable"); }
        catch (IOException) { return ServiceReply.Reject("unavailable"); }
    }
    async ValueTask<RoomAllocation> GetRoomAsync(string roomId, string matchId, CancellationToken cancellationToken)
    {
        if (roomRpc is null) throw new ServiceException("authority_unavailable");
        var reply = await roomRpc.CallAsync(new(ServiceRole.Coordinator), ServiceMethods.RoomGet, new RoomQuery { RoomId = roomId, MatchId = matchId }, cancellationToken);
        return reply.Read(RoomAllocation.Parser);
    }
    static bool SameRoom(RoomAllocation room, string match, string roomId, string node, string epoch, IEnumerable<string> players) => room.MatchId == match && room.RoomId == roomId && room.NodeId == node && room.BootEpoch == epoch && room.PlayerIds.Order(StringComparer.Ordinal).SequenceEqual(players.Order(StringComparer.Ordinal));
    static byte[] HashPassword(string password, byte[] salt) => Rfc2898DeriveBytes.Pbkdf2(password, salt, 210000, HashAlgorithmName.SHA256, 32);
}

internal static class Identifiers
{
    internal static bool Valid(params string[] ids) => ids.All(id => id.Length is > 0 and <= 128 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'));
}

internal sealed record TokenClaims(string Kind, string Player, string Room, string Node, string Epoch, long Expiry);
internal sealed class SignedTokens(RSA key, TimeProvider clock)
{
    readonly object sync = new();
    public string Sign(TokenClaims claims)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(claims);
        byte[] signature;
        lock (sync) signature = key.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        return Convert.ToBase64String(bytes) + "." + Convert.ToBase64String(signature);
    }
    public bool TryRead(string token, string kind, out TokenClaims? claims)
    {
        claims = null;
        if (token.Length is 0 or > 4096) return false;
        var parts = token.Split('.'); if (parts.Length != 2) return false;
        try
        {
            var bytes = Convert.FromBase64String(parts[0]); var signature = Convert.FromBase64String(parts[1]);
            lock (sync) if (!key.VerifyData(bytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)) return false;
            claims = JsonSerializer.Deserialize<TokenClaims>(bytes);
            return claims is not null && claims.Kind == kind && Identifiers.Valid(claims.Player) && claims.Expiry > clock.GetUtcNow().ToUnixTimeSeconds();
        }
        catch (Exception ex) when (ex is FormatException or JsonException or CryptographicException or ArgumentException) { return false; }
    }
}

public sealed class EntryTicketVerifier
{
    readonly SignedTokens tokens;
    public EntryTicketVerifier(RSA publicKey, TimeProvider clock) => tokens = new(publicKey, clock);
    public bool TryVerify(string ticket, string playerId, string roomId, string nodeId, string bootEpoch, out string error)
    {
        var ok = TryVerify(ticket, roomId, nodeId, bootEpoch, out var actualPlayer, out error) && actualPlayer == playerId;
        if (!ok) error = "invalid_ticket";
        return ok;
    }
    public bool TryVerify(string ticket, string roomId, string nodeId, string bootEpoch, out string playerId, out string error)
    {
        playerId = ""; error = "invalid_ticket";
        if (!tokens.TryRead(ticket, "entry", out var claims) || claims!.Room != roomId || claims.Node != nodeId || claims.Epoch != bootEpoch) return false;
        playerId = claims.Player; error = ""; return true;
    }
}
