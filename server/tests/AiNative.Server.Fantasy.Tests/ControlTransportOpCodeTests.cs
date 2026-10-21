using AiNative.Realtime;
using NUnit.Framework;

namespace AiNative.Server.Fantasy.Tests;

public sealed class ControlTransportOpCodeTests
{
    // Literal values are intentionally independent of the implementation: changing one breaks old peers.
    [TestCase("gate-request", 268435633u)]
    [TestCase("gate-response", 402653361u)]
    [TestCase("gate-push", 134217905u)]
    [TestCase("service-request", 1073742001u)]
    [TestCase("service-response", 1207959729u)]
    public void SharedConstantsAndServerMessagesPreservePinnedWireIds(string message, uint expected)
    {
        (uint constant, uint registered) = message switch
        {
            "gate-request" => (GateTransportOpCodes.Request, new C2S_FantasyGateRequest().OpCode()),
            "gate-response" => (
                GateTransportOpCodes.Response,
                new S2C_FantasyGateResponse().OpCode()
            ),
            "gate-push" => (GateTransportOpCodes.Push, new S2C_FantasyGatePush().OpCode()),
            "service-request" => (
                ServiceControlTransportOpCodes.Request,
                new S2S_FantasyControlRequest().OpCode()
            ),
            "service-response" => (
                ServiceControlTransportOpCodes.Response,
                new S2S_FantasyControlResponse().OpCode()
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(message)),
        };
        Assert.That(constant, Is.EqualTo(expected));
        Assert.That(registered, Is.EqualTo(expected));
        Type messageType = message switch
        {
            "gate-request" => typeof(C2S_FantasyGateRequest),
            "gate-response" => typeof(S2C_FantasyGateResponse),
            "gate-push" => typeof(S2C_FantasyGatePush),
            "service-request" => typeof(S2S_FantasyControlRequest),
            _ => typeof(S2S_FantasyControlResponse),
        };
        // Verify the source generator resolved cross-assembly constants into the runtime registry.
        // Inspect generated metadata without adding a static vendor API dependency to tests.
        Type registrarType = messageType
            .Assembly.GetTypes()
            .Single(t =>
                t.GetInterfaces().Any(i => i.FullName == "Fantasy.Assembly.IOpCodeRegistrar")
                && !t.IsInterface
            );
        object registrar = Activator.CreateInstance(registrarType, true)!;
        var types = (Type[])registrarType.GetMethod("OpCodeTypes")!.Invoke(registrar, null)!;
        var codes = (uint[])registrarType.GetMethod("TypeOpCodes")!.Invoke(registrar, null)!;
        int index = Array.IndexOf(types, messageType);
        Assert.That(index, Is.GreaterThanOrEqualTo(0));
        Assert.That(codes[index], Is.EqualTo(expected));
        Assert.That(
            new[]
            {
                GateTransportOpCodes.Request,
                GateTransportOpCodes.Response,
                GateTransportOpCodes.Push,
                ServiceControlTransportOpCodes.Request,
                ServiceControlTransportOpCodes.Response,
            },
            Is.Unique
        );
    }
}
