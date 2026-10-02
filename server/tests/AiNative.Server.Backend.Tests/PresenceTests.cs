using AiNative.Protocol.Backend.V1;
using AiNative.Server.Backend;
using AiNative.Server.Control;
using Google.Protobuf;
using NUnit.Framework;

namespace AiNative.Server.Backend.Tests;

public sealed class PresenceTests
{
    static ValueTask<ServiceReply> Update(LobbyService lobby, string player, bool online, ServiceRole role = ServiceRole.Gate) => lobby.HandleAsync(new(role, "gate", player), ServiceMethods.Presence, new PresenceUpdate { Online = online }.ToByteArray());

    [Test]
    public async Task Presence_requires_authenticated_gate_identity_and_returns_bound_player()
    {
        var lobby = new LobbyService(new PartyRemote());
        Assert.That((await Update(lobby, "p1", true, ServiceRole.Client)).Error, Is.EqualTo("forbidden"));
        Assert.That((await Update(lobby, "", true)).Error, Is.EqualTo("forbidden"));
        var state = (await Update(lobby, "p1", true)).Read(PresenceState.Parser);
        Assert.That((state.PlayerId, state.Online), Is.EqualTo(("p1", true)));
        Assert.That((await Update(lobby, "p1", false)).Read(PresenceState.Parser).Online, Is.False);
    }

    [Test]
    public async Task Presence_is_bounded_and_reclaims_expired_or_disconnected_slots()
    {
        var clock = new TestClock();
        var lobby = new LobbyService(new PartyRemote(), 1, 1, clock);
        await Update(lobby, "p1", true);
        Assert.That((await Update(lobby, "p2", true)).Error, Is.EqualTo("presence_capacity"));
        clock.Now += TimeSpan.FromSeconds(89);
        await Update(lobby, "p1", true); // heartbeat refreshes the same slot.
        clock.Now += TimeSpan.FromSeconds(89);
        Assert.That(lobby.GetPresence("p1").Online, Is.True);
        clock.Now += TimeSpan.FromSeconds(1);
        Assert.That(lobby.GetPresence("p1").Online, Is.False);
        Assert.That((await Update(lobby, "p2", true)).Success, Is.True);
        await Update(lobby, "p2", false);
        Assert.That((await Update(lobby, "p3", true)).Success, Is.True);
    }

    [Test]
    public async Task Disconnect_or_expiry_does_not_discard_queued_party()
    {
        var clock = new TestClock(); var lobby = new LobbyService(new PartyRemote(), clock: clock);
        var gate = new ServiceCallContext(ServiceRole.Gate, "gate", "p1");
        var party = (await lobby.HandleAsync(gate, ServiceMethods.PartyCreate, new PartyCommand().ToByteArray())).Read(PartyState.Parser);
        party = (await lobby.HandleAsync(gate, ServiceMethods.PartyReady, new PartyCommand { PartyId = party.PartyId, ExpectedVersion = party.Version, Ready = true }.ToByteArray())).Read(PartyState.Parser);
        await lobby.HandleAsync(gate, ServiceMethods.PartyQueue, new PartyQueueRequest { PartyId = party.PartyId, ExpectedVersion = party.Version, RequestId = "q" }.ToByteArray());
        party.QueueRequestId = "q";
        Assert.That((await Update(lobby, "p1", true)).Success, Is.True);
        Assert.That((await Update(lobby, "p1", false)).Success, Is.True);
        await Update(lobby, "p1", true);
        clock.Now += TimeSpan.FromSeconds(90);
        Assert.That(lobby.GetPresence("p1").Online, Is.False);
        var after = (await lobby.HandleAsync(gate, ServiceMethods.PartyGet, new PartyCommand { PartyId = party.PartyId }.ToByteArray())).Read(PartyState.Parser);
        Assert.That(after, Is.EqualTo(party));
        Assert.That((await lobby.HandleAsync(gate, ServiceMethods.PartyLeave, new PartyCommand { PartyId = party.PartyId, ExpectedVersion = party.Version }.ToByteArray())).Error, Is.EqualTo("party_queued"));
    }
}
