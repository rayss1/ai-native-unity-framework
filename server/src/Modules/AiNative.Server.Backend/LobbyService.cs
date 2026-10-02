using AiNative.Protocol.Backend.V1;
using AiNative.Server.Control;
using Google.Protobuf;

namespace AiNative.Server.Backend;

public sealed class LobbyService : IServiceHandler
{
    sealed class Party(string id, string leader)
    {
        public PartyState State { get; } = new() { PartyId = id, LeaderId = leader, Version = 1, Members = { new PartyMember { PlayerId = leader } } };
        public Dictionary<string, DateTimeOffset> Invitations { get; } = [];
        public string QueueId = "";
        public string QueueEpoch = "";
        public QueueEntry? FrozenQueue;
    }
    readonly object sync = new();
    readonly Dictionary<string, Party> parties = [];
    readonly Dictionary<string, string> membership = [];
    readonly Dictionary<string, DateTimeOffset> presence = [];
    readonly Dictionary<string, (string[] Players, MatchStatus Status)> retiredQueues = [];
    readonly Queue<string> retiredOrder = new();
    readonly TimeProvider clock;
    readonly int maxPresence;
    static readonly TimeSpan PresenceTtl = TimeSpan.FromSeconds(90);
    readonly IServiceRpc rpc;
    readonly int maxParties, maxMembers;
    public LobbyService(IServiceRpc rpc, int maxParties = 1024, int maxMembers = 8, TimeProvider? clock = null)
    {
        if (maxParties <= 0 || maxMembers <= 0) throw new ArgumentOutOfRangeException(nameof(maxParties));
        this.rpc = rpc; this.maxParties = maxParties; this.maxMembers = maxMembers;
        this.clock = clock ?? TimeProvider.System;
        maxPresence = checked(maxParties * maxMembers);
    }
    public PresenceState GetPresence(string playerId)
    {
        lock (sync)
        {
            PrunePresence();
            return new() { PlayerId = playerId, Online = presence.ContainsKey(playerId) };
        }
    }
    void PrunePresence()
    {
        var now = clock.GetUtcNow();
        foreach (var expired in presence.Where(p => p.Value <= now).Select(p => p.Key).ToArray()) presence.Remove(expired);
    }
    public async ValueTask<ServiceReply> HandleAsync(ServiceCallContext context, string method, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        if (context.Caller != ServiceRole.Gate || !Identifiers.Valid(context.PlayerId)) return ServiceReply.Reject("forbidden");
        if (payload.Length > 16384) return ServiceReply.Reject("request_too_large");
        try
        {
            if (method == ServiceMethods.PartyInvites)
            {
                lock (sync)
                {
                    var now = clock.GetUtcNow();
                    var result = new PartyInvitations();
                    foreach (var party in parties.Values.Where(p => p.Invitations.TryGetValue(context.PlayerId, out var until) && until > now).OrderBy(p => p.State.PartyId, StringComparer.Ordinal))
                    {
                        if (result.Parties.Count >= 32) break;
                        result.Parties.Add(party.State.Clone());
                        if (result.CalculateSize() > 16384) { result.Parties.RemoveAt(result.Parties.Count - 1); break; }
                    }
                    return ServiceReply.From(result);
                }
            }
            if (method == ServiceMethods.Presence)
            {
                var request = PresenceUpdate.Parser.ParseFrom(payload.Span);
                lock (sync)
                {
                    PrunePresence();
                    if (request.Online)
                    {
                        if (!presence.ContainsKey(context.PlayerId) && presence.Count >= maxPresence) return ServiceReply.Reject("presence_capacity");
                        presence[context.PlayerId] = clock.GetUtcNow().Add(PresenceTtl);
                    }
                    else presence.Remove(context.PlayerId);
                    return ServiceReply.From(new PresenceState { PlayerId = context.PlayerId, Online = request.Online });
                }
            }
            if (method is ServiceMethods.QueueCancel or ServiceMethods.MatchStatus)
            {
                var query = MatchQuery.Parser.ParseFrom(payload.Span);
                Party? claimed = null;
                QueueEntry? frozen = null;
                lock (sync)
                {
                    if (retiredQueues.TryGetValue(query.RequestId, out var retired)) return retired.Players.Contains(context.PlayerId) ? ServiceReply.From(retired.Status.Clone()) : ServiceReply.Reject("forbidden");
                    if (membership.TryGetValue(context.PlayerId, out var id) && parties[id].QueueId == query.RequestId) { claimed = parties[id]; frozen = claimed.FrozenQueue?.Clone(); }
                }
                // After Lobby restart the Match service remains authoritative for the
                // authenticated player's old roster; transient membership is not required.
                if (claimed is not null)
                {
                    var epoch = await MatchEpochAsync(cancellationToken);
                    lock (sync) if (claimed.QueueId == query.RequestId && claimed.QueueEpoch != epoch) return Invalidate(claimed, query.RequestId);
                }
                var abort = method == ServiceMethods.QueueCancel && frozen is not null;
                var reply = await rpc.CallAsync(new(ServiceRole.Match), abort ? ServiceMethods.QueueAbort : method, abort ? frozen! : query, cancellationToken, context.PlayerId);
                if (reply.Success)
                {
                    var status = reply.Read(MatchStatus.Parser);
                    if (status.State is "cancelled" or "failed") lock (sync)
                    {
                        if (membership.TryGetValue(context.PlayerId, out var id) && parties[id].QueueId == query.RequestId) { ClearQueue(parties[id]); Mutate(parties[id], true); }
                    }
                }
                return reply;
            }
            if (method == ServiceMethods.PartyQueue)
            {
                var request = PartyQueueRequest.Parser.ParseFrom(payload.Span);
                Party queuedParty;
                bool retry;
                lock (sync)
                {
                    if (!parties.TryGetValue(request.PartyId, out queuedParty!) || !IsMember(queuedParty, context.PlayerId)) return ServiceReply.Reject("party_not_found");
                    if (retiredQueues.TryGetValue(request.RequestId, out var retired)) return retired.Players.Contains(context.PlayerId) ? ServiceReply.From(retired.Status.Clone()) : ServiceReply.Reject("request_conflict");
                    if (queuedParty.State.LeaderId != context.PlayerId) return ServiceReply.Reject("leader_required");
                    if (queuedParty.State.Version != request.ExpectedVersion) return ServiceReply.Reject("version_conflict");
                    if (!Identifiers.Valid(request.RequestId)) return ServiceReply.Reject("invalid_request_id");
                    if (queuedParty.QueueId.Length > 0 && queuedParty.QueueId != request.RequestId) return ServiceReply.Reject("party_queued");
                    if (queuedParty.State.Members.Any(m => !m.Ready)) return ServiceReply.Reject("party_not_ready");
                    retry = queuedParty.QueueId.Length > 0;
                }
                var epoch = await MatchEpochAsync(cancellationToken);
                if (retry)
                {
                    lock (sync) if (queuedParty.QueueId == request.RequestId && queuedParty.QueueEpoch != epoch) return Invalidate(queuedParty, request.RequestId);
                    // A retry only reads the previously claimed request. Never replay
                    // QueueJoin after unknown status, timeout or a Match restart.
                    return await rpc.CallAsync(new(ServiceRole.Match), ServiceMethods.MatchStatus, new MatchQuery { RequestId = request.RequestId }, cancellationToken, context.PlayerId);
                }
                QueueEntry queue;
                lock (sync)
                {
                    if (!parties.TryGetValue(request.PartyId, out var party) || !IsMember(party, context.PlayerId)) return ServiceReply.Reject("party_not_found");
                    if (party.State.LeaderId != context.PlayerId) return ServiceReply.Reject("leader_required");
                    if (party.State.Version != request.ExpectedVersion) return ServiceReply.Reject("version_conflict");
                    if (!Identifiers.Valid(request.RequestId)) return ServiceReply.Reject("invalid_request_id");
                    // Another first-join operation may have claimed while epoch RPC was
                    // in flight. Do not overwrite its frozen epoch or issue another join.
                    if (party.QueueId.Length > 0) return ServiceReply.Reject("party_queued");
                    if (party.State.Members.Any(m => !m.Ready)) return ServiceReply.Reject("party_not_ready");
                    party.QueueId = request.RequestId; // claim before remote call; timeout preserves claim.
                    party.QueueEpoch = epoch;
                    party.State.QueueRequestId = request.RequestId;
                    queue = new() { RequestId = request.RequestId, PartyId = party.State.PartyId, PartyVersion = party.State.Version, ExpectedMatchEpoch = epoch, AdmissionExpiresUnixSeconds = clock.GetUtcNow().AddSeconds(30).ToUnixTimeSeconds() };
                    queue.PlayerIds.Add(party.State.Members.Select(m => m.PlayerId));
                    party.FrozenQueue = queue.Clone();
                }
                var reply = await rpc.CallAsync(new(ServiceRole.Match), ServiceMethods.QueueJoin, queue, cancellationToken, context.PlayerId);
                if (!reply.Success && reply.Error == "stale_match_epoch") lock (sync)
                {
                    if (parties.TryGetValue(request.PartyId, out var party) && party.QueueId == request.RequestId && party.QueueEpoch == epoch) return Invalidate(party, request.RequestId);
                }
                // Only these pre-acceptance domain rejections prove no queue ownership exists.
                // Unknown errors, identity conflicts and timeouts require status/cancel reconciliation.
                if (!reply.Success && reply.Error is "invalid_queue" or "player_busy" or "queue_capacity" or "forbidden" or "request_too_large" or "invalid_request") lock (sync)
                {
                    if (parties.TryGetValue(request.PartyId, out var party) && party.QueueId == request.RequestId) ClearQueue(party);
                }
                return reply;
            }
            var command = PartyCommand.Parser.ParseFrom(payload.Span);
            lock (sync)
            {
                if (method == ServiceMethods.PartyCreate)
                {
                    if (membership.ContainsKey(context.PlayerId)) return ServiceReply.Reject("already_in_party");
                    if (parties.Count >= maxParties) return ServiceReply.Reject("party_capacity");
                    var created = new Party(Guid.NewGuid().ToString("N"), context.PlayerId);
                    parties.Add(created.State.PartyId, created); membership.Add(context.PlayerId, created.State.PartyId);
                    return ServiceReply.From(created.State.Clone());
                }
                if (!parties.TryGetValue(command.PartyId, out var party)) return ServiceReply.Reject("party_not_found");
                if (method == ServiceMethods.PartyGet) return IsMember(party, context.PlayerId) ? ServiceReply.From(party.State.Clone()) : ServiceReply.Reject("forbidden");
                if (party.QueueId.Length > 0) return ServiceReply.Reject("party_queued");
                if (party.State.Version != command.ExpectedVersion) return ServiceReply.Reject("version_conflict");
                foreach (var expired in party.Invitations.Where(p => p.Value <= clock.GetUtcNow()).Select(p => p.Key).ToArray()) party.Invitations.Remove(expired);
                if (method == ServiceMethods.PartyAccept)
                {
                    if (!party.Invitations.ContainsKey(context.PlayerId)) return ServiceReply.Reject("invitation_required");
                    if (membership.ContainsKey(context.PlayerId)) return ServiceReply.Reject("already_in_party");
                    if (party.State.Members.Count >= maxMembers) return ServiceReply.Reject("member_capacity");
                    party.Invitations.Remove(context.PlayerId); party.State.Members.Add(new PartyMember { PlayerId = context.PlayerId }); membership.Add(context.PlayerId, party.State.PartyId); Mutate(party, true);
                }
                else
                {
                    var member = party.State.Members.FirstOrDefault(m => m.PlayerId == context.PlayerId);
                    if (member is null) return ServiceReply.Reject("forbidden");
                    switch (method)
                    {
                        case ServiceMethods.PartyInvite:
                            if (party.State.LeaderId != context.PlayerId) return ServiceReply.Reject("leader_required");
                            if (!Identifiers.Valid(command.TargetPlayerId) || membership.ContainsKey(command.TargetPlayerId)) return ServiceReply.Reject("invalid_invitee");
                            if (party.Invitations.Count >= maxMembers && !party.Invitations.ContainsKey(command.TargetPlayerId)) return ServiceReply.Reject("invitation_capacity");
                            party.Invitations[command.TargetPlayerId] = clock.GetUtcNow().AddMinutes(5); Mutate(party, true); break;
                        case ServiceMethods.PartyReady:
                            member.Ready = command.Ready; Mutate(party, false); break;
                        case ServiceMethods.PartyLeave:
                            party.State.Members.Remove(member); membership.Remove(context.PlayerId); Mutate(party, true);
                            if (party.State.Members.Count == 0) parties.Remove(party.State.PartyId);
                            else if (party.State.LeaderId == context.PlayerId) { party.State.LeaderId = party.State.Members[0].PlayerId; party.Invitations.Clear(); }
                            break;
                        default: return ServiceReply.Reject("unknown_method");
                    }
                }
                return ServiceReply.From(party.State.Clone());
            }
        }
        catch (InvalidProtocolBufferException) { return ServiceReply.Reject("invalid_request"); }
        catch (TimeoutException) { return ServiceReply.Reject("unavailable"); }
        catch (ServiceException ex) { return ServiceReply.Reject(ex.Code); }
    }
    static bool IsMember(Party party, string player) => party.State.Members.Any(m => m.PlayerId == player);
    async ValueTask<string> MatchEpochAsync(CancellationToken ct)
    {
        var reply = await rpc.CallAsync(new(ServiceRole.Match), ServiceMethods.MatchEpoch, new Empty(), ct);
        var epoch = reply.Read(ServiceEpoch.Parser).Epoch;
        if (!Identifiers.Valid(epoch)) throw new ServiceException("unavailable");
        return epoch;
    }
    static void ClearQueue(Party party) { party.QueueId = ""; party.QueueEpoch = ""; party.FrozenQueue = null; party.State.QueueRequestId = ""; }
    ServiceReply Invalidate(Party party, string requestId)
    {
        var status = new MatchStatus { RequestId = requestId, State = "failed", Failure = "queue_lost" };
        if (!retiredQueues.ContainsKey(requestId))
        {
            retiredQueues.Add(requestId, (party.State.Members.Select(m => m.PlayerId).ToArray(), status));
            retiredOrder.Enqueue(requestId);
            while (retiredOrder.Count > maxPresence) retiredQueues.Remove(retiredOrder.Dequeue());
        }
        ClearQueue(party); Mutate(party, true);
        return ServiceReply.From(status);
    }
    static void Mutate(Party party, bool clearReady)
    {
        party.State.Version++;
        if (clearReady) foreach (var member in party.State.Members) member.Ready = false;
    }
}
