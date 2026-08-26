# Battle Host Telemetry and One-Room Capacity Validation

Status: Exact-`main` one-room telemetry and soak evidence qualified
Last updated: 2026-08-25

This validation implements the exporter-outage and bounded-cardinality gates in [ADR-0010](../ADR/0010-observability-and-deployment.md) for the first vertical-slice workload. It is a one-room process baseline, not evidence that more than one 64-player room fits in a process.

## Runtime boundary

The Battle Host owns its OpenTelemetry SDK and OTLP adapter. Shared gameplay, protocol, and realtime contracts do not reference OpenTelemetry. The resource identity records deployment, service instance, process, exact parent source, Fantasy gitlink, protocol schema, application configuration, and the fixed 64-participant room target.

Project metrics are tagless in this baseline. CI observes the exported project meter and fails if it sees a tag, more than 16 project series, or a series-overflow event. The trace export queue is bounded and its producer uses a non-blocking enqueue; overflow increments a local counter. Export attempts, failures, and trace drops are available from `/health/telemetry` and the acceptance report. Exporter degradation does not change liveness or readiness.

The following configuration limits fail at startup when out of range:

| Setting | Accepted range | CI outage value |
| --- | ---: | ---: |
| `AINATIVE_OTEL_EXPORT_TIMEOUT_MILLISECONDS` | 100–30,000 ms | 250 ms |
| `AINATIVE_OTEL_METRIC_EXPORT_INTERVAL_MILLISECONDS` | 1,000–60,000 ms | 1,000 ms |
| `AINATIVE_OTEL_TRACE_QUEUE_SIZE` | 128–8,192 records | 256 records |
| `AINATIVE_OTEL_TRACE_EXPORT_DELAY_MILLISECONDS` | 100–60,000 ms | 1,000 ms |
| `AINATIVE_OTEL_TRACE_EXPORT_BATCH_SIZE` | 1–queue size | 64 records |

## Release-equivalent comparison

`Battle Host production validation` runs the same Linux x64 image twice on the same GitHub runner. Both profiles use 64 real Fantasy KCP sessions, ten seconds of warm-up, and 300 measured seconds at 60 Hz input/30 Hz batches/20 Hz snapshots. The measured window provides about 18,000 Tick samples so the P99 comparison is not decided by only a few scheduler-tail observations:

1. exporter disabled, establishing the local baseline;
2. OTLP configured to an unavailable loopback collector, exercising bounded failure behavior.

`tools/release/verify-telemetry-capacity.sh` rejects either profile unless core Tick, allocation, MTU, session, and input-rate gates pass. It additionally requires real metric and trace export failures, zero trace drops for this ordinary workload, tagless bounded project series, and an outage Tick P99 increment strictly below 0.25 ms. It writes `telemetry-capacity.json` with source identities, both latency results, process working set/peak, total CPU time, managed heap, committed GC memory, thread-pool count, runtime, operating system, and processor count.

The workflow retains the summary and raw Host/load/health/log inputs in the `runtime-telemetry-capacity` artifact. A future Battle Host publication must present that unexpired artifact from the same exact successful `main` qualification run as its provenance and 60-minute soak.

## Qualified exact-main evidence

[Battle Host production validation run 32883119254](https://github.com/rayss1/ai-native-unity-framework/actions/runs/32883119254) completed successfully on 2026-08-25. The repository-owned `tools/release/verify-battle-host-qualification.sh` independently accepted the downloaded `runtime-acceptance-provenance`, `runtime-acceptance-soak`, and `runtime-telemetry-capacity` artifacts against an exact checkout of the source commit. The run reported no failed job or gate; `runtime-acceptance-evidence` also retained the qualified impairment, replay, and backpressure inputs.

### Identities and runner

| Identity | Recorded value |
| --- | --- |
| Parent source | `de84ee2563bb959fca1d36d90fd188e745ffc5cf` |
| Fantasy gitlink | `f8bed0d464924f159d46498f1311206ea0694be8` |
| Protocol SHA-256 | `726a80d6a762913b87fe840f0be9086224598bcaadb0e4a7d4e3e44856c0b92c` |
| SDK image | `mcr.microsoft.com/dotnet/sdk:10.0.202-noble@sha256:adc02be8b87957d07208a4a3e51775935b33bad3317de8c45b1e67357b4c073b` |
| Runtime image | `mcr.microsoft.com/dotnet/aspnet:10.0.4-noble@sha256:8b75cdf59a5068d9adfd8a6d202cc7671b2dc8f5f46c51e3b88a0a632e8fad1f` |
| Product image ID | `sha256:d9d71bc91f5e4f8152a56eec90c5f81be68dfa00e01bc16a1a11f5149604c354` |
| Runtime / runner | .NET `10.0.4`; Linux `6.17.0.1022`; 4 processors |
| Outer KCP MTU | 1,150 bytes |

### Telemetry-outage comparison

Both 64-Bot profiles used 10 seconds of warm-up and 300 measured seconds. The unavailable exporter produced real failures while the room remained within every gate.

| Metric | Exporter disabled | Exporter unavailable |
| --- | ---: | ---: |
| Tick P99 | 0.8353 ms | 0.7491 ms |
| Tick P99.9 | 1.0957 ms | 1.0837 ms |
| Tick P99 regression | — | 0 ms (`< 0.25 ms`) |
| Working set | 127,299,584 bytes | 150,921,216 bytes |
| Peak working set | 145,895,424 bytes | 159,375,360 bytes |
| Total processor time | 73,169.097 ms | 74,708.547 ms |
| Managed heap | 10,897,360 bytes | 17,596,816 bytes |
| GC committed memory | 11,948,032 bytes | 23,855,104 bytes |
| Thread-pool threads | 5 | 5 |

The unavailable-exporter profile recorded 332/332 failed metric exports and 1/1 failed trace export. It recorded zero trace drops, four project metric series against a limit of 16, zero tag violations, and zero series-overflow events. The bounded trace queue and tagless-project-metric gates were both true.

### Sixty-minute soak

The release-equivalent image ran one room with 64 real KCP sessions for 10 warm-up seconds and 3,600 measured seconds. The load completed 13,824,000 measured input frames at 59.999984 Hz and 6,912,000 measured input batches at 29.999992 Hz. Peak connections were 64; the Host emitted 4,622,942 snapshot frames totaling 3,642,248,522 bytes, with newest snapshot Tick 217,950.

| Host metric | Result |
| --- | ---: |
| Setup / load elapsed | 21.5063938 s / 3,610.018455 s |
| Host elapsed | 3,610.1409252 s |
| Warm-up / observed / measured Ticks | 600 / 216,604 / 216,004 |
| Final Tick / state hash | 217,953 / `41403eda83419d03` |
| Tick P99 / P99.9 | 0.7143 ms / 0.9897 ms |
| Slow Ticks | 0% |
| Gameplay P99 / managed allocation | 0.0009 ms / 0 bytes |
| Working set / peak | 141,467,648 / 146,898,944 bytes |
| Total processor time | 552,284.858 ms |
| Managed heap / GC committed | 13,008,512 / 26,599,424 bytes |
| Thread-pool threads | 5 |

The soak intentionally ran without an exporter, so all telemetry export attempt/failure/drop and project-series counters were zero. Publication and deployment were not part of this run.

## Interpretation and next gate

The capacity fields describe one 64-participant room on the recorded runner. They support leak/regression comparisons but do not establish a rooms-per-process limit, production cost, autoscaling threshold, or reserved headroom. Increasing room density requires the separate [multi-room capacity validation](multi-room-capacity-validation.md) and at least 20% headroom in every affected hard budget.

An environment canary remains an operator gate. It needs a named target host, SLO observation window, traffic/admission procedure, compatible fallback digest and configuration, and authority to drain or switch that environment. CI does not infer those details or deploy anything. This exact-`main` result therefore closes WS-20's one-room telemetry and soak evidence only; it does not establish multi-room density or authorize an environment rollout.
