# ADR-0005: Use a 60 Hz Authoritative Simulation

Status: Accepted
Date: 2026-08-13
Decision source: WS-11

## Context

The target action game needs responsive local control and defensible hit validation under latency and loss. Cross-platform physics prevents a safe deterministic-lockstep assumption.

## Decision

A Battle room owns canonical state and advances it on a fixed 60 Hz Tick (`16.666... ms`). `IGameplayClock` supplies Tick index and fixed duration; gameplay never derives Tick time from wall-clock time. The scheduler records overruns and never silently runs concurrent Ticks for one room.

Clients timestamp/sequenced input, predict the locally controlled entity, interpolate remote entities, and reconcile authoritative snapshots. The server validates command rate, sequence, age, authority, and gameplay legality. Deterministic lockstep is excluded.

The server retains a bounded history, initially 250 ms, for rewind/lag-compensated validation. History contains only the state needed by validated queries and is keyed by Tick. The server clamps client-reported time to the accepted window and records rejection reasons.

Record/replay captures versioned initial state, configuration identity, RNG seed/state, ordered inputs, protocol/build identity, and authoritative hashes. Slow persistence, navigation builds, logging export, and blocking I/O do not execute in the fixed-Tick critical section.

## Consequences

Responsiveness comes from prediction while correctness remains server-owned. Reconciliation is a product-visible behavior that must be measured. History costs memory and cannot be unbounded.

## Validation, migration, and rollback

- The 64-player slice must satisfy `Docs/Architecture/performance-budgets.md` under latency, loss, jitter, duplication, and reordering profiles.
- Tune the history window only from measured RTT and memory data; it must cover the accepted validation window without exceeding the room budget.
- New reconciliation algorithms roll out behind a versioned client capability and server configuration, with correction magnitude/frequency compared to the prior version.
- Rollback selects the prior configuration/algorithm and replay format reader. Server authority and the fixed Tick remain invariant unless superseded by ADR.

WS-24 implements the first bounded Shared prediction/reconciliation baseline and an additive recipient-specific Snapshot input acknowledgement. The integer movement rule, history overflow behavior, and zero-allocation rewind/replay path are covered by the same Unity/.NET test sources; a real Fantasy KCP probe verifies the acknowledgement across initial play and reconnect. Unity 6000.3.9f1 passed the 14 shared vectors on reviewed head `9d87fec6c3cf8eea259a957ed84d3e8ae3671ce3`, and exact-`main` source `d2979a742f15eaa1dcfd60f7e1a3292448ceaec9` passed [.NET run 33071031886](https://github.com/rayss1/ai-native-unity-framework/actions/runs/33071031886) plus the complete [Linux production-validation run 33071031962](https://github.com/rayss1/ai-native-unity-framework/actions/runs/33071031962). Those runs prove protocol, replay, wire, capacity, and soak compatibility; they do not execute a real client adapter and therefore do not close Regional correction magnitude/frequency, mobile/IL2CPP, or prediction-physics gates. The detailed evidence is recorded in [Client Prediction and Reconciliation Baseline](../Architecture/client-prediction-baseline.md).

WS-25 adds the Client-layer `com.ainative.client.prediction` candidate. It converts local input to protocol-v1 bytes, maps routed Snapshot/ReconnectResponse acknowledgements into the bounded Shared history, rejects malformed or stale connection data, and exposes transport/backpressure and correction diagnostics without leaking Unity, Fantasy, generated Protobuf, or telemetry SDK types. Exact-`main` source `dfbc0534631ec7cc019919830a93472d3572f61c` passed the 22-test Unity `6000.3.9f1` EditMode suite, [.NET run 33155894500](https://github.com/rayss1/ai-native-unity-framework/actions/runs/33155894500), and [Battle Host run 33155894486](https://github.com/rayss1/ai-native-unity-framework/actions/runs/33155894486). This freezes only the adapter boundary; a concrete Unity KCP transport, Regional real-client correction distributions, PlayMode/mobile IL2CPP, smoothing, and prediction physics remain separate gates.

WS-27 adds a fixed-size millimetre correction histogram to the project-owned prediction adapter and a resettable measurement window without changing prediction or protocol behavior. The macOS Player gate applies the frozen Regional qdisc profile symmetrically across the real Fantasy KCP path, waits through a declared warm-up, then measures the existing P95/P99 and correction-frequency budgets for 60 seconds. Exact-`main` source `6376265658a26fa07b08fc737c3932d52212314a` passed with 1,219 reconciliation samples, correction P95/P99 `9/10 mm`, maximum `12 mm`, zero corrections above `250 mm`, and zero history/input/frame loss. This closes only the local macOS Mono real-client correction gate for the deterministic first-slice movement; smoothing, prediction physics, Windows, mobile IL2CPP, production defaults, and a real Linux environment canary remain independent.

WS-28 adds an engine-neutral, presentation-only smoother in the Client prediction package. It accepts `ReconciliationResult` only after authority has updated simulation, preserves continuity for bounded corrections, decays residual over caller-supplied render time, and snaps on large/untrusted corrections or a reconnect boundary. The initial parameters are 100 ms and 250 mm. Exact-`main` source `b8d4228c20cd9bf05054a956c5cc168711bfdff9` passed [.NET run 33755796726](https://github.com/rayss1/ai-native-unity-framework/actions/runs/33755796726) and the reviewed macOS ARM64 Mono gate: 44/44 EditMode, 2/2 real-KCP PlayMode, reconnect with zero dropped input frames, and 1,221 Regional reconciliation samples at correction P95/P99 `9/10 mm`. Presentation recorded 1,207 smoothed corrections, zero snaps, and a `1 mm` final residual. This does not change the accepted 60 Hz authority decision, protocol, replay, or server behavior; representative game-specific visual/physics evaluation remains required.
