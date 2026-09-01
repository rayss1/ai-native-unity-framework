# Client Prediction and Reconciliation Baseline

Status: Implemented prediction baseline; WS-26 Unity transport/application validation pending
Last updated: 2026-09-01
Decision source: ADR-0005, WS-24, WS-25, and WS-26

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

WS-26 acceptance is intentionally evidence-gated. The current desktop gate requires exactly 36 EditMode and 2 PlayMode passes, followed by a macOS Apple Silicon ARM64 Mono Player smoke against an exact-source Battle Host built with fixed .NET images. The macOS entry point records source/tree/Fantasy/protocol/configuration/UPM/image/Unity identities, NUnit XML, Host and Player logs, staged notices, binary architecture, hashes, and smoke JSON. Windows remains a future supplemental platform gate. No WS-26 Unity result is recorded as passed in this document until an exact clean-commit macOS bundle is reviewed.

## Next gates

1. Run and review the exact-clean-commit WS-26 macOS bundle: 36 EditMode, 2 real-KCP PlayMode, and the Apple Silicon ARM64 Mono smoke must all pass with the pinned identities.
2. Capture correction magnitude and frequency from that exact-build client under the reproducible Regional impairment profile before accepting or changing the thresholds in `performance-budgets.md`.
3. Exercise the composed client in representative Android and iOS IL2CPP builds; macOS Mono evidence does not satisfy the mobile AOT/stripping gate.
4. Keep the production room default unchanged and keep the published server candidate out of a production environment until the project owner supplies a real Linux target and approves the independent environment-canary and rollback procedure.

Until those gates pass, the implementation is a reusable baseline and not a claim that client prediction tuning, physics prediction, or production rollout is complete.
