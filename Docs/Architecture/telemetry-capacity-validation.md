# Battle Host Telemetry and One-Room Capacity Validation

Status: Automated contract implemented; exact-`main` evidence pending merge
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

`Battle Host production validation` runs the same Linux x64 image twice on the same GitHub runner. Both profiles use 64 real Fantasy KCP sessions, ten seconds of warm-up, and 30 measured seconds at 60 Hz input/30 Hz batches/20 Hz snapshots:

1. exporter disabled, establishing the local baseline;
2. OTLP configured to an unavailable loopback collector, exercising bounded failure behavior.

`tools/release/verify-telemetry-capacity.sh` rejects either profile unless core Tick, allocation, MTU, session, and input-rate gates pass. It additionally requires real metric and trace export failures, zero trace drops for this ordinary workload, tagless bounded project series, and an outage Tick P99 increment strictly below 0.25 ms. It writes `telemetry-capacity.json` with source identities, both latency results, process working set/peak, total CPU time, managed heap, committed GC memory, thread-pool count, runtime, operating system, and processor count.

The workflow retains the summary and raw Host/load/health/log inputs in the `runtime-telemetry-capacity` artifact. A future Battle Host publication must present that unexpired artifact from the same exact successful `main` qualification run as its provenance and 60-minute soak.

## Interpretation and next gate

The capacity fields describe one 64-participant room on the recorded runner. They support leak/regression comparisons but do not establish a rooms-per-process limit, production cost, autoscaling threshold, or reserved headroom. Increasing room density requires a separate multi-room harness and at least 20% headroom in every affected hard budget.

An environment canary remains an operator gate. It needs a named target host, SLO observation window, traffic/admission procedure, compatible fallback digest and configuration, and authority to drain or switch that environment. CI does not infer those details or deploy anything.
