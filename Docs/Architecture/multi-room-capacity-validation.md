# Battle Host Multi-Room Capacity Validation

Status: Exact-`main` replay-enabled capacity qualified; environment rollout remains gated
Last updated: 2026-08-26

This validation is the measured gate for considering more than one 64-participant room in a Battle Host process. It does not change the production default, publish an image, or authorize a deployment.

## Evaluation boundary

Production remains one room per process. A second room can be enabled only when `AINATIVE_ENABLE_EVALUATION_ENDPOINTS=true` and `AINATIVE_EVALUATION_ROOM_COUNT=2`; startup rejects larger values or any attempt to use the evaluation mode without the explicit evaluation boundary. The Host expands the same project-owned room/protocol services and the same Fantasy KCP gateway to two isolated room assignments and 128 total connections.

[ADR-0015](../ADR/0015-room-aware-replay-format.md) adds replay format version 2. Its header records room topology and every Input record carries a zero-based room index; verification maintains independent room state and sequence history before comparing the stable combined hash. New captures use version 2, while the reader retains version 1 as a one-room compatibility path and rejects unknown versions or ambiguous topology.

## Release-equivalent profiles

`Battle Host production validation` runs the same non-root, read-only Linux x64 image in a 512 MiB container twice:

1. two rooms and 128 real Fantasy KCP sessions, with 10 seconds of warm-up and 300 measured seconds at 60 Hz input, 30 Hz two-command batches, and 20 Hz snapshots;
2. the same two-room topology for a 60-second Regional `tc netem`/PCAP window, measuring every client independently.

The capacity report records aggregate Tick and Gameplay latency, stable Gameplay allocation, process CPU, working set, managed heap, committed and GC-available memory, thread-pool count, connection count, and source/Fantasy/protocol identities. The same 300-second profile now retains and verifies a version 2 binary replay, its topology and configuration identity, complete Input count, final Tick, and combined state hash. The wire report retains per-client bandwidth percentiles, datagram sizes, impairment configuration, and the PCAP hash.

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

Gameplay steady-state allocation remains exactly zero, the absolute datagram maximum remains 1,200 bytes, all 128 clients must connect, and per-client input/batch rates must remain 60/30 Hz within the existing tolerance. `tools/release/verify-multi-room-capacity.sh` produces `multi-room-capacity.json` only when every identity, capacity, wire, and replay gate passes. CI retains the summary, binary replay and verification JSON, raw Host/load reports, logs, netem configuration, PCAP, and wire report in `runtime-multi-room-capacity`.

## Exact-main evidence

[Battle Host production validation run 32976156874](https://github.com/rayss1/ai-native-unity-framework/actions/runs/32976156874) completed successfully on 2026-08-26 for exact source `ee080b4a2f1af218d909e733b6cc5f3c5e274167`, Fantasy `f8bed0d464924f159d46498f1311206ea0694be8`, protocol SHA-256 `726a80d6a762913b87fe840f0be9086224598bcaadb0e4a7d4e3e44856c0b92c`, and Fantasy configuration SHA-256 `fc0714bcbe7c8c673cf638506a45f3a4440585f4024ff78048434346ab8a66e4`. The release-equivalent container used .NET `10.0.4`, four reported processors, a 512 MiB limit, and Linux `6.17.0.1022`.

After 10 seconds of warm-up, the 300-second replay-enabled capacity window retained 18,011 Tick samples for two isolated 64-Bot rooms and all 128 Fantasy KCP connections. Tick P99/P99.9 were `1.3684/5.9453 ms`, no Tick exceeded `16.67 ms`, Gameplay P99 was `0.0021 ms`, and Gameplay steady-state allocation was zero. Average process CPU was `7.0073%`; peak working set was `162,795,520` bytes (`40.43%` of the `402,653,184` GC-reported available-memory baseline). The load held `60.0001 Hz` measured input and `30.0001 Hz` two-frame batches per client.

The retained format version 2 replay records two rooms with 64 Bots per room. It verified final Tick `21,212`, combined state hash `3683eb7143bc7f01`, and `2,381,794` Inputs. That covers all `2,380,800` load-reported Inputs with 994 setup/tail Inputs, below the one-second allowance of 7,680. The repository-built verifier reproduced the retained verification JSON exactly; the capture contained no dropped records and the final Tick/hash matched the Host report.

The independent 60-second Regional capture retained all 128 clients under `50 +/- 10 ms` one-way netem delay, 1% loss, 0.5% duplication, and 1% reordering. Per-client downstream/upstream P95 were `178.888/43.816 kbit/s`; datagram payload P95 was `860` bytes and the absolute maximum was `923` bytes. The retained PCAP SHA-256 is `640c473ae680a5d32a8317096bc79b295039241fb1740e7c841ba5de2e0787dc`; independent hashing matched it and tcpdump reported zero kernel drops.

`tools/release/verify-multi-room-capacity.sh` independently reproduced the retained summary in normalized JSON form. Host/load logs ended in graceful room and Fantasy gateway drain and contained no failure, fatal, exception, forced-stop, or error records. The same exact-main workflow completed the existing one-room 60-minute soak; that soak remains continuity evidence for the accepted production default rather than a multi-room soak claim.

The earlier no-capture baseline is retained for comparison:

[Battle Host production validation run 32946412201](https://github.com/rayss1/ai-native-unity-framework/actions/runs/32946412201) completed successfully on 2026-08-26 for exact source `1bfc4f09b6176c9a2b0d8cf04122dc9514134512`, Fantasy `f8bed0d464924f159d46498f1311206ea0694be8`, and protocol SHA-256 `726a80d6a762913b87fe840f0be9086224598bcaadb0e4a7d4e3e44856c0b92c`. The release-equivalent container used .NET `10.0.4`, four reported processors, a 512 MiB limit, and Linux `6.17.0.1022`.

After 10 seconds of warm-up, the 300-second capacity window retained 18,011 Tick samples for two isolated 64-Bot rooms and all 128 Fantasy KCP connections. Tick P99/P99.9 were `1.2314/5.9158 ms`, no Tick exceeded `16.67 ms`, Gameplay P99 was `0.0017 ms`, and Gameplay steady-state allocation was zero. Average process CPU was `5.9990%`; peak working set was `169,668,608` bytes (`42.14%` of the `402,653,184` GC-reported available-memory baseline). The load held `60.0000 Hz` measured input and `30.0000 Hz` two-frame batches per client.

The independent 60-second Regional capture retained all 128 clients under `50 +/- 10 ms` one-way netem delay, 1% loss, 0.5% duplication, and 1% reordering. Per-client downstream/upstream P95 were `178.384/43.544 kbit/s`; datagram payload P95 was `859` bytes and the absolute maximum was `944` bytes. The retained PCAP SHA-256 is `55a5461d9073a20fed9736bb721097f813957136c9fdef82b381a7697c6cf9c0`.

The repository-owned verifier independently reproduced the retained `multi-room-capacity.json` byte-for-byte in normalized JSON form, the raw PCAP matched its recorded hash, and Host/load logs contained no failure, fatal, exception, forced-stop, or error records. The same exact-main workflow also completed its existing one-room 60-minute soak; that soak is continuity evidence for the accepted production default, not a multi-room soak claim.

## Interpretation and next gate

The replay-enabled exact-`main` result closes the WS-22 room-aware replay and two-room process-capacity measurement gate. A future red gate keeps the production default at one room and requires profiling, reduced density, or an adapter/scheduler change; it does not weaken the budget.

This result does not determine production cost or authorize rollout. Production remains one room per process. A multi-room rollout still requires a named target host, qualified candidate and fallback digests/configurations, an SLO window, traffic/admission procedure, and operator authority to drain or switch the environment.
