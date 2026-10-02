using AiNative.Protocol.Backend.V1;
using AiNative.Server.Control;
using Google.Protobuf;
using Npgsql;

namespace AiNative.Server.Backend;

/// <summary>Player-owned PostgreSQL persistence. Initialize before exposing the service.</summary>
public sealed class PostgresPlayerStore : IPlayerStore, IAsyncDisposable
{
    readonly NpgsqlDataSource dataSource;
    readonly bool ownsSource;
    public PostgresPlayerStore(string connectionString) : this(NpgsqlDataSource.Create(connectionString), true) { }
    public PostgresPlayerStore(NpgsqlDataSource dataSource) : this(dataSource, false) { }
    PostgresPlayerStore(NpgsqlDataSource source, bool owned) { dataSource = source; ownsSource = owned; }
    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        // Production migrations run with their owner before DML-only runtime accounts start.
        await using (var version = new NpgsqlCommand("SELECT max(version) FROM player.schema_version", connection))
        {
            try
            {
                if (await version.ExecuteScalarAsync(cancellationToken) is not int schemaVersion || schemaVersion != 1)
                    throw new InvalidDataException("player-schema-version-not-supported");
                return;
            }
            catch (PostgresException exception) when (exception.SqlState == "42P01")
            {
                // Preserve bootstrap for an uninitialized database owned by the caller.
            }
        }
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT pg_advisory_xact_lock(91817001);
            CREATE SCHEMA IF NOT EXISTS player;
            CREATE TABLE IF NOT EXISTS player.schema_version (version integer PRIMARY KEY);
            CREATE TABLE IF NOT EXISTS player.accounts (
                player_id text PRIMARY KEY, username text UNIQUE NOT NULL,
                salt bytea NOT NULL, password_hash bytea NOT NULL,
                played bigint NOT NULL DEFAULT 0 CHECK (played >= 0),
                won bigint NOT NULL DEFAULT 0 CHECK (won >= 0),
                kills bigint NOT NULL DEFAULT 0 CHECK (kills >= 0));
            CREATE TABLE IF NOT EXISTS player.settlements (
                match_id text PRIMARY KEY, payload_hash text NOT NULL, payload bytea NOT NULL,
                node_id text NOT NULL, boot_epoch text NOT NULL,
                confirmed_at timestamptz NOT NULL DEFAULT now());
            INSERT INTO player.schema_version(version) VALUES(1) ON CONFLICT DO NOTHING;
            """, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
    public async ValueTask<bool> CreateAsync(PlayerAccount account, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("INSERT INTO player.accounts(player_id, username, salt, password_hash) VALUES($1,$2,$3,$4) ON CONFLICT(username) DO NOTHING");
        command.Parameters.AddWithValue(account.PlayerId); command.Parameters.AddWithValue(account.Username); command.Parameters.AddWithValue(account.Salt); command.Parameters.AddWithValue(account.PasswordHash);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }
    public async ValueTask<PlayerAccount?> FindAsync(string username, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("SELECT player_id,username,salt,password_hash FROM player.accounts WHERE username=$1");
        command.Parameters.AddWithValue(username);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? new(reader.GetString(0), reader.GetString(1), reader.GetFieldValue<byte[]>(2), reader.GetFieldValue<byte[]>(3)) : null;
    }
    public async ValueTask<PlayerProfile?> ProfileAsync(string playerId, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("SELECT player_id,username,played,won,kills FROM player.accounts WHERE player_id=$1");
        command.Parameters.AddWithValue(playerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? new() { PlayerId = reader.GetString(0), DisplayName = reader.GetString(1), Played = reader.GetInt64(2), Won = reader.GetInt64(3), Kills = reader.GetInt64(4) } : null;
    }
    public async ValueTask<SettlementReceipt?> SettlementAsync(string matchId, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("SELECT payload_hash FROM player.settlements WHERE match_id=$1"); command.Parameters.AddWithValue(matchId);
        var hash = await command.ExecuteScalarAsync(cancellationToken) as string;
        return hash is null ? null : new() { MatchId = matchId, PayloadHash = hash, Confirmed = true };
    }
    public async ValueTask<SettlementReceipt> SettleAsync(MatchResult result, string payloadHash, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var insert = new NpgsqlCommand("INSERT INTO player.settlements(match_id,payload_hash,payload,node_id,boot_epoch) VALUES($1,$2,$3,$4,$5) ON CONFLICT(match_id) DO NOTHING", connection, transaction);
        insert.Parameters.AddWithValue(result.MatchId); insert.Parameters.AddWithValue(payloadHash); insert.Parameters.AddWithValue(result.ToByteArray()); insert.Parameters.AddWithValue(result.NodeId); insert.Parameters.AddWithValue(result.BootEpoch);
        if (await insert.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            // Unique conflict waits for the competing transaction; the next READ COMMITTED
            // statement sees its receipt without repeating profile updates.
            await using var existing = new NpgsqlCommand("SELECT payload_hash FROM player.settlements WHERE match_id=$1", connection, transaction); existing.Parameters.AddWithValue(result.MatchId);
            if ((string?)await existing.ExecuteScalarAsync(cancellationToken) != payloadHash) throw new ServiceException("settlement_conflict");
        }
        else
        {
            // Stable lock order prevents deadlocks for overlapping rosters.
            foreach (var player in result.Players.OrderBy(p => p.PlayerId, StringComparer.Ordinal))
            {
                await using var update = new NpgsqlCommand("UPDATE player.accounts SET played=played+1,won=won+$2,kills=kills+$3 WHERE player_id=$1", connection, transaction);
                update.Parameters.AddWithValue(player.PlayerId); update.Parameters.AddWithValue(player.Won ? 1L : 0L); update.Parameters.AddWithValue((long)player.Kills);
                if (await update.ExecuteNonQueryAsync(cancellationToken) != 1) throw new ServiceException("unknown_player");
            }
        }
        await transaction.CommitAsync(cancellationToken);
        return new() { MatchId = result.MatchId, PayloadHash = payloadHash, Confirmed = true };
    }
    public ValueTask DisposeAsync() => ownsSource ? dataSource.DisposeAsync() : ValueTask.CompletedTask;
}
