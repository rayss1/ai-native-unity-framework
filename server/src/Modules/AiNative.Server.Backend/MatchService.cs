using AiNative.Protocol.Backend.V1;
using AiNative.Server.Control;
using Google.Protobuf;

namespace AiNative.Server.Backend;

public sealed class MatchService : IServiceHandler
{
    sealed class Entry(QueueEntry request, DateTimeOffset created)
    {
        public QueueEntry Request { get; } = request;
        public MatchStatus Status { get; } = new() { RequestId = request.RequestId, State = "queued" };
        public DateTimeOffset Created { get; } = created;
        public long TerminalExpires { get; set; } = request.AdmissionExpiresUnixSeconds;
    }
    sealed class Job(string id, Entry[] entries)
    {
        public string Id { get; } = id;
        public Entry[] Entries { get; } = entries;
        public bool Calling;
        public bool CanCancel;
    }
    readonly object sync = new();
    readonly IServiceRpc rpc;
    readonly TimeProvider clock;
    readonly int playersPerMatch, maxQueuedPlayers;
    readonly string bootEpoch;
    readonly Dictionary<string, Entry> entries = [];
    readonly HashSet<string> protectedPlayers = [];
    readonly Dictionary<string, Job> jobs = [];
    readonly int maxEntries;
    public MatchService(IServiceRpc rpc, TimeProvider clock, int playersPerMatch = 2, int maxQueuedPlayers = 1024, string? bootEpoch = null)
    {
        if (playersPerMatch <= 0 || maxQueuedPlayers < playersPerMatch) throw new ArgumentOutOfRangeException(nameof(playersPerMatch));
        this.rpc = rpc; this.clock = clock; this.playersPerMatch = playersPerMatch; this.maxQueuedPlayers = maxQueuedPlayers;
        maxEntries = checked(2 * maxQueuedPlayers);
        this.bootEpoch = bootEpoch ?? Guid.NewGuid().ToString("N");
        if (!Identifiers.Valid(this.bootEpoch)) throw new ArgumentException("Invalid Match epoch", nameof(bootEpoch));
    }
    public ValueTask<ServiceReply> HandleAsync(ServiceCallContext context, string method, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        if (payload.Length > 65536) return ValueTask.FromResult(ServiceReply.Reject("request_too_large"));
        try { lock (sync) return ValueTask.FromResult(Handle(context, method, payload)); }
        catch (InvalidProtocolBufferException) { return ValueTask.FromResult(ServiceReply.Reject("invalid_request")); }
    }
    ServiceReply Handle(ServiceCallContext context, string method, ReadOnlyMemory<byte> payload)
    {
        PruneExpiredTerminalEntries();
        if (method == ServiceMethods.MatchEpoch) return context.Caller == ServiceRole.Lobby ? ServiceReply.From(new ServiceEpoch { Epoch = bootEpoch }) : ServiceReply.Reject("forbidden");
        if (method is ServiceMethods.QueueJoin or ServiceMethods.QueueAbort)
        {
            if (context.Caller != ServiceRole.Lobby) return ServiceReply.Reject("forbidden");
            var request = QueueEntry.Parser.ParseFrom(payload.Span);
            if (request.ExpectedMatchEpoch != bootEpoch) return ServiceReply.Reject("stale_match_epoch");
            if (!Identifiers.Valid(request.RequestId, request.PartyId) || request.PartyVersion == 0 || request.PlayerIds.Count == 0 || request.PlayerIds.Count > playersPerMatch || request.PlayerIds.Any(p => !Identifiers.Valid(p)) || request.PlayerIds.Distinct().Count() != request.PlayerIds.Count) return ServiceReply.Reject("invalid_queue");
            if (method == ServiceMethods.QueueAbort && (!Identifiers.Valid(context.PlayerId) || !request.PlayerIds.Contains(context.PlayerId))) return ServiceReply.Reject("forbidden");
            if (entries.TryGetValue(request.RequestId, out var old))
            {
                if (!old.Request.Equals(request)) return ServiceReply.Reject("request_conflict");
                if (method == ServiceMethods.QueueAbort)
                {
                    if (old.Status.State == "queued") Complete(old, "cancelled", "");
                    else if (old.Status.State != "cancelled" && !CancelRejectedJob(old)) return ServiceReply.Reject("allocation_claimed");
                }
                return ServiceReply.From(old.Status.Clone());
            }
            var now = clock.GetUtcNow().ToUnixTimeSeconds();
            if (request.AdmissionExpiresUnixSeconds <= 0 || request.AdmissionExpiresUnixSeconds > now + 60) return ServiceReply.Reject("invalid_queue");
            if (request.AdmissionExpiresUnixSeconds <= now)
                return method == ServiceMethods.QueueAbort ? ServiceReply.From(new MatchStatus { RequestId = request.RequestId, State = "cancelled" }) : ServiceReply.Reject("queue_expired");
            // Never evict an unexpired terminal fence to make room: a delayed join
            // must see the tombstone until its immutable admission deadline passes.
            if (entries.Count >= maxEntries) return ServiceReply.Reject("queue_capacity");
            if (method == ServiceMethods.QueueAbort)
            {
                var aborted = new Entry(request.Clone(), clock.GetUtcNow()); aborted.Status.State = "cancelled";
                entries.Add(request.RequestId, aborted);
                return ServiceReply.From(aborted.Status.Clone());
            }
            if (request.PlayerIds.Any(protectedPlayers.Contains)) return ServiceReply.Reject("player_busy");
            if (protectedPlayers.Count + request.PlayerIds.Count > maxQueuedPlayers) return ServiceReply.Reject("queue_capacity");
            var entry = new Entry(request.Clone(), clock.GetUtcNow()); entries.Add(request.RequestId, entry);
            foreach (var player in request.PlayerIds) protectedPlayers.Add(player);
            return ServiceReply.From(entry.Status.Clone());
        }
        if (method is not (ServiceMethods.MatchStatus or ServiceMethods.QueueCancel)) return ServiceReply.Reject("unknown_method");
        if (context.Caller is not (ServiceRole.Gate or ServiceRole.Lobby) || !Identifiers.Valid(context.PlayerId)) return ServiceReply.Reject("forbidden");
        var query = MatchQuery.Parser.ParseFrom(payload.Span);
        if (!entries.TryGetValue(query.RequestId, out var found)) return ServiceReply.Reject("queue_not_found");
        if (!found.Request.PlayerIds.Contains(context.PlayerId)) return ServiceReply.Reject("forbidden");
        if (method == ServiceMethods.QueueCancel)
        {
            if (found.Status.State == "queued") Complete(found, "cancelled", "");
            else if (found.Status.State != "cancelled" && !CancelRejectedJob(found)) return ServiceReply.Reject("allocation_claimed");
        }
        return ServiceReply.From(found.Status.Clone());
    }
    public async ValueTask PumpAsync(CancellationToken cancellationToken)
    {
        List<Job> pending;
        List<Job> ready;
        lock (sync)
        {
            // Exact-size packing preserves each party. Dynamic programming avoids greedy starvation
            // when one large party does not fit the remaining seats.
            while (true)
            {
                var selection = Pack(entries.Values.Where(e => e.Status.State == "queued").OrderBy(e => e.Created));
                if (selection is null) break;
                var job = new Job(Guid.NewGuid().ToString("N"), selection); jobs.Add(job.Id, job);
                foreach (var entry in selection) { entry.Status.State = "matching"; entry.Status.MatchId = job.Id; }
            }
            pending = jobs.Values.Where(j => !j.Calling && j.Entries[0].Status.State == "matching").ToList();
            ready = jobs.Values.Where(j => !j.Calling && j.Entries[0].Status.State == "ready").ToList();
        }
        foreach (var job in pending)
        {
            lock (sync) { if (job.Calling || job.Entries[0].Status.State != "matching") continue; job.CanCancel = false; job.Calling = true; }
            try
            {
                var request = new AllocationRequest { MatchId = job.Id }; request.PlayerIds.Add(job.Entries.SelectMany(e => e.Request.PlayerIds));
                var reply = await rpc.CallAsync(new(ServiceRole.Coordinator), ServiceMethods.Allocate, request, cancellationToken);
                lock (sync)
                {
                    if (!reply.Success)
                    {
                        job.CanCancel = reply.Error is "capacity-unavailable" or "room-history-capacity" or "player-already-allocated";
                        foreach (var entry in job.Entries) entry.Status.Failure = reply.Error;
                        continue;
                    }
                    var allocation = reply.Read(RoomAllocation.Parser);
                    if (!ValidAllocation(job, allocation)) { foreach (var entry in job.Entries) entry.Status.Failure = "allocation_conflict"; continue; }
                    if (allocation.State == "Ready") foreach (var entry in job.Entries) { entry.Status.State = "ready"; entry.Status.Allocation = allocation.Clone(); entry.Status.Failure = ""; }
                    else if (allocation.State is "Lost" or "Released") FinishJob(job, "room_" + allocation.State.ToLowerInvariant());
                }
            }
            catch (Exception ex) when (ex is TimeoutException or ServiceException or IOException) { lock (sync) foreach (var entry in job.Entries) entry.Status.Failure = "allocation_uncertain"; }
            finally { lock (sync) job.Calling = false; }
        }
        foreach (var job in ready)
        {
            lock (sync) { if (job.Calling || job.Entries[0].Status.State != "ready") continue; job.Calling = true; }
            try
            {
                var reply = await rpc.CallAsync(new(ServiceRole.Coordinator), ServiceMethods.RoomGet, new RoomQuery { MatchId = job.Id, RoomId = job.Entries[0].Status.Allocation.RoomId }, cancellationToken);
                if (reply.Success)
                {
                    var allocation = reply.Read(RoomAllocation.Parser);
                    lock (sync)
                    {
                        var accepted = job.Entries[0].Status.Allocation;
                        if (!ValidAllocation(job, allocation) || !SameIdentity(accepted, allocation))
                        {
                            foreach (var entry in job.Entries) entry.Status.Failure = "allocation_conflict";
                        }
                        else if (allocation.State is "Lost" or "Released") FinishJob(job, "room_" + allocation.State.ToLowerInvariant());
                        else foreach (var entry in job.Entries) entry.Status.Failure = "";
                    }
                }
            }
            catch (Exception ex) when (ex is TimeoutException or ServiceException or IOException) { /* Keep roster protected while ownership is uncertain. */ }
            finally { lock (sync) job.Calling = false; }
        }
    }
    Entry[]? Pack(IEnumerable<Entry> queued)
    {
        var sums = new Dictionary<int, Entry[]> { [0] = [] };
        foreach (var entry in queued)
        {
            foreach (var prior in sums.OrderByDescending(p => p.Key).ToArray())
            {
                var count = prior.Key + entry.Request.PlayerIds.Count;
                if (count <= playersPerMatch && !sums.ContainsKey(count)) sums[count] = [.. prior.Value, entry];
            }
            if (sums.TryGetValue(playersPerMatch, out var match)) return match;
        }
        return null;
    }
    static bool ValidAllocation(Job job, RoomAllocation allocation) => allocation.MatchId == job.Id && Identifiers.Valid(allocation.AllocationId, allocation.RoomId, allocation.NodeId, allocation.BootEpoch) && allocation.PlayerIds.Order(StringComparer.Ordinal).SequenceEqual(job.Entries.SelectMany(e => e.Request.PlayerIds).Order(StringComparer.Ordinal));
    static bool SameIdentity(RoomAllocation accepted, RoomAllocation current) => accepted.AllocationId == current.AllocationId && accepted.MatchId == current.MatchId && accepted.RoomId == current.RoomId && accepted.NodeId == current.NodeId && accepted.BootEpoch == current.BootEpoch;
    void FinishJob(Job job, string reason)
    {
        foreach (var entry in job.Entries) Complete(entry, "failed", reason);
        jobs.Remove(job.Id);
    }
    void Complete(Entry entry, string state, string reason)
    {
        entry.Status.State = state; entry.Status.Failure = reason;
        // Accepted queues may outlive their admission deadline. Keep the completion
        // briefly observable by every party, while absent-abort fences use admission TTL.
        entry.TerminalExpires = Math.Max(entry.Request.AdmissionExpiresUnixSeconds, clock.GetUtcNow().AddSeconds(30).ToUnixTimeSeconds());
        foreach (var player in entry.Request.PlayerIds) protectedPlayers.Remove(player);
    }
    bool CancelRejectedJob(Entry entry)
    {
        if (entry.Status.State != "matching" || !jobs.TryGetValue(entry.Status.MatchId, out var job) || job.Calling || !job.CanCancel) return false;
        foreach (var member in job.Entries) Complete(member, "cancelled", "");
        jobs.Remove(job.Id);
        return true;
    }
    void PruneExpiredTerminalEntries()
    {
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        foreach (var expired in entries.Where(e => e.Value.Status.State is "cancelled" or "failed" && e.Value.TerminalExpires <= now).Select(e => e.Key).ToArray()) entries.Remove(expired);
    }
}
