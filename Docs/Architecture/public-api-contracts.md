# Public API Contract Catalog

Status: Minimum contract frozen for the first vertical slice
Last updated: 2026-08-31

This catalog defines ownership and semantics before implementation. Names and invariants are stable; value layouts may be extended additively as Spikes reveal required data. Implementations must not expose engine/framework types through these ports.

## Common conventions

- Namespace: `AiNative.Gameplay` for Shared simulation contracts; client-only content contracts use `AiNative.Client.Assets`; transport boundary contracts use `AiNative.Realtime` outside Shared gameplay.
- Tick-path methods are synchronous, non-blocking, and allocation-free in steady state. Work that can block uses an asynchronous service outside the Tick path.
- Value types use explicit units (`Tick`, seconds, metres) and invariant numeric representations. No `DateTime.Now`, global RNG, or implicit frame time.
- Caller-owned buffers use `Span<T>`/`ReadOnlySpan<T>` only where both Unity/C# 9 and target runtimes support the exact contract. No implementation-owned collection escapes without an ownership rule.
- Invalid caller input returns a documented result or domain error; infrastructure exceptions do not cross into gameplay rules.
- World/services are created and disposed by Composition Roots. Gameplay code does not locate global singletons.

## `IGameplayClock`

Owner: Shared Gameplay
Consumers: simulation rules, prediction, replay
Implementations: Battle fixed-Tick scheduler; Unity prediction clock; replay clock

```csharp
public interface IGameplayClock
{
    long Tick { get; }
    float FixedDeltaSeconds { get; }
}
```

Contract:

- `Tick` is monotonic within a simulation epoch and advances exactly once per committed simulation step.
- `FixedDeltaSeconds` is constant for the epoch; the first slice uses `1f / 60f`.
- It is simulation time, not UTC or elapsed wall-clock time. Scheduling delay never changes the simulation delta.

## `IPhysicsWorld`

Owner: Shared Gameplay
Consumers: movement, weapons, hit validation
Implementations: server Jolt adapter candidate; Unity adapter; deterministic test fake

```csharp
public interface IPhysicsWorld
{
    PhysicsStepResult Step(in PhysicsStepInput input);
    bool Raycast(in RaycastQuery query, out PhysicsHit hit);
    int Overlap(in OverlapQuery query, Span<PhysicsHit> results);
}
```

Contract:

- All inputs/outputs are Shared-owned value types with explicit units, layer masks, entity IDs, and query flags.
- `Step` is called at most once per world Tick on its owning thread. Query availability before/after Step is documented by the adapter and consistent across runs.
- Multiple results are normalized by `(fraction/distance, entity ID, shape ID)` before gameplay consumes them.
- `Overlap` returns the total written count and never writes beyond the supplied buffer; overflow is explicit in the query/result contract, not a hidden allocation.
- Native callbacks, pointers, engine colliders, and disposal handles never escape the adapter.

## `INavigationWorld`

Owner: Shared Gameplay
Consumers: AI/movement decision logic
Implementations: server Detour adapter candidate; test fake; optional client adapter

```csharp
public interface INavigationWorld
{
    PathRequestId RequestPath(in PathRequest request);
    PathStatus TryGetPath(PathRequestId id, Span<NavigationPoint> points, out int written);
    bool Cancel(PathRequestId id);
}
```

Contract:

- `RequestPath` queues bounded work and returns immediately; it never performs an unbounded bake/search on the Tick thread.
- Results become visible only at a Tick boundary and include nav-artifact version and completion status.
- Request IDs are scoped to one world epoch. Cancellation is idempotent.
- Callers handle `Pending`, `Complete`, `Partial`, `Unreachable`, `Cancelled`, `Expired`, and `BufferTooSmall` explicitly.

## `IRealtimeTransport`

Owner: Realtime networking abstractions (not Shared Gameplay)
Consumers: client/server networking and replication adapters
Implementations: Fantasy KCP adapter first; loopback/impairment test transports

```csharp
public interface IRealtimeTransport : IAsyncDisposable
{
    TransportState State { get; }
    ValueTask<SendResult> SendAsync(
        TransportChannel channel,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default);
    bool TryReceive(Span<byte> destination, out ReceivedPacket packet);
}
```

Contract:

- `TransportChannel` declares reliable/unreliable, ordered/sequenced semantics; callers do not infer them from numeric channel IDs.
- `SendResult` distinguishes accepted, would-block, dropped-by-policy, closed, payload-too-large, and faulted. Accepted means ownership/copy rules have been satisfied, not remote delivery.
- Queues are bounded. Backpressure is observable and one connection cannot block a room or another connection.
- `TryReceive` is non-blocking and reports required/written size, channel, sequence/capability metadata, and connection epoch.
- Fantasy Session, KCP control blocks, sockets, and native buffers do not cross the interface.

## `FantasyKcpRealtimeTransport`

Owner: `com.ainative.client.fantasy`
Consumers: Unity application Composition Root through `IRealtimeTransport`
Dependencies: `AiNative.Realtime`, pinned `Fantasy.Unity` `2026.1.1001`

This is the first concrete Unity implementation of `IRealtimeTransport`; it does not change that shared port. `FantasyKcpTransportOptions` supplies Host, Port, and connection timeout. `ConnectAsync` returns `FantasyKcpConnectResult`, whose stable `FantasyKcpConnectStatus` distinguishes Connected, InvalidConfiguration, TimedOut, Cancelled, and Faulted without exposing Fantasy types.

Contract:

- The outer KCP MTU is 1,150 bytes and the maximum application frame is 1,200 bytes. Default inbound and outbound limits are 1,024 packets and 256 KiB per direction.
- Accepted sends have been copied into transport-owned bounded storage only. Queue saturation returns `WouldBlock`; closed, oversized, cancelled, and faulted outcomes remain distinct.
- Fixed Tick callers only copy into preallocated queues. Fantasy serialization, Session/KCP update, socket work, and potentially allocating callbacks run in the Fantasy main-thread update phase outside FixedUpdate.
- `TryAdvanceConnectionEpoch` accepts only nonzero, monotonic values. Login and reconnect responses are decoded and bound by the application before prediction receives subsequent packets.
- The read-only `FantasyKcpTransportDiagnostics` snapshot reports accepted sends/receives, send backpressure, oversized frames, invalid channels, stale sequences, inbound drops, and connection faults.
- Disposal unregisters Session routing before releasing the Session; late callbacks are ignored. Fantasy Session, messages, and generated registration types do not enter prediction, Shared, or application state.

## `IAssetService`

Owner: Client asset abstractions
Consumers: Client Gameplay/UI/presentation
Implementations: project content pipeline adapter; test fake

```csharp
public interface IAssetService
{
    ValueTask<AssetHandle<T>> LoadAsync<T>(AssetId id, CancellationToken cancellationToken = default);
    bool TryGetLoaded<T>(AssetId id, out AssetHandle<T> handle);
    ValueTask PreloadAsync(AssetGroupId group, CancellationToken cancellationToken = default);
    ValueTask ReleaseAsync(AssetHandle handle);
}
```

Contract:

- `AssetId` is stable and logical; bundle paths, URLs, hashes, CDN vendors, and cache paths are private implementation details.
- A successful handle pins a verified asset from the active manifest. Handles are reference-counted or otherwise lifetime-tracked; release is idempotent per acquired ownership token.
- Cancellation stops caller interest but cannot expose a partially verified/activated asset.
- Manifest activation/rollback is managed by the bootstrap/update service, not arbitrary gameplay callers.
- Load failures use stable categories: unknown ID, incompatible manifest, unavailable, integrity failure, quota, cancelled, and internal fault.

## `IRandomSource` and `IStateHasher`

Owner: Shared Gameplay
Consumers: simulation rules, replay, golden-vector tests

`IRandomSource` exposes `NextUInt32`, `CaptureState`, and `RestoreState`. The first implementation is portable PCG-XSH-RR 32 with an explicit 128-bit `(state, stream)` value. No global or wall-clock seed is permitted.

`IStateHasher.ComputeHash(ReadOnlySpan<byte>)` hashes a caller-owned canonical representation. The first implementation is xxHash64 with seed zero; canonical state bytes are versioned, little-endian, and independent of object layout or locale.

## Client prediction primitives

Owner: Shared Gameplay
Consumers: Unity client prediction, deterministic tests, future replay diagnostics

`KinematicInput` contains a strictly increasing sequence and normalized X/Z movement axes. `KinematicState` contains the committed/predicted Tick, last processed input sequence, and integer-millimetre X/Z position. `KinematicMovement.Step` advances exactly one fixed Tick using the product-owned integer rule; it does not call Unity physics or another adapter.

`ClientPredictionHistory` owns a constructor-bounded circular history. `Predict` never blocks and reports when it had to drop the oldest entry. `Reconcile` consumes an authoritative `KinematicState`, discards acknowledged inputs, replays only newer inputs, and returns a `ReconciliationResult` that distinguishes matched, corrected, stale, authoritative-ahead, and history-miss paths. Correction components are explicit millimetres; visual smoothing is not part of the Shared contract.

The protocol adapter maps the recipient-specific `Snapshot.last_processed_input_sequence` into the authoritative state. Protobuf, Fantasy, transport, wall-clock, scheduling, and presentation types do not cross this API. See [Client Prediction and Reconciliation Baseline](client-prediction-baseline.md) for evidence and remaining gates.

## `ClientPredictionAdapter`

Owner: Client prediction package
Consumers: Unity application Composition Root and client transport routing
Dependencies: `AiNative.Gameplay`, `AiNative.Realtime`; no Unity, Fantasy, or generated Protobuf runtime dependency

`ClientPredictionAdapter` owns one local entity's bounded `ClientPredictionHistory`, protocol-v1 input sequence, connection epoch, a reusable send buffer, and correction counters. It is constructed after JoinRoom identifies the local entity. The caller supplies an `IRealtimeTransport`; ownership and disposal are explicit.

Contract:

- `PrepareInput` is the synchronous prediction-owner-thread path. It requires a caller-owned buffer of `RequiredInputBufferBytes`, advances prediction and sequence only when it returns `Prepared`, emits the protocol-v1 InputCommand frame, and allocates nothing after initialization.
- `SendInputAsync` is an outside-Tick convenience that forwards the prepared frame over the unreliable/sequenced Input channel. Only one send may be outstanding because the adapter reuses one bounded buffer. Cancellation, backpressure, policy drop, closure, payload rejection, sequence exhaustion, and transport fault remain distinguishable.
- `ApplyPacket` accepts only a complete Snapshot-channel frame or ReconnectResponse control frame already routed by the caller. It validates the v1 message ID, wire types, protocol major, local entity, monotonic connection epoch, reconnect epoch agreement, and payload bounds before mutating prediction state.
- Unknown additive Protobuf fields are skipped. Truncated, malformed, wrong-channel, wrong-message, incompatible-protocol, missing-player, stale-epoch, and arithmetic-overflow inputs fail closed with a stable result. An arithmetic overflow resets to the decoded authoritative state.
- Snapshot acknowledgement maps to `KinematicState.LastProcessedInputSequence`; reconciliation remains server-authoritative and replays only newer local inputs. Diagnostics expose accepted snapshots, bounded correction-sample count, corrections, corrections above 250 mm, exact maximum, P95/P99, history misses, stale snapshots, and dropped inputs without binding to a telemetry SDK. The fixed histogram has exact millimetre buckets through 8,192 mm and one bounded overflow bucket; `ResetDiagnostics` starts a new observation window without changing prediction/session state.
- Packet polling/routing, login/join, visual smoothing, remote interpolation, concrete KCP sockets, elapsed-time/rate calculation, and evidence serialization stay in higher adapters or the Unity Composition Root.

The runtime wire implementation is intentionally small and generated-type-free for Unity. .NET-only compatibility tests compare its InputCommand, Snapshot, and ReconnectResponse behavior with the tracked Google.Protobuf generation.

## Supporting boundary contracts

The first slice also requires, but does not yet freeze method shapes for:

| Contract | Owner | Gate before signature acceptance |
| --- | --- | --- |
| `IReplicationEncoder` | Realtime replication | Protobuf/delta baseline measurement; ADR-0009 codec gate |
| `IContentManifestStore` | Client bootstrap/update | Atomic activation and crash-recovery Spike |
| `IGameplayDiagnostics` | Shared diagnostics abstraction | Zero-allocation disabled path and cardinality review |

Adding one of these APIs requires contract tests, lifecycle/threading notes, and an ADR/catalog update; implementations may remain internal until that gate passes.
