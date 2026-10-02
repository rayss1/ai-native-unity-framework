using AiNative.Protocol.Backend.V1;
using AiNative.Server.Backend;
using AiNative.Server.Control;
using Google.Protobuf;
using NUnit.Framework;
using System.Security.Cryptography;

namespace AiNative.Server.Backend.Tests;

public sealed class ReviewTests
{
    static ValueTask<ServiceReply> Call(IServiceHandler s, string peer, string player, string method, IMessage body, ServiceRole role = ServiceRole.Gate) => s.HandleAsync(new(role, peer, player), method, body.ToByteArray());

    [Test]
    public async Task Settlement_status_rejects_another_battle_owner()
    {
        using var key = RSA.Create(2048);
        var store = new MemoryStore();
        var authority = new RoomAuthority { Room = new RoomAllocation { MatchId = "match", RoomId = "room", NodeId = "owner", BootEpoch = "epoch" } };
        var player = new PlayerService(store, key, new TestClock(), authority);
        var reply = await Call(player, "other", "", ServiceMethods.SettlementStatus, new SettlementQuery { MatchId = "match" }, ServiceRole.Battle);
        Assert.That(reply.Error, Is.EqualTo("forbidden"));
        Assert.That((await Call(player, "owner", "", ServiceMethods.SettlementStatus, new SettlementQuery { MatchId = "match" }, ServiceRole.Battle)).Success, Is.True);
        Assert.That((await Call(new PlayerService(store, key, new TestClock()), "owner", "", ServiceMethods.SettlementStatus, new SettlementQuery { MatchId = "match" }, ServiceRole.Battle)).Error, Is.EqualTo("authority_unavailable"));
    }

    [TestCase("timeout")]
    [TestCase("request_conflict")]
    [TestCase("unknown_future_uncertainty")]
    public async Task Encoded_uncertainty_keeps_party_claimed(string error)
    {
        var lobby = new LobbyService(new ReplyRemote((_, _, _) => ServiceReply.Reject(error)));
        var party = (await Call(lobby, "gate", "p1", ServiceMethods.PartyCreate, new PartyCommand())).Read(PartyState.Parser);
        party = (await Call(lobby, "gate", "p1", ServiceMethods.PartyReady, new PartyCommand { PartyId = party.PartyId, ExpectedVersion = party.Version, Ready = true })).Read(PartyState.Parser);
        Assert.That((await Call(lobby, "gate", "p1", ServiceMethods.PartyQueue, new PartyQueueRequest { PartyId = party.PartyId, ExpectedVersion = party.Version, RequestId = "q" })).Error, Is.EqualTo(error));
        var replies = await Task.WhenAll(
            Call(lobby, "gate", "p1", ServiceMethods.PartyLeave, new PartyCommand { PartyId = party.PartyId, ExpectedVersion = party.Version }).AsTask(),
            Call(lobby, "gate", "p1", ServiceMethods.PartyReady, new PartyCommand { PartyId = party.PartyId, ExpectedVersion = party.Version }).AsTask());
        Assert.That(replies.Select(x => x.Error), Is.All.EqualTo("party_queued"));
    }

    [TestCase("epoch")]
    [TestCase("room")]
    [TestCase("node")]
    [TestCase("allocation")]
    public async Task Contradictory_terminal_room_identity_preserves_player_protection(string changed)
    {
        RoomAllocation? accepted = null;
        var rpc = new ReplyRemote((_, method, payload) =>
        {
            if (method == ServiceMethods.Allocate)
            {
                var request = AllocationRequest.Parser.ParseFrom(payload.Span);
                accepted = new() { AllocationId = "allocation", MatchId = request.MatchId, RoomId = "room", NodeId = "node", BootEpoch = "epoch", State = "Ready" }; accepted.PlayerIds.Add(request.PlayerIds);
                return ServiceReply.From(accepted);
            }
            var contradiction = accepted!.Clone(); contradiction.State = "Released";
            switch (changed) { case "epoch": contradiction.BootEpoch = "different"; break; case "room": contradiction.RoomId = "different"; break; case "node": contradiction.NodeId = "different"; break; case "allocation": contradiction.AllocationId = "different"; break; }
            return ServiceReply.From(contradiction);
        });
        var service = new MatchService(rpc, new TestClock(), bootEpoch: "match-epoch");
        var entry = new QueueEntry { RequestId = "q", PartyId = "party", PartyVersion = 1, ExpectedMatchEpoch = "match-epoch", AdmissionExpiresUnixSeconds = 1800000030 }; entry.PlayerIds.Add(new[] { "p1", "p2" });
        await Call(service, "lobby", "", ServiceMethods.QueueJoin, entry, ServiceRole.Lobby);
        await service.PumpAsync(default);
        await service.PumpAsync(default);
        var status = (await Call(service, "gate", "p1", ServiceMethods.MatchStatus, new MatchQuery { RequestId = "q" })).Read(MatchStatus.Parser);
        Assert.That(status.State, Is.EqualTo("ready"));
        Assert.That(status.Failure, Is.EqualTo("allocation_conflict"));
        entry.RequestId = "second";
        Assert.That((await Call(service, "lobby", "", ServiceMethods.QueueJoin, entry, ServiceRole.Lobby)).Error, Is.EqualTo("player_busy"));
    }

    [Test]
    public async Task Ready_allocation_requires_global_allocation_identity()
    {
        var rpc = new ReplyRemote((_, _, payload) =>
        {
            var request = AllocationRequest.Parser.ParseFrom(payload.Span);
            var room = new RoomAllocation { MatchId = request.MatchId, RoomId = "room", NodeId = "node", BootEpoch = "epoch", State = "Ready" }; room.PlayerIds.Add(request.PlayerIds);
            return ServiceReply.From(room);
        });
        var service = new MatchService(rpc, new TestClock(), bootEpoch: "match-epoch");
        var entry = new QueueEntry { RequestId = "q", PartyId = "party", PartyVersion = 1, ExpectedMatchEpoch = "match-epoch", AdmissionExpiresUnixSeconds = 1800000030 }; entry.PlayerIds.Add(new[] { "p1", "p2" });
        await Call(service, "lobby", "", ServiceMethods.QueueJoin, entry, ServiceRole.Lobby);
        await service.PumpAsync(default);
        var status = (await Call(service, "gate", "p1", ServiceMethods.MatchStatus, new MatchQuery { RequestId = "q" })).Read(MatchStatus.Parser);
        Assert.That(status.State, Is.EqualTo("matching"));
        Assert.That(status.Failure, Is.EqualTo("allocation_conflict"));
    }

    [Test]
    public async Task Unsorted_result_receipt_uses_shared_canonical_hash_and_updates_once()
    {
        using var key = RSA.Create(2048); var store = new MemoryStore();
        var authority = new RoomAuthority { Room = new RoomAllocation { AllocationId = "allocation", MatchId = "match", RoomId = "room", NodeId = "node", BootEpoch = "epoch", State = "Ready" } };
        var service = new PlayerService(store, key, new TestClock(), authority);
        var ids = new List<string>();
        foreach (var name in new[] { "first", "second" }) ids.Add((await Call(service, "gate", "", ServiceMethods.RegisterAccount, new AccountRequest { Username = name, Password = "valid-password" })).Read(LoginResult.Parser).PlayerId);
        authority.Room.PlayerIds.Add(ids);
        var result = new MatchResult { MatchId = "match", RoomId = "room", NodeId = "node", BootEpoch = "epoch", Completion = "completed" };
        result.Players.Add(ids.OrderDescending(StringComparer.Ordinal).Select(id => new PlayerResult { PlayerId = id, Kills = 2 }));
        var receipt = (await Call(service, "node", "", ServiceMethods.Settle, result, ServiceRole.Battle)).Read(SettlementReceipt.Parser);
        Assert.That(receipt.PayloadHash, Is.EqualTo(ResultPayload.Hash(result)));
        Assert.That(result.Players[0].PlayerId, Is.GreaterThan(result.Players[1].PlayerId));
        var reordered = ResultPayload.Canonicalize(result);
        Assert.That((await Call(service, "node", "", ServiceMethods.Settle, reordered, ServiceRole.Battle)).Read(SettlementReceipt.Parser).PayloadHash, Is.EqualTo(receipt.PayloadHash));
        foreach (var id in ids) Assert.That((await store.ProfileAsync(id))!.Played, Is.EqualTo(1));
    }
}

sealed class ReplyRemote(Func<ServiceTarget, string, ReadOnlyMemory<byte>, ServiceReply> reply) : IServiceRpc
{
    public ValueTask<ServiceReply> CallAsync(ServiceTarget target, string method, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default, string playerId = "") => ValueTask.FromResult(method == ServiceMethods.MatchEpoch ? ServiceReply.From(new ServiceEpoch { Epoch = "match-epoch" }) : reply(target, method, payload));
}
