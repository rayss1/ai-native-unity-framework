# Public API Contract Catalog

Status: Minimum contract frozen for the first vertical slice
Last updated: 2026-08-13

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

## Supporting boundary contracts

The first slice also requires, but does not yet freeze method shapes for:

| Contract | Owner | Gate before signature acceptance |
| --- | --- | --- |
| `IReplicationEncoder` | Realtime replication | Protobuf/delta baseline measurement; ADR-0009 codec gate |
| `IContentManifestStore` | Client bootstrap/update | Atomic activation and crash-recovery Spike |
| `IGameplayDiagnostics` | Shared diagnostics abstraction | Zero-allocation disabled path and cardinality review |

Adding one of these APIs requires contract tests, lifecycle/threading notes, and an ADR/catalog update; implementations may remain internal until that gate passes.
