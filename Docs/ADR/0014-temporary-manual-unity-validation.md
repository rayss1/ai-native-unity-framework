# ADR-0014: Temporarily Use Exact-Commit Manual Unity Validation

Status: Accepted
Date: 2026-08-21
Decision source: Project owner instruction
Temporarily supersedes: the automated Unity execution requirement in [ADR-0003](0003-shared-gameplay-dual-compilation.md)

## Context

ADR-0003 requires Unity Batch Mode CI to compile Shared source and execute the same golden vectors as .NET. The repository has a GameCI workflow, but its Personal/Professional activation path requires Unity account credentials or a license artifact. The project owner cannot currently authenticate to Unity and explicitly assigned Unity validation to a local manual run.

Silently skipping the job would weaken the dual-runtime boundary and could be misreported as passing evidence. The exception therefore changes only who executes and records the Unity run; it does not waive Unity compilation, test, version, or exact-commit evidence.

## Decision

- Unity validation no longer runs automatically on pull requests or pushes. `.github/workflows/unity.yml` remains available only through `workflow_dispatch` for a future credentialed run.
- Until automation is restored, the project owner runs [the manual Unity procedure](../Architecture/unity-manual-validation.md) on a clean checkout of each relevant exact commit.
- A qualifying run uses Unity `6000.3.9f1` revision `7a9955a4f2fa` and executes the exact named suite in the manual validation procedure. Beginning with WS-28, the current macOS gate requires 44 passed EditMode tests, 2 passed real-KCP PlayMode tests, one successful Apple Silicon ARM64 Mono Player smoke, and the 10+60-second Regional real-client correction/profile-smoothing evidence, with zero failures or skips. Earlier retained bundles keep the exact test totals recorded for their source commits.
- The evidence bundle records commit/tree/submodule, Unity, package, Fantasy, protocol, configuration, fixed Host image, NUnit, Player architecture, license, smoke, log, and SHA-256 identities.
- Changes to Unity, Shared Gameplay, Shared Realtime, package resolution, or the tested commit invalidate earlier evidence.
- Missing manual evidence is an open gate, not a pass. .NET test success cannot substitute for Unity execution.

The WS-25 exact-main bundle for source `dfbc0534631ec7cc019919830a93472d3572f61c` used Unity `6000.3.9f1` revision `7a9955a4f2fa` and passed all 22 named EditMode tests with zero failures and zero skips. The retained NUnit XML SHA-256 is `c781a3e0f5d1f48811bd1c6eb0c89520d30532685a84b2fdf91288ad11df84bb`. This closes the WS-25 exact-commit EditMode gate only; it does not substitute for PlayMode, mobile IL2CPP, concrete transport, or real-client impairment evidence.

The WS-26 exact-main bundle for source `2987ce08475b2cf2342a98326ff86fa422a3a6a5` and tree `1f441d2cfbadd009533f707da3a78ddabbefbc0a` used the same Unity identity and pinned Fantasy commit `f8bed0d464924f159d46498f1311206ea0694be8`. It passed 36/36 EditMode and 2/2 real-KCP PlayMode tests plus the ARM64 Mono Player login/join/input/reconnect smoke with zero dropped input frames and normal Host drain. The retained bundle hash manifest SHA-256 is `308b1fea377b1509cd8e9fa6a31dddbd2da832830ad451a5936f6858d1e5b538`. This closes the WS-26 macOS desktop gate for that exact commit only; Windows, Regional real-client correction, and Android/iOS IL2CPP remain separate evidence gates.

The WS-27 exact-main bundle for source `6376265658a26fa07b08fc737c3932d52212314a` and tree `e5ea86c01e85e55475052e75f2fcd876db7069e3` used the same Unity and Fantasy identities. It passed 38/38 EditMode, 2/2 real-KCP PlayMode, the ARM64 Mono reconnect smoke, and the 10+60-second symmetric Regional real-client gate. The measured correction P95/P99 was `9/10 mm`, no correction exceeded `250 mm`, and no prediction input or application frame was dropped. The retained bundle hash manifest SHA-256 is `1d9a8f403114c9cdcbbc45ebb42f2d0e95b7539ccffa161d5b5e7aeec4339591`. This closes that exact commit's local macOS Mono Regional correction gate only; Windows, game-specific prediction physics, and Android/iOS IL2CPP remain separate.

The WS-28 exact-main bundle for source `b8d4228c20cd9bf05054a956c5cc168711bfdff9` and tree `2241de28438df0a56fa354d42130741f92a978f8` used the same Unity, Fantasy, protocol, and configuration identities. It passed 44/44 EditMode, 2/2 real-KCP PlayMode, the ARM64 Mono reconnect smoke, and the 10+60-second symmetric Regional gate. Its 1,221 reconciliation samples produced correction P95/P99 `9/10 mm`, maximum `12 mm`, zero corrections above `250 mm`, 1,207 smoothed presentation corrections, zero snaps, and a `1 mm` final residual, with zero history/input/frame loss. The retained bundle hash-manifest SHA-256 is `496da779c7449f2a6ffb1e59d3a37d99f34dba2ae6f73b40d6f3187569fb2604`. This closes that exact commit's bounded macOS presentation-composition gate only; representative visual quality, Windows, game-specific physics, and Android/iOS IL2CPP remain separate.

## Consequences and restoration

Manual execution is slower and depends on operator discipline, but retains auditable dual-compilation evidence without storing Unity credentials. Earlier exact-commit bundles retain their historical named-test counts; every subsequent relevant exact commit is gated by the then-current named suite until automated Unity CI is restored.

The project owner selected macOS Apple Silicon ARM64 Mono as the current complete desktop gate because no Windows environment is available. The Windows script remains a supplemental future platform gate. The WS-28 macOS bundle includes the local deterministic Regional correction and presentation-smoothing profile, but does not claim Windows, Universal/x86_64, Android/iOS IL2CPP, physical-WAN behavior, representative visual quality, or game-specific prediction physics.

Restore automatic CI when Unity authentication becomes available by re-enabling pull-request/push triggers, adding the appropriate GitHub Secrets or licensing-server configuration, and obtaining a green run. At that point a follow-up ADR must end this exception; ADR-0003's automated requirement becomes authoritative again without changing the Shared API.
