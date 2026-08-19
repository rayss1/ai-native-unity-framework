# .NET 10 Fantasy Runtime Successor Recovery

Status: Cross-platform recovery evidence for proposed ADR-0012
Original Spike: 2026-08-13
Recovery update: 2026-08-19
Reviewed public source: `rayss1/Fantasy` `493d5d4dd1dd009cdfcd2846b88ebab9746d4504`
Merged candidate: [rayss1/Fantasy#1](https://github.com/rayss1/Fantasy/pull/1), fork commit `40159864408067f97de3ad569e3a559b597f6d38`

## Scope

WS-13 reconstructs the lost .NET 10 candidate from the current public Fantasy baseline and prepares a reproducible Windows/Ubuntu validation matrix. It does not accept ADR-0012 or claim legal, container, protocol/replay, observability, graceful-shutdown, impairment, allocation, or 64-player load evidence.

## Candidate patch queue

The candidate was merged as five focused commits on top of `493d5d4`:

| Commit | Purpose |
| --- | --- |
| `af75127` | Pin .NET SDK `10.0.202`; migrate supported projects/templates to `net10.0`; retain the Source Generator at `netstandard2.0`; fix nullable/Span warnings and dependency pins. |
| `cd36b88` | Keep the application-owned `Fantasy.config` authoritative during publish and regenerate the .NET 10-only tracked package. |
| `68b9a56` | Add `Fantasy.Net.Tests` to `Fantasy.sln` with package-asset and project/package consumer publish regressions. |
| `0c64c2a` | Add Windows/Ubuntu CI, runtime/config/startup checks, Control Center SQLite smoke coverage, and machine-readable vulnerability auditing. |
| `773b67c` | Deduplicate the Source Generator project instance after Windows exposed a parallel-build file lock. |

Benchmark and Console samples are retained as unsupported historical examples and are not included in the build or validation matrix.

## Local results

The workstation had macOS and .NET SDK `10.0.200`; it did not have the pinned `10.0.202` SDK, Docker, or a Linux runtime. Commands were run from outside the checkout so the available SDK could validate the source. These are local feasibility results, not substitutes for the committed CI matrix.

| Check | Result | Evidence and limitation |
| --- | --- | --- |
| `Fantasy.sln` Release build | Pass, 0 warnings/errors | Supported tooling/runtime projects compiled as .NET 10; Source Generator remained `netstandard2.0`. |
| `examples/Server/Server.sln` Release build | Pass, 0 warnings/errors | Main, Entity, Hotfix, Fantasy.Net, Source Generator, and the complete DotRecast graph compiled. |
| Package/config regression tests | Pass, 3 tests | The package exposes only a `net10.0` library asset; fresh project-reference and package-reference consumers each publish exactly one application config. |
| Tracked NuGet artifact | Pass | Contains `lib/net10.0/Fantasy-Net.dll`, analyzer content, and build/buildTransitive assets; no other runtime TFM. |
| Host publish/runtime config | Pass | Publish selected `net10.0` and contained exactly one `Fantasy.config`. |
| Host startup | Partial | All six configured scenes started and logged `Process:1 Startup Complete SceneCount:6`; graceful signal termination did not complete promptly and the process required a forced stop. |
| Control Center SQLite smoke | Pass | Initialized `data/fantasy-control.db`, listened on `127.0.0.1:5277`, and answered the HTTP probe. |
| Dependency vulnerability audit | Pass locally | Both solutions reported no known direct or transitive vulnerable packages. |
| Windows/Ubuntu GitHub Actions | Pass | [Run 32242324689](https://github.com/rayss1/Fantasy/actions/runs/32242324689) passed both complete jobs on SDK `10.0.202`. The first run exposed a Windows-only Source Generator file lock; `773b67c` fixed the project graph and the full rerun passed. |

## Parent repository integration state

The parent branch `codex/ws-13-runtime-successor-recovery` adds the HTTPS submodule at `server/vendor/Fantasy` and fixes its gitlink to the remotely fetchable merge commit `40159864408067f97de3ad569e3a559b597f6d38`. No floating branch is configured.

The parent integration:

- does not add Fantasy to `AiNative.sln` and introduces no product Server Host or project reference;
- treats `server/vendor/Fantasy` as an opaque vendor prefix in architecture validation, with a regression fixture containing internal Solution/project/source files;
- initializes submodules in parent CI and rejects uninitialized, conflicted, or drifting recursive status;
- documents recursive clone, existing-clone initialization, and exact-SHA update commands;
- keeps the parent SDK pin and .NET 8/.NET 9 validation matrix unchanged.

## Remaining gates

1. Validate Linux container/release startup and graceful shutdown.
2. Add and pass Shared vectors, protocol compatibility, replay, impairment, allocation, backpressure, observability, and 64-player load suites.
3. Complete license/legal review before distributing the fork or derived package.

Until these gates pass, ADR-0012 remains Proposed and the submodule is evaluation source only.
