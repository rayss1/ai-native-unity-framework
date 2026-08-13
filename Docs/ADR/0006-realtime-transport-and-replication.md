# ADR-0006: Start with Fantasy KCP and Explicit Replication

Status: Accepted
Date: 2026-08-13
Decision source: WS-11

## Context

The high-frequency data plane needs loss recovery choices, ordering, AOI, delta state, and isolation from slow clients. Exposing Fantasy Session APIs would couple gameplay to the fork and make transport replacement impractical.

## Decision

The first data-plane adapter uses Fantasy's KCP/UDP capability behind `IRealtimeTransport`. The public port exposes channel delivery/ordering policy, connection state, payload ownership, timeout, congestion, and backpressure outcomes. Shared gameplay receives decoded commands and emits state/events; it does not send packets.

Replication is a server module with per-client AOI, baselines, deltas, quantization, priority, capability negotiation, and bounded queues. A room Tick publishes immutable replication input; encoding and socket I/O cannot stall the room. Queue overflow follows an explicit policy (coalesce replaceable snapshots, preserve bounded reliable events, disconnect when recovery cannot be guaranteed).

Input may be sent at up to 60 Hz. Snapshot publication begins at 20 Hz and may increase toward 30 Hz only while the performance and bandwidth budgets remain green. A 60 Hz Tick never implies full-state snapshots at 60 Hz.

Protobuf remains the reliable/general contract. A high-frequency bit-packed codec is allowed only through ADR-0009's evidence gate. Packets include protocol version/capability, room Tick, sequence/baseline identity, and integrity checks appropriate to the channel.

## Consequences

The first slice can use existing Fantasy networking while retaining a replaceable boundary. Replication remains project-owned and requires substantial measurement/tooling.

## Validation, migration, and rollback

- Impairment tests cover 100/200 ms RTT, 1%/5% loss, jitter, duplication, reordering, reconnect, and one deliberately stalled client.
- Pass thresholds are the Tick, queue, packet, and bandwidth gates in `performance-budgets.md`; a stalled client must not move room Tick P99 outside its gate.
- Codec/transport migration uses capability negotiation and dual decode during a bounded compatibility window. Recorded packet fixtures and replay prove equivalence.
- Rollback disables the new capability and routes clients to the last supported adapter/codec; manifests retain the prior client package until rollout completion.
