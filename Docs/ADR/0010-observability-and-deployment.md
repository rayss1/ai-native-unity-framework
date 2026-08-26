# ADR-0010: Use OpenTelemetry and Cloud-Neutral Deployments

Status: Accepted
Date: 2026-08-13
Decision source: WS-11

## Context

The vertical slice needs evidence about Tick, networking, correction, content, and failures without coupling product code to a hosted vendor or allowing telemetry to disturb simulation.

## Decision

OpenTelemetry APIs/SDK adapters form the application telemetry boundary. Prometheus, Grafana, Loki, and Tempo are the initial self-hosted candidates, not dependencies of gameplay or domain modules. Export is asynchronous, bounded, sampled where appropriate, and cannot block a room Tick.

Runtime modules emit stable semantic events/measurements through a diagnostics abstraction. Required correlation keys include build/protocol identity, deployment, process, room, Tick range, connection/session (privacy-safe), and trace context. Player identifiers and payload contents are excluded by default. Metric labels must come from a reviewed bounded-cardinality allowlist.

Docker and Docker Compose are the first deployment baseline. Build/test/deploy scripts remain runnable locally and avoid cloud-specific contracts. Kubernetes, Agones, Redis, and brokers require measured operational need and separate decisions.

Battle Host publication is a separate manual operation after an exact successful `main` production-validation run. It publishes an immutable Linux x64 digest with source/Fantasy/protocol/configuration/base-image identities, an SBOM, build provenance, and a verifiable artifact attestation. Version and source tags are immutable aliases; deployment and rollback select digests, never `latest`.

Health is split into liveness, readiness, and dependency/room-drain state. Graceful shutdown stops admission, drains or transfers supported sessions, flushes only within a bounded deadline, and then terminates. Deployments retain a last-known-good immutable image and compatible configuration.

## Consequences

Evidence is portable and backends remain replaceable. Cardinality, privacy, buffers, and exporter failure modes require governance.

## Validation, migration, and rollback

- With collectors unavailable or slow, Tick and memory budgets must remain green and buffers must stay bounded; dropped telemetry is counted locally.
- Load tests verify label cardinality, sampling, trace volume, dashboard queries, alert signals, drain behavior, and immutable image/config provenance.
- Backend migration duplicates export through adapters for a bounded comparison period without changing instrumentation contracts.
- Rollback selects the previous image/config/exporter. Runtime correctness must not depend on telemetry availability.
- The [Battle Host release procedure](../Architecture/battle-host-release.md) verifies the qualified run before publication, promotes the version tag only after digest-level smoke/attestation checks, and retains a machine-readable release manifest.
- The [telemetry and one-room capacity validation](../Architecture/telemetry-capacity-validation.md) compares exporter-disabled and unavailable-exporter profiles on the same release-equivalent image. It enforces bounded project series, non-blocking bounded trace buffering, locally counted failure/drop behavior, and the `< 0.25 ms` Tick P99 increment gate.

The first publication completed on 2026-08-25: [`v0.1.0`](../../infrastructure/battle-host/releases/v0.1.0.json) binds qualified source `10b787c73cb78d13bbf5d45e9a6a1253fc88d75e` to immutable digest `sha256:a350b8329d142a07026ac0f0bb28a67baf106cfae3fcb1e292f0cfe17fdb7d5c`. [Release run 32830738674](https://github.com/rayss1/ai-native-unity-framework/actions/runs/32830738674) passed digest smoke, attestation verification, and version-tag resolution.

The telemetry/cardinality contract subsequently passed on exact `main` source `de84ee2563bb959fca1d36d90fd188e745ffc5cf` with Fantasy `f8bed0d464924f159d46498f1311206ea0694be8`. [Production-validation run 32883119254](https://github.com/rayss1/ai-native-unity-framework/actions/runs/32883119254) retained the one-room comparison and 60-minute soak: exporter-outage Tick P99 regression was `0 ms` against the strict `< 0.25 ms` gate, all 332 metric and the single trace export attempts failed as intended, no trace record or metric series was dropped/overflowed, and the soak completed 216,004 measured Ticks at P99/P99.9 `0.7143/0.9897 ms` with zero slow Ticks and zero Gameplay allocation. The [full evidence record](../Architecture/telemetry-capacity-validation.md) preserves identities and capacity measurements. Multi-room deployment sizing and an environment canary with a named rollback target remain open; this validation did not publish or deploy an image.

The [two-room capacity contract](../Architecture/multi-room-capacity-validation.md) keeps the production default at one room and exposes the 128-client topology only through evaluation configuration. Exact-`main` [run 32946412201](https://github.com/rayss1/ai-native-unity-framework/actions/runs/32946412201) passed the 128-client Tick, Gameplay, CPU, memory, connection, input-rate, bandwidth, and datagram gates with the required 20% headroom. ADR-0015 replaces ambiguous new version 1 captures with room-aware version 2 while retaining version 1 read compatibility. Replay-enabled exact-main capacity evidence and the environment canary with named candidate/fallback digests and operator authority remain independent gates; no result here authorizes deployment or changes the production default.
