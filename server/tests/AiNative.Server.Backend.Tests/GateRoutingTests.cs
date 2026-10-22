using AiNative.Protocol.Backend.V1;
using AiNative.Server.Backend;
using AiNative.Server.Control;
using Google.Protobuf;
using NUnit.Framework;

namespace AiNative.Server.Backend.Tests;

public sealed class GateRoutingTests
{
    [TestCase(ServiceMethods.Profile, ServiceRole.Player)]
    [TestCase(ServiceMethods.SettlementStatus, ServiceRole.Player)]
    [TestCase(ServiceMethods.PartyCreate, ServiceRole.Lobby)]
    [TestCase(ServiceMethods.PartyInvite, ServiceRole.Lobby)]
    [TestCase(ServiceMethods.PartyAccept, ServiceRole.Lobby)]
    [TestCase(ServiceMethods.PartyLeave, ServiceRole.Lobby)]
    [TestCase(ServiceMethods.PartyReady, ServiceRole.Lobby)]
    [TestCase(ServiceMethods.PartyGet, ServiceRole.Lobby)]
    [TestCase(ServiceMethods.PartyQueue, ServiceRole.Lobby)]
    [TestCase(ServiceMethods.QueueCancel, ServiceRole.Lobby)]
    [TestCase(ServiceMethods.MatchStatus, ServiceRole.Lobby)]
    [TestCase(ServiceMethods.PartyInvites, ServiceRole.Lobby)]
    public async Task Allowed_routes_forward_exact_body_identity_and_cancellation(
        string method,
        ServiceRole role
    )
    {
        var rpc = new RoutingRpc();
        var gate = new GateService(rpc, new RecordingNotifier(), new TestClock());
        using var cancellation = new CancellationTokenSource();
        byte[] body = [1, 2, 3];
        var reply = await CallAsync(gate, method, "token", body, cancellation.Token);
        Assert.That(reply, Is.SameAs(rpc.Response));
        Assert.That(
            rpc.Calls.Select(c => c.Method),
            Is.EqualTo(new[] { ServiceMethods.ValidateSession, ServiceMethods.Presence, method })
        );
        var call = rpc.Calls.Last();
        Assert.Multiple(() =>
        {
            Assert.That(call.Target, Is.EqualTo(new ServiceTarget(role)));
            Assert.That(call.Body, Is.EqualTo(body));
            Assert.That(call.Player, Is.EqualTo("player"));
            Assert.That(call.Cancellation, Is.EqualTo(cancellation.Token));
        });
    }

    [TestCase("unknown.method")]
    [TestCase(ServiceMethods.Allocate)]
    [TestCase(ServiceMethods.ValidateSession)]
    [TestCase(ServiceMethods.IssueTicket)]
    public async Task Authentication_precedes_allowlist_rejection(string method)
    {
        var rpc = new RoutingRpc();
        var gate = new GateService(rpc, new RecordingNotifier(), new TestClock());
        Assert.That((await CallAsync(gate, method, "")).Error, Is.EqualTo("invalid-session"));
        Assert.That(rpc.Calls, Is.Empty);
        Assert.That((await CallAsync(gate, method, "bad")).Error, Is.EqualTo("invalid-token"));
        Assert.That(
            rpc.Calls.Select(c => c.Method),
            Is.EqualTo(new[] { ServiceMethods.ValidateSession })
        );
        rpc.Calls.Clear();
        Assert.That((await CallAsync(gate, method, "token")).Error, Is.EqualTo("forbidden"));
        Assert.That(
            rpc.Calls.Select(c => c.Method),
            Is.EqualTo(new[] { ServiceMethods.ValidateSession, ServiceMethods.Presence })
        );
    }

    [TestCase(ServiceMethods.PartyCreate)]
    [TestCase(ServiceMethods.PartyAccept)]
    [TestCase(ServiceMethods.PartyGet)]
    [TestCase(ServiceMethods.PartyReady)]
    [TestCase(ServiceMethods.PartyQueue)]
    public async Task Successful_queue_updates_are_used_by_pump_and_reset_notification(
        string method
    )
    {
        var rpc = new RoutingRpc();
        var notifications = new RecordingNotifier();
        var gate = new GateService(rpc, notifications, new TestClock());
        rpc.Response = ServiceReply.From(new MatchStatus { RequestId = "old" });
        await CallAsync(gate, ServiceMethods.PartyQueue, "token");
        rpc.Response = ReadyStatus("old");
        await gate.PumpAsync();
        await gate.PumpAsync();
        Assert.That(notifications.Events.Count, Is.EqualTo(1));

        rpc.Response =
            method == ServiceMethods.PartyQueue
                ? ServiceReply.From(new MatchStatus { RequestId = "new" })
                : ServiceReply.From(new PartyState { PartyId = "party", QueueRequestId = "new" });
        await CallAsync(gate, method, "token");
        rpc.Calls.Clear();
        rpc.Response = ReadyStatus("new");
        await gate.PumpAsync();
        Assert.That(
            MatchQuery
                .Parser.ParseFrom(
                    rpc.Calls.Single(c => c.Method == ServiceMethods.MatchStatus).Body
                )
                .RequestId,
            Is.EqualTo("new")
        );
        Assert.That(notifications.Events.Count, Is.EqualTo(2));
    }

    [TestCase(ServiceMethods.PartyCreate)]
    [TestCase(ServiceMethods.PartyAccept)]
    [TestCase(ServiceMethods.PartyGet)]
    [TestCase(ServiceMethods.PartyReady)]
    [TestCase(ServiceMethods.PartyQueue)]
    [TestCase(ServiceMethods.PartyLeave)]
    [TestCase(ServiceMethods.MatchStatus)]
    public async Task Failed_response_preserves_queue_and_notification_state(string method)
    {
        var rpc = new RoutingRpc();
        var notifications = new RecordingNotifier();
        var gate = new GateService(rpc, notifications, new TestClock());
        rpc.Response = ServiceReply.From(
            new PartyState { PartyId = "party", QueueRequestId = "old" }
        );
        await CallAsync(gate, ServiceMethods.PartyCreate, "token");
        rpc.Response = ReadyStatus("old");
        await gate.PumpAsync();
        rpc.Response = ServiceReply.Reject("business-error");
        Assert.That(await CallAsync(gate, method, "token"), Is.SameAs(rpc.Response));
        rpc.Calls.Clear();
        rpc.Response = ReadyStatus("old");
        await gate.PumpAsync();
        Assert.That(
            MatchQuery
                .Parser.ParseFrom(
                    rpc.Calls.Single(c => c.Method == ServiceMethods.MatchStatus).Body
                )
                .RequestId,
            Is.EqualTo("old")
        );
        Assert.That(notifications.Events.Count, Is.EqualTo(1));
    }

    [TestCase(ServiceMethods.PartyCreate)]
    [TestCase(ServiceMethods.PartyAccept)]
    [TestCase(ServiceMethods.PartyGet)]
    [TestCase(ServiceMethods.PartyReady)]
    public async Task Party_response_without_queue_keeps_party_available_for_polling(string method)
    {
        var rpc = new RoutingRpc();
        var gate = new GateService(rpc, new RecordingNotifier(), new TestClock());
        rpc.Response = ServiceReply.From(new PartyState { PartyId = "party" });
        await CallAsync(gate, method, "token");
        rpc.Calls.Clear();
        await gate.PumpAsync();
        var call = rpc.Calls.Single();
        Assert.That(call.Method, Is.EqualTo(ServiceMethods.PartyGet));
        Assert.That(PartyOperation.Parser.ParseFrom(call.Body).PartyId, Is.EqualTo("party"));
    }

    [Test]
    public async Task Successful_leave_clears_party_queue_and_notification_state()
    {
        var rpc = new RoutingRpc();
        var notifications = new RecordingNotifier();
        var gate = new GateService(rpc, notifications, new TestClock());
        rpc.Response = ServiceReply.From(
            new PartyState { PartyId = "party", QueueRequestId = "old" }
        );
        await CallAsync(gate, ServiceMethods.PartyCreate, "token");
        rpc.Response = ReadyStatus("old");
        await gate.PumpAsync();
        rpc.Response = ServiceReply.From(new Empty());
        await CallAsync(gate, ServiceMethods.PartyLeave, "token");
        rpc.Calls.Clear();
        await gate.PumpAsync();
        Assert.That(rpc.Calls, Is.Empty);
        rpc.Response = ServiceReply.From(new MatchStatus { RequestId = "old" });
        await CallAsync(gate, ServiceMethods.PartyQueue, "token");
        rpc.Response = ReadyStatus("old");
        await gate.PumpAsync();
        Assert.That(notifications.Events.Count, Is.EqualTo(2));
    }

    private static ServiceReply ReadyStatus(string requestId) =>
        ServiceReply.From(
            new MatchStatus
            {
                RequestId = requestId,
                State = "ready",
                Allocation = new RoomAllocation { State = "Ready", PlayerIds = { "player" } },
            }
        );

    private static ValueTask<ServiceReply> CallAsync(
        GateService gate,
        string method,
        string token,
        byte[]? body = null,
        CancellationToken cancellationToken = default
    ) =>
        gate.HandleAsync(
            new(ServiceRole.Client, "client", "spoofed"),
            method,
            new C2S_GateRequest
            {
                Method = method,
                Credential = token,
                DelegatedPlayerId = "spoofed",
                Body = ByteString.CopyFrom(body ?? []),
            }.ToByteArray(),
            cancellationToken
        );

    private sealed class RoutingRpc : IServiceRpc
    {
        public ServiceReply Response = ServiceReply.Reject("business-error");
        public List<(
            ServiceTarget Target,
            string Method,
            byte[] Body,
            string Player,
            CancellationToken Cancellation
        )> Calls { get; } = [];

        public ValueTask<ServiceReply> CallAsync(
            ServiceTarget target,
            string method,
            ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken = default,
            string playerId = ""
        )
        {
            Calls.Add((target, method, payload.ToArray(), playerId, cancellationToken));
            return ValueTask.FromResult(
                method switch
                {
                    ServiceMethods.ValidateSession => SessionCredentials
                        .Parser.ParseFrom(payload.Span)
                        .SessionToken == "token"
                        ? ServiceReply.From(
                            new PlayerIdentity
                            {
                                PlayerId = "player",
                                ExpiresUnixSeconds = long.MaxValue,
                            }
                        )
                        : ServiceReply.Reject("invalid-token"),
                    ServiceMethods.Presence => ServiceReply.From(new Empty()),
                    ServiceMethods.IssueTicket => ServiceReply.From(
                        new EntryTicket { Ticket = "ticket" }
                    ),
                    _ => Response,
                }
            );
        }
    }
}
