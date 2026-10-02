using System.Buffers.Binary;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AiNative.Client.Fantasy;
using AiNative.Protocol.Backend.V1;
using Google.Protobuf;
using NUnit.Framework;

namespace AiNative.Client.Gate.Tests;

public sealed class GateWireTests
{
    [Test]
    public void Request_uses_existing_control_contract_and_outer_RPC_opcode()
    {
        byte[] request = GateWire.Request("player.profile", new ProfileRequest { PlayerId = "p1" }.ToByteArray(), "test-session", "c1");
        ControlRequest decoded = ControlRequest.Parser.ParseFrom(request);
        Assert.That((decoded.Method, decoded.CorrelationId, decoded.Credential), Is.EqualTo(("player.profile", "c1", "test-session")));
        Assert.That(ProfileRequest.Parser.ParseFrom(decoded.Body).PlayerId, Is.EqualTo("p1"));
        byte[] frame = GateWire.Frame(268435633u, 17, GateWire.Bytes(1, request));
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(4)), Is.EqualTo(268435633u));
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(8)), Is.EqualTo(17u));
        Assert.That(BinaryPrimitives.ReadInt32LittleEndian(frame), Is.EqualTo(frame.Length - 20));
    }

    [Test]
    public void Match_response_preserves_authority_identity_and_short_lived_credential()
    {
        MatchReady ready = new() { EntryTicket = "signed-ticket", Status = new() { State = "Ready", MatchId = "m1", RequestId = "q1", Allocation = new() { RoomId = "r1", BootEpoch = "boot1", NodeId = "b1", Address = "127.0.0.1:22000", State = "Ready" } } };
        GateMatch parsed = GateWire.Match(ready.ToByteArray());
        Assert.That((parsed.RoomId, parsed.BootEpoch, parsed.NodeId, parsed.EntryTicket, parsed.Address), Is.EqualTo(("r1", "boot1", "b1", "signed-ticket", "127.0.0.1:22000")));
    }

    [Test]
    public void Unknown_additive_field_does_not_break_profile()
    {
        byte[] profile = new PlayerProfile { PlayerId = "p1", Played = 3, Won = 2, Kills = 4 }.ToByteArray();
        GateProfile parsed = GateWire.Profile(profile.Concat(new byte[] { 0xA0, 0x06, 0x01 }).ToArray());
        Assert.That((parsed.Played, parsed.Won, parsed.Kills), Is.EqualTo((3L, 2L, 4L)));
    }

    [TestCase(new byte[] { 0x0A, 0x7F, 0x01 })]
    [TestCase(new byte[] { 0x0F })]
    [TestCase(new byte[] { 0x08, 0x80 })]
    public void Malformed_wire_data_is_rejected_without_partial_results(byte[] malformed)
    {
        Assert.Throws<InvalidDataException>(() => GateWire.Profile(malformed));
    }

    [Test]
    public void Response_must_match_correlation_and_propagate_service_rejection()
    {
        Assert.Throws<InvalidDataException>(() => GateWire.Response(new ControlResponse { CorrelationId = "other" }.ToByteArray(), "mine"));
        GateCallException denied = Assert.Throws<GateCallException>(() => GateWire.Response(new ControlResponse { CorrelationId = "mine", Error = "unauthorized" }.ToByteArray(), "mine"))!;
        Assert.That(denied.Code, Is.EqualTo("unauthorized"));
    }

    [Test]
    public void Remote_plaintext_is_rejected_and_loopback_is_explicitly_allowed()
    {
        Assert.Throws<ArgumentException>(() => new GateConnectionOptions("111.230.230.247", 32301, useTls: false));
        Assert.DoesNotThrow(() => new GateConnectionOptions("127.0.0.1", 23001, useTls: false));
    }

    [Test]
    public void Pin_cannot_override_wrong_identity_expiry_or_wrong_certificate()
    {
        using RSA rsa = RSA.Create(2048);
        CertificateRequest request = new("CN=127.0.0.1", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        string pin = Convert.ToHexString(SHA256.HashData(certificate.RawData));
        Assert.That(GateTlsTrust.Accept(certificate, SslPolicyErrors.RemoteCertificateChainErrors, pin, DateTime.UtcNow), Is.True);
        Assert.That(GateTlsTrust.Accept(certificate, SslPolicyErrors.RemoteCertificateNameMismatch, pin, DateTime.UtcNow), Is.False);
        Assert.That(GateTlsTrust.Accept(certificate, SslPolicyErrors.RemoteCertificateChainErrors, new string('0', 64), DateTime.UtcNow), Is.False);
        Assert.That(GateTlsTrust.Accept(certificate, SslPolicyErrors.RemoteCertificateChainErrors, pin, DateTime.UtcNow.AddDays(2)), Is.False);
        Assert.That(GateTlsTrust.Accept(certificate, SslPolicyErrors.RemoteCertificateChainErrors, "", DateTime.UtcNow), Is.False);
    }
}
