using AiNative.Protocol.Backend.V1;
using AiNative.Protocol.Transport.V1;
using AiNative.Protocol.V1;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using NUnit.Framework;

namespace AiNative.Protocol.Tests;

public sealed class DirectionNamingCompatibilityTests
{
    // Frozen from d43796f with protoc 35.1 (Grpc.Tools 2.83.0), without source locations.
    private static readonly Dictionary<string, string> Renames = new()
    {
        ["LoginRequest"] = "C2S_LoginRequest",
        ["LoginResponse"] = "S2C_LoginResponse",
        ["JoinRoomRequest"] = "C2S_JoinRoomRequest",
        ["JoinRoomResponse"] = "S2C_JoinRoomResponse",
        ["InputCommand"] = "C2S_InputCommand",
        ["InputBatch"] = "C2S_InputBatch",
        ["Snapshot"] = "S2C_SnapshotPush",
        ["ReliableEvent"] = "S2C_ReliableEventPush",
        ["ReconnectRequest"] = "C2S_ReconnectRequest",
        ["ReconnectResponse"] = "S2C_ReconnectResponse",
        ["ControlRequest"] = "S2S_ControlRequest",
        ["ControlResponse"] = "S2S_ControlResponse",
        ["ControlNotification"] = "S2C_GatePush",
        ["AccountRequest"] = "AccountCredentials",
        ["SessionRequest"] = "SessionCredentials",
        ["ProfileRequest"] = "ProfileQuery",
        ["PartyCommand"] = "PartyOperation",
        ["PartyQueueRequest"] = "PartyQueueData",
        ["AllocationRequest"] = "AllocationSpec",
        ["DrainRequest"] = "DrainSpec",
        ["TicketRequest"] = "TicketSpec",
        ["GateRequestEnvelope"] = "C2S_GateTransportRequest",
        ["GateResponseEnvelope"] = "S2C_GateTransportResponse",
    };

    [Test]
    public void RenamingPreservesEveryExistingFieldAndEnumNumber()
    {
        using var stream =
            typeof(DirectionNamingCompatibilityTests).Assembly.GetManifestResourceStream(
                "AiNative.Protocol.Tests.Fixtures.before-direction-names.pb"
            );
        Assert.That(stream, Is.Not.Null);
        var baseline = FileDescriptorSet.Parser.ParseFrom(stream!);
        var current = new[]
        {
            BackendReflection.Descriptor,
            GameplayReflection.Descriptor,
            GateEnvelopeReflection.Descriptor,
        };
        foreach (var file in baseline.File)
        {
            var updated = current.Single(f => f.Name == file.Name).ToProto();
            foreach (var oldMessage in file.MessageType)
            {
                var expected = oldMessage.Clone();
                expected.Name = Renames.GetValueOrDefault(expected.Name, expected.Name);
                foreach (var field in expected.Field)
                {
                    // Generated C# descriptors omit inferred json_name; compare the effective
                    // name before normalizing this descriptor-only representation difference.
                    var currentField = current
                        .Single(f => f.Name == file.Name)
                        .MessageTypes.Single(m => m.Name == expected.Name)
                        .FindFieldByNumber(field.Number);
                    Assert.That(currentField.JsonName, Is.EqualTo(field.JsonName));
                    field.ClearJsonName();
                    int dot = field.TypeName.LastIndexOf('.');
                    if (
                        dot >= 0
                        && Renames.TryGetValue(field.TypeName[(dot + 1)..], out var renamed)
                    )
                        field.TypeName = field.TypeName[..(dot + 1)] + renamed;
                }
                Assert.That(
                    updated.MessageType.Single(m => m.Name == expected.Name),
                    Is.EqualTo(expected),
                    expected.Name
                );
            }
            foreach (var oldEnum in file.EnumType)
            {
                var updatedEnum = updated.EnumType.Single(e => e.Name == oldEnum.Name);
                if (oldEnum.Name != "MessageId")
                    Assert.That(updatedEnum, Is.EqualTo(oldEnum));
                else
                {
                    Assert.That(updatedEnum.Value.Count, Is.EqualTo(oldEnum.Value.Count));
                    foreach (var value in oldEnum.Value)
                    {
                        string suffix = value.Name["MESSAGE_ID_".Length..];
                        string name =
                            suffix == "UNSPECIFIED"
                                ? value.Name
                                : "MESSAGE_ID_"
                                    + (
                                        suffix is "SNAPSHOT" or "RELIABLE_EVENT"
                                            ? "S2C_" + suffix + "_PUSH"
                                        : suffix.EndsWith("RESPONSE") ? "S2C_" + suffix
                                        : "C2S_" + suffix
                                    );
                        Assert.That(
                            updatedEnum.Value.Single(v => v.Name == name).Number,
                            Is.EqualTo(value.Number)
                        );
                    }
                }
            }
        }
    }

    [Test]
    public void ClientAndServiceEnvelopesKeepTheExistingWireLayout()
    {
        var request = new C2S_GateRequest
        {
            CorrelationId = "a",
            Method = "m",
            Body = ByteString.CopyFrom(new byte[] { 1 }),
            Credential = "c",
            DelegatedPlayerId = "p",
            TraceParent = "t",
            TraceState = "s",
        };
        const string expected = "0A016112016D1A01012201632A01703201743A0173";
        Assert.That(Convert.ToHexString(request.ToByteArray()), Is.EqualTo(expected));
        Assert.That(
            Convert.ToHexString(
                S2S_ControlRequest.Parser.ParseFrom(request.ToByteArray()).ToByteArray()
            ),
            Is.EqualTo(expected)
        );
        var reply = new S2C_GateResponse
        {
            CorrelationId = "a",
            Body = ByteString.CopyFrom(new byte[] { 1 }),
            Error = "e",
        };
        Assert.That(Convert.ToHexString(reply.ToByteArray()), Is.EqualTo("0A01611201011A0165"));
        Assert.That(
            S2S_ControlResponse.Parser.ParseFrom(reply.ToByteArray()).ToByteArray(),
            Is.EqualTo(reply.ToByteArray())
        );
        var external = C2S_GateRequest.Descriptor.ToProto();
        external.Name = nameof(S2S_ControlRequest);
        Assert.That(external, Is.EqualTo(S2S_ControlRequest.Descriptor.ToProto()));
        var response = S2C_GateResponse.Descriptor.ToProto();
        response.Name = nameof(S2S_ControlResponse);
        Assert.That(response, Is.EqualTo(S2S_ControlResponse.Descriptor.ToProto()));
    }
}
