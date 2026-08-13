# ADR-0011: Treat Agent Context and Architecture Checks as Repository Contracts

Status: Accepted
Date: 2026-08-13
Decision source: WS-11

## Context

Autonomous agents can only make safe changes when boundaries, workflows, evidence, and ownership are discoverable and machine-checkable. Prompt-only knowledge drifts and is not reviewable with code.

## Decision

Architecture, coding rules, module ownership, workflows, skills, and machine-readable constraints are versioned in the repository. `AGENTS.md` provides scoped operating instructions; deeper rationale belongs in Docs/ADR and Docs/Architecture. Generated context names its sources and generation command.

Agents and humans use the same manifests, formatters, builds, tests, schema checks, and architecture validators. A claimed result must cite executed evidence. Automated repair may edit only its declared scope, preserves unrelated work, and stops after a bounded number of failed repair cycles.

Architecture validation derives the real graph from `package.json`, asmdef, `.csproj`, schemas, and designated ownership/config files. It enforces ADR-0002 boundaries, Shared forbidden APIs, Editor/Runtime separation, generated drift, protocol compatibility, and required documentation for public contract changes.

Development-tool integrations use MCP as the preferred boundary when a structured tool/resource is needed. A2A or a dedicated multi-agent orchestration framework remains deferred until an interoperability scenario cannot be handled by current issue/workflow contracts.

Agent artifacts are part of Tools/Agents, never linked into shipped Client or Server runtime assemblies.

## Consequences

Decisions become reviewable and automation can stop architectural drift. Maintaining context and validators is ongoing product work; green checks do not replace human review of high-risk changes.

## Validation, migration, and rollback

- The first architecture validator includes positive fixtures and one negative fixture per rule, produces actionable paths/edges, and runs locally and in CI.
- Context freshness checks fail when a generated artifact differs from its sources. Agent workflows record actual commands and unresolved risk without secrets.
- Rules migrate warning-first only when legacy violations already exist, with an issue and deadline; new violations fail immediately.
- Rollback disables a faulty validator version while retaining the documented boundary. Changing a boundary requires a superseding ADR, fixture update, and migration plan.
