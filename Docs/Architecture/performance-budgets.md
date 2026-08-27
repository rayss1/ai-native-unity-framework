# First Vertical Slice Performance Budgets

Status: Provisional numeric budgets; architectural gates are frozen
Scenario: one authoritative 64-player/bot room, 60 Hz, release-equivalent builds
Last updated: 2026-08-27

These are go/no-go engineering budgets, not marketing targets. A measurement report must record hardware/device, build SHA, configuration/content hashes, warm-up, duration, sample count, profiler overhead, and percentile method. Results without that context are diagnostic only.

## Test profiles

| Profile | RTT | Loss | Additional conditions | Purpose |
| --- | ---: | ---: | --- | --- |
| Regional | 100 ms | 1% | Representative jitter, duplication, reordering | Normal acceptance |
| Degraded | 200 ms | 5% | Representative jitter, duplication, reordering | Degradation boundary |
| Backpressure | Regional | 1% | One client consumes no outbound data for bounded intervals | Isolation |
| Soak | Regional | 1% | At least 60 minutes after warm-up | Leaks, queues, drift |

Exact jitter distributions and impairment seed are stored with the test artifact so runs are reproducible.

## Server room Tick budget

The hard wall-time envelope is `16.67 ms`. Sub-budgets are initial P99 caps and total exactly 16.67 ms; work outside the room Tick is excluded but must have its own bounded queue.

| Component | P99 cap | Notes |
| --- | ---: | --- |
| Shared gameplay rules | 5.00 ms | Commands, abilities, damage, state transitions |
| Physics step and gameplay queries | 3.00 ms | Jolt candidate plus normalized results |
| Replication state extraction/publication | 2.50 ms | Encoding and socket I/O occur outside the critical section |
| History, hashing, replay capture | 1.00 ms | Bounded data capture, no blocking flush |
| Scheduler and diagnostics | 1.00 ms | Disabled telemetry path is effectively allocation-free |
| Contingency/OS variance | 4.17 ms | Cannot be reassigned without a measurement note |
| **Total** | **16.67 ms** | Hard P99 gate |

Additional gates:

- Core simulation aggregate (gameplay + physics) P99: **<= 8.00 ms**.
- Full room Tick P99: **<= 16.67 ms** and P99.9: **<= 20.00 ms**; no more than 0.1% of Ticks may exceed 16.67 ms during a qualified run.
- Steady-state managed allocation in the Tick hot path: **0 bytes/Tick** after warm-up. Startup, pool growth during declared warm-up, and test harness allocation are reported separately.
- One stalled client must change room Tick P99 by **< 0.5 ms** and may not create an unbounded queue.
- No blocking file, database, network send, telemetry export, or path-build operation occurs inside Tick.

## Networking and replication budgets

These caps are provisional and must be replaced by measured product targets after the slice, but exceeding one blocks increasing snapshot frequency or room density.

| Metric | Regional gate | Degraded gate |
| --- | ---: | ---: |
| Client input send rate | <= 60 Hz | <= 60 Hz with coalescing |
| Snapshot publication | 20 Hz baseline; <= 30 Hz after evidence | Adaptive, must not amplify congestion |
| Typical realtime datagram | <= 1,200 bytes | <= 1,200 bytes |
| Per-client downstream P95, 60 s window | <= 256 kbit/s | <= 256 kbit/s before deliberate quality reduction |
| Per-client upstream P95, 60 s window | <= 64 kbit/s | <= 64 kbit/s |
| Reliable-event queue | Bounded <= 256 KiB/client | Same; recover or disconnect on overflow |
| Replaceable snapshot backlog | Latest baseline plus <= 2 pending deltas/client | Coalesce to newest recoverable state |

Fragmentation, retransmission, headers, and encryption overhead are included in on-wire bandwidth. Averages alone do not pass; report P50/P95/P99, burst maximum, packet loss/recovery, AOI entity count, and snapshot size.

## Prediction, history, reconnect, and replay

| Metric | Initial gate |
| --- | ---: |
| Authoritative history window | 250 ms initial; never below accepted lag-compensation window |
| Local position correction at Regional profile | P95 <= 0.25 m; P99 <= 0.75 m |
| Corrections above 0.25 m at Regional profile | <= 2 per player-minute after warm-up |
| Reconnect to authoritative playable state | P95 <= 5 s after transport restoration |
| Replay critical-state hash | Exact for non-physics vectors; physics fields use versioned tolerances |

The [WS-24 client prediction baseline](client-prediction-baseline.md) provides the bounded, zero-allocation rewind/replay mechanism and recipient-specific server acknowledgement required to measure these correction gates. Its integer movement vectors do not by themselves pass the Regional magnitude/frequency thresholds; those require an exact-build client adapter and captured impairment traces.

The correction numbers are tuning gates, not truth about game feel. If representative movement speed/map scale makes them invalid, change them only with captured traces, a replacement threshold, and no weakening of server authority.

## Navigation, content, and telemetry

| Area | Gate |
| --- | --- |
| Runtime path requests | No Tick blocking; representative requests P95 <= 10 ms and P99 <= 25 ms on the worker pool; queue bounded |
| Nav artifact | Repeated bake with identical inputs produces identical content hash |
| Content activation | Atomic under process kill; corrupt/incomplete content is never active |
| Content rollback | Next launch selects last-known-good manifest without redownloading already verified retained artifacts |
| Telemetry outage | Buffers bounded; no Tick I/O wait; dropped telemetry counted; Tick delta < 0.25 ms P99 versus exporter disabled |

The telemetry gate uses two sequential 64-Bot profiles of the same release-equivalent image and runner: exporter disabled, then an unavailable OTLP endpoint. Both profiles record process working set/peak, CPU time, managed heap, committed GC memory, thread-pool count, runtime, OS, processor count, and immutable source identities. Passing this comparison establishes only the one-room baseline; it does not authorize additional rooms per process. See [telemetry and one-room capacity validation](telemetry-capacity-validation.md).

The [multi-room capacity validation](multi-room-capacity-validation.md) evaluates a two-room/128-client candidate without changing the one-room production default. It applies the required 20% headroom to Tick, Gameplay, CPU, memory, per-client bandwidth, and typical datagram budgets and requires exact source identities. Exact-`main` run `32976156874` passed the same matrix with room-aware v2 capture, bounded complete Input coverage, and exact final Tick/combined-hash verification. Production authorization remains separate and still requires a named environment canary and rollback target.

## Decision use

- A red hard gate blocks promotion and requires profiling, scope reduction, adapter replacement, or a superseding ADR.
- Snapshot frequency, codec, prediction physics, and rooms-per-process are increased only when every affected hard gate remains green with at least 20% headroom in the constrained resource.
- Results are retained with replay/impairment seeds so a later optimization can be compared against the same workload.
