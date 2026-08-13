# Architecture and Technology Baseline

Status: Baseline for the first vertical slice
Last updated: 2026-08-13
Decision source: WS-9 architecture discussion

This document consolidates the current architecture decisions for the AI-Native Unity Framework. Later Architecture Decision Records (ADRs) may supersede individual decisions. When that happens, the ADR is authoritative and this document must be updated.

## 1. Product constraints

The initial framework is optimized for the following target:

- Unity 6.3 LTS client.
- Android and iOS first, while retaining a PC build path.
- Multiplayer FPS/action gameplay.
- 64 players per match as the first capacity target.
- Authoritative server simulation at 60 ticks per second.
- 200 ms round-trip time is the degradation boundary, not the normal latency target. Regional deployments should target 100 ms RTT or less.
- The game server is independent of the Unity runtime. Physics and navigation are integrated separately.
- The server is built on a project-maintained, deeply customized fork of [Fantasy](https://github.com/qq362946/Fantasy).
- Client and server reuse the same Unity-independent gameplay simulation source code.
- Client asset building, delivery, update, and cache management are implemented by this project rather than Addressables.
- HybridCLR is a candidate for client code hot update.
- Infrastructure is fully self-hosted and cloud-provider-neutral.
- Client and server capabilities are build-time composable plugins. An unused plugin should not be installed or referenced.
- DOTS is not a global default. It is introduced only for a measured performance island.
- The framework must grow from validated needs rather than prebuilding speculative abstractions.

## 2. Architecture principles

### 2.1 Contract first

Schemas, protocol compatibility rules, generated data types, error codes, simulation inputs, and test vectors are defined before adapters and product integrations. Generated output must be reproducible and checked for drift in CI.

### 2.2 Build-time composition

Plugins are selected explicitly at build time. Runtime DLL scanning, reflection-based module discovery, and dynamic server module unloading are not part of the baseline. This keeps startup behavior visible and avoids Unity IL2CPP, iOS AOT, stripping, and deployment ambiguity.

### 2.3 Stable core, replaceable adapters

Gameplay code depends on narrow abstractions such as `IPhysicsWorld`, `INavigationWorld`, `IRealtimeTransport`, `IAssetService`, and `IGameplayClock`. Fantasy, Unity, Jolt, Recast/Detour, networking transports, storage backends, telemetry exporters, and hot-update runtimes stay outside the shared gameplay core and are connected through adapters or composition roots.

Replaceability means that a dependency can be changed without rewriting the gameplay model. It does not imply that state synchronization, prediction, or physics implementations can be swapped at zero cost.

### 2.4 Server authority

The server owns the canonical world state and validates gameplay commands. Clients predict local actions for responsiveness, interpolate remote entities, and reconcile against authoritative snapshots. The baseline does not use deterministic lockstep.

### 2.5 Evidence before expansion

New dependencies and distributed infrastructure require a measured need. Profiling and vertical-slice results decide whether to add DOTS, Redis, a message broker, Kubernetes/Agones, an alternative serializer, or a more complex Agent protocol.

## 3. Logical layers

```text
AI Agents
  Architecture · Context · Rules · Skills · Workflows
                         │
                  Project Context
                         │
       ┌─────────────────┼─────────────────┐
       │                 │                 │
     Client            Server            Tools
     Unity        Backend + GameServer   Editor + CLI
       │                 │                 │
       └─────────────────┼─────────────────┘
                         │
                       Shared
        Contracts · Gameplay · CodeGen · Test Vectors
                         │
                   Infrastructure
       Build · Test · CI/CD · Deploy · Observability
```

The intended repository structure is:

```text
client/UnityProject/              # Integration and sample Unity project
packages/                         # Reusable UPM packages and client adapters
server/src/Hosts/                 # Deployable process composition roots
server/src/Modules/               # Independent server modules and adapters
shared/schemas/                   # Protobuf/OpenAPI and compatibility policy
shared/generated/                 # Reproducible generated code
shared/gameplay/                  # Unity-independent shared simulation source
shared/test-vectors/              # Cross-runtime fixtures and golden vectors
tools/                            # .NET CLI, code generation, architecture checks
infrastructure/                   # CI, containers, deployment, observability
agents/                           # Context, rules, skills, and workflows
Docs/Architecture/                # Architecture documentation
Docs/ADR/                         # Architecture Decision Records
samples/                          # Minimal vertical slices and plugin examples
```

## 4. Dependency rules

- Shared gameplay must not reference `UnityEngine`, ASP.NET Core, Entity Framework, databases, sockets, filesystem APIs, Jolt P/Invoke, Recast bindings, rendering, UI, animation, or platform SDKs.
- Client and server may reference Shared. Shared must not reference Client, Server, Tools, Infrastructure, or Agents.
- Client runtime packages may depend on client abstractions and Shared, but not on server modules.
- Server modules may depend on server abstractions and Shared, but not on Unity packages.
- Tools may inspect and generate artifacts for other layers. Runtime modules must not depend on Tools.
- Infrastructure composes and deploys runtime artifacts but contains no gameplay rules.
- Agents operate through documented workflows and tools and must obey the same module boundaries as human contributors.
- `package.json`, asmdef, and `.csproj` references are the dependency graph's source of truth. CI should derive and validate the architecture graph from these files.

## 5. Client baseline

### 5.1 Runtime and presentation

- Unity 6.3 LTS with a mobile-first configuration.
- URP is the default rendering baseline unless a product profile requires another pipeline.
- Android and iOS release builds use IL2CPP. PC uses the same gameplay and package baseline.
- Quality profiles own platform-specific frame-rate, memory, texture, shader variant, and rendering budgets.
- GameObject/MonoBehaviour is the default gameplay presentation model.
- DOTS/Entities is an optional package for a profiling-proven performance island such as dense crowds, projectiles, or simulation batches.
- uGUI is the safe runtime UI baseline. UI Toolkit is preferred for editor tooling and can be evaluated separately for data-heavy runtime screens.

### 5.2 Package model

Client plugins are UPM packages with asmdef boundaries. A typical package contains:

```text
package.json
Runtime/
Editor/
Tests/
Samples~/
```

The application has an explicit Composition Root. Core does not directly reference optional implementations. Third-party packages such as a DI container, task library, inspector toolkit, analytics SDK, payment SDK, or push SDK are never unconditional Core dependencies.

Initial client packages should remain small:

- Core abstractions and runtime lifecycle.
- Diagnostics.
- Testing support.
- Shared gameplay integration.
- Custom networking adapter required by the vertical slice.
- Custom asset service required by the vertical slice.

Physics, navigation, HybridCLR, UI, DOTS, localization, platform services, and other integrations remain separate optional packages.

## 6. Server baseline

### 6.1 Runtime versions

- `.NET 8` (`net8.0`) remains the accepted development and production policy until a successor ADR is accepted.
- `.NET 9` (`net9.0`) remains the accepted compatibility lane, not a second production artifact.
- Deployable Hosts target one runtime at a time; the project does not publish duplicate Host artifacts without a deployment need.
- Reusable server libraries may multi-target `net8.0;net9.0` when publishing both assets is justified.
- Code shared by the .NET 8 and .NET 9 builds uses the .NET 8 API surface and C# 12 as its common baseline. Shared gameplay remains `netstandard2.1` and C# 9 for Unity dual compilation.
- The repository pins the SDK with `global.json`; local development, CI, and container builds use matching SDK versions.

.NET 8 and .NET 9 both reach the end of Microsoft support on 2026-11-10, and the original successor deadline has been missed. No Server project or Fantasy fork import may merge until a supported successor passes the ADR index gate. ADR-0012 proposes .NET 10 but is not accepted.

### 6.2 Fantasy foundation and fork policy

The server platform is based on a project-maintained, deeply customized fork of [Fantasy](https://github.com/qq362946/Fantasy). This replaces the earlier decision to treat Fantasy only as a Spike candidate.

Fantasy provides the initial server infrastructure for:

- Network sessions and the TCP, KCP, WebSocket, and HTTP protocol implementations.
- Scene and Entity lifecycles, the base server ECS model, and scheduling primitives.
- Gate, routing/Roaming, service discovery, and server-to-server messaging.
- Protocol generation, source generation, configuration, and server bootstrap tooling where they meet project requirements.

The project may customize the Tick scheduler, transport behavior, replication and backpressure paths, generated code, diagnostics, lifecycle behavior, and deployment integration. Product gameplay rules do not belong in the Fantasy fork. They remain in `Gameplay.Shared` or project server modules and access Fantasy through explicit adapters. This prevents the shared simulation from inheriting Fantasy, Unity, networking, persistence, or process-lifecycle types.

The fork follows these maintenance rules:

- Pin every adopted upstream version to a commit or tag and retain an `upstream` remote.
- Keep project changes reviewable through focused commits and a fork change log; do not scatter untracked edits through business modules.
- Integrate upstream changes deliberately, with compile, protocol-compatibility, replay, and load-test validation before promotion.
- Never update the production baseline by floating package version or an unpinned branch.
- Preserve required copyright and license notices.

In the locally reviewed Fantasy commit `6df3507b15737d86f93a36af1a6d28a2404e163d`, the core `Fantasy.Net` project conditionally exposes `net10.0`, but the Server Main/Entity/Hotfix projects, server templates, Benchmark, and bundled DotRecast projects target only .NET 8 and/or .NET 9. A [Windows compatibility Spike](runtime-successor-spike-2026-08-13.md) showed that adding the missing Host-graph TFMs allows a .NET 10 build and startup, while also exposing a runtime-neutral duplicate-config publish defect. End-to-end .NET 10 support remains unproven and must pass ADR-0012 before adoption. The repository license is based on the MIT text but adds an entity-specific restriction. Legal/license review remains a release gate before commercial distribution, redistribution of the fork, or publication of derived packages.

See [ADR-0001: Use Fantasy as the server foundation](../ADR/0001-fantasy-server-foundation.md) for the authoritative decision and validation conditions.

### 6.3 Process model

The first topology uses the smallest Fantasy-based composition that supports a modular backend and a separately deployable Battle Host:

- Fantasy provides network sessions, Gate/routing, Scene/Entity lifecycle, and server-to-server communication.
- ASP.NET Core modules may handle login, account, configuration, matchmaking, room allocation, and administrative APIs where its HTTP stack is the better fit.
- A Battle process/Scene owns the real-time loop. It does not run the 60 Hz simulation on ASP.NET request threads or a shared slow-operation scheduler.
- A Host references only the modules needed for that deployment.
- Modules own their public contracts, configuration, tests, and persistence migrations.
- Modules do not share internal database tables or implementation types.
- Cross-module communication uses public contracts or events.

Fantasy's distributed capabilities do not require the project to pre-split every backend concern into a microservice. The initial deployment remains intentionally small and expands only when the vertical slice or operations demonstrate a need.

### 6.4 Persistence

- PostgreSQL with EF Core is the default durable persistence candidate for backend services.
- Fantasy's optional MongoDB integration does not make MongoDB a mandatory project dependency; persistence remains behind module-owned interfaces.
- Redis is added only for a validated cache, presence, short-lived state, coordination, or locking use case.
- A message broker is not a baseline dependency.
- Battle simulation state stays in memory and is separated from slow persistence paths.

## 7. Shared gameplay code

The client and server compile the same gameplay source rather than copying or translating it.

```text
shared/gameplay/Runtime/**/*.cs
        ├─ Unity asmdef build
        └─ Gameplay.Shared.csproj -> netstandard2.1
                                      └─ referenced by net8.0/net9.0 server
```

The Shared project targets:

```xml
<TargetFramework>netstandard2.1</TargetFramework>
<LangVersion>9.0</LangVersion>
```

Unity 6 supports the .NET Standard 2.1 API profile, while .NET 8 and .NET 9 implement .NET Standard 2.1. A `net8.0` or `net9.0` server assembly must never be passed to Unity as a managed plugin.

Shared gameplay may contain:

- Input commands, Tick context, entity identifiers, and gameplay events.
- Attributes, buffs, abilities, weapons, cooldowns, damage, and state machines.
- Pure movement and combat rules executed for client prediction and server replay.
- Deterministic random-number utilities where required by the simulation contract.
- Configuration data types, state hashing, fixtures, and test vectors.

Shared gameplay accesses environment-dependent behavior through narrow interfaces. Unity and Server provide different implementations. If the client uses Unity PhysX while the server uses Jolt, logic remains reusable but physics results are not assumed to match; authoritative reconciliation is required. For tighter movement prediction, the client may use the same version and configuration of a constrained Jolt adapter.

The Shared Tick path must avoid nondeterministic or platform-dependent inputs such as wall-clock time, `System.Random`, uncontrolled async scheduling, thread order, filesystem state, and locale-sensitive behavior.

## 8. Real-time networking and replication

### 8.1 Simulation model

- Fixed authoritative simulation at 60 Hz, giving 16.67 ms per Tick.
- The core simulation P99 target is 8 ms or less, leaving budget for networking, scheduling variance, and diagnostics.
- Steady-state Tick processing targets zero managed allocations.
- The server retains approximately 200-250 ms of relevant history for lag compensation and hit validation.
- The client uses local prediction and reconciliation for the controlled entity and interpolation for remote entities.

### 8.2 Transport and replication

The first real-time data-plane implementation uses Fantasy's KCP/UDP stack behind `IRealtimeTransport`. The abstraction exposes delivery, ordering, congestion, timeout, and backpressure semantics rather than leaking Fantasy Session APIs into shared gameplay. The Unity client uses a thin Fantasy-compatible networking package or project adapter; generated wire contracts may be shared, but `Gameplay.Shared` does not reference Fantasy.

Fantasy's general request/response protocol is used for login, routing, reliable gameplay events, and service-to-service messages. The 64-player vertical slice may deeply customize or bypass general message serialization on the high-frequency snapshot path when measurements justify a schema/codegen-driven bit-packed codec. Such customization stays inside the networking/replication modules and must remain compatible with packet capture and replay tooling.

A 60 Hz simulation does not imply 60 complete snapshots per second:

- Client input is sampled/sent at up to 60 Hz.
- The first snapshot target is 20-30 Hz.
- Snapshot rate and content can adapt to priority, distance, bandwidth, congestion, and loss.
- Replication includes AOI/interest management, baselines, deltas, quantization, prioritization, and backpressure isolation.
- One slow or congested client must not stall the room Tick.

Protobuf is the primary contract format for login, matchmaking, reliable gameplay events, and service-to-service messages. High-frequency transform snapshots may use a schema/codegen-driven bit-packed codec with quantization and delta encoding. This exception must retain versioning rules, golden vectors, packet capture, and replay support.

## 9. Physics and navigation

### 9.1 Physics

Jolt Physics is the preferred Spike candidate. It is integrated as a native C++ library behind `IPhysicsWorld`, with a stable C ABI and a C# P/Invoke adapter. A third-party C# binding is not the Core contract.

Jolt deterministic behavior has constraints around identical inputs, call order, build options, queries, callback order, and result sorting. The project therefore does not rely on cross-platform lockstep and retains authoritative correction.

### 9.2 Navigation

Recast/Detour is the preferred navigation Spike candidate behind `INavigationWorld`:

- Recast produces navigation data during the build/tooling phase.
- Detour performs runtime navigation queries.
- Tile Cache and Crowd modules are optional according to game needs.
- Pathfinding jobs are asynchronous and must not consume the fixed 60 Hz simulation's hard budget.

## 10. Client asset pipeline

Addressables is explicitly outside the baseline. The project implements `IAssetService` and its own build/update pipeline with at least:

- Stable logical Asset IDs.
- Dependency graph construction and validation.
- Platform and quality-profile bundles.
- Versioned manifests and content hashes.
- Chunking and resumable downloads.
- Atomic activation and rollback.
- Cache quotas and eviction.
- Signature/integrity verification.
- Staged/gray releases.
- HTTP(S) and object-storage-compatible origin semantics so that storage and CDN vendors remain replaceable.

Gameplay and UI code depend on `IAssetService`, not bundle format, downloader, storage provider, or cache implementation details.

## 11. Code hot update

HybridCLR is an optional `hotupdate.hybridclr` package rather than a Core dependency.

- Boot, networking contracts, asset recovery, version checks, and rollback remain AOT-capable.
- Hot-update assemblies are limited to approved Gameplay/UI scopes.
- CI covers AOT generic supplements, managed-code stripping, Unity/HybridCLR compatibility, upgrade, rollback, Android, and iOS builds.
- A failed hot update must recover to a known-good version without preventing application startup.

Technical support for IL2CPP does not establish App Store policy compliance. Apple review rules restrict downloaded code that introduces or changes app features. Each iOS release must review the permitted hot-update scope, and the product must retain a resource/config-only update path plus normal App Store releases.

## 12. Tools and code generation

- `protoc` and standalone .NET tools are the preferred initial code-generation path.
- Generated C# is committed when that improves reproducibility for Unity and reviewed environments.
- CI regenerates artifacts and fails on drift.
- Roslyn Source Generators require explicit Unity compiler compatibility and must not be the only way to reproduce a generated artifact.
- Unity Editor tooling uses custom EditorWindow/PropertyDrawer/UI Toolkit integrations as needed; Odin is optional, not a Core dependency.
- Unity Test Framework and NUnit cover EditMode, PlayMode, integration, and performance tests.
- Server tests use the standard .NET test stack.

## 13. Infrastructure

The baseline is self-hosted and cloud-neutral:

- Git and Git LFS for source and large binary assets.
- GitHub Actions as the initial CI orchestrator, with scripts designed to remain runnable locally or in another CI system.
- Docker and Docker Compose for server builds and early environments.
- PostgreSQL as the default durable datastore candidate.
- OpenTelemetry for application telemetry.
- Prometheus, Grafana, Loki, and Tempo as self-hosted observability candidates.
- Portable object-storage/HTTP interfaces for client content delivery.

Kubernetes, Agones, Redis, message brokers, hosted Unity Gaming Services, and provider-specific managed services are deferred until load tests or operations demonstrate a need.

## 14. Agent engineering system

Agents are first-class engineering participants, but are not game runtime dependencies. The repository will provide:

- Agent-readable architecture and project context.
- Coding and architecture rules.
- Explicit module boundaries and generated dependency graphs.
- Machine-readable configuration and schemas.
- Reusable Agent skills.
- Standard development and review workflows.
- Automated compile, test, lint, schema, and architecture validation.
- Generate-validate-repair loops with auditable tool use.

MCP is the preferred boundary for development tools and context resources. A2A and a complex Agent orchestration framework are deferred until a concrete cross-agent interoperability requirement exists.

## 15. First vertical slice and acceptance criteria

The first implementation slice is:

```text
Start -> Login -> Match -> Allocate room ->
64 bots join -> Move/fire at 60 Tick ->
Replicate state -> Disconnect/reconnect -> Record/replay
```

Acceptance measurements:

- Stable 60 Hz simulation; Tick P99 does not exceed 16.67 ms and core simulation P99 targets 8 ms or less.
- Zero steady-state managed allocations in the Tick hot path.
- Scenarios at 100 ms and 200 ms RTT, 1% and 5% loss, jitter, duplication, and reordering.
- Measured prediction error, correction frequency/magnitude, hit-validation behavior, and reconnect recovery.
- Per-client upstream/downstream bandwidth, snapshot size, AOI entity count, CPU, memory, and physics cost.
- No single-client backpressure can delay room simulation.
- Unity and .NET execute the same input vectors for N ticks and compare critical state hashes.
- Shared gameplay compiles through both Unity Batch Mode and the `netstandard2.1` project.
- Server builds, tests, publishes, and runs under the supported successor matrix accepted before the first Server merge.
- Protocol generation and compatibility tests pass; generated artifacts have no drift.

The results decide the final snapshot frequency, transport library, replication codec, Jolt binding, client prediction physics, and rooms-per-process target.

## 16. Explicitly deferred or rejected defaults

The following are not baseline commitments:

- Global DOTS adoption.
- NGO followed by an assumed migration to Netcode for Entities.
- Addressables.
- Consuming an unpinned or unreviewed Fantasy release directly in production.
- Placing product gameplay rules inside the Fantasy fork or exposing Fantasy types from `Gameplay.Shared`.
- MemoryPack as a second general wire protocol before profiling.
- Reflection-based plugin discovery or runtime server plugin unloading.
- Redis, a message broker, Kubernetes, or Agones without measured need.
- Unity Gaming Services or another cloud-provider-specific runtime dependency.
- A2A or a complex Agent orchestration framework without a concrete workflow.
- .NET 10 for Server Hosts before the proposed successor gate passes.

## 17. Required follow-up ADRs

The following decisions should be captured as individual ADRs before or during the vertical slice:

1. Repository layout and module dependency policy.
2. Shared gameplay source compilation through Unity and .NET Standard 2.1.
3. Fantasy fork ownership, customization boundaries, upstream synchronization, and license release gate. Accepted as [ADR-0001](../ADR/0001-fantasy-server-foundation.md).
4. Server runtime policy and support-expiry upgrade plan. [ADR-0012](../ADR/0012-server-runtime-successor.md) proposes .NET 10, but remains open pending full Fantasy server-stack evidence.
5. Authoritative 60 Hz simulation, client prediction, reconciliation, and lag compensation.
6. Fantasy KCP transport and replication validation criteria.
7. Jolt integration boundary and client prediction strategy.
8. Recast/Detour build and runtime boundary.
9. Custom asset manifest, bundle, signing, rollout, and rollback format.
10. HybridCLR scope, failure recovery, and iOS policy gate.
11. Protocol evolution, generated artifacts, and high-frequency codec exception.
12. Self-hosted observability and deployment baseline.
13. Agent context, rules, and automated architecture validation.

## 18. References

- [Unity .NET profile support](https://docs.unity3d.com/current/Manual/dotnet-profile-support.html)
- [Unity custom packages](https://docs.unity3d.com/current/Manual/CustomPackages.html)
- [Unity UI system comparison](https://docs.unity3d.com/current/Manual/UI-system-compare.html)
- [Microsoft .NET lifecycle](https://learn.microsoft.com/lifecycle/products/microsoft-net-and-net-core)
- [Microsoft .NET Standard compatibility](https://learn.microsoft.com/dotnet/standard/net-standard)
- [Protocol Buffers C# generated code](https://protobuf.dev/reference/csharp/csharp-generated/)
- [Jolt Physics](https://github.com/jrouwe/JoltPhysics)
- [Jolt deterministic simulation constraints](https://github.com/jrouwe/JoltPhysics/blob/master/Docs/Architecture.md#deterministic-simulation)
- [Recast Navigation](https://github.com/recastnavigation/recastnavigation)
- [Fantasy](https://github.com/qq362946/Fantasy)
- [Fantasy.Net core project targets at the reviewed commit](https://github.com/qq362946/Fantasy/blob/6df3507b15737d86f93a36af1a6d28a2404e163d/Fantasy.Packages/Fantasy.Net/Fantasy.Net.csproj)
- [Fantasy license](https://github.com/qq362946/Fantasy/blob/main/LICENSE)
- [HybridCLR](https://github.com/focus-creative-games/hybridclr)
- [Apple App Review Guidelines](https://developer.apple.com/app-store/review/guidelines/#software-requirements)
