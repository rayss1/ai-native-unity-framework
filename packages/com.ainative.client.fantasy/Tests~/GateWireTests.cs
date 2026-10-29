using System.Buffers.Binary;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AiNative.Client.Fantasy;
using AiNative.Client.Protocol.Protobuf;
using AiNative.Protocol.Backend.V1;
using AiNative.Protocol.Transport.V1;
using AiNative.Realtime;
using Google.Protobuf;
using NUnit.Framework;

namespace AiNative.Client.Gate.Tests;

public sealed class GateWireTests
{
    [Test]
    public void Generated_business_requests_preserve_server_contracts()
    {
        Assert.That(
            AccountCredentials
                .Parser.ParseFrom(GateProtocolCodec.Account("user", "password"))
                .Password,
            Is.EqualTo("password")
        );
        var party = PartyOperation.Parser.ParseFrom(
            GateProtocolCodec.PartyRequest("party", "target", true, 42)
        );
        Assert.That(
            (party.PartyId, party.TargetPlayerId, party.Ready, party.ExpectedVersion),
            Is.EqualTo(("party", "target", true, 42UL))
        );
        var queue = PartyQueueData.Parser.ParseFrom(
            GateProtocolCodec.QueueRequest("party", 42, "queue")
        );
        Assert.That(
            (queue.PartyId, queue.ExpectedVersion, queue.RequestId),
            Is.EqualTo(("party", 42UL, "queue"))
        );
        Assert.That(
            MatchQuery.Parser.ParseFrom(GateProtocolCodec.MatchRequest("queue")).RequestId,
            Is.EqualTo("queue")
        );
        Assert.That(
            SettlementQuery.Parser.ParseFrom(GateProtocolCodec.SettlementRequest("match")).MatchId,
            Is.EqualTo("match")
        );
    }

    [Test]
    public void Party_and_invitation_limits_are_preserved()
    {
        PartyState party = new() { PartyId = "party", Version = 9 };
        for (int i = 0; i < 64; i++)
        {
            party.Members.Add(new PartyMember { PlayerId = "p" + i, Ready = true });
        }

        Assert.That(GateWire.Party(party.ToByteArray()).Members.Length, Is.EqualTo(64));
        party.Members.Add(new PartyMember());
        Assert.Throws<InvalidDataException>(() => GateWire.Party(party.ToByteArray()));
        PartyInvitations invitations = new();
        for (int i = 0; i < 64; i++)
        {
            invitations.Parties.Add(new PartyState());
        }

        Assert.That(
            GateProtocolCodec.Invitations(invitations.ToByteArray()).Length,
            Is.EqualTo(64)
        );
        invitations.Parties.Add(new PartyState());
        Assert.Throws<InvalidDataException>(() =>
            GateProtocolCodec.Invitations(invitations.ToByteArray())
        );
    }

    [Test]
    public void Queue_status_maps_without_wrapping_and_reparsing_message()
    {
        var status = new MatchStatus { RequestId = "q", State = "Queued" };
        var result = GateWire.Match(status.ToByteArray(), queueResponse: true);
        Assert.That(
            (result.RequestId, result.State, result.EntryTicket),
            Is.EqualTo(("q", "Queued", ""))
        );
    }

    [Test]
    public void Bounded_fields_are_enforced_in_nested_messages()
    {
        using MemoryStream fields = new();
        using (CodedOutputStream output = new(fields, leaveOpen: true))
        {
            for (int i = 0; i < 1025; i++)
            {
                output.WriteTag(99, WireFormat.WireType.Varint);
                output.WriteUInt32(1);
            }
        }

        Assert.Throws<InvalidDataException>(() => GateWire.Profile(fields.ToArray()));
        using MemoryStream party = new();
        using (CodedOutputStream output = new(party, leaveOpen: true))
        {
            output.WriteTag(4, WireFormat.WireType.LengthDelimited);
            output.WriteBytes(ByteString.CopyFrom(fields.ToArray()));
        }

        Assert.Throws<InvalidDataException>(() => GateWire.Party(party.ToArray()));
    }

    [TestCase(new byte[] { 0x08, 0x01 })] // player_id must be a string
    [TestCase(new byte[] { 0x22, 0x02, 0x08, 0x01 })] // nested member player_id must be a string
    public void Wrong_known_field_wire_type_is_rejected(byte[] bytes)
    {
        Assert.Throws<InvalidDataException>(() => GateWire.Party(bytes));
    }

    [Test]
    public void Body_limits_negative_statistics_and_envelope_overflow_are_rejected()
    {
        Assert.Throws<InvalidDataException>(() =>
            GateProtocolCodec.Account(new string('x', GateWire.MaxBody), "password")
        );
        Assert.Throws<InvalidDataException>(() => GateWire.Profile(new byte[GateWire.MaxBody + 1]));
        Assert.Throws<InvalidDataException>(() =>
            GateWire.Profile(new PlayerProfile { Played = -1 }.ToByteArray())
        );
        using MemoryStream stream = new();
        using (CodedOutputStream output = new(stream, leaveOpen: true))
        {
            output.WriteTag(1, WireFormat.WireType.Varint);
            output.WriteUInt64(1UL << 32);
        }

        Assert.Throws<InvalidDataException>(() =>
            GateProtocolCodec.ResponseEnvelope(stream.ToArray())
        );
    }

    [Test]
    public void Request_uses_existing_control_contract_and_outer_RPC_opcode()
    {
        byte[] request = GateWire.Request(
            "player.profile",
            new ProfileQuery { PlayerId = "p1" }.ToByteArray(),
            "test-session",
            "c1"
        );
        S2S_ControlRequest decoded = S2S_ControlRequest.Parser.ParseFrom(request);
        Assert.That(
            (decoded.Method, decoded.CorrelationId, decoded.Credential),
            Is.EqualTo(("player.profile", "c1", "test-session"))
        );
        Assert.That(ProfileQuery.Parser.ParseFrom(decoded.Body).PlayerId, Is.EqualTo("p1"));
        byte[] frame = GateFrame.Encode(
            GateTransportOpCodes.Request,
            17,
            new C2S_GateTransportRequest { Payload = ByteString.CopyFrom(request) }.ToByteArray()
        );
        Assert.That(
            BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(4)),
            Is.EqualTo(268435633u)
        );
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(8)), Is.EqualTo(17u));
        Assert.That(
            Convert.ToHexString(frame.AsSpan(4, 16)),
            Is.EqualTo("B1000010110000000000000000000000")
        );
        Assert.That(BinaryPrimitives.ReadInt32LittleEndian(frame), Is.EqualTo(frame.Length - 20));
    }

    [Test]
    public void Match_response_preserves_authority_identity_and_short_lived_credential()
    {
        MatchReady ready = new()
        {
            EntryTicket = "signed-ticket",
            Status = new()
            {
                State = "Ready",
                MatchId = "m1",
                RequestId = "q1",
                Allocation = new()
                {
                    RoomId = "r1",
                    BootEpoch = "boot1",
                    NodeId = "b1",
                    Address = "127.0.0.1:22000",
                    State = "Ready",
                },
            },
        };
        GateMatch parsed = GateWire.Match(ready.ToByteArray());
        Assert.That(
            (parsed.RoomId, parsed.BootEpoch, parsed.NodeId, parsed.EntryTicket, parsed.Address),
            Is.EqualTo(("r1", "boot1", "b1", "signed-ticket", "127.0.0.1:22000"))
        );
    }

    [Test]
    public void Unknown_additive_field_does_not_break_profile()
    {
        byte[] profile = new PlayerProfile
        {
            PlayerId = "p1",
            Played = 3,
            Won = 2,
            Kills = 4,
        }.ToByteArray();
        GateProfile parsed = GateWire.Profile(
            profile.Concat(new byte[] { 0xA0, 0x06, 0x01 }).ToArray()
        );
        Assert.That((parsed.Played, parsed.Won, parsed.Kills), Is.EqualTo((3L, 2L, 4L)));
    }

    [TestCase(new byte[] { 0x0A, 0x01, 0xFF })]
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
        Assert.Throws<InvalidDataException>(() =>
            GateWire.Response(
                new S2S_ControlResponse { CorrelationId = "other" }.ToByteArray(),
                "mine"
            )
        );
        GateCallException denied = Assert.Throws<GateCallException>(() =>
            GateWire.Response(
                new S2S_ControlResponse
                {
                    CorrelationId = "mine",
                    Error = "unauthorized",
                }.ToByteArray(),
                "mine"
            )
        )!;
        Assert.That(denied.Code, Is.EqualTo("unauthorized"));
    }

    [Test]
    public void Remote_plaintext_is_rejected_and_loopback_is_explicitly_allowed()
    {
        Assert.Throws<ArgumentException>(() =>
            new GateConnectionOptions("111.230.230.247", 32301, useTls: false)
        );
        Assert.DoesNotThrow(() => new GateConnectionOptions("127.0.0.1", 23001, useTls: false));
    }

    [Test]
    public void Pin_cannot_override_wrong_identity_expiry_or_wrong_certificate()
    {
        using RSA rsa = RSA.Create(2048);
        CertificateRequest request = new(
            "CN=127.0.0.1",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        );
        using X509Certificate2 certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddHours(1)
        );
        string pin = Convert.ToHexString(SHA256.HashData(certificate.RawData));
        Assert.That(
            GateTlsTrust.Accept(
                certificate,
                SslPolicyErrors.RemoteCertificateChainErrors,
                pin,
                DateTime.UtcNow
            ),
            Is.True
        );
        Assert.That(
            GateTlsTrust.Accept(
                certificate,
                SslPolicyErrors.RemoteCertificateNameMismatch,
                pin,
                DateTime.UtcNow
            ),
            Is.False
        );
        Assert.That(
            GateTlsTrust.Accept(
                certificate,
                SslPolicyErrors.RemoteCertificateChainErrors,
                new string('0', 64),
                DateTime.UtcNow
            ),
            Is.False
        );
        Assert.That(
            GateTlsTrust.Accept(
                certificate,
                SslPolicyErrors.RemoteCertificateChainErrors,
                pin,
                DateTime.UtcNow.AddDays(2)
            ),
            Is.False
        );
        Assert.That(
            GateTlsTrust.Accept(
                certificate,
                SslPolicyErrors.RemoteCertificateChainErrors,
                "",
                DateTime.UtcNow
            ),
            Is.False
        );
    }
}
