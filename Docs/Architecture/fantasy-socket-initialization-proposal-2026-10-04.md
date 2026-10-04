# Fantasy socket initialization: evidence and proposed fork change

Status: Implementation authorized on 2026-10-04; local candidate integrated, full qualification in progress.

Current local fork: `df4ad5fe5418c8855932de784c7cea6286c4b082`; versions Fantasy-Net `2026.1.1004-ainative.1`, Fantasy.Unity `2026.1.1002-ainative.1`. The parent repository is uncommitted, and nothing has been pushed or published. Earlier proposal/candidate evidence below is historical. The implemented zero-step behavior is a no-op, as specified in the approved plan.

## Observed problem

The current Arena candidate is not capacity-qualified. `settlement-diagnostic-02` completed its unchanged 32-client, four-room, 60 Hz, 60-second warmup and 300-second measurement with occupancy 0.945895703 (required 0.95). All four Worker gates, both node gates, 22 independent completed replays and accepted-input audit passed. Host CPU P99 was 42%, maximum used memory 75.61%. Timing capture retained 2,038 records with zero omissions.

The 176 fresh settlement connections averaged about 0.956 seconds. Across 22 parallel eight-member groups, the slowest connection averaged 0.983 seconds. Confirmed Profile requests averaged 0.434 seconds; 40 pending reads also occurred. Connection time is not database commit time.

The pinned fork's `Fantasy.Net/Runtime/Core/Helper/NetworkHelper.cs` increments receive and send buffer sizes in two synchronous loops of up to 100,000 iterations each. TCP calls this before `Socket.ConnectAsync`; KCP also calls it. Eight Gate probe initializations run serially on one Scene, delaying its completion callbacks. There is no corresponding one-second scheduler sleep. Unity's mirrored helper has the same loops.

## Reproduction and limits

The experiment in `artifacts/battle-slice-20261004/socket-buffer-diagnostic` directly invokes the DLL consumed by the diagnostic: Fantasy-Net 2026.1.1003, SHA256 `9B36137BD5E4EA886F9B608F83FF01C23302B3CF29C6E8F9113A97CF74E77B61`. Runtime .NET 10.0.6, Windows 10.0.26200, 16 logical processors; the capacity run's host metadata contains machine details. Each method/socket-type combination has one excluded warmup and ten fresh-socket samples. No network workload or random seed is involved. This is a microdiagnostic, not a capacity claim; nearest-rank P99 over ten samples is simply the maximum.

`same-target-result.json` records TCP mean 92.203 ms / P99 105.687 ms and UDP mean 89.086 ms / P99 95.358 ms for the existing helper. Directly setting the same final receive/send sizes (102,465,536 bytes each on this host) took less than 0.002 ms per sample. These configured sizes do not establish that the OS physically allocates that much memory per socket. A separate 256 KiB experiment is retained but is not the recommended production setting.

This demonstrates a substantial avoidable initialization cost consistent with the observed grouped connection delay. It does not prove that eliminating it alone meets occupancy or that Windows results transfer to Linux/mobile.

## Recommended change for review

Prepare a focused fork candidate replacing linear buffer probing with a bounded algorithm. Preserve the current requested maximum on this Windows path to avoid combining this fix with a buffer-capacity policy change. Use overflow-safe arithmetic and a small explicit attempt budget, respect OS-clamped results, and provide a bounded fallback when a requested size is rejected. Do not change wire format, MTU, Tick rate, settlement ordering, match workload or acceptance thresholds.

Keep the existing public helper signatures and mirror the fix in the Unity source, with shared behavioral cases for success, OS clamping, rejection, overflow and termination. Test actual TCP and UDP sockets as well as synthetic option setters; do not assert wall-clock speed in correctness tests. Review TCP and KCP backpressure/memory behavior before adoption.

The change requires a new exact fork commit and a distinct package version. Do not overwrite 2026.1.1003, alter cached packages, or patch runtime methods. ADR-0001 and the technology baseline require focused fork changes, an exact gitlink and renewed review for an adopted-baseline change. Local commits and dependency adoption need the owner's decision; this proposal does not authorize publication.

## Acceptance and rollback

### Prepared candidate (not adopted)

The reviewable patch is `artifacts/battle-slice-20261004/socket-buffer-candidate/bounded-socket-buffers.patch`. It changes only the mirrored Net/Unity buffer helpers. `git apply --check` succeeds against the unchanged pinned checkout; `patch-identities.json` records source hashes. The patch has not been applied and no fork commit/package has been created.

The extracted exact helper fragment passed 14 behavioral checks (`behavior-reviewed-results.json`), including 10,000 seeded monotonic-limit cases, maximum-depth binary-search success cases, OS clamping/scaling, rejection, overflow, disposed-error propagation, and actual Windows TCP/UDP options. The fragment also compiles as C# 9 / .NET Standard 2.1 with zero warnings/errors using the installed SDK (`unity-compatible-local-build.log`). This is not a Unity Editor or complete fork build. The earlier isolated-SDK build failed because it lacked the reference pack and online package retrieval failed; that failure log is retained.

Compatibility review: positive default arguments preserve the Windows maximum request, but nonpositive `stepSize` now throws when attempts are positive, and `attempts` bounds growth rather than actual write count. Nonpositive attempts remain a no-op. On systems that scale/clamp socket options, the algorithm stops after observing that result, so the final buffer may differ from the old repeated-read algorithm. Linux/macOS, socket throughput/backpressure and full topology behavior remain unverified. Independent static review found no blocking default-path algorithm error and requested the added maximum-depth tests. These limitations must be resolved during adoption review.

After owner approval: implement and review the isolated fork patch; run fork build/package/config/startup/protocol tests and adapter socket lifecycle tests; produce a separately versioned local candidate package; bind its source and binary identities. Then run the integrated .NET/Unity/standalone checks affected by the vendor change, the unchanged capacity sweep, independent replay/input/headroom gates, and a successful 3,600-second highest-passing soak. Preserve all failed evidence.

Rollback restores gitlink `f8bed0d464924f159d46498f1311206ea0694be8`, Fantasy-Net 2026.1.1003 and Fantasy.Unity 2026.1.1001, with fresh isolated outputs. Existing evidence remains tied to its original identities. Until this process succeeds, the current slice's functional evidence stands but capacity qualification remains open.
