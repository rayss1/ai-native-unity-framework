# Client Prediction and Reconciliation Baseline

Status: Implemented baseline; exact-main runtime evidence passed, real-client correction evidence still required
Last updated: 2026-08-28
Decision source: ADR-0005 and WS-24

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

The Runtime assembly contains no Unity, Fantasy, or Google.Protobuf reference. Eight Unity EditMode tests cover v1 bytes/channel selection, rewind/replay, matching state, malformed input, packet truncation/routing, reconnect epochs, backpressure, and steady-state allocation. Three additional .NET-only tests compare its handwritten Unity-compatible wire boundary with the tracked generated Protobuf InputCommand, Snapshot, and ReconnectResponse types. This is working-branch evidence until attached to an exact reviewed commit and executed through the ADR-0014 manual Unity gate.

## Next gates

1. Run the expanded 22-test Unity EditMode suite on the exact reviewed WS-25 commit and retain its ADR-0014 evidence bundle.
2. Supply a concrete Unity client `IRealtimeTransport` implementation and wire login/join plus packet routing in the application Composition Root; the prediction package must remain transport-vendor independent.
3. Capture correction magnitude and frequency from that exact-build client under the reproducible Regional impairment profile before accepting or changing the thresholds in `performance-budgets.md`.
4. Exercise the composed client in Unity PlayMode and representative mobile IL2CPP builds.
5. Keep the published server candidate out of a production environment until the project owner supplies a real Linux target and approves the independent environment-canary and rollback procedure.

Until those gates pass, the implementation is a reusable baseline and not a claim that client prediction tuning, physics prediction, or production rollout is complete.
