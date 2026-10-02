using System.Security.Cryptography;
using AiNative.Protocol.Backend.V1;
using AiNative.Server.Backend;
using AiNative.Server.Control;
using Google.Protobuf;
using NUnit.Framework;

namespace AiNative.Server.Backend.Tests;

public sealed class SettlementStatusTests
{
    [TestCase("Reserved")]
    [TestCase("Ready")]
    [TestCase("Released")]
    [TestCase("Lost")]
    public async Task Gate_participant_gets_unconfirmed_receipt_until_result_is_persisted(string state)
    {
        using var fixture = new SettlementFixture();
        fixture.Authority.Room!.State = state;
        var receipt = (await fixture.QueryAsync(ServiceRole.Gate, "gate", "p1")).Read(SettlementReceipt.Parser);
        Assert.That((receipt.MatchId, receipt.Confirmed, receipt.PayloadHash), Is.EqualTo(("match", false, "")));
    }

    [Test]
    public async Task Gate_participant_reads_exact_persisted_receipt_after_player_service_restart()
    {
        using var fixture = new SettlementFixture();
        var committed = await fixture.SettleAsync();
        fixture.Authority.Room!.State = "Released";
        fixture.RestartPlayer();
        var receipt = (await fixture.QueryAsync(ServiceRole.Gate, "gate", "p1")).Read(SettlementReceipt.Parser);
        Assert.That(receipt, Is.EqualTo(committed));
        Assert.That(receipt.Confirmed, Is.True);
        Assert.That(receipt.PayloadHash, Is.Not.Empty);
    }

    [Test]
    public async Task Another_match_for_the_same_player_does_not_confirm_the_queried_match()
    {
        using var fixture = new SettlementFixture();
        await fixture.SettleAsync();
        fixture.Authority.Room!.MatchId = "next-match";
        var receipt = (await fixture.QueryAsync(ServiceRole.Gate, "gate", "p1", "next-match")).Read(SettlementReceipt.Parser);
        Assert.That((receipt.MatchId, receipt.Confirmed, receipt.PayloadHash), Is.EqualTo(("next-match", false, "")));
    }

    [TestCase(ServiceRole.Client)]
    [TestCase(ServiceRole.Player)]
    [TestCase(ServiceRole.Lobby)]
    [TestCase(ServiceRole.Match)]
    [TestCase(ServiceRole.Coordinator)]
    public async Task Other_roles_cannot_read_a_participants_settlement(ServiceRole role)
    {
        using var fixture = new SettlementFixture();
        await fixture.SettleAsync();
        Assert.That((await fixture.QueryAsync(role, "peer", "p1")).Error, Is.EqualTo("forbidden"));
    }

    [TestCase("")]
    [TestCase("other-player")]
    public async Task Gate_requires_an_authenticated_participant_even_with_a_forged_body(string player)
    {
        using var fixture = new SettlementFixture();
        await fixture.SettleAsync();
        // Unknown field 2 pretends to name p1; it must never override adapter identity.
        var payload = new SettlementQuery { MatchId = "match" }.ToByteArray().Concat(new byte[] { 0x12, 0x02, (byte)'p', (byte)'1' }).ToArray();
        var reply = await fixture.Player.HandleAsync(new(ServiceRole.Gate, "gate", player), ServiceMethods.SettlementStatus, payload);
        Assert.That(reply.Error, Is.EqualTo("forbidden"));
    }

    [TestCase("match")]
    [TestCase("room")]
    [TestCase("node")]
    [TestCase("epoch")]
    public async Task Gate_rejects_mismatched_or_invalid_authoritative_allocation_identity(string changed)
    {
        using var fixture = new SettlementFixture();
        await fixture.SettleAsync();
        var room = fixture.Authority.Room!;
        switch (changed)
        {
            case "match": room.MatchId = "different-match"; break;
            case "room": room.RoomId = ""; break;
            case "node": room.NodeId = "invalid node"; break;
            case "epoch": room.BootEpoch = ""; break;
        }
        Assert.That((await fixture.QueryAsync(ServiceRole.Gate, "gate", "p1")).Error, Is.EqualTo("forbidden"));
    }

    [Test]
    public async Task Battle_owner_can_read_confirmation_but_other_nodes_cannot_delegate_a_participant()
    {
        using var fixture = new SettlementFixture();
        var committed = await fixture.SettleAsync();
        Assert.That((await fixture.QueryAsync(ServiceRole.Battle, "battle", "")).Read(SettlementReceipt.Parser), Is.EqualTo(committed));
        Assert.That((await fixture.QueryAsync(ServiceRole.Battle, "other-node", "p1")).Error, Is.EqualTo("forbidden"));
    }

    [TestCase("")]
    [TestCase("invalid match")]
    public async Task Gate_rejects_invalid_match_identifiers(string match)
    {
        using var fixture = new SettlementFixture();
        Assert.That((await fixture.QueryAsync(ServiceRole.Gate, "gate", "p1", match)).Error, Is.EqualTo("invalid_request"));
    }

    [Test]
    public async Task Gate_cannot_read_without_coordinator_authority()
    {
        using var fixture = new SettlementFixture();
        var player = new PlayerService(fixture.Store, fixture.Key, new TestClock());
        var reply = await player.HandleAsync(new(ServiceRole.Gate, "gate", "p1"), ServiceMethods.SettlementStatus,
            new SettlementQuery { MatchId = "match" }.ToByteArray());
        Assert.That(reply.Error, Is.EqualTo("authority_unavailable"));
    }
}

sealed class SettlementFixture : IDisposable
{
    public RSA Key { get; } = RSA.Create(2048);
    public MemoryStore Store { get; } = new();
    public RoomAuthority Authority { get; } = new()
    {
        Room = new RoomAllocation { AllocationId = "allocation", MatchId = "match", RoomId = "room", NodeId = "battle", BootEpoch = "epoch", State = "Ready" }
    };
    public PlayerService Player { get; private set; }
    public SettlementFixture()
    {
        Authority.Room!.PlayerIds.Add("p1");
        Store.CreateAsync(new("p1", "player", new byte[32], new byte[32])).GetAwaiter().GetResult();
        Player = new(Store, Key, new TestClock(), Authority);
    }
    public void RestartPlayer() => Player = new(Store, Key, new TestClock(), Authority);
    public ValueTask<ServiceReply> QueryAsync(ServiceRole role, string peer, string player, string match = "match") =>
        Player.HandleAsync(new(role, peer, player), ServiceMethods.SettlementStatus, new SettlementQuery { MatchId = match }.ToByteArray());
    public async Task<SettlementReceipt> SettleAsync()
    {
        var result = new MatchResult { MatchId = "match", RoomId = "room", NodeId = "battle", BootEpoch = "epoch", Completion = "completed" };
        result.Players.Add(new PlayerResult { PlayerId = "p1", Won = true, Kills = 2 });
        return (await Player.HandleAsync(new(ServiceRole.Battle, "battle"), ServiceMethods.Settle, result.ToByteArray())).Read(SettlementReceipt.Parser);
    }
    public void Dispose() => Key.Dispose();
}
