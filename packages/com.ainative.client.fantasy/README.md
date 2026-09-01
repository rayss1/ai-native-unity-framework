# AI-Native Fantasy KCP Client Transport

This package implements `AiNative.Realtime.IRealtimeTransport` for a Unity client using Fantasy KCP. The public surface is framework-neutral; Fantasy message and session types remain internal to this assembly.

## Connection

```csharp
FantasyKcpConnectResult result = await FantasyKcpRealtimeTransport.ConnectAsync(
    new FantasyKcpTransportOptions("127.0.0.1", 22000));

if (result.Status == FantasyKcpConnectStatus.Connected)
{
    FantasyKcpRealtimeTransport transport = result.Transport;
}
```

The package fixes the outer KCP MTU at 1150 bytes and accepts application frames up to 1200 bytes. Both directions are bounded to 1024 packets and 256 KiB by default. `SendStatus.Accepted` means that the payload was copied into the transport-owned queue; Fantasy serialization and socket work occur later on Fantasy's main-thread update path.

Supported channel contracts are:

- 0: reliable, ordered control traffic.
- 1 and 2: unreliable, sequenced realtime traffic.
- 3: reliable, ordered control traffic.

Call `TryAdvanceConnectionEpoch` after decoding a successful login or reconnect response and before routing subsequent packets into prediction. Epoch zero and regressions are rejected.

## Third-party software

Fantasy.Unity is consumed at version `2026.1.1001` from the repository-pinned Fantasy commit. See [Third Party Notices](THIRD-PARTY-NOTICES.md) for the complete applicable modified MIT text and explicit entity restriction. Approved Windows and macOS Player distributions must carry that notice/license; the application-owned Player build scripts are responsible for copying it beside the built Player.
