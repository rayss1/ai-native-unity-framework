# Architecture Decision Records

This directory is the decision log for the first vertical slice. An accepted ADR is authoritative for its subject; the architecture baseline summarizes the set but does not override it.

## Status model

- **Proposed**: review is still required; implementations must not depend on it as a stable contract.
- **Accepted**: the contract is frozen for the first vertical slice.
- **Superseded**: a newer ADR replaces it and links back to it.
- **Rejected**: retained to prevent the same option from being reconsidered without new evidence.

Changing an accepted contract requires a superseding ADR that records compatibility impact, migration, rollback, and evidence. Measurements may tune values explicitly marked as provisional without changing the invariant around them.

## Index

| ADR | Status | Decision | Frozen invariant | Evidence still required |
| --- | --- | --- | --- | --- |
| [0001](0001-fantasy-server-foundation.md) | Accepted | Fantasy server foundation | Fantasy stays behind adapters; product gameplay stays outside the fork | Fork pin, legal review, vertical-slice gates |
| [0002](0002-repository-layout-and-module-dependencies.md) | Accepted | Repository layout and dependencies | Six layers, manifest-derived dependency graph, build-time plugins | First architecture-check implementation |
| [0003](0003-shared-gameplay-dual-compilation.md) | Accepted | Shared gameplay dual compilation | One source set, Unity and `netstandard2.1`, identical golden vectors | Unity Batch Mode proof and state-hash corpus |
| [0004](0004-server-runtime-policy.md) | Accepted | .NET runtime policy | `net8.0` default, `net9.0` compatibility, C# 12 common server baseline | Successor decision overdue; no new Server Host before the gate passes |
| [0005](0005-authoritative-simulation-and-reconciliation.md) | Accepted | 60 Hz authority model | Server authority, fixed Tick, prediction/reconciliation, no lockstep | Tuning of history and correction thresholds |
| [0006](0006-realtime-transport-and-replication.md) | Accepted | Realtime transport and replication | Fantasy KCP adapter first; AOI/delta/backpressure are mandatory | Snapshot rate, codec, bandwidth and room density |
| [0007](0007-physics-and-navigation-boundaries.md) | Accepted | Physics and navigation boundaries | Jolt/Recast candidates behind Shared-owned ports | Binding, prediction and path-job benchmarks |
| [0008](0008-client-content-and-hot-update.md) | Accepted | Content and hot update | Project-owned atomic content pipeline; HybridCLR optional | Manifest format Spike and per-release iOS policy review |
| [0009](0009-protocol-evolution-and-code-generation.md) | Accepted | Protocol evolution | Protobuf-first, additive compatibility, reproducible generation | High-frequency codec threshold and compatibility harness |
| [0010](0010-observability-and-deployment.md) | Accepted | Observability and deployment | OTel boundary, self-hosted/cloud-neutral deployment | Cardinality/load test and deployment sizing |
| [0011](0011-agent-engineering-and-architecture-enforcement.md) | Accepted | Agent engineering | Repository-owned context plus automated boundary checks | First validator and drift-repair workflow |
| [0012](0012-server-runtime-successor.md) | Proposed | .NET 10 successor candidate | Evaluation submodule allowed; no product reference or production Host until all acceptance gates pass | Exact merged fork and Windows/Ubuntu CI passed; graceful shutdown, Linux container release, replay, load, legal, and rollout remain |

## Open decision gates

These are deliberately not final product choices. Each owner must record the resulting evidence in a new or superseding ADR before crossing the named gate.

The runtime successor deadline has passed. [ADR-0012](0012-server-runtime-successor.md) records `.NET 10` as the preferred candidate. The [recovered fork commit](../Architecture/runtime-successor-spike-2026-08-13.md) is remotely pinned and passes the SDK 10.0.202 Windows/Ubuntu build, test, publish, startup, SQLite, and vulnerability matrix. Graceful shutdown, Linux container release, protocol/replay, load, legal, and rollout gates remain open; product references remain forbidden. ADR-0012 stays Proposed and does not supersede ADR-0004.

| Decision | Default during Spike | Decision gate | Pass/trigger threshold | Migration and rollback |
| --- | --- | --- | --- | --- |
| Fantasy upstream revision and fork release | Exact evaluation gitlink, never a floating branch | Before a product project references the fork or any derived artifact is distributed | Build matrix, replay/load gates pass; legal review complete before distribution | Rebase focused patches or remove/revert the gitlink; replace only the failing subsystem behind adapters |
| Snapshot frequency and codec | 20 Hz, schema-generated representation; increase toward 30 Hz only within budget | 64-player impairment test | Budgets in `performance-budgets.md`; introduce bit-packing only when schema/delta tuning cannot meet them | Capability-negotiated codec version; retain prior decoder and manifest-selectable rollout |
| Jolt binding and client prediction physics | Stable C ABI/P/Invoke; Unity physics remains presentation/query fallback | Movement/combat prediction Spike | Physics budget and correction/error bands hold on Android, iOS, and server | Swap adapter; keep authoritative correction and a non-Jolt client path |
| Recast/Detour options | Offline Recast build, runtime Detour query | Representative navigation bake/load/path test | Build reproducibility and async path latency budgets hold | Regenerate versioned nav data or replace the adapter; retain prior nav artifact |
| HybridCLR adoption | Not installed | Release-specific hot-update Spike | AOT/stripping/rollback/mobile tests pass and policy review approves scope | Disable optional package and activate last-known-good AOT/resource-only release |
| Rooms per process | One measured room per worker baseline | Soak and failure-isolation test | Add density only while all Tick, memory, and blast-radius budgets remain green | Reduce scheduler concurrency or return to one room per worker |
| Runtime successor | `net8.0` production policy and `net9.0` compatibility remain the parent skeleton baseline; evaluation submodule only | Before the first production Server Host or product reference to Fantasy; original 2026-06-30 deadline missed | Selected supported LTS builds, publishes, and starts the exact merged Fantasy composition and passes Shared vectors, replay, load, and operational gates | Accept a successor ADR before migration; retain the skeleton or remove the evaluation submodule if the candidate fails |
