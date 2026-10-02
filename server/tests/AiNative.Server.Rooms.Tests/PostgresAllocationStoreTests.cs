using AiNative.Protocol.Backend.V1;
using AiNative.Server.Rooms;
using Npgsql;
using NUnit.Framework;

namespace AiNative.Server.Rooms.Tests;

// A unique, explicitly owned test database avoids touching the running topology's allocation schema/lock.
public sealed class PostgresAllocationStoreTests
{
    private string _admin = "", _database = "", _connection = "";
    [OneTimeSetUp]
    public async Task CreateOwnedDatabase()
    {
        _admin = Environment.GetEnvironmentVariable("AINATIVE_TEST_POSTGRES") ?? "";
        if (_admin.Length == 0) Assert.Ignore("AINATIVE_TEST_POSTGRES is required for real Coordinator persistence tests.");
        _database = "ainative_coord_test_" + Guid.NewGuid().ToString("N");
        await using NpgsqlConnection admin = new(_admin); await admin.OpenAsync();
        await using NpgsqlCommand create = new($"CREATE DATABASE {_database}", admin); await create.ExecuteNonQueryAsync();
        _connection = new NpgsqlConnectionStringBuilder(_admin) { Database = _database }.ConnectionString;
    }
    [OneTimeTearDown]
    public async Task DropOwnedDatabase()
    {
        if (_connection.Length == 0) return;
        await using NpgsqlConnection admin = new(_admin); await admin.OpenAsync();
        await using NpgsqlCommand drop = new($"DROP DATABASE {_database} WITH (FORCE)", admin); await drop.ExecuteNonQueryAsync();
    }
    [Test]
    public async Task TerminatedOwnershipConnectionCannotContinueAllocating()
    {
        string application = "coord_owned_" + Guid.NewGuid().ToString("N");
        string connection = new NpgsqlConnectionStringBuilder(_connection) { ApplicationName = application }.ConnectionString;
        await using PostgresAllocationStore owner = new(connection); await owner.InitializeAsync();
        await using NpgsqlConnection admin = new(_admin); await admin.OpenAsync();
        await using NpgsqlCommand terminate = new("SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE application_name=$1 AND datname=$2", admin);
        terminate.Parameters.AddWithValue(application); terminate.Parameters.AddWithValue(_database);
        Assert.That(await terminate.ExecuteScalarAsync(), Is.True);
        Assert.That(await owner.CheckOwnershipAsync(), Is.False);
        Assert.That(owner.OwnsCoordinator, Is.False);
    }
    [Test]
    public async Task ExclusiveOwnerDurableReloadAndConflictingIdentityAreEnforcedByPostgres()
    {
        RoomAllocation room = new() { RoomId = "r1", MatchId = "m1", NodeId = "b1", BootEpoch = "e1", AllocationId = "a1", State = "Reserved", PlayerIds = { "p1" } };
        await using (PostgresAllocationStore first = new(_connection))
        {
            await first.InitializeAsync(); Assert.That(await first.CheckOwnershipAsync(), Is.True);
            await first.SaveAsync(room);
            await using PostgresAllocationStore other = new(_connection);
            Assert.That(async () => await other.InitializeAsync(), Throws.TypeOf<InvalidOperationException>().With.Message.EqualTo("coordinator-already-active"));
            Assert.That(other.OwnsCoordinator, Is.False);
            Assert.That(async () => await other.SaveAsync(room), Throws.TypeOf<InvalidOperationException>().With.Message.EqualTo("coordinator-ownership-required"));
            RoomAllocation conflict = room.Clone(); conflict.NodeId = "b2";
            Assert.That(async () => await first.SaveAsync(conflict), Throws.TypeOf<InvalidDataException>().With.Message.EqualTo("allocation-owner-conflict"));
            conflict = room.Clone(); conflict.AllocationId = "changed";
            Assert.That(async () => await first.SaveAsync(conflict), Throws.TypeOf<InvalidDataException>().With.Message.EqualTo("allocation-owner-conflict"));
            conflict = room.Clone(); conflict.RoomId = "r2";
            Assert.That(async () => await first.SaveAsync(conflict), Throws.TypeOf<PostgresException>());
            room.State = "Lost"; await first.SaveAsync(room);
        }
        await using PostgresAllocationStore restarted = new(_connection); await restarted.InitializeAsync();
        var records = await restarted.LoadAsync();
        Assert.That(records, Has.Count.EqualTo(1)); Assert.That(records[0], Is.EqualTo(room));
    }
}
