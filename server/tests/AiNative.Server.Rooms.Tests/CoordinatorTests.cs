using AiNative.Protocol.Backend.V1;
using AiNative.Server.Control;
using AiNative.Server.Rooms;
using Google.Protobuf;
using NUnit.Framework;

namespace AiNative.Server.Rooms.Tests;

public sealed class CoordinatorTests
{
    [Test]
    public async Task FreshFenceCannotDiscardHeartbeatAdmissionBlockWhenAnotherNodeIsAvailable()
    {
        BattleRpc rpc = new();
        RoomCoordinator service = new(new MemoryAllocations(), rpc, TimeProvider.System);
        await service.InitializeAsync();
        BattleNodeReport full = Node("epoch-a"); full.OutboxHealthy = false;
        Assert.That((await service.HandleAsync(new(ServiceRole.Battle, "node-a"), ServiceMethods.NodeReport, full.ToByteArray())).Success, Is.True);
        BattleNodeReport available = Node("epoch-b"); available.NodeId = "node-b";
        rpc.Inventory = available.Clone();
        Assert.That((await service.HandleAsync(new(ServiceRole.Battle, "node-b"), ServiceMethods.NodeReport, available.ToByteArray())).Success, Is.True);
        RoomAllocation allocation = await Allocate(service, new() { MatchId = "new", PlayerIds = { "p1" } });
        Assert.That(allocation.NodeId, Is.EqualTo("node-b"));
        Assert.That(allocation.State, Is.EqualTo("Ready"));
    }

    [Test]
    public async Task PartitionedAuthorityQuarantinesPlayersUntilAuthenticatedRevocation()
    {
        Clock clock = new(); BattleRpc rpc = new();
        RoomCoordinator service = new(new MemoryAllocations(), rpc, clock);
        await service.InitializeAsync(); await Report(service, "epoch-a");
        RoomAllocation room = await Allocate(service, new() { MatchId = "old", PlayerIds = { "p1" } });
        clock.Now = clock.Now.AddSeconds(21); await service.SweepAsync();
        ServiceReply blocked = await service.HandleAsync(new(ServiceRole.Match, "match"), ServiceMethods.Allocate,
            new AllocationRequest { MatchId = "new", PlayerIds = { "p1" } }.ToByteArray());
        Assert.That(blocked.Error, Is.EqualTo("player-already-allocated"));
        ServiceReply reconciling = await service.HandleAsync(new(ServiceRole.Battle, "node-a"), ServiceMethods.NodeReport, Node("epoch-a").ToByteArray());
        Assert.That(reconciling.Error, Is.EqualTo("node-reconciling"));
        Assert.That(rpc.ReleasedRoomIds, Is.EqualTo(new[] { room.RoomId }));
        await Report(service, "epoch-a");
        Assert.That((await Allocate(service, new() { MatchId = "new", PlayerIds = { "p1" } })).State, Is.EqualTo("Ready"));
    }
    [Test]
    public async Task LosingDatabaseOwnershipStopsAdmissionAndRequiresRestart()
    {
        MemoryAllocations store = new();
        RoomCoordinator service = new(store, new BattleRpc(), TimeProvider.System);
        await service.InitializeAsync(); await Report(service, "epoch-a");
        store.Owns = false; await service.SweepAsync();
        Assert.That(service.IsAllocating, Is.False);
        store.Owns = true; await service.SweepAsync();
        ServiceReply reply = await service.HandleAsync(new(ServiceRole.Match, "match"), ServiceMethods.Allocate,
            new AllocationRequest { MatchId = "later", PlayerIds = { "p1" } }.ToByteArray());
        Assert.That(reply.Error, Is.EqualTo("coordinator-ownership-lost"));
    }

    [Test]
    public async Task HistoryCapacityRejectsNewIdentityButRetainsIdempotentExistingAllocation()
    {
        RoomCoordinator service = new(new MemoryAllocations(), new BattleRpc(), TimeProvider.System, 1);
        await service.InitializeAsync(); await Report(service, "epoch-a");
        AllocationRequest request = new() { MatchId = "m1", PlayerIds = { "p1" } };
        RoomAllocation first = await Allocate(service, request);
        Assert.That((await Allocate(service, request)).RoomId, Is.EqualTo(first.RoomId));
        ServiceReply rejected = await service.HandleAsync(new(ServiceRole.Match, "match"), ServiceMethods.Allocate,
            new AllocationRequest { MatchId = "m2", PlayerIds = { "p2" } }.ToByteArray());
        Assert.That(rejected.Error, Is.EqualTo("room-history-capacity"));
    }
    [Test]
    public async Task UncertainCreationKeepsSameAuthorityWhenRetried()
    {
        MemoryAllocations store = new();
        BattleRpc rpc = new() { FailCreate = true };
        RoomCoordinator service = new(store, rpc, TimeProvider.System);
        await service.InitializeAsync();
        await Report(service, "epoch-a");
        AllocationRequest request = new() { MatchId = "match-a", PlayerIds = { "p1", "p2" } };
        RoomAllocation pending = await Allocate(service, request);
        Assert.That(pending.State, Is.EqualTo("Reserved"));
        rpc.FailCreate = false;
        RoomAllocation ready = await Allocate(service, request);
        Assert.That(ready.RoomId, Is.EqualTo(pending.RoomId));
        Assert.That(ready.AllocationId, Is.EqualTo(pending.AllocationId));
        Assert.That(ready.State, Is.EqualTo("Ready"));
        Assert.That(rpc.ReservedRoomIds.Distinct().Count(), Is.EqualTo(1));
        request.PlayerIds[1] = "p3";
        ServiceReply conflict = await service.HandleAsync(new(ServiceRole.Match, "match"), ServiceMethods.Allocate, request.ToByteArray());
        Assert.That(conflict.Error, Is.EqualTo("allocation-conflict"));
    }

    [Test]
    public async Task RestartWaitsForInventoryAndNewBootEpochEndsOldRooms()
    {
        MemoryAllocations store = new();
        BattleRpc rpc = new();
        RoomCoordinator before = new(store, rpc, TimeProvider.System);
        await before.InitializeAsync();
        await Report(before, "epoch-a");
        RoomAllocation room = await Allocate(before, new() { MatchId = "m1", PlayerIds = { "p1", "p2" } });
        RoomCoordinator after = new(store, rpc, TimeProvider.System);
        await after.InitializeAsync();
        Assert.That(after.IsAllocating, Is.False);
        rpc.Inventory = Node("epoch-b");
        await Report(after, "epoch-b");
        Assert.That(after.IsAllocating, Is.True);
        ServiceReply old = await after.HandleAsync(new(ServiceRole.Match, "match"), ServiceMethods.RoomGet,
            new RoomQuery { RoomId = room.RoomId }.ToByteArray());
        Assert.That(old.Read(RoomAllocation.Parser).State, Is.EqualTo("Lost"));
        RoomAllocation duplicate = await Allocate(after, new() { MatchId = "m1", PlayerIds = { "p1", "p2" } });
        Assert.That(duplicate.RoomId, Is.EqualTo(room.RoomId));
        Assert.That(duplicate.State, Is.EqualTo("Lost"));
    }

    [Test]
    public async Task BattleCannotAllocateAndUnregisteredInventoryCannotClaimUnknownRoom()
    {
        RoomCoordinator service = new(new MemoryAllocations(), new BattleRpc(), TimeProvider.System);
        await service.InitializeAsync();
        ServiceReply reply = await service.HandleAsync(new(ServiceRole.Battle, "node-a"), ServiceMethods.Allocate,
            new AllocationRequest { MatchId = "attack", PlayerIds = { "p1" } }.ToByteArray());
        Assert.That(reply.Error, Is.EqualTo("forbidden"));
        BattleNodeReport report = Node("epoch-a");
        report.Rooms.Add(new RoomAllocation { RoomId = "foreign", NodeId = "node-a", BootEpoch = "epoch-a" });
        // The authoritative fenced report is returned by the node rather than the potentially stale heartbeat.
        var conflictRpc = new BattleRpc { Inventory = report };
        service = new(new MemoryAllocations(), conflictRpc, TimeProvider.System); await service.InitializeAsync();
        reply = await service.HandleAsync(new(ServiceRole.Battle, "node-a"), ServiceMethods.NodeReport, report.ToByteArray());
        Assert.That(reply.Error, Is.EqualTo("inventory-conflict"));
    }

    private static BattleNodeReport Node(string epoch) => new()
    {
        NodeId = "node-a", BootEpoch = epoch, Address = "127.0.0.1:22000", OutboxHealthy = true,
        Workers = { new WorkerCapacity { WorkerId = 0, AvailableRooms = 2, MailboxAvailable = 32 } }
    };
    private static async Task Report(RoomCoordinator service, string epoch) =>
        Assert.That((await service.HandleAsync(new(ServiceRole.Battle, "node-a"), ServiceMethods.NodeReport, Node(epoch).ToByteArray())).Success, Is.True);
    private static async Task<RoomAllocation> Allocate(RoomCoordinator service, AllocationRequest request) =>
        (await service.HandleAsync(new(ServiceRole.Match, "match"), ServiceMethods.Allocate, request.ToByteArray())).Read(RoomAllocation.Parser);

    private sealed class MemoryAllocations : IAllocationStore
    {
        public bool Owns = true;
        public ValueTask<bool> CheckOwnershipAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Owns);
        private readonly Dictionary<string, RoomAllocation> _rooms = [];
        public ValueTask<IReadOnlyList<RoomAllocation>> LoadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<RoomAllocation>>(_rooms.Values.Select(x => x.Clone()).ToArray());
        public ValueTask SaveAsync(RoomAllocation room, CancellationToken cancellationToken = default)
        { _rooms[room.RoomId] = room.Clone(); return ValueTask.CompletedTask; }
    }
    private sealed class BattleRpc : IServiceRpc
    {
        public bool FailCreate;
        public BattleNodeReport Inventory = Node("epoch-a");
        public List<string> ReservedRoomIds { get; } = [];
        public List<string> ReleasedRoomIds { get; } = [];
        public ValueTask<ServiceReply> CallAsync(ServiceTarget target, string method, ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken = default, string playerId = "")
        {
            if (method == ServiceMethods.Fence) return ValueTask.FromResult(ServiceReply.From(Inventory));
            if (method == ServiceMethods.Release)
            {
                string roomId = RoomRelease.Parser.ParseFrom(payload.Span).RoomId;
                ReleasedRoomIds.Add(roomId); Inventory.Rooms.Remove(Inventory.Rooms.Single(x => x.RoomId == roomId));
            }
            if (method == ServiceMethods.Reserve)
            {
                RoomAllocation room = RoomReservation.Parser.ParseFrom(payload.Span).Allocation;
                ReservedRoomIds.Add(room.RoomId);
                if (!Inventory.Rooms.Any(x => x.RoomId == room.RoomId)) Inventory.Rooms.Add(room.Clone());
                return ValueTask.FromResult(ServiceReply.From(room));
            }
            if (method == ServiceMethods.Create && FailCreate) throw new TimeoutException();
            if (method == ServiceMethods.Create) Inventory.Rooms.Single(x => x.RoomId == RoomCreation.Parser.ParseFrom(payload.Span).RoomId).State = "Ready";
            return ValueTask.FromResult(ServiceReply.From(new Empty()));
        }
    }
    private sealed class Clock : TimeProvider
    { public DateTimeOffset Now = DateTimeOffset.Parse("2026-10-02T00:00:00Z"); public override DateTimeOffset GetUtcNow() => Now; }
}
