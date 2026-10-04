using AiNative.Protocol.Backend.V1;
using AiNative.Server.Backend;
using AiNative.Server.Control;
using Google.Protobuf;
using NUnit.Framework;
using System.Security.Cryptography;

namespace AiNative.Server.Backend.Tests;

public sealed class RecoveryTests
{
    [Test]
    public async Task Released_room_rejects_ticket_until_match_pump_reconciles_original_party()
    {
        using var f = new RecoveryFixture();
        var login = await f.LoginAsync("leader", "leader");
        var party = await f.CreateReadyQueueAsync("leader", login, "queue");
        await f.Router.Match.PumpAsync(default);
        f.Router.Room!.State = "Released";
        var query = new MatchQuery { RequestId = "queue" };
        Assert.That((await f.CallAsync("leader", ServiceMethods.MatchStatus, query, login.SessionToken)).Error,
            Is.EqualTo("invalid_allocation"));
        Assert.That((await LobbyCall(f, login.PlayerId, ServiceMethods.PartyGet,
            new PartyCommand { PartyId = party.PartyId })).Read(PartyState.Parser).QueueRequestId, Is.EqualTo("queue"));
        await f.Router.Match.PumpAsync(default);
        var terminal = (await f.CallAsync("leader", ServiceMethods.MatchStatus, query, login.SessionToken)).Read(MatchReady.Parser);
        Assert.That((terminal.Status.State, terminal.Status.Failure), Is.EqualTo(("failed", "room_released")));
        var cleared = (await LobbyCall(f, login.PlayerId, ServiceMethods.PartyGet,
            new PartyCommand { PartyId = party.PartyId })).Read(PartyState.Parser);
        Assert.That(cleared.QueueRequestId, Is.Empty);
        Assert.That((await LobbyCall(f, login.PlayerId, ServiceMethods.PartyLeave,
            new PartyCommand { PartyId = party.PartyId, ExpectedVersion = cleared.Version })).Success, Is.True);
        Assert.That((await LobbyCall(f, login.PlayerId, ServiceMethods.PartyGet,
            new PartyCommand { PartyId = party.PartyId })).Error, Is.EqualTo("party_not_found"));
    }

    static ValueTask<ServiceReply> LobbyCall(RecoveryFixture f, string player, string method, IMessage request) => f.Router.Lobby.HandleAsync(new(ServiceRole.Gate, "gate", player), method, request.ToByteArray());
    [Test]
    public async Task Match_restart_invalidates_party_readiness_and_cannot_silently_rejoin_old_request()
    {
        using var f = new RecoveryFixture(); var login = await f.LoginAsync("leader", "leader");
        var party = await f.CreateReadyQueueAsync("leader", login, "queue");
        f.Router.Match = new MatchService(f.Router, f.Clock, playersPerMatch: 1);
        var reply = (await LobbyCall(f, login.PlayerId, ServiceMethods.PartyQueue, new PartyQueueRequest { PartyId = party.PartyId, ExpectedVersion = party.Version, RequestId = "queue" })).Read(MatchStatus.Parser);
        Assert.That((reply.State, reply.Failure), Is.EqualTo(("failed", "queue_lost")));
        var cleared = (await LobbyCall(f, login.PlayerId, ServiceMethods.PartyGet, new PartyCommand { PartyId = party.PartyId })).Read(PartyState.Parser);
        Assert.That(cleared.QueueRequestId, Is.Empty); Assert.That(cleared.Version, Is.GreaterThan(party.Version)); Assert.That(cleared.Members.All(m => !m.Ready), Is.True);
        Assert.That((await f.Router.Match.HandleAsync(new(ServiceRole.Gate, "gate", login.PlayerId), ServiceMethods.MatchStatus, new MatchQuery { RequestId = "queue" }.ToByteArray())).Error, Is.EqualTo("queue_not_found"));
        Assert.That((await LobbyCall(f, login.PlayerId, ServiceMethods.PartyQueue, new PartyQueueRequest { PartyId = cleared.PartyId, ExpectedVersion = cleared.Version, RequestId = "queue" })).Read(MatchStatus.Parser).Failure, Is.EqualTo("queue_lost"));
        cleared = (await LobbyCall(f, login.PlayerId, ServiceMethods.PartyReady, new PartyCommand { PartyId = cleared.PartyId, ExpectedVersion = cleared.Version, Ready = true })).Read(PartyState.Parser);
        Assert.That((await LobbyCall(f, login.PlayerId, ServiceMethods.PartyQueue, new PartyQueueRequest { PartyId = cleared.PartyId, ExpectedVersion = cleared.Version, RequestId = "fresh" })).Read(MatchStatus.Parser).State, Is.EqualTo("queued"));
    }
    [Test]
    public async Task Late_queuejoin_cannot_revive_retired_match_generation()
    {
        using var f = new RecoveryFixture();
        var epoch = (await f.Router.Match.HandleAsync(new(ServiceRole.Lobby, "lobby"), ServiceMethods.MatchEpoch, new Empty().ToByteArray())).Read(ServiceEpoch.Parser).Epoch;
        f.Router.Match = new MatchService(f.Router, f.Clock);
        var entry = new QueueEntry { RequestId = "late", PartyId = "party", PartyVersion = 1, ExpectedMatchEpoch = epoch }; entry.PlayerIds.Add("p1");
        Assert.That((await f.Router.Match.HandleAsync(new(ServiceRole.Lobby, "lobby"), ServiceMethods.QueueJoin, entry.ToByteArray())).Error, Is.EqualTo("stale_match_epoch"));
        entry.ExpectedMatchEpoch = "";
        Assert.That((await f.Router.Match.HandleAsync(new(ServiceRole.Lobby, "lobby"), ServiceMethods.QueueJoin, entry.ToByteArray())).Error, Is.EqualTo("stale_match_epoch"));
        Assert.That((await f.Router.Match.HandleAsync(new(ServiceRole.Gate, "gate", "p1"), ServiceMethods.MatchEpoch, new Empty().ToByteArray())).Error, Is.EqualTo("forbidden"));
    }
    [TestCase(false)]
    [TestCase(true)]
    public async Task Lobby_restart_forwards_authenticated_old_queue_status_and_cancel(bool ready)
    {
        using var f = new RecoveryFixture(); var login = await f.LoginAsync("leader", "leader"); await f.CreateReadyQueueAsync("leader", login, "queue");
        if (ready) await f.Router.Match.PumpAsync(default);
        f.Router.Lobby = new LobbyService(f.Router, clock: f.Clock);
        var status = (await LobbyCall(f, login.PlayerId, ServiceMethods.MatchStatus, new MatchQuery { RequestId = "queue" })).Read(MatchStatus.Parser);
        Assert.That(status.State, Is.EqualTo(ready ? "ready" : "queued"));
        Assert.That((await LobbyCall(f, "intruder", ServiceMethods.MatchStatus, new MatchQuery { RequestId = "queue" })).Error, Is.EqualTo("forbidden"));
        if (!ready) Assert.That((await LobbyCall(f, login.PlayerId, ServiceMethods.QueueCancel, new MatchQuery { RequestId = "queue" })).Read(MatchStatus.Parser).State, Is.EqualTo("cancelled"));
        else
        {
            var result = (await f.CallAsync("leader", ServiceMethods.MatchStatus, new MatchQuery { RequestId = "queue" }, login.SessionToken)).Read(MatchReady.Parser);
            Assert.That(new EntryTicketVerifier(f.Key, f.Clock).TryVerify(result.EntryTicket, login.PlayerId, result.Status.Allocation.RoomId, "battle", "epoch", out _), Is.True);
        }
    }
    [Test]
    public async Task Invitation_discovery_and_nonleader_queue_discovery_fan_out_each_members_own_ticket()
    {
        using var f = new RecoveryFixture(playersPerMatch: 2);
        var leader = await f.LoginAsync("leader", "leader"); var member = await f.LoginAsync("member", "member");
        var party = (await f.CallAsync("leader", ServiceMethods.PartyCreate, new PartyCommand(), leader.SessionToken)).Read(PartyState.Parser);
        party = (await f.CallAsync("leader", ServiceMethods.PartyInvite, new PartyCommand { PartyId = party.PartyId, ExpectedVersion = party.Version, TargetPlayerId = member.PlayerId }, leader.SessionToken)).Read(PartyState.Parser);
        var invites = (await f.CallAsync("member", ServiceMethods.PartyInvites, new Empty(), member.SessionToken)).Read(PartyInvitations.Parser);
        Assert.That(invites.Parties.Single().PartyId, Is.EqualTo(party.PartyId));
        Assert.That((await LobbyCall(f, "uninvited", ServiceMethods.PartyInvites, new Empty())).Read(PartyInvitations.Parser).Parties, Is.Empty);
        party = (await f.CallAsync("member", ServiceMethods.PartyAccept, new PartyCommand { PartyId = invites.Parties[0].PartyId, ExpectedVersion = invites.Parties[0].Version }, member.SessionToken)).Read(PartyState.Parser);
        party = (await f.CallAsync("leader", ServiceMethods.PartyReady, new PartyCommand { PartyId = party.PartyId, ExpectedVersion = party.Version, Ready = true }, leader.SessionToken)).Read(PartyState.Parser);
        party = (await f.CallAsync("member", ServiceMethods.PartyReady, new PartyCommand { PartyId = party.PartyId, ExpectedVersion = party.Version, Ready = true }, member.SessionToken)).Read(PartyState.Parser);
        await f.CallAsync("leader", ServiceMethods.PartyQueue, new PartyQueueRequest { PartyId = party.PartyId, ExpectedVersion = party.Version, RequestId = "queue" }, leader.SessionToken);
        var discovered = (await f.CallAsync("member", ServiceMethods.PartyGet, new PartyCommand { PartyId = party.PartyId }, member.SessionToken)).Read(PartyState.Parser);
        Assert.That(discovered.QueueRequestId, Is.EqualTo("queue"));
        await f.Router.Match.PumpAsync(default); await f.Gate.PumpAsync();
        Assert.That(f.Notifier.Events.Select(e => e.Connection), Is.EquivalentTo(new[] { "leader", "member" }));
        foreach (var notification in f.Notifier.Events)
        {
            var result = MatchReady.Parser.ParseFrom(notification.Payload); var expected = notification.Connection == "leader" ? leader.PlayerId : member.PlayerId;
            Assert.That(new EntryTicketVerifier(f.Key, f.Clock).TryVerify(result.EntryTicket, expected, result.Status.Allocation.RoomId, "battle", "epoch", out _), Is.True);
        }
        f.Router.Match = new MatchService(f.Router, f.Clock, playersPerMatch: 2);
        await f.Gate.PumpAsync();
        Assert.That(f.Notifier.Events.Count(e => e.Method == "match.failed"), Is.EqualTo(2), "Every member must receive explicit queue_lost after Match restart.");
        party = (await f.CallAsync("leader", ServiceMethods.PartyGet, new PartyCommand { PartyId = party.PartyId }, leader.SessionToken)).Read(PartyState.Parser);
        party = (await f.CallAsync("leader", ServiceMethods.PartyReady, new PartyCommand { PartyId = party.PartyId, ExpectedVersion = party.Version, Ready = true }, leader.SessionToken)).Read(PartyState.Parser);
        party = (await f.CallAsync("member", ServiceMethods.PartyReady, new PartyCommand { PartyId = party.PartyId, ExpectedVersion = party.Version, Ready = true }, member.SessionToken)).Read(PartyState.Parser);
        await f.CallAsync("leader", ServiceMethods.PartyQueue, new PartyQueueRequest { PartyId = party.PartyId, ExpectedVersion = party.Version, RequestId = "fresh" }, leader.SessionToken);
        await f.Router.Match.PumpAsync(default); await f.Gate.PumpAsync();
        Assert.That(f.Notifier.Events.Count(e => e.Method == "match.ready"), Is.EqualTo(4), "Both members must discover a freshly confirmed queue after old terminal notification.");
    }
    [Test]
    public async Task Uncertain_join_and_unknown_status_preserve_claim_without_replaying_join()
    {
        var remote = new UncertainQueueRemote(); var lobby = new LobbyService(remote);
        var gate = new ServiceCallContext(ServiceRole.Gate, "gate", "p1");
        var party = (await lobby.HandleAsync(gate, ServiceMethods.PartyCreate, new PartyCommand().ToByteArray())).Read(PartyState.Parser);
        party = (await lobby.HandleAsync(gate, ServiceMethods.PartyReady, new PartyCommand { PartyId = party.PartyId, ExpectedVersion = party.Version, Ready = true }.ToByteArray())).Read(PartyState.Parser);
        var request = new PartyQueueRequest { PartyId = party.PartyId, ExpectedVersion = party.Version, RequestId = "q" };
        Assert.That((await lobby.HandleAsync(gate, ServiceMethods.PartyQueue, request.ToByteArray())).Error, Is.EqualTo("timeout"));
        Assert.That((await lobby.HandleAsync(gate, ServiceMethods.PartyQueue, request.ToByteArray())).Error, Is.EqualTo("queue_not_found"));
        Assert.That(remote.Joins, Is.EqualTo(1));
        Assert.That((await lobby.HandleAsync(gate, ServiceMethods.PartyLeave, new PartyCommand { PartyId = party.PartyId, ExpectedVersion = party.Version }.ToByteArray())).Error, Is.EqualTo("party_queued"));
    }
    [Test]
    public async Task Invitation_discovery_expires_and_is_only_available_to_gate()
    {
        using var f = new RecoveryFixture(); var login = await f.LoginAsync("leader", "leader");
        var party = (await f.CallAsync("leader", ServiceMethods.PartyCreate, new PartyCommand(), login.SessionToken)).Read(PartyState.Parser);
        await LobbyCall(f, login.PlayerId, ServiceMethods.PartyInvite, new PartyCommand { PartyId = party.PartyId, ExpectedVersion = party.Version, TargetPlayerId = "target" });
        Assert.That((await LobbyCall(f, "target", ServiceMethods.PartyInvites, new Empty())).Read(PartyInvitations.Parser).Parties.Count, Is.EqualTo(1));
        f.Clock.Now += TimeSpan.FromMinutes(5);
        Assert.That((await LobbyCall(f, "target", ServiceMethods.PartyInvites, new Empty())).Read(PartyInvitations.Parser).Parties, Is.Empty);
        Assert.That((await f.Router.Lobby.HandleAsync(new(ServiceRole.Client, "client", "target"), ServiceMethods.PartyInvites, new Empty().ToByteArray())).Error, Is.EqualTo("forbidden"));
    }
    [Test]
    public async Task Invitation_discovery_caps_response_to_transport_safe_page()
    {
        var lobby = new LobbyService(new PartyRemote(), maxParties: 40);
        for (var i = 0; i < 40; i++)
        {
            var context = new ServiceCallContext(ServiceRole.Gate, "gate", "leader" + i);
            var party = (await lobby.HandleAsync(context, ServiceMethods.PartyCreate, new PartyCommand().ToByteArray())).Read(PartyState.Parser);
            await lobby.HandleAsync(context, ServiceMethods.PartyInvite, new PartyCommand { PartyId = party.PartyId, ExpectedVersion = party.Version, TargetPlayerId = "target" }.ToByteArray());
        }
        var result = (await lobby.HandleAsync(new(ServiceRole.Gate, "gate", "target"), ServiceMethods.PartyInvites, new Empty().ToByteArray())).Read(PartyInvitations.Parser);
        Assert.That(result.Parties.Count, Is.LessThanOrEqualTo(32));
        Assert.That(result.CalculateSize(), Is.LessThanOrEqualTo(16384));
    }
}
sealed class UncertainQueueRemote : IServiceRpc
{
    public int Joins;
    public ValueTask<ServiceReply> CallAsync(ServiceTarget target, string method, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default, string playerId = "")
    {
        if (method == ServiceMethods.MatchEpoch) return ValueTask.FromResult(ServiceReply.From(new ServiceEpoch { Epoch = "stable" }));
        if (method == ServiceMethods.QueueJoin) { Joins++; return ValueTask.FromResult(ServiceReply.Reject("timeout")); }
        return ValueTask.FromResult(ServiceReply.Reject("queue_not_found"));
    }
}
sealed class RecoveryFixture : IDisposable
{
    public RSA Key { get; } = RSA.Create(2048);
    public TestClock Clock { get; } = new();
    public GateRouter Router { get; } = new();
    public RecordingNotifier Notifier { get; } = new();
    public GateService Gate { get; }
    public RecoveryFixture(int playersPerMatch = 1)
    {
        Router.Player = new PlayerService(new MemoryStore(), Key, Clock, Router);
        Router.Lobby = new LobbyService(Router, clock: Clock);
        Router.Match = new MatchService(Router, Clock, playersPerMatch: playersPerMatch);
        Gate = new(Router, Notifier, Clock);
    }
    public ValueTask<ServiceReply> CallAsync(string connection, string method, IMessage body, string token = "") => Gate.HandleAsync(new(ServiceRole.Client, connection), method, new ControlRequest { Method = method, Credential = token, Body = ByteString.CopyFrom(body.ToByteArray()) }.ToByteArray());
    public async Task<LoginResult> LoginAsync(string connection, string username) => (await CallAsync(connection, ServiceMethods.RegisterAccount, new AccountRequest { Username = username, Password = "valid-password" })).Read(LoginResult.Parser);
    public async Task<PartyState> CreateReadyQueueAsync(string connection, LoginResult login, string request)
    {
        var party = (await CallAsync(connection, ServiceMethods.PartyCreate, new PartyCommand(), login.SessionToken)).Read(PartyState.Parser);
        party = (await CallAsync(connection, ServiceMethods.PartyReady, new PartyCommand { PartyId = party.PartyId, ExpectedVersion = party.Version, Ready = true }, login.SessionToken)).Read(PartyState.Parser);
        await CallAsync(connection, ServiceMethods.PartyQueue, new PartyQueueRequest { PartyId = party.PartyId, ExpectedVersion = party.Version, RequestId = request }, login.SessionToken);
        return party;
    }
    public void Dispose() => Key.Dispose();
}
