using AiNative.Protocol.Backend.V1;
using AiNative.Server.Control;
using AiNative.Server.Rooms;
using Google.Protobuf;
using NUnit.Framework;

namespace AiNative.Server.Rooms.Tests;

public sealed class CoordinatorCapacityTests
{
    [TestCase("reject")]
    [TestCase("timeout")]
    [TestCase("unavailable")]
    public async Task FailedInventoryRefreshCanStillAdmitOnAnotherHealthyNode(string failure)
    {
        CapacityRpc rpc = new(("node-a", 1), ("node-b", 1));
        RoomCoordinator coordinator = new(new Store(), rpc, TimeProvider.System);
        await coordinator.InitializeAsync();
        await Report(coordinator, rpc, "node-a");
        await Report(coordinator, rpc, "node-b");
        await Allocate(coordinator, "first");
        RoomAllocation second = await Allocate(coordinator, "second");
        await Report(coordinator, rpc, "node-a");
        await Report(coordinator, rpc, "node-b");
        rpc.Complete(second);
        rpc.FailFenceOn = "node-a";
        rpc.FenceFailure = failure;

        RoomAllocation replacement = await Allocate(coordinator, "replacement");

        Assert.That(replacement.State, Is.EqualTo("Ready"));
        Assert.That(replacement.NodeId, Is.EqualTo("node-b"));
    }

    [Test]
    public async Task InventoryRefreshCannotReopenTheOnlyNodesNegativeAdmissionGate()
    {
        CapacityRpc rpc = new(("node-a", 1));
        rpc.OutboxHealth["node-a"] = false;
        Store store = new();
        RoomCoordinator coordinator = new(store, rpc, TimeProvider.System);
        await coordinator.InitializeAsync();
        await Report(coordinator, rpc, "node-a");
        rpc.OutboxHealth["node-a"] = true; // Recovery has not been reported in a new heartbeat.

        Assert.That((await Request(coordinator, "blocked")).Error, Is.EqualTo("capacity-unavailable"));
        Assert.That(store.Rooms, Is.Empty);
        await Report(coordinator, rpc, "node-a");
        Assert.That((await Allocate(coordinator, "recovered")).State, Is.EqualTo("Ready"));
    }

    [Test]
    public async Task ExpiredNodeIsNotRefreshedOrAllocatedEvenBeforeSweep()
    {
        CapacityRpc rpc = new(("node-a", 1));
        Clock clock = new();
        Store store = new();
        RoomCoordinator coordinator = new(store, rpc, clock);
        await coordinator.InitializeAsync();
        await Report(coordinator, rpc, "node-a");
        clock.Now = clock.Now.AddSeconds(21);

        Assert.That((await Request(coordinator, "expired")).Error, Is.EqualTo("capacity-unavailable"));
        Assert.That(store.Rooms, Is.Empty);
    }

    [Test]
    public async Task InventoryRefreshWithANewBootDoesNotRecreateAnUncertainOriginalMatch()
    {
        CapacityRpc rpc = new(("node-a", 1)) { FailCreationOn = "node-a" };
        RoomCoordinator coordinator = new(new Store(), rpc, TimeProvider.System);
        await coordinator.InitializeAsync();
        await Report(coordinator, rpc, "node-a");
        RoomAllocation pending = await Allocate(coordinator, "uncertain");
        await Report(coordinator, rpc, "node-a");
        rpc.Restart("node-a");
        rpc.FailCreationOn = null;

        Assert.That((await Request(coordinator, "fresh")).Error, Is.EqualTo("capacity-unavailable"),
            "A refresh against the old boot must reject the changed identity until its authenticated heartbeat.");
        RoomAllocation beforeHeartbeat = await Allocate(coordinator, "uncertain");
        Assert.That(beforeHeartbeat.State, Is.EqualTo("Reserved"));
        Assert.That(beforeHeartbeat.RoomId, Is.EqualTo(pending.RoomId));
        await Report(coordinator, rpc, "node-a");
        RoomAllocation fresh = await Allocate(coordinator, "fresh");
        RoomAllocation original = await Allocate(coordinator, "uncertain");

        Assert.That(fresh.State, Is.EqualTo("Ready"));
        Assert.That(fresh.BootEpoch, Is.Not.EqualTo(pending.BootEpoch));
        Assert.That(original.State, Is.EqualTo("Lost"));
        Assert.That(original.RoomId, Is.EqualTo(pending.RoomId));
        Assert.That(original.AllocationId, Is.EqualTo(pending.AllocationId));
        Assert.That(original.BootEpoch, Is.EqualTo(pending.BootEpoch));
    }

    [Test]
    public async Task RefreshedHealthyCapacityDoesNotWaitForAnUnrelatedUnresponsiveNode()
    {
        CapacityRpc rpc = new(("node-a", 1), ("node-b", 1));
        RoomCoordinator coordinator = new(new Store(), rpc, TimeProvider.System);
        await coordinator.InitializeAsync();
        await Report(coordinator, rpc, "node-a");
        await Report(coordinator, rpc, "node-b");
        RoomAllocation first = await Allocate(coordinator, "first");
        await Allocate(coordinator, "second");
        await Report(coordinator, rpc, "node-a");
        await Report(coordinator, rpc, "node-b");
        rpc.Complete(first);
        rpc.BlockFenceOn = "node-b";

        Task<ServiceReply> admission = Request(coordinator, "replacement").AsTask();
        try
        {
            Task finished = await Task.WhenAny(admission, rpc.FenceEntered.Task).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.That(finished, Is.SameAs(admission), "A healthy slot was already found before reaching the blocked node.");
            Assert.That((await admission).Read(RoomAllocation.Parser).State, Is.EqualTo("Ready"));
        }
        finally
        {
            rpc.FenceReleased.TrySetResult(ServiceReply.Reject("unavailable"));
            await admission.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Test]
    public async Task ReservationsBetweenHeartbeatsUseBothNodesInsteadOfOverbookingTheFirst()
    {
        CapacityRpc rpc = new(("node-a", 1), ("node-b", 1));
        Store store = new();
        RoomCoordinator coordinator = new(store, rpc, TimeProvider.System);
        await coordinator.InitializeAsync();
        await Report(coordinator, rpc, "node-a");
        await Report(coordinator, rpc, "node-b");

        RoomAllocation first = await Allocate(coordinator, "first");
        RoomAllocation second = await Allocate(coordinator, "second");
        ServiceReply exhausted = await Request(coordinator, "third");

        Assert.That(first.State, Is.EqualTo("Ready"));
        Assert.That(second.State, Is.EqualTo("Ready"));
        Assert.That(second.NodeId, Is.EqualTo("node-b"));
        Assert.That(exhausted.Error, Is.EqualTo("capacity-unavailable"));
        Assert.That(store.Rooms, Has.Count.EqualTo(2), "No ownership should be persisted for an exhausted admission.");
    }

    [Test]
    public async Task CompletedRoomCanBeReplacedBeforeTheNextHeartbeat()
    {
        CapacityRpc rpc = new(("node-a", 1));
        RoomCoordinator coordinator = new(new Store(), rpc, TimeProvider.System);
        await coordinator.InitializeAsync();
        await Report(coordinator, rpc, "node-a");
        RoomAllocation first = await Allocate(coordinator, "first");
        await Report(coordinator, rpc, "node-a"); // Cached inventory is full.
        rpc.Complete(first); // Battle frees its slot; the next heartbeat has not arrived.

        RoomAllocation replacement = await Allocate(coordinator, "replacement");

        Assert.That(replacement.State, Is.EqualTo("Ready"));
        Assert.That(replacement.RoomId, Is.Not.EqualTo(first.RoomId));
        RoomAllocation old = (await coordinator.HandleAsync(new(ServiceRole.Match, "match"), ServiceMethods.RoomGet,
            new RoomQuery { RoomId = first.RoomId }.ToByteArray())).Read(RoomAllocation.Parser);
        Assert.That(old.State, Is.EqualTo("Released"));
    }

    [Test]
    public async Task UncertainCreationConsumesCapacityAndRetryKeepsItsOriginalAuthority()
    {
        CapacityRpc rpc = new(("node-a", 1), ("node-b", 1)) { FailCreationOn = "node-a" };
        RoomCoordinator coordinator = new(new Store(), rpc, TimeProvider.System);
        await coordinator.InitializeAsync();
        await Report(coordinator, rpc, "node-a");
        await Report(coordinator, rpc, "node-b");
        RoomAllocation pending = await Allocate(coordinator, "uncertain");
        RoomAllocation other = await Allocate(coordinator, "other");
        rpc.FailCreationOn = null;
        RoomAllocation retry = await Allocate(coordinator, "uncertain");

        Assert.That(pending.State, Is.EqualTo("Reserved"));
        Assert.That(other.State, Is.EqualTo("Ready"));
        Assert.That(other.NodeId, Is.EqualTo("node-b"));
        Assert.That(retry.RoomId, Is.EqualTo(pending.RoomId));
        Assert.That(retry.AllocationId, Is.EqualTo(pending.AllocationId));
        Assert.That(retry.State, Is.EqualTo("Ready"));
    }

    [Test]
    public async Task FreshInventoryDoesNotDeductTheSameReservationTwice()
    {
        CapacityRpc rpc = new(("node-a", 2));
        RoomCoordinator coordinator = new(new Store(), rpc, TimeProvider.System);
        await coordinator.InitializeAsync();
        await Report(coordinator, rpc, "node-a");
        await Allocate(coordinator, "first");
        await Report(coordinator, rpc, "node-a"); // One occupied slot is already reflected in AvailableRooms.

        Assert.That((await Allocate(coordinator, "second")).State, Is.EqualTo("Ready"));
        Assert.That((await Request(coordinator, "third")).Error, Is.EqualTo("capacity-unavailable"));
    }

    private static async Task Report(RoomCoordinator coordinator, CapacityRpc rpc, string node) =>
        Assert.That((await coordinator.HandleAsync(new(ServiceRole.Battle, node), ServiceMethods.NodeReport,
            rpc.Inventory(node).ToByteArray())).Success, Is.True);
    private static ValueTask<ServiceReply> Request(RoomCoordinator coordinator, string match) =>
        coordinator.HandleAsync(new(ServiceRole.Match, "match"), ServiceMethods.Allocate,
            new AllocationRequest { MatchId = match, PlayerIds = { "player-" + match } }.ToByteArray());
    private static async Task<RoomAllocation> Allocate(RoomCoordinator coordinator, string match)
    {
        ServiceReply reply = await Request(coordinator, match);
        Assert.That(reply.Success, Is.True, reply.Error);
        return reply.Read(RoomAllocation.Parser);
    }

    private sealed class Store : IAllocationStore
    {
        public readonly Dictionary<string, RoomAllocation> Rooms = [];
        public ValueTask<IReadOnlyList<RoomAllocation>> LoadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<RoomAllocation>>(Rooms.Values.Select(room => room.Clone()).ToArray());
        public ValueTask SaveAsync(RoomAllocation room, CancellationToken cancellationToken = default)
        { Rooms[room.RoomId] = room.Clone(); return ValueTask.CompletedTask; }
    }

    private sealed class CapacityRpc(params (string Node, int Slots)[] capacities) : IServiceRpc
    {
        private readonly Dictionary<string, int> slots = capacities.ToDictionary(item => item.Node, item => item.Slots);
        private readonly Dictionary<string, RoomAllocation> rooms = [];
        public string? FailCreationOn;
        public string? BlockFenceOn;
        public string? FailFenceOn;
        public string FenceFailure = "reject";
        public readonly Dictionary<string, bool> OutboxHealth = [];
        private readonly Dictionary<string, string> boots = [];
        public readonly TaskCompletionSource<bool> FenceEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<ServiceReply> FenceReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Complete(RoomAllocation room) => rooms.Remove(room.RoomId);
        public void Restart(string node)
        {
            foreach (string room in rooms.Values.Where(room => room.NodeId == node).Select(room => room.RoomId).ToArray()) rooms.Remove(room);
            boots[node] = "restarted-" + node;
        }
        public BattleNodeReport Inventory(string node) => new()
        {
            NodeId = node, BootEpoch = boots.GetValueOrDefault(node, "boot-" + node), Address = "127.0.0.1:22000",
            OutboxHealthy = OutboxHealth.GetValueOrDefault(node, true),
            Workers = { new WorkerCapacity { WorkerId = 0, AvailableRooms = slots[node] - rooms.Values.Count(room => room.NodeId == node), MailboxAvailable = 32 } },
            Rooms = { rooms.Values.Where(room => room.NodeId == node).Select(room => room.Clone()) }
        };
        public ValueTask<ServiceReply> CallAsync(ServiceTarget target, string method, ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken = default, string playerId = "")
        {
            if (method == ServiceMethods.Fence)
            {
                if (target.NodeId == FailFenceOn)
                {
                    if (FenceFailure == "timeout") throw new TimeoutException();
                    if (FenceFailure == "unavailable") throw new ServiceException("unavailable");
                    return ValueTask.FromResult(ServiceReply.Reject("unavailable"));
                }
                if (target.NodeId == BlockFenceOn)
                {
                    FenceEntered.TrySetResult(true);
                    return new(FenceReleased.Task);
                }
                return ValueTask.FromResult(ServiceReply.From(Inventory(target.NodeId)));
            }
            if (method == ServiceMethods.Reserve)
            {
                RoomAllocation room = RoomReservation.Parser.ParseFrom(payload.Span).Allocation;
                if (!rooms.ContainsKey(room.RoomId))
                {
                    if (Inventory(target.NodeId).Workers[0].AvailableRooms == 0)
                        return ValueTask.FromResult(ServiceReply.Reject("capacity-unavailable"));
                    rooms.Add(room.RoomId, room.Clone());
                }
                return ValueTask.FromResult(ServiceReply.From(room));
            }
            if (method == ServiceMethods.Create)
            {
                if (target.NodeId == FailCreationOn) throw new TimeoutException();
                rooms[RoomCreation.Parser.ParseFrom(payload.Span).RoomId].State = "Ready";
            }
            return ValueTask.FromResult(ServiceReply.From(new Empty()));
        }
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-10-03T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
