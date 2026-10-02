using AiNative.Protocol.Backend.V1;
using AiNative.Server.Backend;
using AiNative.Server.Control;

namespace AiNative.Server.Backend.Tests;

sealed class MemoryStore : IPlayerStore
{
    readonly Dictionary<string, PlayerAccount> accounts = [];
    readonly Dictionary<string, PlayerProfile> profiles = [];
    readonly Dictionary<string, SettlementReceipt> receipts = [];
    public ValueTask<bool> CreateAsync(PlayerAccount account, CancellationToken cancellationToken = default)
    {
        if (!accounts.TryAdd(account.Username, account)) return ValueTask.FromResult(false);
        profiles.Add(account.PlayerId, new PlayerProfile { PlayerId = account.PlayerId, DisplayName = account.Username });
        return ValueTask.FromResult(true);
    }
    public ValueTask<PlayerAccount?> FindAsync(string username, CancellationToken cancellationToken = default) => ValueTask.FromResult(accounts.GetValueOrDefault(username));
    public ValueTask<PlayerProfile?> ProfileAsync(string playerId, CancellationToken cancellationToken = default) => ValueTask.FromResult(profiles.GetValueOrDefault(playerId)?.Clone());
    public ValueTask<SettlementReceipt?> SettlementAsync(string matchId, CancellationToken cancellationToken = default) => ValueTask.FromResult(receipts.GetValueOrDefault(matchId)?.Clone());
    public ValueTask<SettlementReceipt> SettleAsync(MatchResult result, string payloadHash, CancellationToken cancellationToken = default)
    {
        if (receipts.TryGetValue(result.MatchId, out var old))
        {
            if (old.PayloadHash != payloadHash) throw new ServiceException("settlement_conflict");
            return ValueTask.FromResult(old.Clone());
        }
        if (result.Players.Any(x => !profiles.ContainsKey(x.PlayerId))) throw new ServiceException("unknown_player");
        foreach (var item in result.Players) { var profile = profiles[item.PlayerId]; profile.Played++; profile.Won += item.Won ? 1 : 0; profile.Kills += item.Kills; }
        var receipt = new SettlementReceipt { MatchId = result.MatchId, PayloadHash = payloadHash, Confirmed = true };
        receipts.Add(result.MatchId, receipt);
        return ValueTask.FromResult(receipt.Clone());
    }
}
