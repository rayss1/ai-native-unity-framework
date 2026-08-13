# ADR-0009: Use Protobuf-First Compatible Protocol Evolution

Status: Accepted
Date: 2026-08-13
Decision source: WS-11

## Context

Client, server, tools, replay, and packet inspection need one reproducible contract process. Mobile rollouts require mixed versions. High-frequency snapshots may eventually need tighter encoding without creating a second uncontrolled protocol system.

## Decision

Schemas live under `shared/schemas`; generated outputs and golden vectors are reproducible artifacts. Protobuf is the primary format for login, matchmaking, reliable gameplay events, and service-to-service messages. `protoc` plus pinned standalone .NET tooling is the baseline generator path.

Evolution is additive within a supported protocol major: never reuse field numbers, names, enum values, message IDs, or error codes; reserve removed identifiers; add optional fields with backward-safe defaults; preserve unknown fields where the runtime permits; and avoid changing semantic units without a new field. Breaking changes require a new major/capability and a compatibility window.

Generated code is committed when Unity reproducibility benefits, and CI regenerates then fails on diff. A Roslyn generator may improve developer experience but cannot be the sole source of an artifact Unity/build tooling needs.

A bit-packed high-frequency codec is an exception, not a second general protocol. It must have a declarative versioned schema, generated readers/writers, bounds checks, golden byte vectors, fuzz/property tests, packet inspection, replay support, and capability negotiation. It is introduced only if Protobuf plus AOI/delta/quantization misses the packet/bandwidth or CPU budgets.

## Consequences

Mixed-version behavior is explicit and testable. Compatibility discipline adds schema review and retained fixtures. Codec optimization remains possible without leaking hand-coded layouts across modules.

## Validation, migration, and rollback

- CI performs generation-drift, previous-reader/new-writer, new-reader/previous-writer, unknown-field, malformed-input, and golden-byte tests for every supported protocol range.
- A high-frequency codec must improve the failing budget by at least 20% in the constrained metric without regressing its encoding/decoding Tick budget or correctness gates.
- Migration advertises capabilities, dual-decodes during the support window, and records per-version telemetry before changing the default encoder.
- Rollback restores the prior encoder while retaining the new decoder. A deployed field/ID is reserved forever even after rollback.
