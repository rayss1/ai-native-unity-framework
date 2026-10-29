# Public API Contract Catalog

Status: Minimum contract frozen for the first vertical slice
Last updated: 2026-10-16

This catalog defines ownership and semantics before implementation. Names and invariants are stable; value layouts may be extended additively as Spikes reveal required data. Implementations must not expose engine/framework types through these ports.

## Client protocol boundary (ADR-0019, 2026-10-20)

`IBattleProtocolCodec` belongs to `AiNative.Client.Protocol`, with explicit per-session injection. It exposes project-owned values, spans, channel/buffer requirements and caller-owned `BattlePacketBuffer`; no generated message, ByteString, Unity or Fantasy type crosses this port. All calls are owner-thread-only. Failed decode leaves the whole output unchanged. Views expire at the next successful decode, and consumers retain value copies only. Input encoding failure cannot advance prediction or sequence. Session owns Reset/Dispose; borrowed prediction adapters do not dispose the codec. Snapshot/event parsing occurs outside fixed Tick.

Prediction constructors now require `(transport, codec, entityId, ...)`; session constructors require codec first. `RequiredInputBufferBytes` is an instance property. Remote presentation takes decoded player values, Tick and local ID. No implicit implementation constructor is retained. Both prediction adapters expose a pure `CanReplaySafely(in state)` numeric preflight so reconnect can reject unsafe replay before advancing transport epoch; check and apply on the same owner thread without intervening input. See [API migration and ownership](client-protocol-codecs.md).

## Common conventions

### Network character presentation (P1, 2026-10-16)

Client-only additions; Shared ports and wire schemas are unchanged. Application composition owns
`NetworkCharacterPresenter` and the stripped visual prefab. `CharacterAnimationDriver.SetPresentationState`
accepts a `CharacterMotionState` value on the Unity main thread; it takes precedence over Motor until
`Configure` restores local mode. `CharacterOrbitCamera.ConfigurePresentation(Transform)` and
`SetPresentationState` follow an application-owned pose; `Yaw` / `Pitch` are degrees, positive Pitch
points down in Unity. Neither interface simulates gameplay or applies health.

`ArenaRemotePose.State` carries render-time motion and discrete snapshot state. Existing constructor
and coordinate properties remain compatible. `IBattleProtocolCodec.TryDecode` validates the existing
ReliableEvent combat payload; `ArenaPresentationEvent` carries server feedback, not commands.
`BattleClientSession.DrainCombatEvents(Span<ArenaPresentationEvent>)` is owner-thread-only, writes at most
caller capacity and removes those items. The 256-event queue drops oldest feedback on overflow and
increments `DroppedCombatEvents`; snapshots remain authoritative. Same-room reconnect clears pending
feedback while preserving event sequence deduplication. No event applies local damage.

See [P1 integration](p1-network-character.md) for controls, asset generation and validation limits.

### Arena hit geometry (2026-10-16 repair)

`ArenaHitGeometry` is a pure Shared math helper, in millimetres. `RayCapsule` takes a ray origin
relative to target feet, a unit direction and a maximum distance; it returns the nearest nonnegative
intersection with the upright capsule (radius 450, cap centres at 450 and 1350). The shot origin
height is 600. Server hitscan uses historical poses and retains lifetime/protection checks. Client
presentation may use this geometry to choose a camera aim point, but cannot authorize damage.
No wire fields or message identities change. The gameplay source fingerprint includes this helper;
old rule replays require their matching build, and fail closed under the new verifier.

`ArenaTrainingMap` defines the immutable training-ground v1 AABBs and spawn points in millimetres.
`GetObstacle` returns value copies; `Raycast` accepts a unit direction and range, returning the nearest
box/floor distance (optional padding supplies projectile sweeps). `Occluded` tests a finite segment.
`ResolveMovement` applies conservative upright-box collision in stable Y/X/Z order, including support
and grounded state; callers use it through `ArenaMovement.Step` on both prediction and authority.
The 40m boundary is shared, and its source participates in the replay gameplay fingerprint.
Client `ArenaTrainingGroundPresentation` owns only geometry/material construction and destruction;
visual Collider queries do not become server authority. No wire fields or external physics types are added.

### Local character rifle prototype

Owner: `com.ainative.client.character`. Unity main thread only; no Shared or wire changes.
`CharacterMotor.ShotFired` emits once per accepted grounded shooter attack, after action
exclusion checks. Consumers subscribe/unsubscribe with their enabled lifecycle; respawn
does not emit a shot. `CharacterRifle.Configure` injects the motor, camera, muzzle and
tracer material. `Launch(origin, direction)` and `Advance(seconds)` are local test/adapter
entry points; automatic LateUpdate must not also advance a manually driven instance.
The camera chooses an aim point; actual projectiles originate at the muzzle and stop at
the nearest non-owner collider. A 32-slot pool bounds active tracers; full capacity rejects
additional projectiles. Physics queries currently allocate and are outside the fixed Tick.
`CharacterHealth.ApplyDamage` rejects invalid/nonpositive values and already-dead targets,
clamps HP to zero and returns whether damage was accepted; `ResetHealth` restores maximum.
These local values must never be trusted as multiplayer authority. Disabling the rifle
unsubscribes and clears queued/in-flight shots; destruction releases tracer objects.


Developer-only local testing: Server Hosting owns `LocalDebugBootstrap`, an explicit Debug-build
configuration handoff selected by `AINATIVE_LOCAL_DEBUG_PROFILE` and `AINATIVE_LOCAL_DEBUG_SERVICE`.
It validates supervisor/service/file identities before applying process-local settings. It is not
a Shared port, wire protocol, or production configuration source. Release builds reject an explicit
handoff request; ordinary launches retain their existing configuration. See the
[Windows local testing guide](../../tools/topology/README-local-debug.md).

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
Dependencies: `AiNative.Realtime`, pinned `Fantasy.Unity` `2026.1.1002-ainative.1`

This is the first concrete Unity implementation of `IRealtimeTransport`; it does not change that shared port. `FantasyKcpTransportOptions` supplies Host, Port, and connection timeout. `ConnectAsync` returns `FantasyKcpConnectResult`, whose stable `FantasyKcpConnectStatus` distinguishes Connected, InvalidConfiguration, TimedOut, Cancelled, and Faulted without exposing Fantasy types.

Contract:

- The outer KCP MTU is 1,150 bytes and the maximum application frame is 1,200 bytes. Default inbound and outbound limits are 1,024 packets and 256 KiB per direction.
- Accepted sends have been copied into transport-owned bounded storage only. Queue saturation returns `WouldBlock`; closed, oversized, cancelled, and faulted outcomes remain distinct.
- Fixed Tick callers only copy into preallocated queues. Fantasy serialization, Session/KCP update, socket work, and potentially allocating callbacks run in the Fantasy main-thread update phase outside FixedUpdate.
- `TryAdvanceConnectionEpoch` accepts only nonzero, monotonic values. Login and reconnect responses are decoded and bound by the application before prediction receives subsequent packets.
- The read-only `FantasyKcpTransportDiagnostics` snapshot reports accepted sends/receives, send backpressure, oversized frames, invalid channels, stale sequences, inbound drops, and connection faults.
- Disposal unregisters Session routing before releasing the Session; late callbacks are ignored. Fantasy Session, messages, and generated registration types do not enter prediction, Shared, or application state.
- Remote closure stops new sends and packet admission but preserves the already accepted, bounded inbound queue for `TryReceive`. Explicit disposal releases the remainder. The application consumes these packets before deciding to reconnect; exhausting its per-frame receive budget defers that decision to a later frame. Neither transport acknowledgement nor queue admission proves that an application has displayed a terminal state.

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

## Arena prediction input forwarding

Owner: Client prediction package (`com.ainative.client.prediction`)

Arena input forwarding uses `ArenaClientPredictionAdapter.PrepareInput` with a
caller-owned destination, followed outside Tick by
`SendPreparedAsync(ReadOnlyMemory<byte>, CancellationToken)`. The supplied memory
remains immutable until send completion; the returned `SendResult`, cancellation
and transport exception remain observable. Retrying backpressure resends identical
bytes without advancing prediction. The 2026-10-04 pre-release correction removes
the unused byte-count-only send and fire-and-forget convenience method; source
consumers migrate to this explicit-memory path (see the prediction package README).
No Shared port, input sequence semantics or wire representation changes.

## Arena remote presentation

Owner: Client prediction package (`com.ainative.client.prediction`)
Consumer: Unity application composition

`ArenaClientProtocolV1.TryDecodePlayers` decodes the existing full Snapshot into
caller-owned `Span<ArenaSnapshotPlayer>` storage, with at most eight distinct
nonzero entity IDs. Invalid, duplicate or oversized data fails without changing
the destination. This bounded Arena decoder does not constrain the retained
legacy 64-player snapshot path.

`ArenaRemotePresentation` is single-owner-thread state with eight entity slots
and sixteen snapshots per entity. `ApplySnapshot(frame, localEntityId)` rejects
non-increasing server ticks and excludes the local entity from presentation.
`Advance(deltaSeconds, Span<ArenaRemotePose>)` emits world-space millimetres,
millidegree yaw and alive state. It targets a six-Tick delay, interpolates shortest
yaw, holds rather than extrapolates on starvation, and rebuilds the delay without
moving render time backward. Alive transitions or jumps above 5,000 mm reset that
entity's history. Missing members are removed immediately. `Reset` clears history
at every connection/room change; the application validates admission and packet
epoch before applying snapshots. The low-level decoder can describe eight remote
entities when the local entity is absent; application composition requires its
local entity to be present and therefore owns seven reusable remote visuals.

No authority, collision, local prediction, wire schema or Shared port changes.
These initial presentation parameters are bounded implementation defaults,
independent of the server correction and performance acceptance thresholds.

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
- Packet polling/routing, login/join, presentation-smoother ownership, remote interpolation, concrete KCP sockets, elapsed-time/rate calculation, and evidence serialization stay in higher adapters or the Unity Composition Root.

The runtime wire implementation is intentionally small and generated-type-free for Unity. .NET-only compatibility tests compare its InputCommand, Snapshot, and ReconnectResponse behavior with the tracked Google.Protobuf generation.

## `PresentationCorrectionSmoother`

Owner: Client prediction package
Consumers: Unity application Composition Root; future presentation adapters
Dependencies: `AiNative.Gameplay`; no Unity, Fantasy, Protobuf, transport, or telemetry dependency

The smoother consumes the project-owned `ReconciliationResult` after simulation has already accepted authority. It maintains only a bounded X/Z visual residual. The WS-28 candidate preserves continuity for corrections at or below 250 mm and linearly removes that residual within 100 ms of render time. A single correction or accumulated residual above 250 mm, an authoritative-ahead/history-miss result, or a reconnect boundary snaps to current simulation state.

`Advance` receives caller-supplied delta time and the latest predicted state and returns millimetre presentation coordinates. It never writes prediction history, changes acknowledgements, delays authority, reads wall-clock time, or allocates after construction. Diagnostics expose smoothed/snap counts and the current residual without binding to Unity or a telemetry SDK. Remote-entity interpolation and game-specific physics presentation remain outside this contract.

## Server topology control ports

Owner: `AiNative.Server.Control`; consumers: Server backend/room modules and Host composition roots. Accepted by [ADR-0017](../ADR/0017-single-region-service-topology.md). Exact signatures live in [ServicePorts.cs](../../server/src/Modules/AiNative.Server.Control/ServicePorts.cs); versioned wire bodies live in [backend.proto](../../shared/schemas/ainative/v1/backend.proto).

`IServiceRpc.CallAsync` accepts a `ServiceTarget`, stable method name, caller-owned serialized body, cancellation and optional delegated player identity. `IServiceHandler.HandleAsync` receives the adapter-authenticated `ServiceCallContext`. `IClientNotifier.NotifyAsync` sends bounded backend notifications; `IServiceConnectionObserver.DisconnectedAsync` releases connection-owned state. These are asynchronous control-plane ports outside fixed Tick. Fantasy types terminate at the transport adapter. Protobuf bodies belong to Shared Protocol, not Shared Gameplay.

Contract:

- Caller role and peer identity are established by signed adapter ingress. Request bodies cannot grant authority. Only Gate/Lobby can delegate an authenticated player; the Client role has no internal service privilege. Gate freezes a connection's player identity after authentication.
- `ServiceReply.Success` distinguishes a serialized success body from a stable error code. Cancellation or a transport timeout stops caller interest, but does not prove that the remote mutation did not happen. Retry allocation/settlement with the same identity and immutable payload; never allocate a replacement for an uncertain match.
- Fantasy control requests have a 64 KiB body limit, bounded outstanding calls/handlers/notifications and explicit unavailable/backpressure outcomes. Endpoint-specific limits can be stricter. Correlation and signed tracing metadata stay outside domain authority. See [operations](server-topology-operations.md) for configured budgets.
- Player owns login sessions, profiles, offline-verifiable tickets and settlement transactions. Tickets bind player, global room, Battle node and boot epoch. A confirmed settlement means the exact canonical result was committed once; conflicting duplicates fail.
- `player.settlement-status` reuses `SettlementQuery`/`SettlementReceipt`. Battle callers remain bound to the allocation's node identity; Gate callers must delegate an authenticated player in the Coordinator's exact match roster. Other roles, anonymous users and nonparticipants are denied. An existing allocation without a durable receipt returns `confirmed=false`. Clients bind confirmation to their original player and match, including after backend reconnect, rather than inferring it from aggregate profile counters.
- Lobby owns parties/readiness; Match owns queue entries/jobs. Queue admission is bound to the Match startup epoch and a short admission deadline. Cancellation of a request that may not have arrived installs a bounded tombstone, preventing a delayed join from restoring it. Match restart requires ready-state reconfirmation.
- Coordinator owns durable allocation identity and node ownership. `Reserved` is durable before Battle reservation; `Ready` requires Worker installation. Global match/room identity is independent of local Worker slots. Reserve/create/release/drain are fenced by Coordinator and Battle startup epochs. Room lists are paged; inventories are bounded.
- Battle owns live room state on one fixed Worker for the whole match. Result/replay persistence and retry run outside Tick. Result capacity is reserved before room admission; durable results are deleted only after exact Player confirmation. Replay quota rejection before creation is a definite failure and releases allocation; uncertain creation retains its reservation.
- Backend services access other services' data through these ports. Player and Coordinator own separate PostgreSQL schemas/migrations. Additive wire fields preserve existing v1 message IDs and the explicitly selectable legacy evaluation chain.

Behavior, failure recovery and local evidence are recorded in the [implementation ledger](server-topology-plan.md). Bounds are admission controls, not production capacity measurements.

## Supporting boundary contracts

### Local character presentation prototype

`AiNative.Client.Character` (UPM `com.ainative.client.character`, version 0.1.0) owns
client-only `CharacterInputFrame`, `CharacterMotionState`, `CharacterConfiguration` and
the `CharacterInputReader`, `CharacterMotor`, `CharacterOrbitCamera`, `CharacterAnimationDriver`
components. These Unity-bearing types are not Shared ports and may not be referenced by Server
or Shared. Explicit `Configure` methods wire references before Awake. All calls are main-thread only.

`CharacterInputReader.ConsumeFrame()` consumes action edges once and samples continuous input;
disable/focus loss clears input. Each reader owns/disposes its Input System asset. `CharacterMotor.State`
returns a value snapshot, while `Configuration` is an instance-owned mutable tuning object.
`Simulate(frame, cameraYawDegrees, deltaSeconds)` performs one local collision-constrained step
(metres/seconds, delta capped at .05 s); callers must not combine manual steps with automatic Update.
`CanStand` tests clearance, `Respawn` resets local motion and `CharacterOrbitCamera.Snap` resets
presentation smoothing. Runtime input, physics and animation remain isolated from network state.
`CharacterAnimationDriver.ConfigureAimPose` accepts an application-calibrated hand-local weapon
position/rotation; it blends from the configured hip pose when aiming, without changing physics.

The 0.1 prototype has no network/serialized-wire compatibility promise. Breaking changes must
update its application builder and tests together. See [local 3C design](character-3c-demo.md)
and the [package usage/lifecycle contract](../../packages/com.ainative.client.character/README.md).

The first slice also requires, but does not yet freeze method shapes for:

| Contract | Owner | Gate before signature acceptance |
| --- | --- | --- |
| `IReplicationEncoder` | Realtime replication | Protobuf/delta baseline measurement; ADR-0009 codec gate |
| `IContentManifestStore` | Client bootstrap/update | Atomic activation and crash-recovery Spike |
| `IGameplayDiagnostics` | Shared diagnostics abstraction | Zero-allocation disabled path and cardinality review |

Adding one of these APIs requires contract tests, lifecycle/threading notes, and an ADR/catalog update; implementations may remain internal until that gate passes.

## Control transport opcode constants (2026-10-21)

Owner: `shared/realtime`, namespace `AiNative.Realtime`. `GateTransportOpCodes`
exposes `Request`, `Response`, `Push`; `ServiceControlTransportOpCodes` exposes
`Request`, `Response`. All are immutable `const uint` wire identifiers shared by
Client and Server adapters. They contain no vendor types and add no dependency
edge. Values and migration constraints are documented in [the protocol guide](protocol-naming.md#opcode传输层消息编号).
They do not select business methods or authenticate callers. Changing an inlined
constant requires rebuilding both peers and a compatible protocol rollout.

## 客户端基础框架（ADR-0021）

依据 [ADR-0021](../ADR/0021-client-framework-foundation.md)。这些类型仅属于客户端；不修改 Shared、协议或固定 Tick 契约。公共异步结果为 `ValueTask`，取消为 `CancellationToken`。以下约定对应当前源码，不是平台验收结论。

| 所有者及源码 | 公开入口 | 线程、所有权和失败语义 |
| --- | --- | --- |
| [Core](../../packages/com.ainative.client.core/README.md) | `IClientModule.StartAsync/StopAsync`；`ClientApplication.StartAsync/StopAsync`、`IsRunning` | 同一所有者上下文调用；显式模块顺序启动，包含失败模块在内逆序清理；停止先取消启动并等待，受理后清理使用 None。重复停止共享结果，清理异常聚合，完成后可再次启动。 |
| Core MVVM | `BindableProperty<T>.Value`、`PropertyChanged`；`BindingScope.Add/Dispose` | 所属线程同步通知；相等值不通知；订阅所有权进入 BindingScope，逆序释放且聚合失败。 |
| Core MVVM | `AsyncCommand.ExecuteAsync/Cancel/Dispose/RefreshCanExecute`；`IsExecuting/CanExecute/Error` | 执行中拒绝重入；业务异常保存于 Error，取消为正常结束。Dispose 请求取消并断开通知，不等待业务结束；调用方仍须等待 ExecuteAsync 观察收尾。 |
| Core 诊断 | `IClientDiagnostics.Record/ReportException` | 事件和错误不携带凭据；调用上下文由所有者决定，不作为固定 Tick 热路径或业务成功前置条件。 |
| [Runtime](../../packages/com.ainative.client.runtime/README.md) | `SceneId`；`ISceneService.SwitchAsync/UnloadAsync`；`ClientSceneService` | Unity 主线程；逻辑 ID 由应用解析为构建场景路径。Additive 串行加载，成功后移除旧内容；取消等待原生操作结束并卸载迟到场景。启动场景不归该服务释放。 |
| [uGUI](../../packages/com.ainative.client.ui.ugui/README.md) | `IUIService.OpenAsync/CloseAsync/BackAsync`、`BlocksGameplay`；`UiService.Register/DisposeAsync` | Unity 主线程；每个 ID 单实例，按 Page/Modal/Overlay 分层，Back 跳过 Overlay。服务拥有成功打开的 UiView；关闭恢复可用焦点，工厂迟到结果释放后报告取消。 |
| uGUI | `UiView.DisposeAsync`；`UguiBindings.Bind`；`KeyedViewList.Apply/Dispose` | UiView 重复释放共享同一结果，应用提供完整释放回调。绑定 scope 只解绑自身监听，不自动销毁 ViewModel。列表按稳定键复用，重复键在修改前拒绝；销毁时聚合释放异常。 |
| [UniTask 适配](../../packages/com.ainative.client.async.unitask/README.md) | `UnityAsync.CompleteAsync/NextFrameAsync` | 主线程等待；原生操作不能被调用方取消，CompleteAsync 必须完成后由拥有者处理迟到资源。NextFrameAsync 支持取消。 |
| [VContainer 装配](../../packages/com.ainative.client.composition.vcontainer/README.md) | `ClientServiceScope.Resolve/CreateChild/Dispose` | 仅组合根接触 VContainer 注册类型；父子所有权显式，先停止异步服务，再释放子作用域，最后父作用域。同步 Dispose 幂等。 |

资源端口仍使用上文 `IAssetService`。ADR-0021 本期实现只加载随 Player 分发的本地 catalog/Bundle；不提供远程 manifest 激活、热更新或回滚服务。`TryGetLoaded` 成功也返回独立持有令牌；共享底层请求不共享释放所有权，单调用方取消不取消其他持有者的加载。实例应先销毁，再释放源句柄；预加载由服务作用域持有。

应用应显式等待所有释放操作，不能把异步 OnDestroy 当作唯一关闭入口。每个应用实例管理自己的 MultiplayerEventSystem 及 InputSystemUIInputModule，显式注入 UiService 和 View，playerRoot 限定到本实例 UI；文本输入、失焦和模态阻挡由应用输入入口统一判断。Runtime 的 `ClientFrameEvents` 组件在主线程同步转发 Update/FixedUpdate/LateUpdate 及 FocusChanged/PauseChanged，时间参数单位为秒，销毁时清空监听。订阅者应随自身作用域解绑；组件不驱动网络或预测，不能增加第二个 Tick 入口。
