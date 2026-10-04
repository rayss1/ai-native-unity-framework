using AiNative.Protocol.Backend.V1;
using AiNative.Server.Control;

namespace AiNative.Server.Rooms;

public interface IAllocationStore
{
    ValueTask<bool> CheckOwnershipAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
    ValueTask<IReadOnlyList<RoomAllocation>> LoadAsync(CancellationToken cancellationToken = default);
    ValueTask SaveAsync(RoomAllocation room, CancellationToken cancellationToken = default);
}

public sealed class RoomCoordinator : IServiceHandler
{
    private readonly IAllocationStore _store;
    private readonly IServiceRpc _rpc;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly Dictionary<string, RoomAllocation> _rooms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RoomAllocation> _matches = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unreconciled = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unfencedLost = new(StringComparer.Ordinal);
    private bool _initialized;
    private DateTimeOffset _started;
    private volatile bool _allocating;
    private readonly string _epoch = Guid.NewGuid().ToString("N");
    private readonly int _maxStoredRooms;
    private bool _ownershipLost;
    public RoomCoordinator(IAllocationStore store, IServiceRpc rpc, TimeProvider clock, int maxStoredRooms = 100000)
    { if (maxStoredRooms is < 1 or > 100000) throw new ArgumentOutOfRangeException(nameof(maxStoredRooms)); _store = store; _rpc = rpc; _clock = clock; _maxStoredRooms = maxStoredRooms; }
    public bool IsAllocating => _allocating;

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _serial.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;
            foreach (RoomAllocation room in await _store.LoadAsync(cancellationToken))
            {
                if (_rooms.Count >= _maxStoredRooms) throw new InvalidDataException("room-history-capacity");
                RoomAllocation copy = room.Clone();
                if (!_matches.TryAdd(copy.MatchId, copy)) throw new InvalidDataException("duplicate-match-owner");
                _rooms.Add(copy.RoomId, copy);
                if (Active(room)) _unreconciled.Add(room.NodeId);
                if (room.State == "Lost" && !room.AuthorityFenced) _unfencedLost.Add(room.RoomId);
            }
            _started = _clock.GetUtcNow();
            _initialized = true;
            _allocating = _unreconciled.Count == 0;
        }
        finally { _serial.Release(); }
    }

    public async ValueTask<ServiceReply> HandleAsync(ServiceCallContext context, string method, ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        if (context.Caller == ServiceRole.Client) return ServiceReply.Reject("forbidden");
        await _serial.WaitAsync(cancellationToken);
        try
        {
            if (!_initialized) return ServiceReply.Reject("recovering");
            if (_ownershipLost) return ServiceReply.Reject("coordinator-ownership-lost");
            return method switch
            {
                ServiceMethods.NodeReport when context.Caller == ServiceRole.Battle =>
                    await ReportAsync(context, BattleNodeReport.Parser.ParseFrom(payload.Span), cancellationToken),
                ServiceMethods.Allocate when context.Caller == ServiceRole.Match =>
                    await AllocateAsync(AllocationRequest.Parser.ParseFrom(payload.Span), cancellationToken),
                ServiceMethods.RoomGet => Get(RoomQuery.Parser.ParseFrom(payload.Span)),
                ServiceMethods.RoomList => List(RoomQuery.Parser.ParseFrom(payload.Span)),
                _ => ServiceReply.Reject("forbidden")
            };
        }
        catch (Google.Protobuf.InvalidProtocolBufferException) { return ServiceReply.Reject("invalid-payload"); }
        finally { _serial.Release(); }
    }

    public async ValueTask SweepAsync(CancellationToken cancellationToken = default)
    {
        await _serial.WaitAsync(cancellationToken);
        try
        {
            if (_ownershipLost || !await _store.CheckOwnershipAsync(cancellationToken))
            { _ownershipLost = true; _allocating = false; return; }
            DateTimeOffset now = _clock.GetUtcNow();
            string[] expired = _nodes.Where(x => now - x.Value.Seen > TimeSpan.FromSeconds(20)).Select(x => x.Key)
                .Concat(now - _started > TimeSpan.FromSeconds(30) ? _unreconciled : []).Distinct().ToArray();
            foreach (string nodeId in expired)
            {
                foreach (RoomAllocation room in _rooms.Values.Where(x => x.NodeId == nodeId && Active(x)))
                    await SetStateAsync(room, "Lost", cancellationToken);
                _nodes.Remove(nodeId);
                _unreconciled.Remove(nodeId);
            }
            _allocating = _initialized && _unreconciled.Count == 0;
        }
        finally { _serial.Release(); }
    }

    private async ValueTask<ServiceReply> ReportAsync(ServiceCallContext context, BattleNodeReport report, CancellationToken ct)
    {
        if (report.NodeId != context.PeerId || !ValidReport(report))
            return ServiceReply.Reject("invalid-node-report");
        // Obtain a fresh inventory after invalidating delayed control commands from older Coordinator starts.
        ServiceReply fenced = await _rpc.CallAsync(new(ServiceRole.Battle, report.NodeId), ServiceMethods.Fence,
            new CoordinatorFence { CoordinatorEpoch = _epoch, BootEpoch = report.BootEpoch }, ct);
        if (!fenced.Success) { _nodes.Remove(report.NodeId); return ServiceReply.Reject("node-fence-unavailable"); }
        BattleNodeReport inventoryReport = fenced.Read(BattleNodeReport.Parser);
        if (!ValidReport(inventoryReport) || inventoryReport.NodeId != report.NodeId || inventoryReport.BootEpoch != report.BootEpoch)
        { _nodes.Remove(report.NodeId); return ServiceReply.Reject("inventory-conflict"); }
        // The heartbeat includes external persistence/replay admission gates. Fresh slot
        // inventory must not erase a negative gate; recovery waits for the next heartbeat.
        inventoryReport.OutboxHealthy &= report.OutboxHealthy;
        report = inventoryReport;
        foreach (RoomAllocation inventory in report.Rooms)
        {
            if (!_rooms.TryGetValue(inventory.RoomId, out RoomAllocation? recorded) ||
                recorded.NodeId != report.NodeId || recorded.BootEpoch != report.BootEpoch ||
                inventory.NodeId != report.NodeId || inventory.BootEpoch != report.BootEpoch ||
                recorded.AllocationId != inventory.AllocationId || recorded.MatchId != inventory.MatchId ||
                !SameRoster(recorded.PlayerIds, inventory.PlayerIds))
            { _nodes.Remove(report.NodeId); return ServiceReply.Reject("inventory-conflict"); }
            if (!Active(recorded))
            {
                ServiceReply revoked = await _rpc.CallAsync(new(ServiceRole.Battle, report.NodeId), ServiceMethods.Release,
                    new RoomRelease { RoomId = recorded.RoomId, AllocationId = recorded.AllocationId,
                        BootEpoch = report.BootEpoch, CoordinatorEpoch = _epoch, Reason = recorded.State }, ct);
                if (!revoked.Success) { _nodes.Remove(report.NodeId); return ServiceReply.Reject("node-fence-unavailable"); }
                await MarkFencedAsync(recorded, ct);
                // A terminal allocation can never be revived by a late node report.
                _nodes.Remove(report.NodeId);
                return ServiceReply.Reject("node-reconciling");
            }
        }
        bool recovering = _unreconciled.Contains(report.NodeId);
        foreach (RoomAllocation lost in _rooms.Values.Where(x => x.NodeId == report.NodeId && x.State == "Lost"))
            if (lost.BootEpoch != report.BootEpoch || !report.Rooms.Any(x => x.RoomId == lost.RoomId))
                await MarkFencedAsync(lost, ct);
        foreach (RoomAllocation room in _rooms.Values.Where(x => x.NodeId == report.NodeId && Active(x)))
        {
            if (room.BootEpoch == report.BootEpoch && room.CoordinatorEpoch != _epoch)
            { RoomAllocation copy = room.Clone(); copy.CoordinatorEpoch = _epoch; await _store.SaveAsync(copy, ct); room.CoordinatorEpoch = _epoch; }
            RoomAllocation? inventory = report.Rooms.FirstOrDefault(x => x.RoomId == room.RoomId);
            if (room.BootEpoch != report.BootEpoch || (recovering && inventory is null))
                await SetStateAsync(room, "Lost", ct);
            else if (inventory?.State == "Ready") await SetStateAsync(room, "Ready", ct);
            else if (inventory?.State == "Lost") await SetStateAsync(room, "Lost", ct);
            else if (inventory is null && room.State == "Ready") await SetStateAsync(room, "Released", ct);
        }
        _nodes[report.NodeId] = new(report.Clone(), _clock.GetUtcNow());
        _unreconciled.Remove(report.NodeId);
        _allocating = _unreconciled.Count == 0;
        return ServiceReply.From(new Empty());
    }

    private async ValueTask<ServiceReply> AllocateAsync(AllocationRequest request, CancellationToken ct)
    {
        if (!Identifier(request.MatchId) || request.PlayerIds.Count is < 1 or > 8 ||
            request.PlayerIds.Any(x => !Identifier(x)) || request.PlayerIds.Distinct(StringComparer.Ordinal).Count() != request.PlayerIds.Count)
            return ServiceReply.Reject("invalid-allocation");
        _matches.TryGetValue(request.MatchId, out RoomAllocation? room);
        if (room is not null)
        {
            if (!SameRoster(room.PlayerIds, request.PlayerIds)) return ServiceReply.Reject("allocation-conflict");
            if (room.State != "Reserved") return ServiceReply.From(room.Clone());
        }
        if (!_allocating) return ServiceReply.Reject("recovering");
        if (room is null)
        {
            if (_rooms.Count >= _maxStoredRooms) return ServiceReply.Reject("room-history-capacity");
            if (_rooms.Values.Any(x => (Active(x) || _unfencedLost.Contains(x.RoomId)) && x.PlayerIds.Intersect(request.PlayerIds, StringComparer.Ordinal).Any()))
                return ServiceReply.Reject("player-already-allocated");
            Node? selected = SelectNode();
            if (selected is null)
            {
                // Completed rooms may free slots before the next heartbeat. Refresh
                // fenced inventory once without discarding a negative admission gate.
                foreach (Node cached in _nodes.Values.Where(Admitting).ToArray())
                {
                    try
                    {
                        await ReportAsync(new(ServiceRole.Battle, cached.Report.NodeId), cached.Report.Clone(), ct);
                    }
                    catch (TimeoutException) { _nodes.Remove(cached.Report.NodeId); }
                    catch (ServiceException ex) when (ex.Code is "unavailable" or "timeout")
                    { _nodes.Remove(cached.Report.NodeId); }
                    if (SelectNode() is not null) break;
                }
                selected = SelectNode();
            }
            if (selected is null) return ServiceReply.Reject("capacity-unavailable");
            room = new()
            {
                AllocationId = Guid.NewGuid().ToString("N"), MatchId = request.MatchId,
                RoomId = Guid.NewGuid().ToString("N"), NodeId = selected.Report.NodeId, BootEpoch = selected.Report.BootEpoch,
                Address = selected.Report.Address, State = "Reserved", CreatedUnixSeconds = _clock.GetUtcNow().ToUnixTimeSeconds(), CoordinatorEpoch = _epoch
            };
            room.PlayerIds.Add(request.PlayerIds);
            await _store.SaveAsync(room.Clone(), ct); // Persist ownership before any remote side effect.
            _rooms.Add(room.RoomId, room);
            _matches.Add(room.MatchId, room);
        }
        if (!_nodes.TryGetValue(room.NodeId, out Node? node) || node.Report.BootEpoch != room.BootEpoch)
            return ServiceReply.From(room.Clone());
        ServiceTarget target = new(ServiceRole.Battle, room.NodeId);
        try
        {
            ServiceReply reservation = await _rpc.CallAsync(target, ServiceMethods.Reserve, new RoomReservation { Allocation = room.Clone() }, ct);
            if (!reservation.Success)
            {
                // A definite rejection from this epoch proves no room was created.
                if (reservation.Error is "capacity-unavailable" or "draining" or "outbox-unhealthy" or "outbox-capacity-unavailable")
                    await SetStateAsync(room, "Released", ct);
                return ServiceReply.From(room.Clone());
            }
            RoomAllocation accepted = reservation.Read(RoomAllocation.Parser);
            if (accepted.RoomId != room.RoomId || accepted.AllocationId != room.AllocationId || accepted.BootEpoch != room.BootEpoch)
                return ServiceReply.Reject("reservation-conflict");
            ServiceReply creation = await _rpc.CallAsync(target, ServiceMethods.Create,
                new RoomCreation { AllocationId = room.AllocationId, RoomId = room.RoomId, BootEpoch = room.BootEpoch, CoordinatorEpoch = _epoch }, ct);
            if (creation.Success) await SetStateAsync(room, "Ready", ct);
            else if (creation.Error is "replay-storage-quota" or "replay-persistence-unavailable")
                await SetStateAsync(room, "Released", ct);
        }
        catch (TimeoutException) { }
        catch (ServiceException ex) when (ex.Code is "unavailable" or "timeout") { }
        return ServiceReply.From(room.Clone());
    }

    private Node? SelectNode() => _nodes.Values.Where(node => Admitting(node) && AvailableCapacity(node) > 0)
        .OrderByDescending(AvailableCapacity).ThenBy(node => node.Report.NodeId, StringComparer.Ordinal).FirstOrDefault();
    private bool Admitting(Node node) => !node.Report.Draining && node.Report.OutboxHealthy &&
        _clock.GetUtcNow() - node.Seen <= TimeSpan.FromSeconds(20);
    private int AvailableCapacity(Node node)
    {
        // Reported rooms are already reflected in AvailableRooms. Only ownership
        // persisted after that inventory needs an additional conservative deduction.
        int unreported = _rooms.Values.Count(room => Active(room) && room.NodeId == node.Report.NodeId &&
            room.BootEpoch == node.Report.BootEpoch && !node.Report.Rooms.Any(inventory => inventory.RoomId == room.RoomId));
        int available = node.Report.Workers.Where(worker => worker.MailboxAvailable > 0).Sum(worker => worker.AvailableRooms);
        return Math.Max(0, available - unreported);
    }
    private static bool Active(RoomAllocation room) => room.State is "Reserved" or "Ready";
    private static bool ValidReport(BattleNodeReport report) => Identifier(report.NodeId) && Identifier(report.BootEpoch) &&
        !string.IsNullOrWhiteSpace(report.Address) && System.Text.Encoding.UTF8.GetByteCount(report.Address) <= 512 &&
        report.CalculateSize() <= 60000 && report.Workers.Count is > 0 and <= 64 &&
        !report.Workers.Any(x => x.WorkerId < 0 || x.AvailableRooms < 0 || x.AvailableRooms > 256 || x.MailboxAvailable < 0 || x.MailboxAvailable > 4096) &&
        report.Workers.Select(x => x.WorkerId).Distinct().Count() == report.Workers.Count &&
        report.Rooms.Select(x => x.RoomId).Distinct().Count() == report.Rooms.Count;
    private static bool Identifier(string value) => value.Length is > 0 and <= 128 &&
        value.All(x => x is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.');
    private static bool SameRoster(IEnumerable<string> a, IEnumerable<string> b) =>
        a.Order(StringComparer.Ordinal).SequenceEqual(b.Order(StringComparer.Ordinal), StringComparer.Ordinal);
    private async ValueTask SetStateAsync(RoomAllocation room, string state, CancellationToken ct)
    {
        if (room.State == state) return;
        RoomAllocation copy = room.Clone(); copy.State = state;
        if (state == "Lost") copy.AuthorityFenced = false;
        await _store.SaveAsync(copy, ct);
        room.State = state; room.AuthorityFenced = copy.AuthorityFenced;
        if (state == "Lost") _unfencedLost.Add(room.RoomId);
    }
    private async ValueTask MarkFencedAsync(RoomAllocation room, CancellationToken ct)
    {
        if (!room.AuthorityFenced)
        { RoomAllocation copy = room.Clone(); copy.AuthorityFenced = true; await _store.SaveAsync(copy, ct); room.AuthorityFenced = true; }
        _unfencedLost.Remove(room.RoomId);
    }
    private ServiceReply Get(RoomQuery query)
    {
        RoomAllocation? room = query.RoomId.Length != 0 ? _rooms.GetValueOrDefault(query.RoomId) :
            _matches.GetValueOrDefault(query.MatchId);
        return room is null ? ServiceReply.Reject("room-not-found") : ServiceReply.From(room.Clone());
    }
    private ServiceReply List(RoomQuery query)
    {
        RoomList list = new() { Allocating = _allocating };
        int size = query.PageSize == 0 ? 16 : (int)Math.Min(query.PageSize, 16u);
        RoomAllocation[] page = _rooms.Values.Where(x => StringComparer.Ordinal.Compare(x.RoomId, query.AfterRoomId) > 0)
            .OrderBy(x => x.RoomId, StringComparer.Ordinal).Take(size + 1).ToArray();
        list.Rooms.Add(page.Take(size).Select(x => x.Clone()));
        if (page.Length > size) list.NextRoomId = page[size - 1].RoomId;
        return ServiceReply.From(list);
    }
    private sealed record Node(BattleNodeReport Report, DateTimeOffset Seen);
}
