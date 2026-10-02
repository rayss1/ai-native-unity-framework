using AiNative.Protocol.Backend.V1;

namespace AiNative.Server.Backend;

public sealed record PlayerAccount(string PlayerId, string Username, byte[] Salt, byte[] PasswordHash);
public interface IPlayerStore
{
    ValueTask<bool> CreateAsync(PlayerAccount account, CancellationToken cancellationToken = default);
    ValueTask<PlayerAccount?> FindAsync(string username, CancellationToken cancellationToken = default);
    ValueTask<PlayerProfile?> ProfileAsync(string playerId, CancellationToken cancellationToken = default);
    ValueTask<SettlementReceipt> SettleAsync(MatchResult result, string payloadHash, CancellationToken cancellationToken = default);
    ValueTask<SettlementReceipt?> SettlementAsync(string matchId, CancellationToken cancellationToken = default);
}
