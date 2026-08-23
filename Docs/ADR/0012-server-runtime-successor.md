# ADR-0012: Evaluate .NET 10 LTS as the Server Runtime Successor

Status: Proposed
Date: 2026-08-13
Last evidence update: 2026-08-19
Decision source: WS-13
Proposes to supersede: [ADR-0004](0004-server-runtime-policy.md)

## Context

ADR-0004 selected `.NET 8` for production Hosts and `.NET 9` as a compatibility lane, with a successor decision due by 2026-06-30. That gate was missed, and both runtimes reach end of support on 2026-11-10. The project must select a supported successor before introducing a production Server Host.

.NET 10 is the preferred LTS candidate, but the complete Fantasy composition—not only `Fantasy.Net`—must build, publish, start, and satisfy project operational and gameplay gates. The recovery baseline is [rayss1/Fantasy `493d5d4`](https://github.com/rayss1/Fantasy/commit/493d5d4dd1dd009cdfcd2846b88ebab9746d4504).

WS-13 rebuilt the candidate on branch `codex/ws-13-dotnet10` and merged [Fantasy PR #1](https://github.com/rayss1/Fantasy/pull/1). WS-14 then merged [Fantasy PR #2](https://github.com/rayss1/Fantasy/pull/2), and the parent repository pins fork `main` commit `b65e6fd60224cf264a3ee62207f0f9041e9f6d92`. It:

- pins SDK `10.0.202` and migrates the supported runtime, tools, example Server, templates, and bundled DotRecast projects to `net10.0`/C# 14;
- keeps the Source Generator on `netstandard2.0` and leaves orphan Benchmark/Console examples outside the supported matrix;
- corrects `Fantasy.config` publication so both project and package consumers publish exactly one application-owned configuration;
- pins `Tmds.DBus.Protocol` `0.21.3` and `SQLitePCLRaw.bundle_e_sqlite3` `2.1.12`;
- regenerates the tracked package with only a `lib/net10.0` runtime asset while preserving analyzer and MSBuild assets;
- adds three NUnit package/config regressions and a Windows/Ubuntu GitHub Actions workflow.
- removes conflicting Source Generator project-instance metadata after the first Windows run exposed a parallel-build file lock.

Local validation on macOS used installed SDK `10.0.200` because the pinned `10.0.202` SDK was unavailable locally. Both solutions built in Release with zero warnings/errors, all three tests passed, publish selected .NET 10 and produced one application configuration, the Host reached `Startup Complete`, Control Center initialized SQLite and listened, and dependency scans found no known vulnerabilities. WS-14 added cancellation-aware host lifetime, Unix SIGTERM/SIGINT and Windows Ctrl+C handling, main-scheduler pumping during asynchronous disposal, and bounded NLog shutdown.

The final [Windows/Ubuntu CI run](https://github.com/rayss1/Fantasy/actions/runs/32242324689) passed restore, warnings-as-errors builds of both Solutions, all three regressions, Host publish/start and runtime/config assertions, Control Center SQLite smoke, and direct/transitive vulnerability auditing on SDK `10.0.202`. This establishes reproducible cross-platform migration evidence, but does not satisfy the remaining release, gameplay, load, shutdown, observability, or legal gates.

The WS-14 [Windows/Ubuntu CI run](https://github.com/rayss1/Fantasy/actions/runs/32248783092) additionally passed a real Linux SIGTERM probe: after startup, the example Host completed Scene/Process disposal, emitted `Shutdown Complete`, and exited with code zero within ten seconds without a forced stop. Parent [container run 32278435820](https://github.com/rayss1/ai-native-unity-framework/actions/runs/32278435820) then built that exact fork commit with SDK image digest `sha256:adc02be8b87957d07208a4a3e51775935b33bad3317de8c45b1e67357b4c073b`, ran it on ASP.NET runtime image digest `sha256:8b75cdf59a5068d9adfd8a6d202cc7671b2dc8f5f46c51e3b88a0a632e8fad1f` as the image-defined non-root user, reached `Startup Complete`, and exited normally on SIGTERM within ten seconds. The run published source, SDK, runtime-image, evaluation-image, and protocol identities as provenance. Integrated readiness/drain, replay/load, observability, and legal gates remain open.

## Proposed decision

Adopt `.NET 10` (`net10.0`) and C# 14 for future Server Hosts only when the required evidence below passes for the exact merged Fantasy fork commit and project-owned Server composition. Shared gameplay remains `netstandard2.1` with C# 9; no .NET 10 assembly is imported into Unity.

This ADR remains Proposed and does not supersede ADR-0004. The exact, unreferenced Fantasy evaluation source may be present at `server/vendor/Fantasy` behind the architecture-check ignore boundary. Until this ADR is accepted, neither `AiNative.sln` nor any production Server Host or product project may reference the submodule.

The parent `global.json` and existing .NET 8/.NET 9 skeleton matrix remain unchanged.

## Required evidence

Completed recovery evidence: the focused fork commits are merged and pinned by exact SHA, and the committed SDK `10.0.202` Windows/Ubuntu matrix is green.

Remaining acceptance evidence:

1. Add project-owned readiness/drain and observability to the release-equivalent Linux candidate; container publish/start/non-root/SIGTERM and provenance gates are complete.
2. Pass Shared vectors in both .NET and Unity, protocol compatibility, replay, impairment, allocation, and backpressure tests. While ADR-0014 is active, Unity proof is an exact-commit manual evidence bundle rather than an automatic CI result.
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
- [Pinned Fantasy fork commit](https://github.com/rayss1/Fantasy/commit/b65e6fd60224cf264a3ee62207f0f9041e9f6d92)
- [Graceful-shutdown PR](https://github.com/rayss1/Fantasy/pull/2)
- [Green graceful-shutdown matrix](https://github.com/rayss1/Fantasy/actions/runs/32248783092)
- [Green parent Linux container and provenance run](https://github.com/rayss1/ai-native-unity-framework/actions/runs/32278435820)
- [Green parent submodule and .NET 8/.NET 9 validation run](https://github.com/rayss1/ai-native-unity-framework/actions/runs/32242925933)
- [Microsoft .NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy)
- [Microsoft .NET lifecycle](https://learn.microsoft.com/lifecycle/products/microsoft-net-and-net-core)
- [.NET 10 breaking changes](https://learn.microsoft.com/dotnet/core/compatibility/10)
