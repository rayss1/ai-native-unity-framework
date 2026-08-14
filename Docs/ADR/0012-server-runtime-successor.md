# ADR-0012: Evaluate .NET 10 LTS as the Server Runtime Successor

Status: Proposed
Date: 2026-08-13
Last evidence update: 2026-08-14
Decision source: WS-13
Proposes to supersede: [ADR-0004](0004-server-runtime-policy.md)

## Context

ADR-0004 selected `.NET 8` for production Hosts and `.NET 9` as a compatibility lane, with a successor decision due by 2026-06-30. That decision gate was missed. Microsoft support for both versions ends on 2026-11-10, so the project must resolve the successor before introducing a Server Host.

.NET 10 is an active LTS release supported through 2028-11-14 and is the preferred successor candidate. A current runtime alone is not sufficient: the exact Fantasy fork, server composition, source generators, navigation dependencies, publish path, and workload must support it.

The locally reviewed Fantasy checkout is `qq362946/Fantasy` commit `6df3507b15737d86f93a36af1a6d28a2404e163d` dated 2026-08-08. It does not demonstrate end-to-end .NET 10 server support:

- `Fantasy.Packages/Fantasy.Net/Fantasy.Net.csproj` conditionally exposes `net10.0` only when evaluated by a .NET 10 SDK.
- `examples/Server/APP/Main`, `Entity`, and `Hotfix` target only `net8.0;net9.0`.
- the `Skills/fantasy-net/templates` server projects target only `net8.0`.
- `Fantasy.Benchmark` and the bundled server-side DotRecast projects target only `net8.0;net9.0`.

Therefore the core package declaration is migration evidence for one component, not proof that the Fantasy server stack supports .NET 10.

A Windows compatibility Spike against this exact commit subsequently added `net10.0` to the example Host dependency graph without changing production source. The complete Server solution matrix built, and the tooling solution built its declared targets under SDK 10.0.202. The unmodified Host publish failed on both `net9.0` and `net10.0` because `Entity/Fantasy.config` and `Fantasy.Net/Fantasy.config` collide; after an isolated, runtime-neutral config-publication correction, both targets published and the .NET 10 Host completed startup. The checkout has no .NET tests, and its standalone Benchmark project has a missing project reference. This is positive feasibility evidence, not acceptance evidence. See the [Spike report](../Architecture/runtime-successor-spike-2026-08-13.md).

A follow-up local candidate branch based on that upstream commit is pinned at `c6c3f06f4ae54af122b24bac1d7dd1048445123b`. It retargets the supported Fantasy core packages, tools, Server composition, templates, and bundled DotRecast projects to `net10.0`; fixes the duplicate-config publish path; pins patched transitive dependencies; and regenerates the tracked NuGet artifact with only a `net10.0` library asset. On Windows with SDK 10.0.202, the tooling and complete Server solutions build with zero warnings and errors, the Host publishes with one application-owned `Fantasy.config`, the Host starts all configured scenes, the local NuGet package restores in an independent .NET 10 consumer, and the solution vulnerability scan reports no known vulnerable packages. The removed Console platform and its orphan Benchmark/Console examples are explicitly outside the supported composition.

This candidate commit exists only in the local Fantasy checkout and is not yet a remotely fetchable fork pin. The current workstation has neither Docker nor an installed WSL distribution, so Linux/container evidence could not be produced. These facts improve the Windows migration evidence but do not satisfy the reproducibility, CI, test, load, legal, or release gates.

An uncommitted follow-up adds `Fantasy.Net.Tests` to the tooling solution with three .NET 10 regression tests: the tracked package must expose only the `net10.0` library asset, and fresh project-reference and package-reference publishes must each contain exactly one application-owned `Fantasy.config`. All three tests pass locally, as do the complete tooling and Server builds and the vulnerability scan. It also prepares a Windows/Ubuntu GitHub Actions matrix for those checks. The workflow has not run because neither the candidate nor the follow-up is committed to a remotely fetchable fork, so this evidence does not cover protocol compatibility, replay, shutdown, load, or Linux behavior.

## Proposed decision

Adopt `.NET 10` (`net10.0`) and C# 14 for future Server Hosts only if the evidence gate below passes for the exact pinned Fantasy fork and project-owned server composition. Shared gameplay remains `netstandard2.1` with C# 9 and the same Unity-independent source set; no .NET 10 assembly is imported into Unity.

This ADR is not yet accepted and does not supersede ADR-0004. No Server project or Fantasy fork import may merge while the successor decision is overdue. The current `global.json` and `.NET 8`/`.NET 9` skeleton matrix remain unchanged until the Spike produces evidence and this ADR is accepted or replaced.

## Required evidence

1. Pin the exact Fantasy upstream/fork commit and inventory every project required by the intended Host, including source generators, templates or replacements, ASP.NET Core components, DotRecast/native adapters, and NuGet dependencies.
2. Retarget or replace the required server composition so the Host and project-owned server modules build, test, publish, and start as `net10.0`; a down-level dependency is acceptable only when its compatibility and ownership are explicit.
3. Pin a supported .NET 10 SDK feature band, configure Server-only projects for C# 14, and prove matching local, CI, container, and release toolchains.
4. Pass protocol compatibility, Shared vectors, replay, impairment, allocation, shutdown, container, and observability tests.
5. Pass the 64-player load and Tick budgets on release-equivalent Linux artifacts, with comparison data against the current runtime baseline where available.
6. Record required Fantasy fork changes and their upstream-maintenance cost; complete the existing license review gate before distributing the fork.

The Windows migration, build, publish, startup, package-consumption, dependency-vulnerability, and initial package/config regression portions now have local evidence. A cross-platform CI definition is prepared but has no run evidence. Linux/container validation, a committed and remotely fetchable fork pin, broader protocol/replay/shutdown/load tests, operational behavior, and legal review remain open, so this ADR stays Proposed.

## Acceptance or rejection

If all required evidence passes, this ADR may be changed to Accepted, ADR-0004 becomes Superseded, and the repository SDK/CI/container migration is implemented before the first Server project merges.

If the evidence fails, record the failing dependency or workload with measurements and either isolate/replace that subsystem behind the existing adapters or select another supported runtime in a replacement ADR. The project must not create a new production Host on an unsupported runtime as a fallback.

## Migration and rollback if accepted

There is currently no deployed Host to migrate in place. The first Server Host would be introduced on the accepted successor only after the implementation gates pass. Temporary multi-targeting used during the Spike is build evidence, not a release topology.

Canary rollout would use immutable images, backward-compatible protocols and data, and staged promotion. Rollback would select the previous known-good image on a still-supported runtime or disable the undeployed Host; an unsupported runtime is not the default rollback.

## References

- [Microsoft .NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy)
- [Microsoft .NET lifecycle](https://learn.microsoft.com/lifecycle/products/microsoft-net-and-net-core)
- [.NET SDK targeting and support rules](https://learn.microsoft.com/dotnet/core/porting/versioning-sdk-msbuild-vs)
- [C# language versioning](https://learn.microsoft.com/dotnet/csharp/language-reference/language-versioning)
- [.NET 10 breaking changes](https://learn.microsoft.com/dotnet/core/compatibility/10)
- [Fantasy.Net target frameworks at the reviewed commit](https://github.com/qq362946/Fantasy/blob/6df3507b15737d86f93a36af1a6d28a2404e163d/Fantasy.Packages/Fantasy.Net/Fantasy.Net.csproj)
- [Fantasy server Main target frameworks at the reviewed commit](https://github.com/qq362946/Fantasy/blob/6df3507b15737d86f93a36af1a6d28a2404e163d/examples/Server/APP/Main/Main.csproj)
