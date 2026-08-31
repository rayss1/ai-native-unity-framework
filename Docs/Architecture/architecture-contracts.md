# First Vertical Slice Architecture Contracts

Status: Frozen
Decision source: WS-11
Last updated: 2026-08-31

This page is the entry point for implementing the first vertical slice. It completes the follow-up ADR list in section 17 of the technology baseline; the accepted ADRs are authoritative where the baseline still describes an item as follow-up work.

## Goal

Provide one implementation entry condition for the `Start -> Login -> Match -> Allocate room -> 64 bots -> Move/fire -> Replicate -> Reconnect -> Record/replay` slice: stable module direction, minimum public ports, measurable budgets, explicit evidence gates, and recoverable migrations.

## Non-goals

- Creating empty repository/module scaffolding before its first owned artifact.
- Finalizing codec, snapshot rate, client prediction physics, nav options, HybridCLR adoption, or rooms per process without vertical-slice evidence.
- Referencing Fantasy outside the accepted Server adapter/Battle Host boundary and the dedicated `com.ainative.client.fantasy` adapter, or changing the pinned runtime/fork without a superseding ADR and equivalent evidence.
- Adopting deterministic lockstep, global DOTS, Addressables, runtime plugin discovery, Kubernetes/Agones, Redis, a broker, or a hosted cloud dependency as a default.
- Freezing private implementation types. Only documented public contracts, ownership, dependency direction, lifecycle/threading behavior, compatibility policy, and gates are stable.

## Required reading by change type

| Change | Required contract |
| --- | --- |
| Any new module, package, project, or reference | [Dependency matrix](dependency-matrix.md) and [ADR-0002](../ADR/0002-repository-layout-and-module-dependencies.md) |
| Shared gameplay or public port | [Public API catalog](public-api-contracts.md), [ADR-0003](../ADR/0003-shared-gameplay-dual-compilation.md), and cross-runtime vectors |
| Server Host/runtime dependency | [ADR-0001](../ADR/0001-fantasy-server-foundation.md) and [ADR-0012](../ADR/0012-server-runtime-successor.md); ADR-0004 is historical |
| Tick, prediction, replay, or lag compensation | [ADR-0005](../ADR/0005-authoritative-simulation-and-reconciliation.md) and [performance budgets](performance-budgets.md) |
| Transport, replication, or protocol | [ADR-0006](../ADR/0006-realtime-transport-and-replication.md), [ADR-0009](../ADR/0009-protocol-evolution-and-code-generation.md), and impairment gates |
| Physics or navigation | [ADR-0007](../ADR/0007-physics-and-navigation-boundaries.md) |
| Content delivery or hot update | [ADR-0008](../ADR/0008-client-content-and-hot-update.md) |
| Deployment or telemetry | [ADR-0010](../ADR/0010-observability-and-deployment.md) |
| Agent rules or architecture automation | [ADR-0011](../ADR/0011-agent-engineering-and-architecture-enforcement.md) |

The complete status and open evidence gates are in the [ADR index](../ADR/README.md). Known technical and delivery risks are tracked in the [risk register](risk-register.md).

## Stage gate

Downstream implementation may assume the six-layer dependency direction, minimum public ports, build-time plugin composition, fixed authoritative 60 Hz model, protocol compatibility rules, asset atomicity, and diagnostics boundaries are stable. It may not treat provisional choices as final without satisfying the evidence gate in the ADR index.

Any incompatible public contract or dependency-direction change requires a superseding ADR with affected consumers, compatibility window, migration sequence, evidence, rollback, and validator updates. Provisional numeric tuning may change through a measured report only where its ADR explicitly permits it.
