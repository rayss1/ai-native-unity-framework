# Client Prediction and Reconciliation Baseline

Status: Implemented candidate; cross-runtime and impairment evidence still required
Last updated: 2026-08-27
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

Local .NET 10 validation on the WS-24 working candidate used SDK `10.0.204`, selected by the repository's `10.0.202` `latestPatch` policy, and completed a zero-warning Release build and the repository test matrix. Shared prediction vectors cover clamping, exact fixed-step movement, acknowledged-input discard, rewind/replay, bounded-history loss, authoritative reset, and a 1,000-iteration steady-state zero-allocation loop. Protocol tests retain stable bytes for the additive acknowledgement and verify legacy defaulting.

A real local Fantasy KCP Host probe then sent input sequence 1, received a Snapshot acknowledgement of 1, disconnected, reconnected with a new connection epoch, and received the same acknowledgement in the resume Snapshot. The Host drained normally after the probe.

The deterministic macOS diagnostic profiles also remained green with protocol identity `3cb86e21687e65af0e0d409d9186384d0f959fd6aa873eb9e1cd0cb39c77d37d`: the worst Tick P99/P99.9 across Regional, Degraded, and Backpressure was `0.0821/0.1937 ms`, the Backpressure Tick P99 increment was `0.0545 ms`, per-client downstream/upstream P95 stayed at or below `144.368/26.424 kbit/s`, and the maximum application frame was `799 bytes`. These in-process profiles are not qualified Linux socket/netem evidence.

This is diagnostic candidate evidence until attached to an exact commit. It does not pass the Regional correction-frequency/magnitude gates, Unity exact-commit validation, mobile/IL2CPP behavior, or a Linux production-validation run.

## Next gates

1. Run the shared prediction vectors in Unity 6000.3.9f1 on the exact reviewed commit and retain the manual evidence bundle while ADR-0014 is active.
2. Run the existing Linux Regional, Degraded, Backpressure, replay, bandwidth, allocation, and soak workflows after the protocol identity changes.
3. Add a client transport/protocol adapter that maps local inputs and authoritative snapshots to the Shared history without exposing generated or Fantasy types through Shared APIs.
4. Capture correction magnitude and frequency under the reproducible impairment profiles before accepting or changing the thresholds in `performance-budgets.md`.

Until those gates pass, the implementation is a reusable baseline and not a claim that client prediction tuning, physics prediction, or production rollout is complete.
