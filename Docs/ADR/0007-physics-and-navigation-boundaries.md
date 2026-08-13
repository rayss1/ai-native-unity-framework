# ADR-0007: Isolate Physics and Navigation Behind Shared Ports

Status: Accepted
Date: 2026-08-13
Decision source: WS-11

## Context

The authoritative server cannot depend on Unity runtime physics/navigation. Gameplay still needs testable queries, while native integrations, asynchronous jobs, and cross-platform numerical differences require explicit ownership.

## Decision

Shared owns `IPhysicsWorld` and `INavigationWorld` plus engine-neutral value types. The interfaces are query/command ports scoped to a simulation world; they do not expose Unity, Jolt, Recast/Detour, native handles, callbacks, or allocator-owned memory.

Jolt is the preferred server physics Spike. Native code exposes a versioned stable C ABI, and a server adapter owns P/Invoke, thread affinity, callback normalization, result sorting, and disposal. Unity PhysX may serve client presentation and prediction initially; no cross-engine lockstep is assumed. A constrained client Jolt adapter is adopted only if prediction measurements justify its mobile cost.

Recast runs offline in Tools to produce versioned navigation artifacts. Detour performs runtime queries behind the navigation port. Runtime path requests are asynchronous relative to the fixed Tick: a Tick submits a request or consumes a prior result but never blocks on a path job. Tile Cache and Crowd remain optional modules.

All unordered native query results are normalized to a documented stable ordering before gameplay consumes them. World lifecycle is explicit and disposal is owned by the composition root.

## Consequences

Gameplay can be tested with deterministic fakes and native technology can change independently. Translation, lifecycle management, and reconciliation remain adapter responsibilities.

## Validation, migration, and rollback

- Physics Spike covers representative movement, projectile, ray/shape queries, contacts, memory, and mobile/server interop. It must meet the budgets in `performance-budgets.md` without steady-state Tick allocations.
- Navigation Spike proves reproducible bake hashes, versioned load rejection, and P95/P99 path latency without blocking Tick.
- Adapter migration runs the same query corpus against old/new implementations and compares normalized results within documented tolerances.
- Rollback selects the previous adapter and previous versioned nav artifact. Authoritative correction remains enabled throughout client physics changes.
