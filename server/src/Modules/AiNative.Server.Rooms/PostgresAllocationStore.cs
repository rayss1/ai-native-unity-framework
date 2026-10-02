using AiNative.Protocol.Backend.V1;
using Google.Protobuf;
using Npgsql;

namespace AiNative.Server.Rooms;

/// <summary>Owned schema and one active Coordinator lock. Losing this connection invalidates allocation readiness.</summary>
public sealed class PostgresAllocationStore(string connectionString) : IAllocationStore, IAsyncDisposable
{
    private readonly NpgsqlDataSource _source = NpgsqlDataSource.Create(connectionString);
    private NpgsqlConnection? _ownership;
    private bool _locked;
    public bool OwnsCoordinator => _locked && _ownership?.State == System.Data.ConnectionState.Open;
    public async ValueTask<bool> CheckOwnershipAsync(CancellationToken cancellationToken = default)
    {
        if (!OwnsCoordinator) return false;
        try
        {
            await using NpgsqlCommand command = new("SELECT 1", _ownership);
            return await command.ExecuteScalarAsync(cancellationToken) is int value && value == 1;
        }
        catch (NpgsqlException) { _locked = false; return false; }
        catch (InvalidOperationException) { _locked = false; return false; }
    }
    public async ValueTask InitializeAsync(CancellationToken ct = default)
    {
        _ownership = await _source.OpenConnectionAsync(ct);
        await using NpgsqlCommand own = new("SELECT pg_try_advisory_lock(91817002)", _ownership);
        if (await own.ExecuteScalarAsync(ct) is not true) throw new InvalidOperationException("coordinator-already-active");
        // A pre-migrated schema must work without database CREATE or schema ownership.
        await using (NpgsqlCommand version = new("SELECT max(version) FROM coordinator.schema_version", _ownership))
        {
            try
            {
                if (await version.ExecuteScalarAsync(ct) is not int schemaVersion || schemaVersion != 1)
                    throw new InvalidDataException("coordinator-schema-version-not-supported");
                _locked = true;
                return;
            }
            catch (PostgresException exception) when (exception.SqlState == "42P01")
            {
                // Development bootstrap still requires the caller's migration privileges.
            }
        }
        await using NpgsqlCommand migrate = new("""
            CREATE SCHEMA IF NOT EXISTS coordinator;
            CREATE TABLE IF NOT EXISTS coordinator.schema_version(version integer PRIMARY KEY);
            CREATE TABLE IF NOT EXISTS coordinator.rooms (
                room_id text PRIMARY KEY, match_id text UNIQUE NOT NULL,
                node_id text NOT NULL, boot_epoch text NOT NULL,
                state text NOT NULL CHECK(state IN ('Reserved','Ready','Released','Lost')),
                payload bytea NOT NULL);
            INSERT INTO coordinator.schema_version(version) VALUES(1) ON CONFLICT DO NOTHING;
            """, _ownership);
        await migrate.ExecuteNonQueryAsync(ct);
        _locked = true;
    }
    public async ValueTask<IReadOnlyList<RoomAllocation>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!OwnsCoordinator) throw new InvalidOperationException("coordinator-ownership-required");
        List<RoomAllocation> rooms = [];
        await using NpgsqlCommand command = new("SELECT payload FROM coordinator.rooms ORDER BY room_id LIMIT 100001", _ownership);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) rooms.Add(RoomAllocation.Parser.ParseFrom(reader.GetFieldValue<byte[]>(0)));
        return rooms;
    }
    public async ValueTask SaveAsync(RoomAllocation room, CancellationToken cancellationToken = default)
    {
        if (!OwnsCoordinator) throw new InvalidOperationException("coordinator-ownership-required");
        // Same connection as the advisory lock: no writes after losing the active-instance fence.
        await using (NpgsqlCommand prior = new("SELECT payload FROM coordinator.rooms WHERE room_id=$1", _ownership))
        {
            prior.Parameters.AddWithValue(room.RoomId);
            if (await prior.ExecuteScalarAsync(cancellationToken) is byte[] bytes)
            {
                RoomAllocation existing = RoomAllocation.Parser.ParseFrom(bytes);
                if (existing.AllocationId != room.AllocationId || existing.MatchId != room.MatchId || existing.NodeId != room.NodeId ||
                    existing.BootEpoch != room.BootEpoch || !existing.PlayerIds.Order(StringComparer.Ordinal).SequenceEqual(room.PlayerIds.Order(StringComparer.Ordinal)))
                    throw new InvalidDataException("allocation-owner-conflict");
            }
        }
        await using NpgsqlCommand command = new("""
            INSERT INTO coordinator.rooms(room_id,match_id,node_id,boot_epoch,state,payload) VALUES($1,$2,$3,$4,$5,$6)
            ON CONFLICT(room_id) DO UPDATE SET state=EXCLUDED.state,payload=EXCLUDED.payload
            WHERE coordinator.rooms.match_id=EXCLUDED.match_id AND coordinator.rooms.node_id=EXCLUDED.node_id
              AND coordinator.rooms.boot_epoch=EXCLUDED.boot_epoch
            """, _ownership);
        command.Parameters.AddWithValue(room.RoomId); command.Parameters.AddWithValue(room.MatchId);
        command.Parameters.AddWithValue(room.NodeId); command.Parameters.AddWithValue(room.BootEpoch);
        command.Parameters.AddWithValue(room.State); command.Parameters.AddWithValue(room.ToByteArray());
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw new InvalidDataException("allocation-owner-conflict");
    }
    public async ValueTask DisposeAsync()
    { _locked = false; if (_ownership is not null) await _ownership.DisposeAsync(); await _source.DisposeAsync(); }
}
