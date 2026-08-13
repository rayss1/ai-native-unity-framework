# ADR-0004: Use .NET 8 by Default with .NET 9 Compatibility

Status: Accepted
Date: 2026-08-13
Decision source: WS-11

## Context

Fantasy supports the selected runtimes, but deployable artifacts need one predictable baseline. Both selected versions have a finite support window, so compatibility cannot substitute for an upgrade plan.

## Decision

Server Hosts target `net8.0` for local development and production. `net9.0` is a compile/test compatibility lane, not a second production artifact unless deployment evidence requires it. Shared server libraries may multi-target only when both assets have a consumer.

Common server code uses the .NET 8 API surface and C# 12. .NET 9/C# 13-specific behavior belongs in an isolated adapter with a .NET 8 implementation. `global.json` pins the SDK feature band and CI/container images pin matching versions.

The project must accept a supported successor runtime in a superseding ADR by 2026-06-30 and complete production migration before the current support expiry recorded in the technology baseline.

## Consequences

One production target reduces artifact and operational variance while a compatibility lane exposes upgrade blockers early. The project carries a scheduled runtime migration.

## Validation, migration, and rollback

- Every server change builds and tests on .NET 8 and .NET 9; replay/golden-vector tests are part of both lanes.
- Dependency updates must support `net8.0` or be isolated; warnings introduced only in one lane are reviewed, not suppressed globally.
- Successor migration uses dual-built libraries, a canary Host image, protocol/data compatibility checks, then staged promotion.
- Rollback restores the previous Host image and SDK pin. Database and protocol changes deployed during migration must remain backward compatible until rollback expires.
