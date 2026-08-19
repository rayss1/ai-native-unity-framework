# ADR-0012: Evaluate .NET 10 LTS as the Server Runtime Successor

Status: Proposed
Date: 2026-08-13
Last evidence update: 2026-08-19
Decision source: WS-13
Proposes to supersede: [ADR-0004](0004-server-runtime-policy.md)

## Context

ADR-0004 selected `.NET 8` for production Hosts and `.NET 9` as a compatibility lane, with a successor decision due by 2026-06-30. That gate was missed, and both runtimes reach end of support on 2026-11-10. The project must select a supported successor before introducing a production Server Host.

.NET 10 is the preferred LTS candidate, but the complete Fantasy composition—not only `Fantasy.Net`—must build, publish, start, and satisfy project operational and gameplay gates. The recovery baseline is [rayss1/Fantasy `493d5d4`](https://github.com/rayss1/Fantasy/commit/493d5d4dd1dd009cdfcd2846b88ebab9746d4504).

WS-13 rebuilt the candidate on branch `codex/ws-13-dotnet10` and merged [Fantasy PR #1](https://github.com/rayss1/Fantasy/pull/1). The parent repository pins the resulting fork `main` commit `40159864408067f97de3ad569e3a559b597f6d38`. It:

- pins SDK `10.0.202` and migrates the supported runtime, tools, example Server, templates, and bundled DotRecast projects to `net10.0`/C# 14;
- keeps the Source Generator on `netstandard2.0` and leaves orphan Benchmark/Console examples outside the supported matrix;
- corrects `Fantasy.config` publication so both project and package consumers publish exactly one application-owned configuration;
- pins `Tmds.DBus.Protocol` `0.21.3` and `SQLitePCLRaw.bundle_e_sqlite3` `2.1.12`;
- regenerates the tracked package with only a `lib/net10.0` runtime asset while preserving analyzer and MSBuild assets;
- adds three NUnit package/config regressions and a Windows/Ubuntu GitHub Actions workflow.
- removes conflicting Source Generator project-instance metadata after the first Windows run exposed a parallel-build file lock.

Local validation on macOS used installed SDK `10.0.200` because the pinned `10.0.202` SDK was unavailable locally. Both solutions built in Release with zero warnings/errors, all three tests passed, publish selected .NET 10 and produced one application configuration, the Host reached `Startup Complete`, Control Center initialized SQLite and listened, and dependency scans found no known vulnerabilities. The Host did not exit promptly on the exercised termination signal and required a forced stop, so graceful shutdown remains open.

The final [Windows/Ubuntu CI run](https://github.com/rayss1/Fantasy/actions/runs/32242324689) passed restore, warnings-as-errors builds of both Solutions, all three regressions, Host publish/start and runtime/config assertions, Control Center SQLite smoke, and direct/transitive vulnerability auditing on SDK `10.0.202`. This establishes reproducible cross-platform migration evidence, but does not satisfy the remaining release, gameplay, load, shutdown, observability, or legal gates.

## Proposed decision

Adopt `.NET 10` (`net10.0`) and C# 14 for future Server Hosts only when the required evidence below passes for the exact merged Fantasy fork commit and project-owned Server composition. Shared gameplay remains `netstandard2.1` with C# 9; no .NET 10 assembly is imported into Unity.

This ADR remains Proposed and does not supersede ADR-0004. The exact, unreferenced Fantasy evaluation source may be present at `server/vendor/Fantasy` behind the architecture-check ignore boundary. Until this ADR is accepted, neither `AiNative.sln` nor any production Server Host or product project may reference the submodule.

The parent `global.json` and existing .NET 8/.NET 9 skeleton matrix remain unchanged.

## Required evidence

Completed recovery evidence: the focused fork commits are merged and pinned by exact SHA, and the committed SDK `10.0.202` Windows/Ubuntu matrix is green.

Remaining acceptance evidence:

1. Reproduce publish, startup, graceful shutdown, and observability on the intended Linux container/release path.
2. Pass Shared vectors, protocol compatibility, replay, impairment, allocation, and backpressure tests.
3. Pass the 64-player load and Tick budgets on release-equivalent Linux artifacts.
4. Complete the Fantasy license/legal review before commercial distribution, redistribution, or publication of derived artifacts.

## Acceptance, migration, and rollback

If all evidence passes, change this ADR to Accepted, mark ADR-0004 Superseded, and migrate the parent SDK/CI/container baseline before the first production Server project merges.

If the evidence fails, keep the Shared/Tools skeleton, isolate or replace the failing subsystem behind existing adapters, or select another supported runtime in a replacement ADR. The evaluation submodule can be rolled back by removing its gitlink, `.gitmodules` entry, CI initialization, documentation, and architecture-ignore rule.

## References

- [Runtime successor Spike and recovery evidence](../Architecture/runtime-successor-spike-2026-08-13.md)
- [Fantasy recovery baseline](https://github.com/rayss1/Fantasy/commit/493d5d4dd1dd009cdfcd2846b88ebab9746d4504)
- [Fantasy recovery PR](https://github.com/rayss1/Fantasy/pull/1)
- [Green Windows/Ubuntu validation run](https://github.com/rayss1/Fantasy/actions/runs/32242324689)
- [Pinned Fantasy fork commit](https://github.com/rayss1/Fantasy/commit/40159864408067f97de3ad569e3a559b597f6d38)
- [Microsoft .NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy)
- [Microsoft .NET lifecycle](https://learn.microsoft.com/lifecycle/products/microsoft-net-and-net-core)
- [.NET 10 breaking changes](https://learn.microsoft.com/dotnet/core/compatibility/10)
