# .NET 10 Fantasy Runtime Successor Spike

Status: Partial evidence for proposed ADR-0012
Date: 2026-08-13
Reviewed source: local `qq362946/Fantasy` commit `6df3507b15737d86f93a36af1a6d28a2404e163d`
Candidate follow-up: local migration commit `c6c3f06f4ae54af122b24bac1d7dd1048445123b`, validated 2026-08-14

## Scope

Determine whether the reviewed Fantasy Server solution has an immediate source, build, publish, or startup blocker on .NET 10. The Spike used an isolated clone under the ignored repository `artifacts/` directory and did not modify the user's Fantasy checkout.

This is not a production-acceptance result. It does not cover Linux, CI, containers, protocol/replay compatibility, load, allocation, impairment, shutdown, observability, native platforms, or long-running behavior.

## Baseline finding

The reviewed source does not declare end-to-end .NET 10 support:

- `Fantasy.Net` conditionally includes `net10.0` when built by a .NET 10 SDK.
- Server `Main`, `Entity`, and `Hotfix`, the server templates, Benchmark, and bundled DotRecast projects omit `net10.0`.
- `examples/Server/Server.sln` includes `Fantasy.Net`, `Main`, `Entity`, `Hotfix`, the Source Generator, and the DotRecast project graph, so the omitted targets affect the real example-server composition.
- The checkout contains no .NET test project. `Fantasy.Benchmark` is outside the solutions and references the nonexistent `../Fantays.Console/Fantasy.Console.csproj`, so it cannot supply migration/load evidence in its current state.

## Method

- Checked out the exact reviewed commit in an isolated clone.
- Pinned the clone to installed SDK `10.0.202` and isolated its build output.
- Added `net10.0` only to the Server Host dependency graph; kept `Fantasy.SourceGenerator` on `netstandard2.0`.
- Built `examples/Server/APP/Main/Main.csproj` for `net10.0` in Release, then built the complete `examples/Server/Server.sln` matrix after adding the candidate TFM to every Server/DotRecast project.
- Published both `net9.0` and `net10.0` as a control pair.
- Started the .NET 10 framework-dependent publish with `-m Develop`, observed initialization for 10 seconds, and then stopped the probe process.

## Results

| Check | Result | Evidence and interpretation |
| --- | --- | --- |
| .NET 10 Release build | Pass with 13 warnings and 0 errors | Main, Entity, Hotfix, Fantasy.Net, Source Generator, and required DotRecast projects compiled. Warnings were DotRecast `CA2265` Span/null diagnostics and existing nullable-field warnings in example Entity/Hotfix code. |
| Complete Server solution matrix | Pass | All 12 solution projects, including Crowd, Dynamic, Extras, and TileCache projects not required directly by Main, built after adding the candidate TFM; the existing net8/net9 lanes also remained buildable. |
| Fantasy tooling solution under SDK 10.0.202 | Pass with 0 warnings and 0 errors | `Fantasy.sln` built its declared framework matrix. Fantasy.Cli and Fantasy.Net produced net10 assets; ControlCenter and protocol tools remained on their declared net8 targets. |
| Unmodified publish model | Fail on both `net9.0` and `net10.0` | `NETSDK1152` reported duplicate `Fantasy.config` from `Entity` and `Fantasy.Net`; the identical net9 control proves this is not introduced by .NET 10. |
| Publish after isolated config fix | Pass on both `net9.0` and `net10.0` | The Spike prevented the `Fantasy.Net` package-template config from entering project-reference publish output while retaining the application-owned config. This is a required fork/build-system correction independent of the successor TFM. |
| .NET 10 runtime selection | Pass | `Main.runtimeconfig.json` requested `net10.0`, `Microsoft.NETCore.App` 10.0.0, and `Microsoft.AspNetCore.App` 10.0.0. |
| Controlled startup | Pass | The Host loaded local `Fantasy.config`, opened configured TCP/KCP/HTTP listeners, logged `Process:1 Startup Complete SceneCount:6`, and remained alive until the probe stopped it after 10 seconds. |

## Conclusion

The reviewed Fantasy checkout does not support .NET 10 as a declared, tested server matrix. However, the exact commit has no immediate source-level or startup incompatibility in the exercised Windows path: adding the missing TFMs and correcting the pre-existing publish-content conflict was sufficient to build, publish, and start the example Host.

This lowers the estimated migration cost but does not satisfy ADR-0012. The ADR remains Proposed until the exact project-owned fork and Host pass the untested evidence gates. The config publication fix also requires a deliberate fork patch with regression tests; it must not be hidden as a runtime-specific workaround.

## Candidate migration follow-up

The local Fantasy `dev_study` branch subsequently implemented the candidate as four focused commits on top of the reviewed upstream revision, ending at `c6c3f06f4ae54af122b24bac1d7dd1048445123b`:

- supported core packages, tools, Server Main/Entity/Hotfix, templates, and the bundled DotRecast graph target `net10.0`;
- `Fantasy.SourceGenerator` remains `netstandard2.0` as a compiler/analyzer compatibility component;
- the package-template `Fantasy.config` no longer enters project-reference publish output, while the application-owned config remains;
- the tracked `Fantasy-Net.2026.1.1001.nupkg` contains only `lib/net10.0/Fantasy-Net.dll`;
- `Tmds.DBus.Protocol` is pinned to `0.21.3` and `SQLitePCLRaw.bundle_e_sqlite3` to `2.1.12`, eliminating the two high-severity transitive vulnerability findings present during the first migration build;
- the already removed Console platform is not reintroduced; its orphan Benchmark and Console examples are marked as unsupported historical code.

### Follow-up results

| Check | Result | Evidence and interpretation |
| --- | --- | --- |
| Fantasy tooling solution | Pass, 0 warnings and 0 errors | All supported tooling projects build under SDK 10.0.202; package vulnerability scan reports no known vulnerable packages. |
| Complete Server solution | Pass, 0 warnings and 0 errors | Main, Entity, Hotfix, Fantasy.Net, Source Generator, and the complete DotRecast graph produce .NET 10 artifacts. |
| Host publish | Pass | The runtime config selects `net10.0`; publish output contains exactly one application-owned `Fantasy.config`. |
| Controlled Host startup | Pass | Develop mode opened the configured TCP/KCP/HTTP listeners and logged `Process:1 Startup Complete SceneCount:6`; the probe then stopped the process. |
| Tracked NuGet artifact | Pass | Package inspection found only the .NET 10 library asset, analyzer, and build/buildTransitive config assets. |
| Independent package consumer | Pass, 0 warnings and 0 errors | A fresh ignored .NET 10 console project restored the local package and built successfully. |
| Package/config regression tests | Pass, 3 tests | An uncommitted `Fantasy.Net.Tests` follow-up verifies the tracked package has only the .NET 10 library asset and fresh project-reference and package-reference publishes each contain one application-owned config. It is not yet part of the pinned candidate commit. |
| Windows/Ubuntu CI definition | Prepared, not run | An uncommitted GitHub Actions matrix restores and builds the tooling solution, runs the regression tests, builds the Server example, and audits dependencies on both operating systems. It cannot provide evidence until committed and pushed to a remotely fetchable fork. |
| Control Center SQLite smoke test | Pass | Control Center initialized its SQLite database and listened on `127.0.0.1:5277` with the patched SQLitePCLRaw line; the probe then stopped the process. |
| Linux/container | Not run | Docker is not installed and WSL has no distribution on the validation workstation. |

The candidate commit is local-only and therefore is not yet a reproducible fork pin for CI or another developer. The follow-up test project and CI workflow are also uncommitted. The three packaging regressions do not substitute for protocol compatibility, Shared vectors, replay, graceful shutdown, observability, impairment, or load suites.

## Remaining gates

1. Commit the initial package/config tests and cross-platform workflow, publish the exact candidate fork state, then require the Windows/Ubuntu matrix to pass.
2. Reproduce publish/start on the intended Linux container base image with warnings-as-errors.
3. Create project-owned tests for the fork and Host; add protocol compatibility, Shared vectors, replay, graceful shutdown, and observability suites on .NET 10.
4. Replace the orphan Benchmark with a project-owned load harness, then run the 64-player impairment workload and compare Tick, allocation, memory, bandwidth, and tail latency against the baseline.
5. Record the minimal fork patch queue and upstream synchronization cost, and complete the license review before distributing the fork or its package.
