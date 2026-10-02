using AiNative.Protocol.Backend.V1;
using AiNative.Server.Backend;
using AiNative.Server.Control;
using Google.Protobuf;
using NUnit.Framework;
using System.Security.Cryptography;

namespace AiNative.Server.Backend.Tests;

public sealed class EdgeTests
{
    static ValueTask<ServiceReply> Call(IServiceHandler s, ServiceRole role, string player, string method, IMessage body) => s.HandleAsync(new(role, role.ToString().ToLowerInvariant(), player), method, body.ToByteArray());
    static QueueEntry Entry(string request, params string[] players) { var entry = new QueueEntry { RequestId = request, PartyId = request, PartyVersion = 1, ExpectedMatchEpoch = "match-epoch", AdmissionExpiresUnixSeconds = 1800000030 }; entry.PlayerIds.Add(players); return entry; }

    [Test]
    public async Task Cancellation_of_background_pump_does_not_leave_claimed_jobs_unretryable()
    {
        var remote = new CancellingRemote();
        var service = new MatchService(remote, new TestClock(), bootEpoch: "match-epoch");
        await Call(service, ServiceRole.Lobby, "", ServiceMethods.QueueJoin, Entry("q1", "p1", "p2"));
        await Call(service, ServiceRole.Lobby, "", ServiceMethods.QueueJoin, Entry("q2", "p3", "p4"));
        Assert.ThrowsAsync<OperationCanceledException>(async () => await service.PumpAsync(default));
        remote.Cancel = false;
        await service.PumpAsync(default);
        Assert.That((await Call(service, ServiceRole.Gate, "p3", ServiceMethods.MatchStatus, new MatchQuery { RequestId = "q2" })).Read(MatchStatus.Parser).State, Is.EqualTo("ready"));
    }

    [Test]
    public async Task Cancel_packed_parties_and_terminal_room_release_allow_requeue()
    {
        var remote = new AllocationRemote();
        var service = new MatchService(remote, new TestClock(), maxQueuedPlayers: 2, bootEpoch: "match-epoch");
        var one = Entry("q1", "p1");
        await Call(service, ServiceRole.Lobby, "", ServiceMethods.QueueJoin, one);
        var conflicting = one.Clone(); conflicting.PartyVersion++;
        Assert.That((await Call(service, ServiceRole.Lobby, "", ServiceMethods.QueueJoin, conflicting)).Error, Is.EqualTo("request_conflict"));
        Assert.That((await Call(service, ServiceRole.Gate, "intruder", ServiceMethods.MatchStatus, new MatchQuery { RequestId = "q1" })).Error, Is.EqualTo("forbidden"));
        Assert.That((await Call(service, ServiceRole.Gate, "p1", ServiceMethods.QueueCancel, new MatchQuery { RequestId = "q1" })).Read(MatchStatus.Parser).State, Is.EqualTo("cancelled"));
        await Call(service, ServiceRole.Lobby, "", ServiceMethods.QueueJoin, Entry("q2", "p1"));
        await Call(service, ServiceRole.Lobby, "", ServiceMethods.QueueJoin, Entry("q3", "p2"));
        Assert.That((await Call(service, ServiceRole.Lobby, "", ServiceMethods.QueueJoin, Entry("q4", "p3"))).Error, Is.EqualTo("queue_capacity"));
        await service.PumpAsync(default);
        Assert.That((await Call(service, ServiceRole.Gate, "p1", ServiceMethods.MatchStatus, new MatchQuery { RequestId = "q2" })).Read(MatchStatus.Parser).State, Is.EqualTo("ready"));
        remote.Release = true;
        await service.PumpAsync(default);
        Assert.That((await Call(service, ServiceRole.Gate, "p1", ServiceMethods.MatchStatus, new MatchQuery { RequestId = "q2" })).Read(MatchStatus.Parser).State, Is.EqualTo("failed"));
        Assert.That((await Call(service, ServiceRole.Lobby, "", ServiceMethods.QueueJoin, Entry("q5", "p1", "p2"))).Success, Is.True);
        Assert.That((await Call(new MatchService(remote, new TestClock(), bootEpoch: "match-epoch"), ServiceRole.Gate, "p1", ServiceMethods.MatchStatus, new MatchQuery { RequestId = "q5" })).Error, Is.EqualTo("queue_not_found"));
    }

    [Test]
    public async Task Party_bounds_version_changes_and_cancel_reconciliation_are_enforced()
    {
        var lobby = new LobbyService(new PartyRemote(), maxParties: 1, maxMembers: 1);
        var party = (await Call(lobby, ServiceRole.Gate, "p1", ServiceMethods.PartyCreate, new PartyCommand())).Read(PartyState.Parser);
        Assert.That((await Call(lobby, ServiceRole.Gate, "p2", ServiceMethods.PartyCreate, new PartyCommand())).Error, Is.EqualTo("party_capacity"));
        party = (await Call(lobby, ServiceRole.Gate, "p1", ServiceMethods.PartyInvite, new PartyCommand { PartyId = party.PartyId, ExpectedVersion = party.Version, TargetPlayerId = "p2" })).Read(PartyState.Parser);
        Assert.That((await Call(lobby, ServiceRole.Gate, "p2", ServiceMethods.PartyAccept, new PartyCommand { PartyId = party.PartyId, ExpectedVersion = party.Version })).Error, Is.EqualTo("member_capacity"));
        Assert.That((await Call(lobby, ServiceRole.Gate, "p1", ServiceMethods.PartyReady, new PartyCommand { PartyId = party.PartyId, ExpectedVersion = party.Version - 1, Ready = true })).Error, Is.EqualTo("version_conflict"));
        party = (await Call(lobby, ServiceRole.Gate, "p1", ServiceMethods.PartyReady, new PartyCommand { PartyId = party.PartyId, ExpectedVersion = party.Version, Ready = true })).Read(PartyState.Parser);
        await Call(lobby, ServiceRole.Gate, "p1", ServiceMethods.PartyQueue, new PartyQueueRequest { PartyId = party.PartyId, ExpectedVersion = party.Version, RequestId = "q" });
        Assert.That((await Call(lobby, ServiceRole.Gate, "p1", ServiceMethods.QueueCancel, new MatchQuery { RequestId = "q" })).Success, Is.True);
        party = (await Call(lobby, ServiceRole.Gate, "p1", ServiceMethods.PartyGet, new PartyCommand { PartyId = party.PartyId })).Read(PartyState.Parser);
        Assert.That(party.Members[0].Ready, Is.False);
        Assert.That((await Call(lobby, ServiceRole.Gate, "p1", ServiceMethods.PartyLeave, new PartyCommand { PartyId = party.PartyId, ExpectedVersion = party.Version })).Success, Is.True);
    }

    [Test]
    public async Task Session_expiry_input_validation_and_database_outage_fail_closed()
    {
        using var key = RSA.Create(2048); var clock = new TestClock(); var store = new MemoryStore(); var service = new PlayerService(store, key, clock, sessionLifetime: TimeSpan.FromSeconds(1));
        var request = new AccountRequest { Username = "player", Password = "valid-password" };
        var login = (await Call(service, ServiceRole.Gate, "", ServiceMethods.RegisterAccount, request)).Read(LoginResult.Parser);
        clock.Now += TimeSpan.FromSeconds(1);
        Assert.That((await Call(service, ServiceRole.Gate, "", ServiceMethods.ValidateSession, new SessionRequest { SessionToken = login.SessionToken })).Error, Is.EqualTo("invalid_session"));
        Assert.That((await Call(service, ServiceRole.Gate, "other", ServiceMethods.Profile, new ProfileRequest { PlayerId = login.PlayerId })).Error, Is.EqualTo("forbidden"));
        Assert.That((await Call(service, ServiceRole.Client, "", ServiceMethods.Login, request)).Error, Is.EqualTo("forbidden"));
        request.Password = "short";
        Assert.That((await Call(service, ServiceRole.Gate, "", ServiceMethods.RegisterAccount, request)).Error, Is.EqualTo("invalid_credentials"));
        await using var unavailable = new PostgresPlayerStore("Host=127.0.0.1;Port=1;Database=unavailable;Username=test;Timeout=1;Pooling=false");
        var offline = new PlayerService(unavailable, key, clock); request.Password = "valid-password";
        Assert.That((await Call(offline, ServiceRole.Gate, "", ServiceMethods.RegisterAccount, request)).Error, Is.EqualTo("unavailable"));
    }

    [Test]
    public async Task Player_checks_authoritative_epoch_roster_and_requires_coordinator()
    {
        using var key = RSA.Create(2048); var clock = new TestClock(); var store = new MemoryStore();
        var authority = new RoomAuthority(); var service = new PlayerService(store, key, clock, authority);
        var login = (await Call(service, ServiceRole.Gate, "", ServiceMethods.RegisterAccount, new AccountRequest { Username = "player", Password = "valid-password" })).Read(LoginResult.Parser);
        var room = new RoomAllocation { AllocationId = "allocation", MatchId = "match", RoomId = "room", NodeId = "battle", BootEpoch = "epoch", State = "Ready" }; room.PlayerIds.Add(login.PlayerId);
        authority.Room = room.Clone();
        var request = new TicketRequest { PlayerId = login.PlayerId, Allocation = room };
        var unavailable = new PlayerService(store, key, clock);
        Assert.That((await Call(unavailable, ServiceRole.Gate, login.PlayerId, ServiceMethods.IssueTicket, request)).Error, Is.EqualTo("authority_unavailable"));
        authority.Room.BootEpoch = "new-epoch";
        Assert.That((await Call(service, ServiceRole.Gate, login.PlayerId, ServiceMethods.IssueTicket, request)).Error, Is.EqualTo("invalid_allocation"));
        authority.Room = room.Clone();
        var result = new MatchResult { MatchId = "match", RoomId = "room", NodeId = "battle", BootEpoch = "epoch", Completion = "completed" }; result.Players.Add(new PlayerResult { PlayerId = login.PlayerId, Kills = 1 });
        authority.Room.PlayerIds.Add("extra-player");
        Assert.That((await Call(service, ServiceRole.Battle, "", ServiceMethods.Settle, result)).Error, Is.EqualTo("invalid_result"));
        authority.Room = room.Clone(); authority.Room.State = "Lost";
        Assert.That((await Call(service, ServiceRole.Battle, "", ServiceMethods.Settle, result)).Success, Is.True);
        var negative = result.Clone(); negative.Players[0].Kills = -1;
        Assert.That((await Call(service, ServiceRole.Battle, "", ServiceMethods.Settle, negative)).Error, Is.EqualTo("invalid_result"));
        var duplicated = result.Clone(); duplicated.Players.Add(duplicated.Players[0].Clone());
        Assert.That((await Call(service, ServiceRole.Battle, "", ServiceMethods.Settle, duplicated)).Error, Is.EqualTo("invalid_result"));
    }
}

class AllocationRemote : IServiceRpc
{
    public bool Release;
    protected RoomAllocation? Allocation;
    public virtual ValueTask<ServiceReply> CallAsync(ServiceTarget target, string method, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default, string playerId = "")
    {
        if (method == ServiceMethods.Allocate)
        {
            var request = AllocationRequest.Parser.ParseFrom(payload.Span);
            Allocation = new() { AllocationId = "allocation", MatchId = request.MatchId, RoomId = "room", NodeId = "battle", BootEpoch = "epoch", State = "Ready" }; Allocation.PlayerIds.Add(request.PlayerIds);
        }
        var result = Allocation!.Clone(); if (Release) result.State = "Released";
        return ValueTask.FromResult(ServiceReply.From(result));
    }
}
sealed class CancellingRemote : AllocationRemote
{
    public bool Cancel = true;
    public override ValueTask<ServiceReply> CallAsync(ServiceTarget target, string method, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default, string playerId = "")
    {
        if (Cancel) throw new OperationCanceledException();
        return base.CallAsync(target, method, payload, cancellationToken, playerId);
    }
}
sealed class PartyRemote : IServiceRpc
{
    public ValueTask<ServiceReply> CallAsync(ServiceTarget target, string method, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default, string playerId = "")
    {
        if (method == ServiceMethods.MatchEpoch) return ValueTask.FromResult(ServiceReply.From(new ServiceEpoch { Epoch = "match-epoch" }));
        if (method == ServiceMethods.QueueAbort) return ValueTask.FromResult(ServiceReply.From(new MatchStatus { RequestId = QueueEntry.Parser.ParseFrom(payload.Span).RequestId, State = "cancelled" }));
        if (method == ServiceMethods.QueueCancel) return ValueTask.FromResult(ServiceReply.From(new MatchStatus { RequestId = MatchQuery.Parser.ParseFrom(payload.Span).RequestId, State = "cancelled" }));
        return ValueTask.FromResult(ServiceReply.From(new MatchStatus { RequestId = QueueEntry.Parser.ParseFrom(payload.Span).RequestId, State = "queued" }));
    }
}
