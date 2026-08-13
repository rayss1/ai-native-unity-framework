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
