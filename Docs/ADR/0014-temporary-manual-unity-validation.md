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
- A qualifying run uses Unity `6000.3.9f1` revision `7a9955a4f2fa` and executes the exact named suite in the manual validation procedure. The current WS-26 macOS gate requires 36 passed EditMode tests, 2 passed real-KCP PlayMode tests, and one successful Apple Silicon ARM64 Mono Player smoke, with zero failures or skips.
- The evidence bundle records commit/tree/submodule, Unity, package, Fantasy, protocol, configuration, fixed Host image, NUnit, Player architecture, license, smoke, log, and SHA-256 identities.
- Changes to Unity, Shared Gameplay, Shared Realtime, package resolution, or the tested commit invalidate earlier evidence.
- Missing manual evidence is an open gate, not a pass. .NET test success cannot substitute for Unity execution.

The WS-25 exact-main bundle for source `dfbc0534631ec7cc019919830a93472d3572f61c` used Unity `6000.3.9f1` revision `7a9955a4f2fa` and passed all 22 named EditMode tests with zero failures and zero skips. The retained NUnit XML SHA-256 is `c781a3e0f5d1f48811bd1c6eb0c89520d30532685a84b2fdf91288ad11df84bb`. This closes the WS-25 exact-commit EditMode gate only; it does not substitute for PlayMode, mobile IL2CPP, concrete transport, or real-client impairment evidence.

The WS-26 exact-main bundle for source `2987ce08475b2cf2342a98326ff86fa422a3a6a5` and tree `1f441d2cfbadd009533f707da3a78ddabbefbc0a` used the same Unity identity and pinned Fantasy commit `f8bed0d464924f159d46498f1311206ea0694be8`. It passed 36/36 EditMode and 2/2 real-KCP PlayMode tests plus the ARM64 Mono Player login/join/input/reconnect smoke with zero dropped input frames and normal Host drain. The retained bundle hash manifest SHA-256 is `308b1fea377b1509cd8e9fa6a31dddbd2da832830ad451a5936f6858d1e5b538`. This closes the WS-26 macOS desktop gate for that exact commit only; Windows, Regional real-client correction, and Android/iOS IL2CPP remain separate evidence gates.

## Consequences and restoration

Manual execution is slower and depends on operator discipline, but retains auditable dual-compilation evidence without storing Unity credentials. Earlier exact-commit bundles retain their historical named-test counts; every subsequent relevant exact commit is gated by the then-current named suite until automated Unity CI is restored.

The project owner selected macOS Apple Silicon ARM64 Mono as the current WS-26 complete desktop gate because no Windows environment is available. The Windows script remains a supplemental future platform gate. Passing macOS does not claim Windows, Universal/x86_64, Android/iOS IL2CPP, or Regional correction coverage.

Restore automatic CI when Unity authentication becomes available by re-enabling pull-request/push triggers, adding the appropriate GitHub Secrets or licensing-server configuration, and obtaining a green run. At that point a follow-up ADR must end this exception; ADR-0003's automated requirement becomes authoritative again without changing the Shared API.
