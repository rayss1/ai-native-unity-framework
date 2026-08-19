# Runtime Acceptance Candidate

Status: Unmerged WS-16 candidate; not ADR-0012 acceptance evidence
Branch: `codex/ws-16-runtime-acceptance`
Base: `codex/ws-14-runtime-operations`

## Implemented candidate surface

- `server/RuntimeAcceptance.sln` builds a `net10.0` Battle Host without adding it to `AiNative.sln`.
- `AiNative.Server.Fantasy` consumes only the tracked, exact-submodule Fantasy package and confines `Session`/`MemoryStreamBuffer` to internal adapter types.
- The realtime adapter caps datagrams at 1,200 bytes, bounds inbound data to 256 KiB by default, reports truncation, and exposes explicit closed/faulted results.
- The Host exposes `/health/live` and `/health/ready`; an evaluation-only drain endpoint makes readiness return 503 before process shutdown.
- OTel metrics/traces are enabled with an optional OTLP endpoint. Runtime correctness does not require an exporter.
- A 64-bot synthetic room runs at 60 Hz. Tests report P99/P99.9, slow-Tick count, and steady-state managed allocation after warm-up.
- `infrastructure/runtime-acceptance` builds an internal Linux x64 image from immutable .NET base-image digests. CI verifies non-root execution, .NET 10 runtime identity, readiness within the candidate, drain within two seconds, and a normal SIGTERM exit within ten seconds, then uploads provenance without publishing the image.

## Evidence boundary

Local SDK 10.0.200 evidence: Release build completed with zero warnings/errors; two adapter tests and two Host tests passed; liveness/readiness/drain worked with an unavailable OTLP endpoint; Ctrl+C exited with code zero.

[Linux candidate run 32280508631](https://github.com/rayss1/ai-native-unity-framework/actions/runs/32280508631) passed at commit `e4e636ced83ca967d90995b2bfb9ea6618629e36` on SDK `10.0.202`: warnings-as-errors build, all four candidate tests, `net10.0` publish, readiness-to-drain transition, unavailable-OTLP operation, normal SIGTERM exit within ten seconds, and direct/transitive vulnerability audit. The established parent [.NET validation run 32280508637](https://github.com/rayss1/ai-native-unity-framework/actions/runs/32280508637) also passed for the same commit.

This is not a qualified 64-player or Fantasy-integrated vertical slice. The candidate does not yet route real Fantasy KCP sessions into the Battle room, execute Regional/Degraded impairment profiles, run the 60-minute soak, prove reconnect/replay over the transport, validate Unity vectors in licensed CI, or satisfy legal review. It must remain on an unmerged candidate branch until those gates pass and ADR-0012 is accepted atomically with the production Host.
