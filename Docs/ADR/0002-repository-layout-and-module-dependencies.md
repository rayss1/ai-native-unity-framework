# ADR-0002: Freeze Repository Layout and Module Dependencies

Status: Accepted
Date: 2026-08-13
Decision source: WS-11

## Context

The framework must support Client, Server, Shared, Tools, Infrastructure, and Agents without allowing convenience references to turn those areas into a monolith. Unity AOT constraints also make runtime discovery and implicit plugin loading unsafe defaults.

## Decision

The canonical top-level layout is the one in `Docs/Architecture/technology-baseline.md`: `client`, `packages`, `server`, `shared`, `tools`, `infrastructure`, `agents`, `Docs`, and `samples`. New top-level production roots require an ADR update.

The six logical layers and their permitted dependencies are frozen in `Docs/Architecture/dependency-matrix.md`. Shared is the innermost runtime layer. Tools may read or generate artifacts but are never runtime dependencies. Infrastructure deploys artifacts and contains no gameplay rules. Agents consume repository context and tools but are not product runtime dependencies.

Client plugins are UPM packages with explicit `package.json` and asmdef references. Server plugins are projects/modules with explicit `.csproj` references and Host registration. A Composition Root selects plugins at build time. Runtime assembly scanning, reflection-based module discovery, and dynamic module unloading are excluded.

Every optional integration has an abstractions assembly/project, an implementation package/module, and composition-root wiring. The abstraction must not reference the implementation. A package absent from manifests must contribute no transitive runtime reference.

`package.json`, `.asmdef`, `.csproj`, and solution/build manifests are the dependency graph source of truth. CI will derive a graph and reject forbidden edges, cycles across module boundaries, Editor-to-Runtime leakage, and generated-artifact drift.

## Consequences

The layout makes dependency direction machine-verifiable and lets projects ship only selected capabilities. It adds manifests and adapter projects, and prevents shortcuts such as referencing Unity or Fantasy types from Shared.

## Validation, migration, and rollback

- The first validator must parse all manifest types, compare them with the matrix, and pass on a valid fixture while rejecting one fixture for each forbidden edge.
- Initial directory creation is incremental: create a root only with its first owned artifact; do not add empty scaffolding.
- Existing code that violates a boundary moves behind an adapter in small commits. Temporary compatibility shims must have an owner and removal issue.
- Rollback reverts the offending module/import, not the dependency rule. Changing the rule requires a superseding ADR and updated validator fixtures.
