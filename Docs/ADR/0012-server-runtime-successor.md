# ADR-0012: Adopt .NET 10 LTS as the Server Runtime

Status: Accepted
Date: 2026-08-13
Accepted: 2026-08-24
Last evidence update: 2026-08-24
Decision source: WS-13
Supersedes: [ADR-0004](0004-server-runtime-policy.md)

## Context

ADR-0004 selected `.NET 8` for production Hosts and `.NET 9` as a compatibility lane, with a successor decision due by 2026-06-30. That gate was missed, and both runtimes reach end of support on 2026-11-10. The project must select a supported successor before introducing a production Server Host.

.NET 10 is the preferred LTS candidate, but the complete Fantasy composition—not only `Fantasy.Net`—must build, publish, start, and satisfy project operational and gameplay gates. The recovery baseline is [rayss1/Fantasy `493d5d4`](https://github.com/rayss1/Fantasy/commit/493d5d4dd1dd009cdfcd2846b88ebab9746d4504).

WS-13 rebuilt the candidate on branch `codex/ws-13-dotnet10` and merged [Fantasy PR #1](https://github.com/rayss1/Fantasy/pull/1). WS-14 then merged [Fantasy PR #2](https://github.com/rayss1/Fantasy/pull/2). WS-16 advanced the parent evaluation pin from the WS-14 commit to fork `main` commit `f8bed0d464924f159d46498f1311206ea0694be8`. It:

- pins SDK `10.0.202` and migrates the supported runtime, tools, example Server, templates, and bundled DotRecast projects to `net10.0`/C# 14;
- keeps the Source Generator on `netstandard2.0` and leaves orphan Benchmark/Console examples outside the supported matrix;
- corrects `Fantasy.config` publication so both project and package consumers publish exactly one application-owned configuration;
- pins `Tmds.DBus.Protocol` `0.21.3` and `SQLitePCLRaw.bundle_e_sqlite3` `2.1.12`;
- regenerates the tracked package with only a `lib/net10.0` runtime asset while preserving analyzer and MSBuild assets;
- adds three NUnit package/config regressions and a Windows/Ubuntu GitHub Actions workflow.
- removes conflicting Source Generator project-instance metadata after the first Windows run exposed a parallel-build file lock.

Local validation on macOS used installed SDK `10.0.200` because the pinned `10.0.202` SDK was unavailable locally. Both solutions built in Release with zero warnings/errors, all three tests passed, publish selected .NET 10 and produced one application configuration, the Host reached `Startup Complete`, Control Center initialized SQLite and listened, and dependency scans found no known vulnerabilities. WS-14 added cancellation-aware host lifetime, Unix SIGTERM/SIGINT and Windows Ctrl+C handling, main-scheduler pumping during asynchronous disposal, and bounded NLog shutdown.

The final [Windows/Ubuntu CI run](https://github.com/rayss1/Fantasy/actions/runs/32242324689) passed restore, warnings-as-errors builds of both Solutions, all three regressions, Host publish/start and runtime/config assertions, Control Center SQLite smoke, and direct/transitive vulnerability auditing on SDK `10.0.202`. This establishes reproducible cross-platform migration evidence, but does not satisfy the remaining release, gameplay, load, shutdown, observability, or legal gates.

The WS-14 [Windows/Ubuntu CI run](https://github.com/rayss1/Fantasy/actions/runs/32248783092) additionally passed a real Linux SIGTERM probe: after startup, the example Host completed Scene/Process disposal, emitted `Shutdown Complete`, and exited with code zero within ten seconds without a forced stop. Parent [container run 32278435820](https://github.com/rayss1/ai-native-unity-framework/actions/runs/32278435820) then built that exact fork commit with SDK image digest `sha256:adc02be8b87957d07208a4a3e51775935b33bad3317de8c45b1e67357b4c073b`, ran it on ASP.NET runtime image digest `sha256:8b75cdf59a5068d9adfd8a6d202cc7671b2dc8f5f46c51e3b88a0a632e8fad1f` as the image-defined non-root user, reached `Startup Complete`, and exited normally on SIGTERM within ten seconds. The run published source, SDK, runtime-image, evaluation-image, and protocol identities as provenance.

WS-16 exposed that the tracked `Fantasy-Net` package lagged the graceful-shutdown source. [Fantasy PR #3](https://github.com/rayss1/Fantasy/pull/3) regenerated package `2026.1.1002`, compiled its cancellation-aware Entry API from independent project/package consumers, and enabled a real .NET KCP acceptance client. Its [Windows/Ubuntu matrix](https://github.com/rayss1/Fantasy/actions/runs/32630652446) passed. [Fantasy PR #4](https://github.com/rayss1/Fantasy/pull/4) aligned the runtime banner with package `2026.1.1002` and made both published-consumer regressions execute and assert that identity; its [Windows/Ubuntu matrix](https://github.com/rayss1/Fantasy/actions/runs/32630957890) passed. The gated parent [real-KCP candidate run](https://github.com/rayss1/ai-native-unity-framework/actions/runs/32631900769) then passed the exact source/package/config checks, all ten tests, direct Host and non-root image Login/Join/Input/Snapshot/Reconnect probes, readiness/drain, dependency audit, and normal SIGTERM. This is minimum integration and operational evidence, not impairment, deterministic replay, qualified load, final Unity, or legal evidence.

[Fantasy PR #5](https://github.com/rayss1/Fantasy/pull/5) retained the existing 470-byte outer KCP MTU by default while allowing a process to select and freeze an internet-safe value up to 1150 bytes before the first outer network is created. It regenerated tracked package `2026.1.1003`, kept the .NET and Unity KCP sources aligned, and passed the [SDK 10.0.202 Windows/Ubuntu matrix](https://github.com/rayss1/Fantasy/actions/runs/32696781372). The parent candidate fixes 1150 at its adapter boundary so a 799-byte Snapshot fits one KCP segment while the Fantasy reserved header plus IPv4/UDP overhead remains below 1200 bytes.

The first exact 60 Hz Linux socket rerun passed Regional wire budgets but Degraded loss caused stale reliable KCP Input retransmissions to raise upstream P95 to `75.648 kbit/s`. The candidate therefore adds protocol ID 1103 as an additive, Protobuf-first `InputBatch`: clients still sample 60 ordered movement/fire commands per second, but send two commands per 30 Hz batch while the original ID 1100 decoder remains supported. This follows ADR-0006's input-coalescing allowance and ADR-0009's compatibility rules.

WS-16 adds newest-only Snapshot coalescing, bounded asynchronous production Input capture with strict deterministic replay, fixed-seed Regional/Degraded/Backpressure codec profiles, exact Host performance reports, and a real 64-session Fantasy KCP load probe. Linux CI additionally applies explicit Regional/Degraded `tc netem` profiles inside the candidate network namespace and derives per-client bandwidth/datagram evidence from a hashed packet capture. The label-gated soak measures 60 full minutes only after clients join and the declared warm-up ends.

[Exact Linux candidate run 32710003483](https://github.com/rayss1/ai-native-unity-framework/actions/runs/32710003483) passed at parent commit `fd366411991d78e5ac517f79e020976608ca4113`, Fantasy `f8bed0d464924f159d46498f1311206ea0694be8`, and protocol SHA-256 `726a80d6a762913b87fe840f0be9086224598bcaadb0e4a7d4e3e44856c0b92c`. It passed 17 candidate tests, deterministic Regional/Degraded/Backpressure gates, direct and non-root image KCP/replay/load probes, zero-vulnerability audit, bounded drain/SIGTERM, and the two qualified 64-client socket profiles. Regional measured exactly 230,400 Input frames at `60.0006 Hz` and 115,200 batches at `30.0003 Hz`; PCAP P95 was `173.480 kbit/s` downstream and `43.744 kbit/s` upstream with a 917-byte maximum UDP payload. Degraded measured the same frame/batch counts at `60.0001/30.0001 Hz`; PCAP P95 was `197.488/49.064 kbit/s` with a 987-byte maximum. Host Tick P99/P99.9 remained at or below `0.7743/1.0147 ms`, Gameplay allocation was zero, and both captures passed their immutable identity and wire gates.

[Qualified-duration run 32710967424](https://github.com/rayss1/ai-native-unity-framework/actions/runs/32710967424) then passed at exact source `ab3c60c52a1aa8a9a2ad42fb3b46277b3a73e91d`. After a distinct ten-second warm-up, 64 real KCP clients ran for 3,600 measured seconds and produced 13,824,000 Input frames at `60.0000045 Hz`, 6,912,000 two-frame batches at `30.0000022 Hz`, 4,622,834 Snapshot frames, and 216,004 measured Host Ticks. Tick P99/P99.9 were `0.6573/0.9548 ms`, slow Tick rate was zero, Gameplay P99 was `0.0007 ms`, and stable Gameplay managed allocation was zero.

The ADR-0014 manual Unity run for the same exact source used Unity `6000.3.9f1` revision `7a9955a4f2fa` and passed all seven required EditMode tests with zero failed/skipped. The retained NUnit XML SHA-256 is `4a1a4a2fc17866f9b31ae5d2ba3ab2b1b7c4b75e55595625f9c8cfba309d464d`. The project owner also supplied the [written Fantasy license approval](../Architecture/fantasy-license-approval-2026-08-24.md), accepting the current restrictions and the project legal risk for commercial use, modification, and distribution.

## Decision

Adopt `.NET 10` (`net10.0`) and C# 14 for Server Hosts, Server tools, and .NET test executables. Shared gameplay remains `netstandard2.1` with C# 9; no .NET 10 assembly is imported into Unity.

Fantasy remains pinned at `server/vendor/Fantasy` and opaque to the architecture graph. Only the dedicated `AiNative.Server.Fantasy` adapter and Battle Host composition root may reference `Fantasy-Net`; Fantasy runtime namespaces terminate inside the adapter. Product gameplay, Shared contracts, protocol models, and Unity assemblies expose only project-owned types.

The parent `global.json` pins SDK `10.0.202`, `AiNative.sln` owns the product Host and Server modules, and CI builds/tests/publishes the single `net10.0` product lane. The former .NET 8/.NET 9 skeleton matrix and evaluation-only container are retired.

## Acceptance evidence

All required gates passed for the pinned Fantasy baseline and project-owned composition:

- SDK `10.0.202` Windows/Ubuntu fork build, package, publish, startup, SQLite, shutdown, and vulnerability matrices;
- .NET and Unity Shared vectors plus generated protocol drift checks;
- real Login/Join/Input/Snapshot/Reconnect, strict production replay, readiness/drain, SIGTERM, telemetry-outage, bounded queue, and zero-allocation checks;
- Regional/Degraded netem/PCAP bandwidth and payload gates, deterministic Backpressure, and the qualified 60-minute 64-client Linux soak;
- project-owner written license approval for the pinned fork baseline.

## Acceptance, migration, and rollback

The acceptance and parent SDK/Solution/CI/container migration merge atomically. Production promotion uses the recorded immutable source, Fantasy, protocol, configuration, SDK, runtime image, and built image identities. Release operators preserve the prior image digest during rollout and keep protocol evolution additive so a canary can be rolled back without data or wire incompatibility.

Rollback restores the last qualified Host image and configuration first. If the runtime/fork itself must be removed, retain Shared/Tools and protocol assets, replace `AiNative.Server.Fantasy` behind project-owned contracts, remove the Host/Fantasy package references from `AiNative.sln`, and select another supported runtime in a superseding ADR. Database and protocol changes made during rollout must stay backward compatible until the rollback window closes.

## References

- [Runtime successor Spike and recovery evidence](../Architecture/runtime-successor-spike-2026-08-13.md)
- [Fantasy recovery baseline](https://github.com/rayss1/Fantasy/commit/493d5d4dd1dd009cdfcd2846b88ebab9746d4504)
- [Fantasy recovery PR](https://github.com/rayss1/Fantasy/pull/1)
- [Green Windows/Ubuntu validation run](https://github.com/rayss1/Fantasy/actions/runs/32242324689)
- [Pinned Fantasy fork commit](https://github.com/rayss1/Fantasy/commit/f8bed0d464924f159d46498f1311206ea0694be8)
- [Graceful-shutdown PR](https://github.com/rayss1/Fantasy/pull/2)
- [Green graceful-shutdown matrix](https://github.com/rayss1/Fantasy/actions/runs/32248783092)
- [Runtime-package consistency and .NET KCP client PR](https://github.com/rayss1/Fantasy/pull/3)
- [Green runtime-package Windows/Ubuntu matrix](https://github.com/rayss1/Fantasy/actions/runs/32630652446)
- [Runtime/package identity PR](https://github.com/rayss1/Fantasy/pull/4)
- [Green runtime/package identity Windows/Ubuntu matrix](https://github.com/rayss1/Fantasy/actions/runs/32630957890)
- [Outer KCP MTU and package 2026.1.1003 PR](https://github.com/rayss1/Fantasy/pull/5)
- [Green outer KCP MTU Windows/Ubuntu matrix](https://github.com/rayss1/Fantasy/actions/runs/32696781372)
- [Green exact-commit 64-client Linux socket, replay, and image run](https://github.com/rayss1/ai-native-unity-framework/actions/runs/32710003483)
- [Green exact-commit 60-minute 64-client soak](https://github.com/rayss1/ai-native-unity-framework/actions/runs/32710967424)
- [Fantasy license project-owner approval](../Architecture/fantasy-license-approval-2026-08-24.md)
- [Green exact-source real-KCP candidate and image run](https://github.com/rayss1/ai-native-unity-framework/actions/runs/32631900769)
- [Green parent Linux container and provenance run](https://github.com/rayss1/ai-native-unity-framework/actions/runs/32278435820)
- [Green parent submodule and .NET 8/.NET 9 validation run](https://github.com/rayss1/ai-native-unity-framework/actions/runs/32242925933)
- [Microsoft .NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy)
- [Microsoft .NET lifecycle](https://learn.microsoft.com/lifecycle/products/microsoft-net-and-net-core)
- [.NET 10 breaking changes](https://learn.microsoft.com/dotnet/core/compatibility/10)
