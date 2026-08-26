# Battle Host Multi-Room Capacity Validation

Status: Automated two-room candidate contract implemented; exact-`main` evidence pending
Last updated: 2026-08-26

This validation is the measured gate for considering more than one 64-participant room in a Battle Host process. It does not change the production default, publish an image, or authorize a deployment.

## Evaluation boundary

Production remains one room per process. A second room can be enabled only when `AINATIVE_ENABLE_EVALUATION_ENDPOINTS=true` and `AINATIVE_EVALUATION_ROOM_COUNT=2`; startup rejects larger values or any attempt to use the evaluation mode without the explicit evaluation boundary. The Host expands the same project-owned room/protocol services and the same Fantasy KCP gateway to two isolated room assignments and 128 total connections.

Replay format version 1 has no room identity. Multi-room evaluation therefore fails at startup if `AINATIVE_REPLAY_CAPTURE_PATH` is also configured. This prevents ambiguous evidence instead of silently treating two rooms as one replay stream. A production multi-room rollout requires either a versioned room-aware replay format or an explicit decision that preserves the accepted replay gate.

## Release-equivalent profiles

`Battle Host production validation` runs the same non-root, read-only Linux x64 image in a 512 MiB container twice:

1. two rooms and 128 real Fantasy KCP sessions, with 10 seconds of warm-up and 300 measured seconds at 60 Hz input, 30 Hz two-command batches, and 20 Hz snapshots;
2. the same two-room topology for a 60-second Regional `tc netem`/PCAP window, measuring every client independently.

The capacity report records aggregate Tick and Gameplay latency, stable Gameplay allocation, process CPU, working set, managed heap, committed and GC-available memory, thread-pool count, connection count, and source/Fantasy/protocol identities. The wire report retains per-client bandwidth percentiles, datagram sizes, impairment configuration, and the PCAP hash.

## Candidate gates

The candidate must preserve at least 20% headroom in every directly affected hard budget:

| Metric | One-room hard budget | Two-room candidate gate |
| --- | ---: | ---: |
| Tick P99 | 16.67 ms | 13.336 ms |
| Tick P99.9 | 20 ms | 16 ms |
| Gameplay + physics P99 | 8 ms | 6.4 ms |
| Ticks above 16.67 ms | 0.1% | 0.08% |
| Process CPU capacity | 100% of recorded processors | 80% average |
| Process peak working set | GC-reported available memory | 80% |
| Per-client downstream P95 | 256 kbit/s | 204.8 kbit/s |
| Per-client upstream P95 | 64 kbit/s | 51.2 kbit/s |
| Typical datagram P95 | 1,200 bytes | 960 bytes |

Gameplay steady-state allocation remains exactly zero, the absolute datagram maximum remains 1,200 bytes, all 128 clients must connect, and per-client input/batch rates must remain 60/30 Hz within the existing tolerance. `tools/release/verify-multi-room-capacity.sh` produces `multi-room-capacity.json` only when every identity and gate passes. CI retains the summary, raw Host/load reports, logs, netem configuration, PCAP, and wire report in `runtime-multi-room-capacity`.

## Interpretation and next gate

A passing PR run proves the contract and candidate implementation, not the final measurement. The two-room density can be proposed for production only after an exact-`main` run retains the same complete artifact and its measured report is reviewed. A red gate keeps the production default at one room and requires profiling, reduced density, or an adapter/scheduler change; it does not weaken the budget.

Even a passing exact-`main` result does not determine production cost or authorize rollout. The environment Canary still needs a named host, candidate and fallback digests/configurations, an SLO window, traffic/admission procedure, and operator authority to drain or switch the environment.
