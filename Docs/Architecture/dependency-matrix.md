# Module Dependency Matrix

Status: Frozen for the first vertical slice
Authority: ADR-0002
Last updated: 2026-08-31

This document defines compile-time and runtime dependency direction. “May depend” still requires an explicit manifest reference and a concrete need; it is not a default reference.

## Six-layer matrix

Rows are consumers and columns are dependencies.

| Consumer \\ Dependency | Client | Server | Shared | Tools | Infrastructure | Agents |
| --- | --- | --- | --- | --- | --- | --- |
| **Client** | Within package rules | Forbidden | Allowed | Forbidden at runtime | Forbidden | Forbidden |
| **Server** | Forbidden | Within module rules | Allowed | Forbidden at runtime | Forbidden | Forbidden |
| **Shared** | Forbidden | Forbidden | Within acyclic submodules | Forbidden | Forbidden | Forbidden |
| **Tools** | Inspect/generate only | Inspect/generate only | Allowed for test/codegen models | Within tool rules | Forbidden as product dependency | May read context |
| **Infrastructure** | Compose artifacts only | Compose artifacts only | No source dependency | May invoke pinned tools | Within deployment code | May read runbooks |
| **Agents** | Read/change via workflow | Read/change via workflow | Read/change via workflow | Invoke documented tools | Read/change via workflow | Within scoped rules |

“Inspect/generate” and “compose” are build/development relationships, not runtime assembly references. Agents are engineering participants, not a product process or library.

## Canonical ownership

| Root | Owns | Must not own |
| --- | --- | --- |
| `client/UnityProject` | Application composition, scenes, product integration | Reusable framework package implementation |
| `packages` | UPM runtime/editor/test packages and client adapters | Server modules or product-specific scene content |
| `server/src/Hosts` | Deployable composition roots and configuration binding | Gameplay rules or module internals |
| `server/src/Modules` | Server abstractions, adapters, backend/battle modules | Unity types or another module's persistence internals |
| `shared/schemas` | Wire schemas, IDs, compatibility declarations | Transport/session implementations |
| `shared/generated` | Reproducible generated artifacts | Handwritten gameplay rules |
| `shared/gameplay` | Unity-independent simulation values, rules, ports | Engine/framework/I/O concerns |
| `shared/test-vectors` | Cross-runtime fixtures and golden vectors | Environment-specific expected output |
| `tools` | CLI, codegen, architecture/build/test tooling | Shipped runtime behavior |
| `infrastructure` | CI, images, deployment, observability configuration | Gameplay/domain rules |
| `agents` | Agent context, rules, skills, workflows | Runtime code or secrets |
| `samples` | Minimal composition examples and vertical slices | Canonical framework implementation |

## Intra-layer rules

### Client

- Each plugin is a UPM package with Runtime, optional Editor, Tests, and Samples boundaries.
- Runtime asmdefs never reference Editor asmdefs. Editor may reference its package Runtime assembly.
- Core abstractions reference Shared; optional implementations reference Core abstractions and their third-party SDK.
- The Unity application Composition Root references selected implementation packages. Core never references them back.
- An optional package missing from `Packages/manifest.json` and package manifests contributes no assembly reference.
- `com.ainative.client.prediction` is the first concrete package under `packages`: it depends only on Shared Gameplay/Realtime contracts; generated Protobuf compatibility code is confined to its Unity-ignored `.NET` test source.
- `com.ainative.client.fantasy` is the only Client package allowed to reference `Fantasy` namespaces. Its Runtime assembly depends on `AiNative.Realtime` and pinned `Fantasy.Unity`; Fantasy Session, generated registration, KCP, and socket types terminate there. The prediction package and Unity application Composition Root consume only project-owned contracts and application state.

### Server

- Hosts reference module public contracts/registration packages, never module internals. The Battle Host may reference the pinned `Fantasy-Net` package only for compile-time generated startup metadata; handwritten Fantasy runtime namespace usage remains inside `AiNative.Server.Fantasy`.
- A module owns configuration, persistence schema/migrations, and public contracts. Modules do not share tables or implementation types.
- Cross-module calls use public contracts/events. Cycles are forbidden; orchestration belongs in a Host/application module.
- Fantasy, ASP.NET Core, EF Core, native physics/navigation, and telemetry SDK types terminate at adapters/composition roots.
- Battle Tick code does not call slow persistence, blocking I/O, exporter, or navigation-build paths.

### Shared

- Recommended direction: `Contracts/Primitives -> Gameplay Model -> Gameplay Rules`; test support depends inward only.
- Shared must not reference `UnityEngine`, Fantasy, ASP.NET Core, EF Core, databases, sockets, filesystem APIs, native P/Invoke, UI/rendering, platform SDKs, wall-clock time, or uncontrolled scheduling.
- Per ADR-0013, `shared/realtime` may use `CancellationToken`, `ValueTask`, and `IAsyncDisposable` as passive boundary types; active scheduling and I/O remain forbidden, and `shared/gameplay` receives no exception.
- Public ports are owned by the consumer policy layer (Shared); adapters are owned by Client/Server.
- Generated wire types do not become the gameplay domain model by default. Translation occurs at a boundary.

## Plugin contract

Every plugin declares:

1. Stable package/module ID and owner.
2. Public abstractions/contracts and their compatibility policy.
3. Explicit manifest dependencies.
4. Registration entry point called by a Composition Root—never discovered by runtime scanning.
5. Lifecycle and disposal behavior, threading constraints, configuration schema, diagnostics, and tests.
6. Whether it is AOT/IL2CPP safe and which platforms/runtimes it supports.

## Automated enforcement

CI derives the graph from `package.json`, `.asmdef`, `.csproj`, solution/build manifests, and schema generator configuration. It fails on forbidden edges, cross-module cycles, Editor-to-Runtime edges, direct Shared forbidden APIs, Fantasy namespace use outside `AiNative.Server.Fantasy` and `com.ainative.client.fantasy`, disallowed Fantasy package references, runtime references to Tools/Infrastructure/Agents, or undocumented new public contracts.

See [ADR-0002](../ADR/0002-repository-layout-and-module-dependencies.md) for migration and rollback rules.
