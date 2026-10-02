using AiNative.Protocol.Backend.V1;
using AiNative.Server.Backend;
using AiNative.Server.Control;
using Google.Protobuf;
using NUnit.Framework;
using System.Security.Cryptography;

namespace AiNative.Server.Backend.Tests;

public sealed class DomainTests
{
    static ServiceCallContext Gate(string player = "") => new(ServiceRole.Gate, "gate", player);
    static ValueTask<ServiceReply> Call(IServiceHandler service, ServiceCallContext context, string method, IMessage body) => service.HandleAsync(context, method, body.ToByteArray());

    [Test]
    public async Task Player_credentials_tickets_and_settlement_are_bound_and_idempotent()
    {
        using var key = RSA.Create(2048);
        var clock = new TestClock();
        var store = new MemoryStore();
        var authority = new RoomAuthority();
        var player = new PlayerService(store, key, clock, authority);
        var account = new AccountRequest { Username = "TestUser", Password = "correct-password" };
        var login = (await Call(player, Gate(), ServiceMethods.RegisterAccount, account)).Read(LoginResult.Parser);
        Assert.That((await Call(player, Gate(), ServiceMethods.RegisterAccount, account)).Error, Is.EqualTo("username_exists"));
        Assert.That((await Call(player, Gate(), ServiceMethods.Login, new AccountRequest { Username = "testuser", Password = "wrong-password" })).Success, Is.False);
        Assert.That((await Call(player, Gate(), ServiceMethods.ValidateSession, new SessionRequest { SessionToken = login.SessionToken })).Read(PlayerIdentity.Parser).PlayerId, Is.EqualTo(login.PlayerId));
        var room = new RoomAllocation { MatchId = "match", RoomId = "room", NodeId = "battle", BootEpoch = "epoch", State = "Ready" };
        room.PlayerIds.Add(login.PlayerId);
        authority.Room = room;
        var ticket = (await Call(player, Gate(login.PlayerId), ServiceMethods.IssueTicket, new TicketRequest { PlayerId = login.PlayerId, Allocation = room })).Read(EntryTicket.Parser);
        var verifier = new EntryTicketVerifier(key, clock);
        Assert.That(verifier.TryVerify(ticket.Ticket, login.PlayerId, "room", "battle", "epoch", out _), Is.True);
        Assert.That(verifier.TryVerify(ticket.Ticket, login.PlayerId, "other", "battle", "epoch", out _), Is.False);
        Assert.That(verifier.TryVerify(ticket.Ticket, login.PlayerId, "room", "battle", "other", out _), Is.False);
        Assert.That(verifier.TryVerify(ticket.Ticket + "x", login.PlayerId, "room", "battle", "epoch", out _), Is.False);
        clock.Now += TimeSpan.FromMinutes(10);
        Assert.That(verifier.TryVerify(ticket.Ticket, login.PlayerId, "room", "battle", "epoch", out _), Is.False);
        var result = new MatchResult { MatchId = "match", RoomId = "room", NodeId = "battle", BootEpoch = "epoch", Completion = "completed" };
        result.Players.Add(new PlayerResult { PlayerId = login.PlayerId, Won = true, Kills = 3 });
        Assert.That((await Call(player, Gate(login.PlayerId), ServiceMethods.Settle, result)).Success, Is.False);
        Assert.That((await Call(player, new(ServiceRole.Battle, "impostor"), ServiceMethods.Settle, result)).Success, Is.False);
        Assert.That((await Call(player, new(ServiceRole.Battle, "battle"), ServiceMethods.Settle, result)).Success, Is.True);
        Assert.That((await Call(player, new(ServiceRole.Battle, "battle"), ServiceMethods.Settle, result)).Success, Is.True);
        result.Players[0].Kills++;
        Assert.That((await Call(player, new(ServiceRole.Battle, "battle"), ServiceMethods.Settle, result)).Error, Is.EqualTo("settlement_conflict"));
        var profile = (await Call(player, Gate(login.PlayerId), ServiceMethods.Profile, new ProfileRequest { PlayerId = login.PlayerId })).Read(PlayerProfile.Parser);
        Assert.That((profile.Played, profile.Won, profile.Kills), Is.EqualTo((1L, 1L, 3L)));
    }

    [Test]
    public async Task Lobby_requires_invitation_leader_and_current_ready_version()
    {
        var lobby = new LobbyService(new Remote(), maxMembers: 2);
        var party = (await Call(lobby, Gate("p1"), ServiceMethods.PartyCreate, new PartyCommand())).Read(PartyState.Parser);
        Assert.That((await Call(lobby, Gate("p2"), ServiceMethods.PartyAccept, new PartyCommand { PartyId = party.PartyId, ExpectedVersion = party.Version })).Error, Is.EqualTo("invitation_required"));
        Assert.That((await Call(lobby, Gate("p2"), ServiceMethods.PartyInvite, new PartyCommand { PartyId = party.PartyId, TargetPlayerId = "p3", ExpectedVersion = party.Version })).Success, Is.False);
        party = (await Call(lobby, Gate("p1"), ServiceMethods.PartyInvite, new PartyCommand { PartyId = party.PartyId, TargetPlayerId = "p2", ExpectedVersion = party.Version })).Read(PartyState.Parser);
        party = (await Call(lobby, Gate("p2"), ServiceMethods.PartyAccept, new PartyCommand { PartyId = party.PartyId, ExpectedVersion = party.Version })).Read(PartyState.Parser);
        Assert.That((await Call(lobby, Gate("p1"), ServiceMethods.PartyQueue, new PartyQueueRequest { PartyId = party.PartyId, ExpectedVersion = party.Version, RequestId = "q" })).Success, Is.False);
        party = (await Call(lobby, Gate("p1"), ServiceMethods.PartyReady, new PartyCommand { PartyId = party.PartyId, ExpectedVersion = party.Version, Ready = true })).Read(PartyState.Parser);
        party = (await Call(lobby, Gate("p2"), ServiceMethods.PartyReady, new PartyCommand { PartyId = party.PartyId, ExpectedVersion = party.Version, Ready = true })).Read(PartyState.Parser);
        Assert.That((await Call(lobby, Gate("p2"), ServiceMethods.PartyQueue, new PartyQueueRequest { PartyId = party.PartyId, ExpectedVersion = party.Version, RequestId = "q" })).Success, Is.False);
        Assert.That((await Call(lobby, Gate("p1"), ServiceMethods.PartyQueue, new PartyQueueRequest { PartyId = party.PartyId, ExpectedVersion = party.Version, RequestId = "q" })).Success, Is.True);
        Assert.That((await Call(lobby, Gate("p1"), ServiceMethods.PartyLeave, new PartyCommand { PartyId = party.PartyId, ExpectedVersion = party.Version })).Error, Is.EqualTo("party_queued"));
        Assert.That((await Call(new LobbyService(new Remote()), Gate("p1"), ServiceMethods.PartyGet, new PartyCommand { PartyId = party.PartyId })).Success, Is.False);
    }

    [Test]
    public async Task Match_claims_before_allocation_and_retries_same_identity_after_timeout()
    {
        var remote = new Remote { Timeout = true };
        var match = new MatchService(remote, new TestClock(), bootEpoch: "match-epoch");
        var entry = new QueueEntry { RequestId = "q1", PartyId = "party", PartyVersion = 3, ExpectedMatchEpoch = "match-epoch", AdmissionExpiresUnixSeconds = 1800000030 };
        entry.PlayerIds.Add(new[] { "p1", "p2" });
        Assert.That((await Call(match, new(ServiceRole.Lobby, "lobby"), ServiceMethods.QueueJoin, entry)).Success, Is.True);
        Assert.That((await Call(match, new(ServiceRole.Lobby, "lobby"), ServiceMethods.QueueJoin, entry)).Success, Is.True);
        var duplicate = entry.Clone(); duplicate.RequestId = "q2";
        Assert.That((await Call(match, new(ServiceRole.Lobby, "lobby"), ServiceMethods.QueueJoin, duplicate)).Error, Is.EqualTo("player_busy"));
        Assert.That((await Call(match, Gate("intruder"), ServiceMethods.QueueCancel, new MatchQuery { RequestId = "q1" })).Success, Is.False);
        await match.PumpAsync(default);
        Assert.That((await Call(match, Gate("p1"), ServiceMethods.QueueCancel, new MatchQuery { RequestId = "q1" })).Error, Is.EqualTo("allocation_claimed"));
        remote.Timeout = false;
        await match.PumpAsync(default);
        var status = (await Call(match, Gate("p1"), ServiceMethods.MatchStatus, new MatchQuery { RequestId = "q1" })).Read(MatchStatus.Parser);
        Assert.That(status.State, Is.EqualTo("ready"));
        Assert.That(remote.MatchIds.Distinct().Count(), Is.EqualTo(1));
        Assert.That((await Call(match, new(ServiceRole.Lobby, "lobby"), ServiceMethods.QueueJoin, duplicate)).Success, Is.False);
    }
}

sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1800000000);
    public override DateTimeOffset GetUtcNow() => Now;
}
sealed class Remote : IServiceRpc
{
    public bool Timeout;
    public List<string> MatchIds { get; } = [];
    public ValueTask<ServiceReply> CallAsync(ServiceTarget target, string method, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default, string playerId = "")
    {
        if (method == ServiceMethods.MatchEpoch) return ValueTask.FromResult(ServiceReply.From(new ServiceEpoch { Epoch = "match-epoch" }));
        if (method == ServiceMethods.QueueJoin) return ValueTask.FromResult(ServiceReply.From(new MatchStatus { RequestId = QueueEntry.Parser.ParseFrom(payload.Span).RequestId, State = "queued" }));
        var request = AllocationRequest.Parser.ParseFrom(payload.Span);
        MatchIds.Add(request.MatchId);
        if (Timeout) throw new TimeoutException();
        var allocation = new RoomAllocation { AllocationId = "allocation", MatchId = request.MatchId, RoomId = "room", NodeId = "node", BootEpoch = "epoch", State = "Ready" }; allocation.PlayerIds.Add(request.PlayerIds);
        return ValueTask.FromResult(ServiceReply.From(allocation));
    }
}
sealed class RoomAuthority : IServiceRpc
{
    public RoomAllocation? Room;
    public ValueTask<ServiceReply> CallAsync(ServiceTarget target, string method, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default, string playerId = "") => ValueTask.FromResult(ServiceReply.From(Room!));
}
