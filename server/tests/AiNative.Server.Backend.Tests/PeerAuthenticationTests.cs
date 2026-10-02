using System.Security.Cryptography;
using AiNative.Protocol.Backend.V1;
using AiNative.Server.Control;
using Google.Protobuf;
using NUnit.Framework;

namespace AiNative.Server.Backend.Tests;

public sealed class PeerAuthenticationTests
{
    [Test]
    public void ConfiguredAcceptanceClientCannotAuthenticateAsAnInternalService()
    {
        using RSA client = RSA.Create(2048);
        using PeerAuthentication auth = new("client", client.ExportPkcs8PrivateKeyPem(),
            new[] { new ServicePeer("client", ServiceRole.Client, 99, client.ExportSubjectPublicKeyInfoPem()) }, new Clock());
        ControlRequest request = new() { CorrelationId = "a", Method = ServiceMethods.Allocate };
        auth.Sign(request); Assert.That(auth.TryAuthenticate(request, out _), Is.False);
    }
    [Test]
    public void SignedBodyAndDelegationAreAuthenticatedAndReplayIsRejected()
    {
        using RSA gate = RSA.Create(2048), player = RSA.Create(2048);
        Clock clock = new();
        ServicePeer[] peers = { new("gate", ServiceRole.Gate, 1, gate.ExportSubjectPublicKeyInfoPem()), new("player", ServiceRole.Player, 2, player.ExportSubjectPublicKeyInfoPem()) };
        using PeerAuthentication sender = new("gate", gate.ExportPkcs8PrivateKeyPem(), peers, clock), receiver = new("player", player.ExportPkcs8PrivateKeyPem(), peers, clock);
        ControlRequest request = new() { CorrelationId = "a", Method = ServiceMethods.Profile, Body = ByteString.CopyFromUtf8("body"), DelegatedPlayerId = "p1" };
        sender.Sign(request);
        ControlRequest tampered = request.Clone(); tampered.DelegatedPlayerId = "p2";
        Assert.That(receiver.TryAuthenticate(tampered, out _), Is.False);
        Assert.That(receiver.TryAuthenticate(request, out var context), Is.True);
        Assert.That(context, Is.EqualTo(new ServiceCallContext(ServiceRole.Gate, "gate", "p1")));
        Assert.That(receiver.TryAuthenticate(request, out _), Is.False);
        sender.Sign(request); clock.Now = clock.Now.AddSeconds(21);
        Assert.That(receiver.TryAuthenticate(request, out _), Is.False);
    }

    [Test]
    public void NonDelegatingPeerCannotClaimPlayerAndMalformedSignaturesFailClosed()
    {
        using RSA battle = RSA.Create(2048);
        ServicePeer[] peers = { new("battle", ServiceRole.Battle, 1, battle.ExportSubjectPublicKeyInfoPem()) };
        using PeerAuthentication auth = new("battle", battle.ExportPkcs8PrivateKeyPem(), peers, new Clock());
        ControlRequest request = new() { CorrelationId = "a", Method = ServiceMethods.Settle, DelegatedPlayerId = "p1" };
        auth.Sign(request); Assert.That(auth.TryAuthenticate(request, out _), Is.False);
        request.DelegatedPlayerId = ""; auth.Sign(request);
        request.Credential = request.Credential[..(request.Credential.LastIndexOf('|') + 1)] + "AA==";
        Assert.That(auth.TryAuthenticate(request, out _), Is.False);
    }
    private sealed class Clock : TimeProvider
    { public DateTimeOffset Now = DateTimeOffset.Parse("2026-10-02T00:00:00Z"); public override DateTimeOffset GetUtcNow() => Now; }
}
