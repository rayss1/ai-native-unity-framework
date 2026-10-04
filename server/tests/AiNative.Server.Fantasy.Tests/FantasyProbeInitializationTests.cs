using System.Security.Cryptography;
using AiNative.Server.Control;
using AiNative.Server.Fantasy;
using NUnit.Framework;

namespace AiNative.Server.Fantasy.Tests;

public sealed class FantasyProbeInitializationTests
{
    [Test]
    public async Task IndependentProbeRuntimeConfiguresOuterKcpBeforeItsFirstNetwork()
    {
        // Also run this test alone in a fresh process: Gateway construction sets
        // vendor-global settings and can otherwise conceal missing probe setup.
        using RSA key = RSA.Create(2048);
        ServicePeer peer = new("probe", ServiceRole.Gate, 1, key.ExportSubjectPublicKeyInfoPem());
        PeerAuthentication authentication = new("probe", key.ExportPkcs8PrivateKeyPem(), [peer], TimeProvider.System);
        await using FantasyServiceRuntime runtime = new(1, ServiceRole.Gate, authentication);

        // Inspect the dependency in test code without importing vendor runtime
        // types beyond the adapter's compile-time namespace boundary (ARC008).
        Type settingsType = Type.GetType("Fantasy.Network.KCP.KCPSettings, Fantasy-Net", throwOnError: true)!;
        Type targetType = Type.GetType("Fantasy.Network.NetworkTarget, Fantasy-Net", throwOnError: true)!;
        object firstNetworkSettings = settingsType.GetMethod("Create")!
            .Invoke(null, [Enum.Parse(targetType, "Outer")])!;

        Assert.That(settingsType.GetProperty("Mtu")!.GetValue(firstNetworkSettings), Is.EqualTo(1150),
            "An independent probe must accept the same UDP datagrams as Battle and the Unity client.");
    }
}
