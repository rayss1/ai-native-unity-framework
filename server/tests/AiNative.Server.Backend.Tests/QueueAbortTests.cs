using AiNative.Protocol.Backend.V1;
using AiNative.Server.Backend;
using AiNative.Server.Control;
using Google.Protobuf;
using NUnit.Framework;

namespace AiNative.Server.Backend.Tests;

public sealed class QueueAbortTests
{
    static QueueEntry Entry(TestClock clock, string id, string player = "p1")
    {
        var entry = new QueueEntry { RequestId = id, PartyId = "party", PartyVersion = 1, ExpectedMatchEpoch = "epoch", AdmissionExpiresUnixSeconds = clock.GetUtcNow().AddSeconds(30).ToUnixTimeSeconds() }; entry.PlayerIds.Add(player); return entry;
    }
    static ValueTask<ServiceReply> Call(MatchService service, string method, QueueEntry entry, string player = "p1", ServiceRole role = ServiceRole.Lobby) => service.HandleAsync(new(role, "lobby", player), method, entry.ToByteArray());
    [Test]
    public async Task Absent_abort_tombstone_prevents_delayed_join_and_rejects_mismatched_roster()
    {
        var clock = new TestClock(); var match = new MatchService(new PartyRemote(), clock, bootEpoch: "epoch"); var entry = Entry(clock, "lost");
        Assert.That((await Call(match, ServiceMethods.QueueAbort, entry)).Read(MatchStatus.Parser).State, Is.EqualTo("cancelled"));
        Assert.That((await Call(match, ServiceMethods.QueueJoin, entry)).Read(MatchStatus.Parser).State, Is.EqualTo("cancelled"));
        var conflict = entry.Clone(); conflict.PartyVersion++;
        Assert.That((await Call(match, ServiceMethods.QueueAbort, conflict)).Error, Is.EqualTo("request_conflict"));
        Assert.That((await Call(match, ServiceMethods.QueueAbort, entry, "intruder")).Error, Is.EqualTo("forbidden"));
        Assert.That((await Call(match, ServiceMethods.QueueAbort, entry, role: ServiceRole.Gate)).Error, Is.EqualTo("forbidden"));
    }
    [Test]
    public async Task Capacity_pressure_cannot_evict_unexpired_abort_but_expiry_reclaims_space()
    {
        var clock = new TestClock(); var match = new MatchService(new PartyRemote(), clock, playersPerMatch: 1, maxQueuedPlayers: 1, bootEpoch: "epoch"); var first = Entry(clock, "first");
        await Call(match, ServiceMethods.QueueAbort, first);
        await Call(match, ServiceMethods.QueueAbort, Entry(clock, "second"));
        Assert.That((await Call(match, ServiceMethods.QueueAbort, Entry(clock, "third"))).Error, Is.EqualTo("queue_capacity"));
        Assert.That((await Call(match, ServiceMethods.QueueJoin, Entry(clock, "new"))).Error, Is.EqualTo("queue_capacity"));
        Assert.That((await Call(match, ServiceMethods.QueueJoin, first)).Read(MatchStatus.Parser).State, Is.EqualTo("cancelled"));
        clock.Now += TimeSpan.FromSeconds(30);
        Assert.That((await Call(match, ServiceMethods.QueueJoin, first)).Error, Is.EqualTo("queue_expired"));
        Assert.That((await Call(match, ServiceMethods.QueueJoin, Entry(clock, "fresh"))).Read(MatchStatus.Parser).State, Is.EqualTo("queued"));
    }
    [Test]
    public async Task Admission_deadline_does_not_expire_accepted_queue_and_abort_cannot_cancel_claimed_allocation()
    {
        var clock = new TestClock(); var match = new MatchService(new AllocationRemote(), clock, playersPerMatch: 1, bootEpoch: "epoch"); var entry = Entry(clock, "accepted");
        await Call(match, ServiceMethods.QueueJoin, entry);
        clock.Now += TimeSpan.FromSeconds(61);
        Assert.That((await Call(match, ServiceMethods.QueueJoin, entry)).Read(MatchStatus.Parser).State, Is.EqualTo("queued"));
        await match.PumpAsync(default);
        Assert.That((await Call(match, ServiceMethods.QueueAbort, entry)).Error, Is.EqualTo("allocation_claimed"));
        var tooFar = Entry(clock, "future"); tooFar.AdmissionExpiresUnixSeconds += 31;
        Assert.That((await Call(match, ServiceMethods.QueueJoin, tooFar)).Error, Is.EqualTo("invalid_queue"));
    }
    [Test]
    public async Task Lobby_lost_before_send_is_cancelable_and_frozen_join_cannot_resurrect()
    {
        var clock = new TestClock(); var remote = new LostJoinRemote(clock); var lobby = new LobbyService(remote, clock: clock); var gate = new ServiceCallContext(ServiceRole.Gate, "gate", "p1");
        var party = (await lobby.HandleAsync(gate, ServiceMethods.PartyCreate, new PartyCommand().ToByteArray())).Read(PartyState.Parser);
        party = (await lobby.HandleAsync(gate, ServiceMethods.PartyReady, new PartyCommand { PartyId = party.PartyId, ExpectedVersion = party.Version, Ready = true }.ToByteArray())).Read(PartyState.Parser);
        var request = new PartyQueueRequest { PartyId = party.PartyId, ExpectedVersion = party.Version, RequestId = "lost" };
        Assert.That((await lobby.HandleAsync(gate, ServiceMethods.PartyQueue, request.ToByteArray())).Error, Is.EqualTo("timeout"));
        clock.Now += TimeSpan.FromSeconds(5);
        Assert.That((await lobby.HandleAsync(gate, ServiceMethods.PartyQueue, request.ToByteArray())).Error, Is.EqualTo("queue_not_found"));
        var cancelled = (await lobby.HandleAsync(gate, ServiceMethods.QueueCancel, new MatchQuery { RequestId = "lost" }.ToByteArray())).Read(MatchStatus.Parser);
        Assert.That(cancelled.State, Is.EqualTo("cancelled"));
        Assert.That(remote.Abort!.Equals(remote.Frozen), Is.True, "Abort must carry the exact first claim including its unchanged expiry.");
        Assert.That((await Call(remote.Match, ServiceMethods.QueueJoin, remote.Frozen!)).Read(MatchStatus.Parser).State, Is.EqualTo("cancelled"));
        var cleared = (await lobby.HandleAsync(gate, ServiceMethods.PartyGet, new PartyCommand { PartyId = party.PartyId }.ToByteArray())).Read(PartyState.Parser);
        Assert.That(cleared.QueueRequestId, Is.Empty); Assert.That(cleared.Members[0].Ready, Is.False);
        Assert.That((await lobby.HandleAsync(gate, ServiceMethods.PartyLeave, new PartyCommand { PartyId = party.PartyId, ExpectedVersion = cleared.Version }.ToByteArray())).Success, Is.True);
    }
    [TestCase("capacity-unavailable")]
    [TestCase("room-history-capacity")]
    [TestCase("player-already-allocated")]
    public async Task Definitive_new_allocation_rejection_allows_atomic_packed_job_cancel(string code)
    {
        var clock = new TestClock(); var match = new MatchService(new ReplyRemote((_, _, _) => ServiceReply.Reject(code)), clock, bootEpoch: "epoch");
        var first = Entry(clock, "first", "p1"); var second = Entry(clock, "second", "p2");
        await Call(match, ServiceMethods.QueueJoin, first); await Call(match, ServiceMethods.QueueJoin, second, "p2"); await match.PumpAsync(default);
        Assert.That((await Call(match, ServiceMethods.QueueAbort, first)).Read(MatchStatus.Parser).State, Is.EqualTo("cancelled"));
        Assert.That((await match.HandleAsync(new(ServiceRole.Gate, "gate", "p2"), ServiceMethods.MatchStatus, new MatchQuery { RequestId = "second" }.ToByteArray())).Read(MatchStatus.Parser).State, Is.EqualTo("cancelled"));
        Assert.That((await Call(match, ServiceMethods.QueueJoin, Entry(clock, "fresh", "p1"))).Success, Is.True);
    }
    [Test]
    public async Task Unknown_allocation_outcome_and_inflight_retry_cannot_be_cancelled()
    {
        var clock = new TestClock(); var remote = new CapacityThenBlockingRemote(); var match = new MatchService(remote, clock, playersPerMatch: 1, bootEpoch: "epoch"); var entry = Entry(clock, "request");
        await Call(match, ServiceMethods.QueueJoin, entry); await match.PumpAsync(default);
        var pump = match.PumpAsync(default).AsTask(); await remote.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That((await Call(match, ServiceMethods.QueueAbort, entry)).Error, Is.EqualTo("allocation_claimed"));
        remote.Resume.TrySetResult(); await pump;
        Assert.That((await Call(match, ServiceMethods.QueueAbort, entry)).Error, Is.EqualTo("allocation_claimed"));
    }
    [Test]
    public async Task Cancelling_aged_packed_queue_keeps_terminal_status_for_the_other_party()
    {
        var clock = new TestClock(); var match = new MatchService(new ReplyRemote((_, _, _) => ServiceReply.Reject("capacity-unavailable")), clock, bootEpoch: "epoch");
        var first = Entry(clock, "first", "p1"); var second = Entry(clock, "second", "p2");
        await Call(match, ServiceMethods.QueueJoin, first); await Call(match, ServiceMethods.QueueJoin, second, "p2");
        clock.Now += TimeSpan.FromSeconds(61); await match.PumpAsync(default);
        Assert.That((await Call(match, ServiceMethods.QueueAbort, first)).Read(MatchStatus.Parser).State, Is.EqualTo("cancelled"));
        Assert.That((await match.HandleAsync(new(ServiceRole.Gate, "gate", "p2"), ServiceMethods.MatchStatus, new MatchQuery { RequestId = "second" }.ToByteArray())).Read(MatchStatus.Parser).State, Is.EqualTo("cancelled"));
    }
}
sealed class CapacityThenBlockingRemote : IServiceRpc
{
    int calls;
    public TaskCompletionSource Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async ValueTask<ServiceReply> CallAsync(ServiceTarget target, string method, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default, string playerId = "")
    {
        if (++calls == 1) return ServiceReply.Reject("capacity-unavailable");
        Arrived.TrySetResult(); await Resume.Task;
        return ServiceReply.Reject("timeout");
    }
}
sealed class LostJoinRemote : IServiceRpc
{
    public MatchService Match { get; }
    public QueueEntry? Frozen, Abort;
    public LostJoinRemote(TestClock clock) => Match = new(new PartyRemote(), clock, bootEpoch: "epoch");
    public ValueTask<ServiceReply> CallAsync(ServiceTarget target, string method, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default, string playerId = "")
    {
        if (method == ServiceMethods.QueueJoin) { Frozen = QueueEntry.Parser.ParseFrom(payload.Span); return ValueTask.FromResult(ServiceReply.Reject("timeout")); }
        if (method == ServiceMethods.QueueAbort) Abort = QueueEntry.Parser.ParseFrom(payload.Span);
        return Match.HandleAsync(new(ServiceRole.Lobby, "lobby", playerId), method, payload, cancellationToken);
    }
}
