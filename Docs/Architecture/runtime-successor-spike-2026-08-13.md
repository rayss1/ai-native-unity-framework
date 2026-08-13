# .NET 10 Fantasy Runtime Successor Spike

Status: Partial evidence for proposed ADR-0012
Date: 2026-08-13
Reviewed source: local `qq362946/Fantasy` commit `6df3507b15737d86f93a36af1a6d28a2404e163d`

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

## Remaining gates

1. Reproduce restore/build/publish/start on Linux CI and the intended container base image with warnings-as-errors policy decided explicitly.
2. Add tests that guarantee one application-owned `Fantasy.config` in publish output for both project-reference and packaged consumption.
3. Create project-owned tests for the fork and Host because the reviewed checkout has no .NET test projects; add protocol compatibility, Shared vectors, replay, shutdown, and observability suites on .NET 10.
4. Repair or replace the currently broken `Fantasy.Benchmark` project, then run the 64-player impairment/load workload and compare Tick, allocation, memory, bandwidth, and tail latency against the current baseline.
5. Pin the fork, record the minimal TFM/config changes, and assess their upstream synchronization cost.
