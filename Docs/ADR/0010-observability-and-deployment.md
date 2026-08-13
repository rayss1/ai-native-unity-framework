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

Health is split into liveness, readiness, and dependency/room-drain state. Graceful shutdown stops admission, drains or transfers supported sessions, flushes only within a bounded deadline, and then terminates. Deployments retain a last-known-good immutable image and compatible configuration.

## Consequences

Evidence is portable and backends remain replaceable. Cardinality, privacy, buffers, and exporter failure modes require governance.

## Validation, migration, and rollback

- With collectors unavailable or slow, Tick and memory budgets must remain green and buffers must stay bounded; dropped telemetry is counted locally.
- Load tests verify label cardinality, sampling, trace volume, dashboard queries, alert signals, drain behavior, and immutable image/config provenance.
- Backend migration duplicates export through adapters for a bounded comparison period without changing instrumentation contracts.
- Rollback selects the previous image/config/exporter. Runtime correctness must not depend on telemetry availability.
