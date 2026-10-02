using AiNative.Protocol.Backend.V1;
using AiNative.Server.Backend;
using AiNative.Server.Control;
using Google.Protobuf;
using NUnit.Framework;
using System.Security.Cryptography;

namespace AiNative.Server.Backend.Tests;

public sealed class GateServiceTests
{
    [Test]
    public async Task Settlement_query_routes_to_player_with_the_authenticated_identity()
    {
        using var fixture = new GateFixture();
        var login = await fixture.RegisterAsync();
        await fixture.QueueAsync(login);
        await fixture.Match.PumpAsync(default);
        var room = fixture.Router.Room!;
        var query = new SettlementQuery { MatchId = room.MatchId };
        var pending = (await fixture.CallAsync(ServiceMethods.SettlementStatus, query, login.SessionToken,
            delegated: "victim", contextPlayer: "victim")).Read(SettlementReceipt.Parser);
        Assert.That((pending.MatchId, pending.Confirmed, pending.PayloadHash), Is.EqualTo((room.MatchId, false, "")));

        var result = new MatchResult { MatchId = room.MatchId, RoomId = room.RoomId, NodeId = room.NodeId, BootEpoch = room.BootEpoch, Completion = "completed" };
        result.Players.Add(new PlayerResult { PlayerId = login.PlayerId, Won = true, Kills = 2 });
        var committed = (await fixture.Router.Player.HandleAsync(new(ServiceRole.Battle, room.NodeId), ServiceMethods.Settle,
            result.ToByteArray())).Read(SettlementReceipt.Parser);
        var confirmed = (await fixture.CallAsync(ServiceMethods.SettlementStatus, query, login.SessionToken)).Read(SettlementReceipt.Parser);
        Assert.That(confirmed, Is.EqualTo(committed));
        Assert.That(confirmed.Confirmed, Is.True);
    }

    [Test]
    public async Task Settlement_query_rejects_anonymous_and_client_identity_spoofing()
    {
        using var fixture = new GateFixture();
        var login = await fixture.RegisterAsync();
        fixture.Router.Room = new RoomAllocation { MatchId = "victim-match", RoomId = "room", NodeId = "battle", BootEpoch = "epoch", State = "Released" };
        fixture.Router.Room.PlayerIds.Add("victim");
        var query = new SettlementQuery { MatchId = "victim-match" };
        Assert.That((await fixture.CallAsync(ServiceMethods.SettlementStatus, query)).Error, Is.EqualTo("invalid-session"));
        Assert.That((await fixture.CallAsync(ServiceMethods.SettlementStatus, query, "forged-token")).Error, Is.EqualTo("invalid_session"));
        Assert.That((await fixture.CallAsync(ServiceMethods.SettlementStatus, query, login.SessionToken,
            delegated: "victim", contextPlayer: "victim")).Error, Is.EqualTo("forbidden"));
    }

    [Test]
    public async Task Concurrent_credentials_cannot_replace_an_inflight_requests_authenticated_player()
    {
        using var fixture = new GateFixture();
        var first = await fixture.RegisterAsync();
        var second = (await fixture.Router.Player.HandleAsync(new(ServiceRole.Gate, "gate"), ServiceMethods.RegisterAccount,
            new AccountRequest { Username = "second", Password = "valid-password" }.ToByteArray())).Read(LoginResult.Parser);
        var delayed = new DelayedPresenceRpc(fixture.Router, first.PlayerId);
        var gate = new GateService(delayed, fixture.Notifications, fixture.Clock);
        static byte[] Request(string token) => new ControlRequest { Method = ServiceMethods.PartyCreate, Credential = token, Body = ByteString.CopyFrom(new PartyCommand().ToByteArray()) }.ToByteArray();
        var slow = gate.HandleAsync(new(ServiceRole.Client, "shared"), ServiceMethods.PartyCreate, Request(first.SessionToken)).AsTask();
        await delayed.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var fastTask = gate.HandleAsync(new(ServiceRole.Client, "shared"), ServiceMethods.PartyCreate, Request(second.SessionToken)).AsTask();
        // Permit implementations that serialize a connection: resume the first request if
        // the second waits for it, while still forcing overlap for unsynchronized handlers.
        await Task.WhenAny(fastTask, Task.Delay(TimeSpan.FromSeconds(1)));
        delayed.Resume.TrySetResult();
        var slowResult = await slow;
        var fast = await fastTask;
        Assert.That(slowResult.Success, Is.True, "The first valid request must keep its own authenticated identity while another credential is validated.");
        Assert.That(slowResult.Read(PartyState.Parser).LeaderId, Is.EqualTo(first.PlayerId));
        if (fast.Success) Assert.That(fast.Read(PartyState.Parser).LeaderId, Is.EqualTo(second.PlayerId));
    }
    [TestCase(ServiceMethods.Allocate)]
    [TestCase(ServiceMethods.Create)]
    [TestCase(ServiceMethods.Settle)]
    [TestCase(ServiceMethods.NodeReport)]
    public async Task Client_cannot_access_internal_authority_operations(string method)
    {
        using var fixture = new GateFixture();
        var login = await fixture.RegisterAsync();
        var reply = await fixture.CallAsync(method, new Empty(), login.SessionToken);
        Assert.That(reply.Error, Is.EqualTo("forbidden"));
        Assert.That(fixture.Router.InternalCalls, Is.EqualTo(0));
    }

    [Test]
    public async Task Authenticated_session_ignores_client_delegated_id_and_context_player_spoof()
    {
        using var fixture = new GateFixture(); var login = await fixture.RegisterAsync();
        var response = await fixture.CallAsync(ServiceMethods.PartyCreate, new PartyCommand(), login.SessionToken, delegated: "victim", contextPlayer: "victim");
        var party = response.Read(PartyState.Parser);
        Assert.That(party.LeaderId, Is.EqualTo(login.PlayerId));
        Assert.That(party.Members.Select(x => x.PlayerId), Is.EquivalentTo(new[] { login.PlayerId }));
        Assert.That(fixture.Lobby.GetPresence(login.PlayerId).Online, Is.True);
        Assert.That(fixture.Lobby.GetPresence("victim").Online, Is.False);
    }

    [Test]
    public async Task Restart_validates_existing_token_and_enforces_profile_ownership_and_invalid_token()
    {
        using var fixture = new GateFixture(); var login = await fixture.RegisterAsync();
        fixture.RestartGate();
        var own = await fixture.CallAsync(ServiceMethods.Profile, new ProfileRequest { PlayerId = login.PlayerId }, login.SessionToken);
        Assert.That(own.Read(PlayerProfile.Parser).PlayerId, Is.EqualTo(login.PlayerId));
        Assert.That(fixture.Router.SessionValidations, Is.EqualTo(1));
        Assert.That((await fixture.CallAsync(ServiceMethods.Profile, new ProfileRequest { PlayerId = "victim" }, login.SessionToken)).Error, Is.EqualTo("forbidden"));
        Assert.That((await fixture.CallAsync(ServiceMethods.Profile, new ProfileRequest { PlayerId = login.PlayerId }, "tampered-token")).Error, Is.EqualTo("invalid_session"));
        fixture.Clock.Now += TimeSpan.FromHours(9);
        Assert.That((await fixture.CallAsync(ServiceMethods.Profile, new ProfileRequest { PlayerId = login.PlayerId }, login.SessionToken)).Error, Is.EqualTo("invalid_session"));
    }

    [Test]
    public async Task Ready_status_and_notification_carry_offline_ticket_and_terminal_failure_notifies_once()
    {
        using var fixture = new GateFixture(); var login = await fixture.RegisterAsync();
        var requestId = await fixture.QueueAsync(login);
        await fixture.Match.PumpAsync(default);
        var ready = (await fixture.CallAsync(ServiceMethods.MatchStatus, new MatchQuery { RequestId = requestId }, login.SessionToken)).Read(MatchReady.Parser);
        Assert.That(ready.Status.State, Is.EqualTo("ready"));
        Assert.That(new EntryTicketVerifier(fixture.Key, fixture.Clock).TryVerify(ready.EntryTicket, login.PlayerId, ready.Status.Allocation.RoomId, ready.Status.Allocation.NodeId, ready.Status.Allocation.BootEpoch, out _), Is.True);
        await fixture.Gate.PumpAsync(); await fixture.Gate.PumpAsync();
        Assert.That(fixture.Notifications.Events.Count, Is.EqualTo(1));
        Assert.That(fixture.Notifications.Events[0].Method, Is.EqualTo("match.ready"));
        var notification = MatchReady.Parser.ParseFrom(fixture.Notifications.Events[0].Payload);
        Assert.That(new EntryTicketVerifier(fixture.Key, fixture.Clock).TryVerify(notification.EntryTicket, login.PlayerId, notification.Status.Allocation.RoomId, notification.Status.Allocation.NodeId, notification.Status.Allocation.BootEpoch, out _), Is.True);
        fixture.Router.Room!.State = "Lost";
        await fixture.Match.PumpAsync(default);
        await fixture.Gate.PumpAsync(); await fixture.Gate.PumpAsync();
        Assert.That(fixture.Notifications.Events.Select(x => x.Method), Is.EqualTo(new[] { "match.ready", "match.failed" }));
        Assert.That(MatchReady.Parser.ParseFrom(fixture.Notifications.Events[1].Payload).EntryTicket, Is.Empty);
    }

    [Test]
    public async Task Disconnect_sends_offline_presence_without_canceling_live_room_or_party()
    {
        using var fixture = new GateFixture(); var login = await fixture.RegisterAsync();
        var requestId = await fixture.QueueAsync(login);
        await fixture.Match.PumpAsync(default);
        Assert.That(fixture.Lobby.GetPresence(login.PlayerId).Online, Is.True);
        await fixture.Gate.DisconnectedAsync("client");
        Assert.That(fixture.Lobby.GetPresence(login.PlayerId).Online, Is.False);
        Assert.That(fixture.Router.PresenceUpdates.Last(), Is.False);
        var status = (await fixture.Match.HandleAsync(new(ServiceRole.Gate, "gate", login.PlayerId), ServiceMethods.MatchStatus, new MatchQuery { RequestId = requestId }.ToByteArray())).Read(MatchStatus.Parser);
        Assert.That(status.State, Is.EqualTo("ready"));
        Assert.That(fixture.Router.Room!.State, Is.EqualTo("Ready"));
        var party = (await fixture.Lobby.HandleAsync(new(ServiceRole.Gate, "gate", login.PlayerId), ServiceMethods.PartyGet, new PartyCommand { PartyId = fixture.PartyId }.ToByteArray())).Read(PartyState.Parser);
        Assert.That(party.Members.Single().PlayerId, Is.EqualTo(login.PlayerId));
    }

    [Test]
    public async Task Heartbeat_refreshes_presence_and_idle_connection_times_out()
    {
        using var fixture = new GateFixture(); var login = await fixture.RegisterAsync();
        fixture.Clock.Now += TimeSpan.FromSeconds(11);
        await fixture.Gate.PumpAsync();
        Assert.That(fixture.Router.PresenceUpdates, Is.EqualTo(new[] { true, true }));
        fixture.Clock.Now += TimeSpan.FromMinutes(2);
        await fixture.Gate.PumpAsync();
        Assert.That(fixture.Router.PresenceUpdates.Last(), Is.False);
        Assert.That(fixture.Lobby.GetPresence(login.PlayerId).Online, Is.False);
    }

    [Test]
    public async Task Connection_and_request_limits_reset_after_disconnect_or_clock_window()
    {
        using var fixture = new GateFixture(maxConnections: 1);
        for (var i = 0; i < 40; i++) Assert.That((await fixture.CallAsync("gate.ping", new Empty())).Success, Is.True);
        Assert.That((await fixture.CallAsync("gate.ping", new Empty())).Error, Is.EqualTo("rate-limited"));
        Assert.That((await fixture.CallAsync("gate.ping", new Empty(), connection: "other")).Error, Is.EqualTo("capacity-unavailable"));
        fixture.Clock.Now += TimeSpan.FromSeconds(1);
        Assert.That((await fixture.CallAsync("gate.ping", new Empty())).Success, Is.True);
        await fixture.Gate.DisconnectedAsync("client");
        Assert.That((await fixture.CallAsync("gate.ping", new Empty(), connection: "other")).Success, Is.True);
    }

    [Test]
    public async Task Account_attempt_limit_blocks_sixth_attempt_and_resets_each_minute()
    {
        using var fixture = new GateFixture();
        var bad = new AccountRequest { Username = "missing", Password = "valid-password" };
        for (var i = 0; i < 5; i++) Assert.That((await fixture.CallAsync(ServiceMethods.Login, bad)).Error, Is.EqualTo("invalid_credentials"));
        Assert.That((await fixture.CallAsync(ServiceMethods.Login, bad)).Error, Is.EqualTo("rate-limited"));
        fixture.Clock.Now += TimeSpan.FromMinutes(1);
        Assert.That((await fixture.CallAsync(ServiceMethods.Login, bad)).Error, Is.EqualTo("invalid_credentials"));
    }

    [Test]
    public async Task Mismatched_control_method_and_nonclient_ingress_are_rejected()
    {
        using var fixture = new GateFixture();
        var request = new ControlRequest { Method = ServiceMethods.Allocate };
        Assert.That((await fixture.Gate.HandleAsync(new(ServiceRole.Client, "client"), "gate.ping", request.ToByteArray())).Error, Is.EqualTo("invalid-method"));
        Assert.That((await fixture.Gate.HandleAsync(new(ServiceRole.Battle, "node"), ServiceMethods.Allocate, request.ToByteArray())).Error, Is.EqualTo("forbidden"));
    }
}

sealed class GateFixture : IDisposable
{
    public RSA Key { get; } = RSA.Create(2048);
    public TestClock Clock { get; } = new();
    public GateRouter Router { get; } = new();
    public RecordingNotifier Notifications { get; } = new();
    public GateService Gate { get; private set; }
    public LobbyService Lobby { get; }
    public MatchService Match { get; }
    public string PartyId = "";
    readonly int maxConnections;
    public GateFixture(int maxConnections = 1024)
    {
        this.maxConnections = maxConnections;
        Router.Player = new PlayerService(new MemoryStore(), Key, Clock, Router);
        Lobby = Router.Lobby = new LobbyService(Router, clock: Clock);
        Match = Router.Match = new MatchService(Router, Clock, playersPerMatch: 1);
        Gate = new(Router, Notifications, Clock, maxConnections);
    }
    public void RestartGate() => Gate = new(Router, Notifications, Clock, maxConnections);
    public ValueTask<ServiceReply> CallAsync(string method, IMessage body, string token = "", string delegated = "", string contextPlayer = "", string connection = "client")
    {
        var request = new ControlRequest { Method = method, Credential = token, DelegatedPlayerId = delegated, Body = ByteString.CopyFrom(body.ToByteArray()) };
        return Gate.HandleAsync(new(ServiceRole.Client, connection, contextPlayer), method, request.ToByteArray());
    }
    public async Task<LoginResult> RegisterAsync() => (await CallAsync(ServiceMethods.RegisterAccount, new AccountRequest { Username = "player", Password = "valid-password" })).Read(LoginResult.Parser);
    public async Task<string> QueueAsync(LoginResult login)
    {
        var party = (await CallAsync(ServiceMethods.PartyCreate, new PartyCommand(), login.SessionToken)).Read(PartyState.Parser); PartyId = party.PartyId;
        party = (await CallAsync(ServiceMethods.PartyReady, new PartyCommand { PartyId = party.PartyId, ExpectedVersion = party.Version, Ready = true }, login.SessionToken)).Read(PartyState.Parser);
        return (await CallAsync(ServiceMethods.PartyQueue, new PartyQueueRequest { PartyId = party.PartyId, ExpectedVersion = party.Version, RequestId = "queue" }, login.SessionToken)).Read(MatchStatus.Parser).RequestId;
    }
    public void Dispose() => Key.Dispose();
}

sealed class GateRouter : IServiceRpc
{
    public PlayerService Player = null!;
    public LobbyService Lobby = null!;
    public MatchService Match = null!;
    public RoomAllocation? Room;
    public int SessionValidations, InternalCalls;
    public List<bool> PresenceUpdates { get; } = [];
    public ValueTask<ServiceReply> CallAsync(ServiceTarget target, string method, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default, string playerId = "")
    {
        if (target.Role == ServiceRole.Player)
        {
            if (method == ServiceMethods.ValidateSession) SessionValidations++;
            return Player.HandleAsync(new(ServiceRole.Gate, "gate", playerId), method, payload, cancellationToken);
        }
        if (target.Role == ServiceRole.Lobby)
        {
            if (method == ServiceMethods.Presence) PresenceUpdates.Add(PresenceUpdate.Parser.ParseFrom(payload.Span).Online);
            return Lobby.HandleAsync(new(ServiceRole.Gate, "gate", playerId), method, payload, cancellationToken);
        }
        if (target.Role == ServiceRole.Match) return Match.HandleAsync(new(ServiceRole.Lobby, "lobby", playerId), method, payload, cancellationToken);
        if (method == ServiceMethods.Allocate)
        {
            InternalCalls++;
            var request = AllocationRequest.Parser.ParseFrom(payload.Span);
            Room = new() { AllocationId = "allocation", MatchId = request.MatchId, RoomId = "room", NodeId = "battle", BootEpoch = "epoch", State = "Ready" }; Room.PlayerIds.Add(request.PlayerIds);
        }
        else if (method != ServiceMethods.RoomGet) { InternalCalls++; return ValueTask.FromResult(ServiceReply.Reject("unexpected_rpc")); }
        return ValueTask.FromResult(ServiceReply.From(Room ?? new RoomAllocation()));
    }
}
sealed class RecordingNotifier : IClientNotifier
{
    public List<(string Connection, string Method, byte[] Payload)> Events { get; } = [];
    public ValueTask NotifyAsync(string connectionId, string method, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) { Events.Add((connectionId, method, payload.ToArray())); return ValueTask.CompletedTask; }
}
sealed class DelayedPresenceRpc(IServiceRpc inner, string delayedPlayer) : IServiceRpc
{
    public TaskCompletionSource Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async ValueTask<ServiceReply> CallAsync(ServiceTarget target, string method, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default, string playerId = "")
    {
        if (method == ServiceMethods.Presence && playerId == delayedPlayer) { Arrived.TrySetResult(); await Resume.Task.WaitAsync(cancellationToken); }
        return await inner.CallAsync(target, method, payload, cancellationToken, playerId);
    }
}
