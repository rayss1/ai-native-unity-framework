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

WS-25 adds the Client-layer `com.ainative.client.prediction` candidate. It converts local input to protocol-v1 bytes, maps routed Snapshot/ReconnectResponse acknowledgements into the bounded Shared history, rejects malformed or stale connection data, and exposes transport/backpressure and correction diagnostics without leaking Unity, Fantasy, generated Protobuf, or telemetry SDK types. This freezes only the adapter boundary; a concrete Unity KCP transport, exact-commit Unity evidence, Regional correction distributions, mobile/IL2CPP, smoothing, and prediction physics remain separate gates.
