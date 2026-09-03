# Client Prediction and Reconciliation Baseline

Status: Implemented prediction baseline; WS-28 exact-main macOS smoothing gate passed
Last updated: 2026-09-03
Decision source: ADR-0005, WS-24, WS-25, WS-26, WS-27, and WS-28

## Scope

WS-24 establishes the first product-owned client prediction primitive without selecting a physics engine or weakening server authority. The same `AiNative.Gameplay` source compiles for Unity and .NET. It owns fixed-integer movement, a bounded input history, authoritative reconciliation, and observable history loss; it performs no scheduling, transport, engine access, or I/O.

The Battle Host remains authoritative. Each recipient's `Snapshot.last_processed_input_sequence` identifies the newest input accepted for that logical session. The Host injects that one acknowledgement into the cached room snapshot immediately before synchronous encoding, so the 64-player state is not cloned per connection and the wire cost is one scalar per snapshot rather than one scalar per player. Legacy payloads decode the additive field as zero, and older readers ignore it.

## Shared behavior

- `KinematicMovement` clamps each normalized movement axis to `[-1000, 1000]` and applies at most 50 millimetres per fixed Tick. The resulting 3 metres/second full-input speed is a provisional first-slice test rule, not a final physics choice.
- `ClientPredictionHistory` is initialized from an authoritative `KinematicState`. It accepts strictly increasing input sequences and stores only a caller-selected bounded capacity between 2 and 1,024 entries.
- When full, the history advances its baseline and drops the oldest entry while incrementing `DroppedInputCount`; it never grows an unbounded collection.
- Reconciliation discards inputs acknowledged by the server, rewinds to the authoritative position, and replays only newer inputs. Results distinguish matched, corrected, stale, authoritative-ahead, and history-miss paths and expose correction components in millimetres.
- The implementation allocates its arrays only in the constructor. Predict/reconcile operations are synchronous and allocation-free after initialization.

The Shared layer deliberately does not smooth presentation corrections, interpolate remote entities, open a transport, or translate Protobuf messages. Those responsibilities belong to client adapters and the Unity composition root. Jolt/Unity physics integration remains behind ADR-0007 and must not be inferred from this integer movement baseline.

## Current evidence

Local .NET 10 validation on the WS-24 working candidate used SDK `10.0.204`, selected by the repository's `10.0.202` `latestPatch` policy, and completed a zero-warning Release build and the repository test matrix. Shared prediction vectors cover clamping, exact fixed-step movement, acknowledged-input discard, rewind/replay, bounded-history loss, authoritative reset, and a 1,000-iteration steady-state zero-allocation loop. Protocol tests retain stable bytes for the additive acknowledgement and verify legacy defaulting. Unity 6000.3.9f1 manual validation on reviewed head `9d87fec6c3cf8eea259a957ed84d3e8ae3671ce3` passed all 14 shared EditMode vectors with zero failures or skips; PR #16 merged that reviewed tree without changing the prediction implementation.

A real local Fantasy KCP Host probe then sent input sequence 1, received a Snapshot acknowledgement of 1, disconnected, reconnected with a new connection epoch, and received the same acknowledgement in the resume Snapshot. The Host drained normally after the probe.

The deterministic macOS diagnostic profiles also remained green with protocol identity `3cb86e21687e65af0e0d409d9186384d0f959fd6aa873eb9e1cd0cb39c77d37d`: the worst Tick P99/P99.9 across Regional, Degraded, and Backpressure was `0.0821/0.1937 ms`, the Backpressure Tick P99 increment was `0.0545 ms`, per-client downstream/upstream P95 stayed at or below `144.368/26.424 kbit/s`, and the maximum application frame was `799 bytes`.

Exact-`main` source `d2979a742f15eaa1dcfd60f7e1a3292448ceaec9` with Fantasy `f8bed0d464924f159d46498f1311206ea0694be8` then passed [.NET validation run 33071031886](https://github.com/rayss1/ai-native-unity-framework/actions/runs/33071031886) and [Battle Host production-validation run 33071031962](https://github.com/rayss1/ai-native-unity-framework/actions/runs/33071031962). The .NET run completed the 78-test matrix, including all 12 Shared Gameplay vectors and the real Fantasy acknowledgement/reconnect probe, with zero build warnings or errors.

The release-equivalent Linux x64 run retained the new protocol identity and passed Regional/Degraded socket impairment, replay, telemetry-outage, capacity, and 60-minute soak gates. Regional/Degraded downstream P95 was `178.048/198.544 kbit/s`, upstream P95 was `42.872/48.808 kbit/s`, and maximum UDP payload was `919/1019 bytes`. The two-room candidate reached Tick P99/P99.9 `1.1297/1.4529 ms`, `0` Gameplay allocation, `6.344%` process CPU, and exact room-aware replay at final Tick `21244`, combined hash `adc18718c44fe25b`, and configuration identity `fc0714bcbe7c8c673cf638506a45f3a4440585f4024ff78048434346ab8a66e4`. Its 128-client wire P95 was `179.28/43.752 kbit/s` downstream/upstream with `924-byte` maximum payload. The 64-Bot soak recorded 216,004 measured Ticks over 3,600 seconds at Tick P99/P99.9 `0.3329/0.9707 ms`, zero slow Ticks, and zero Gameplay allocation.

Repository-owned telemetry and multi-room verifiers were rerun against the downloaded artifacts and reproduced their tracked summaries byte-for-byte. The replay verifier rebuilt from the exact-main checkout reproduced all source, Fantasy, protocol, configuration, final-Tick, Input-count, and combined-hash fields; all three retained PCAP hashes also matched their reports.

This evidence proves that the additive acknowledgement and bounded Shared primitive preserve the existing server, protocol, replay, wire, capacity, and soak gates. The synthetic load client does not execute a Unity client adapter, so it does not measure correction frequency or magnitude and cannot close those product-visible gates.

## WS-25 client adapter candidate

`packages/com.ainative.client.prediction` is the first reusable Client-layer consumer of this baseline. It owns the local input sequence and bounded history, emits protocol-v1 InputCommand bytes, accepts routed Snapshot and ReconnectResponse frames, validates connection epochs and the recipient entity, and feeds the decoded acknowledgement and position into Shared reconciliation. It keeps packet routing, concrete KCP sockets, Unity presentation, and remote interpolation outside the package.

The synchronous `PrepareInput` path uses a caller-owned fixed buffer and is allocation-free after initialization. The optional asynchronous send path reuses one 1,200-byte buffer, rejects overlapping sends, and preserves all `IRealtimeTransport` backpressure outcomes. Stable result values and diagnostic counters expose correction magnitude, corrections above 250 mm, history misses, stale snapshots, and bounded-history drops without referencing a telemetry SDK.

The Runtime assembly contains no Unity, Fantasy, or Google.Protobuf reference. Eight Unity EditMode tests cover v1 bytes/channel selection, rewind/replay, matching state, malformed input, packet truncation/routing, reconnect epochs, backpressure, and steady-state allocation. Three additional .NET-only tests compare its handwritten Unity-compatible wire boundary with the tracked generated Protobuf InputCommand, Snapshot, and ReconnectResponse types.

Exact-`main` source `dfbc0534631ec7cc019919830a93472d3572f61c` passed [.NET run 33155894500](https://github.com/rayss1/ai-native-unity-framework/actions/runs/33155894500) and the complete [Battle Host production-validation run 33155894486](https://github.com/rayss1/ai-native-unity-framework/actions/runs/33155894486), both with Fantasy `f8bed0d464924f159d46498f1311206ea0694be8` and protocol identity `3cb86e21687e65af0e0d409d9186384d0f959fd6aa873eb9e1cd0cb39c77d37d`. Unity `6000.3.9f1` revision `7a9955a4f2fa` independently passed the exact-main 22-test EditMode suite with zero failures or skips; its NUnit XML SHA-256 is `c781a3e0f5d1f48811bd1c6eb0c89520d30532685a84b2fdf91288ad11df84bb`.

The release-equivalent exact-main run retained the acknowledgement-compatible protocol across replay, wire, capacity, impairment, telemetry-outage, and 60-minute soak gates. The two-room/128-Bot profile recorded Tick P99/P99.9 `1.1757/1.4206 ms`, Gameplay P99 `0.0016 ms`, zero Gameplay allocation, `6.462342%` process CPU, `146,165,760`-byte working set, and per-client wire P95 `178.912/43.616 kbit/s` downstream/upstream with `928-byte` maximum UDP payload. Room-aware replay consumed `2,381,710` Inputs and reproduced final Tick `21246` with combined hash `07f5c22398b534ea`; the retained PCAP matched SHA-256 `e61bd9ce25273157db122278e6db700e14573433a6b8bc8f2e5ea6ff95af23d2`.

The Regional/Degraded socket profiles stayed within wire budgets at downstream/upstream P95 `176.752/43.744` and `198.08/48.68 kbit/s`, with maximum payloads `927` and `988 bytes`. The 60-minute one-room/64-Bot soak recorded 216,004 measured Ticks at Tick P99/P99.9 `0.7948/1.0458 ms`, zero slow Ticks, zero Gameplay allocation, `3.767792%` process CPU, and final Tick/hash `217947`/`6a2800790eb00278`.

Telemetry outage comparison passed on exact main with exporter-disabled Tick P99 `0.6834 ms`, unavailable-exporter Tick P99 `0.6195 ms`, zero measured P99 increment, bounded series `4/16`, and zero trace-record drops. On the PR head, an initial hosted-runner sample produced an unusually low `0.2637 ms` baseline and normal `0.7235 ms` outage, so the strict delta gate failed at `0.4598 ms`; a failed-job rerun, without a code or threshold change, passed at `0.5782/0.6931 ms` and `0.1149 ms` delta. The exact-main run passed on its first attempt. This fluctuation is retained as runner-variance evidence and does not weaken the `< 0.25 ms` gate.

Repository-owned qualification, telemetry-capacity, and multi-room-capacity verifiers were rerun against the downloaded exact-main artifacts. They confirmed the source, Fantasy, protocol, configuration, base-image and product-image identities, replay result, and PCAP hashes. These server-side and manual EditMode results prove compatibility of the WS-25 adapter boundary; they do not execute a concrete Unity network transport or measure player-visible correction behavior.

## WS-26 Unity transport and application candidate

`packages/com.ainative.client.fantasy` owns the concrete bounded Fantasy KCP transport. It is the only Client package permitted to reference Fantasy namespaces, consumes `Fantasy.Unity` `2026.1.1001` at the same approved fork commit, and exposes only project-owned connection results, diagnostics, and `IRealtimeTransport`. The transport fixes the outer MTU at 1,150 bytes, caps application frames at 1,200 bytes, bounds each direction to 1,024 packets/256 KiB by default, and keeps Fantasy serialization/socket work outside FixedUpdate.

The Unity application Composition Root owns login, room join, reconnect, packet routing, and the replaceable active transport. Its state progression is `Connecting -> LoggingIn -> JoiningRoom -> Active`; disconnect enters `Reconnecting`, and terminal paths enter `Faulted` or `Disposed`. Protocol v1 uses room 1 and 60 Hz. FixedUpdate performs prediction and writes a preallocated input ring; Update pumps Fantasy and routes/sends queued frames. Reconnect retains the prediction instance and advances the transport epoch only after a successful decoded response.

WS-26 acceptance is intentionally evidence-gated. The current desktop gate requires exactly 36 EditMode and 2 PlayMode passes, followed by a macOS Apple Silicon ARM64 Mono Player smoke against an exact-source Battle Host built with fixed .NET images. The macOS entry point records source/tree/Fantasy/protocol/configuration/UPM/image/Unity identities, NUnit XML, Host and Player logs, staged notices, binary architecture, hashes, and smoke JSON. Windows remains a future supplemental platform gate.

Exact-`main` source `2987ce08475b2cf2342a98326ff86fa422a3a6a5`, tree `1f441d2cfbadd009533f707da3a78ddabbefbc0a`, and Fantasy `f8bed0d464924f159d46498f1311206ea0694be8` passed the reviewed macOS bundle. Unity `6000.3.9f1` revision `7a9955a4f2fa` completed 36/36 EditMode and 2/2 real-KCP PlayMode tests. The ARM64 Mono Player logged in, joined, acknowledged inputs, forced a reconnect, advanced epoch `4 -> 5`, advanced acknowledgement `30 -> 31`, received Tick `2319`, and reported zero dropped input frames; the exact-source Host then drained normally with exit code zero. The bundle hash manifest SHA-256 is `308b1fea377b1509cd8e9fa6a31dddbd2da832830ad451a5936f6858d1e5b538`.

The same source passed [.NET run 33486172442](https://github.com/rayss1/ai-native-unity-framework/actions/runs/33486172442) and [Battle Host run 33500838422](https://github.com/rayss1/ai-native-unity-framework/actions/runs/33500838422). Repository-owned verifiers confirmed source, Fantasy, protocol, configuration, telemetry, replay, wire, impairment, capacity, and 60-minute soak evidence. The two-room/128-Bot profile recorded Tick P99/P99.9 `1.5372/6.1424 ms`, zero slow Ticks and Gameplay allocation, `7.5024%` process CPU, `149,823,488`-byte working set, and per-client downstream/upstream P95 `173.896/43.632 kbit/s` with a `929-byte` maximum datagram. Replay consumed `2,381,638` Inputs and reproduced final Tick/hash `21275`/`7b3d1ab98cfcd13d`. The 60-minute one-room/64-Bot soak recorded 216,005 measured Ticks at Tick P99/P99.9 `1.1245/1.7049 ms`, zero slow Ticks, zero Gameplay allocation, and final Tick/hash `217952`/`0c8e36c2de78e947`.

Attempt 1 of the Battle Host run failed only the unchanged telemetry comparison because an anomalously low `0.2685 ms` baseline made the normal `0.6833 ms` outage sample appear as a `0.4148 ms` increment. One controlled failed-job rerun, without source or threshold changes, passed at `0.7970/0.9864 ms` and `0.1894 ms` delta. This is retained as hosted-runner variance evidence and does not weaken the `< 0.25 ms` gate.

## WS-27 Regional real-client correction evidence

The prediction adapter now maintains a fixed-size millimetre histogram for matched and corrected reconciliations and exposes P95/P99 through `PredictionDiagnostics`. Resetting the diagnostics window clears only bounded counters and histogram state; it does not reset the session, input sequence, prediction history, protocol epoch, or transport. Snapshot processing performs no per-sample allocation.

The macOS validation uses the same exact ARM64 Mono Player and exact-source Host as the WS-26 smoke. After the smoke, it applies `50 +/- 10 ms` one-way delay with 25% correlation, 1% random loss, 0.5% duplication, and 1% reordering with 50% correlation in both directions of the Colima bridge. A ten-second warm-up precedes the 60-second measured window. The gate enforces at least 1,000 reconciliation samples, P95 `<= 250 mm`, P99 `<= 750 mm`, corrections above 250 mm `<= 2` per player-minute, and zero history misses or dropped inputs/frames. The qdisc configuration and statistics are retained with the Player result.

Exact-`main` source `6376265658a26fa07b08fc737c3932d52212314a`, tree `e5ea86c01e85e55475052e75f2fcd876db7069e3`, and Fantasy `f8bed0d464924f159d46498f1311206ea0694be8` passed the reviewed bundle with Unity `6000.3.9f1` revision `7a9955a4f2fa`. After `10.0017 s` warm-up, the ARM64 Mono Player measured `60.0169 s` and produced `1,219` reconciliation samples, correction P95/P99 `9/10 mm`, maximum correction `12 mm`, zero corrections above `250 mm`, and zero history misses, stale Snapshots, dropped prediction inputs, or dropped input frames. The symmetric qdiscs recorded `172` packet drops and were restored afterward.

The enclosing gate passed 38/38 EditMode and 2/2 real-KCP PlayMode tests, the reconnect smoke advanced epoch `4 -> 5` and acknowledgement `30 -> 32`, and the Host drained rooms/KCP with exit code zero and no forced termination. The bundle hash manifest SHA-256 is `1d9a8f403114c9cdcbbc45ebb42f2d0e95b7539ccffa161d5b5e7aeec4339591`. Exact-main [.NET run 33604282890](https://github.com/rayss1/ai-native-unity-framework/actions/runs/33604282890) passed 92/92 tests, its zero-warning build, dependency audit, generated-protocol check, Host publish, and architecture check.

This passes the frozen local macOS Mono Regional correction magnitude/frequency budgets for the deterministic first-slice movement. A local Colima measurement is real-client transport evidence, but it is not a physical Regional network, mobile-device result, game-specific prediction-physics result, production deployment, or real Linux environment canary.

## WS-28 presentation correction evidence

`PresentationCorrectionSmoother` keeps authority and rendering separate. After reconciliation has already updated `ClientPredictionHistory`, the client composes the old display residual with the new simulation displacement. Corrections and accumulated residuals at or below 250 mm decay linearly to zero over 100 ms; larger or untrusted corrections snap. Reconnect and fault boundaries clear the residual before a new epoch can render.

The Battle Client session owns the smoother and the Unity Composition Root applies its millimetre result in `LateUpdate`. Five cross-runtime package tests cover continuity, repeated correction composition, snap behavior, reconnect reset, and zero allocation. One application test proves that a visible correction can remain continuous while the underlying predicted state immediately takes the authoritative value.

Exact-`main` source `b8d4228c20cd9bf05054a956c5cc168711bfdff9`, tree `2241de28438df0a56fa354d42130741f92a978f8`, and Fantasy `f8bed0d464924f159d46498f1311206ea0694be8` passed [.NET run 33755796726](https://github.com/rayss1/ai-native-unity-framework/actions/runs/33755796726) and the reviewed macOS Unity bundle. Unity `6000.3.9f1` revision `7a9955a4f2fa` passed 44/44 EditMode and 2/2 real-KCP PlayMode tests. The ARM64 Mono reconnect smoke advanced epoch `4 -> 5` and acknowledgement `30 -> 31` with zero dropped input frames, and the Host drained normally.

After a ten-second warm-up, the Regional run measured 1,221 reconciliations over 60 seconds: correction P95/P99 `9/10 mm`, maximum `12 mm`, zero corrections above `250 mm`, 1,207 smoothed corrections, zero presentation snaps, final residual `1 mm`, and zero history/input/frame loss. The retained hash-manifest SHA-256 is `496da779c7449f2a6ffb1e59d3a37d99f34dba2ae6f73b40d6f3187569fb2604`; all entries and the `arm64` executable were independently verified. This passes the bounded local macOS composition gate, but it does not establish representative game feel or cross-platform/mobile quality.

## Next gates

1. Exercise the composed client in representative Android and iOS IL2CPP builds; macOS Mono evidence does not satisfy the mobile AOT/stripping gate.
2. Retain Windows as a supplemental desktop validation target; the macOS result does not claim Windows compatibility.
3. Measure the chosen smoothing parameters with game-specific physics, animation/camera presentation, and representative visual-quality captures; the engine-neutral integer baseline alone does not close those product gates.
4. Keep the production room default unchanged and keep the published server candidate out of a production environment until the project owner supplies a real Linux target and approves the independent environment-canary and rollback procedure.

Until those gates pass, the implementation is a reusable baseline and not a claim that client prediction tuning, physics prediction, or production rollout is complete.
