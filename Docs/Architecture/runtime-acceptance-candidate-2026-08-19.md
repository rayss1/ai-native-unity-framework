# Runtime Acceptance Candidate

Status: Unmerged WS-16 candidate; not ADR-0012 acceptance evidence
Branch: `codex/ws-16-runtime-acceptance`
Base: `codex/ws-14-runtime-operations`

## Implemented candidate surface

- `server/RuntimeAcceptance.sln` builds a `net10.0` Battle Host without adding it to `AiNative.sln`.
- `AiNative.Server.Fantasy` consumes only the tracked, exact-submodule Fantasy package and confines `Session`/`MemoryStreamBuffer` to internal adapter types.
- `AiNative.Server.Protocol` owns the two-byte message envelope, validates message-ID/type pairs, maps control/input/snapshot/event traffic to explicit delivery channels, rejects malformed frames, and enforces the 1,200-byte datagram ceiling without exposing Fantasy types.
- The realtime adapter caps datagrams at 1,200 bytes, bounds inbound data to 256 KiB by default, reports truncation, and exposes explicit closed/faulted results.
- The Host exposes `/health/live` and `/health/ready`; an evaluation-only drain endpoint makes readiness return 503 before process shutdown.
- OTel metrics/traces are enabled with an optional OTLP endpoint. Runtime correctness does not require an exporter.
- A 64-bot synthetic room runs at 60 Hz. Tests report P99/P99.9, slow-Tick count, and steady-state managed allocation after warm-up.
- `infrastructure/runtime-acceptance` builds an internal Linux x64 image from immutable .NET base-image digests. CI verifies non-root execution, .NET 10 runtime identity, readiness within the candidate, drain within two seconds, and a normal SIGTERM exit within ten seconds, then uploads provenance without publishing the image.

## Evidence boundary

Local SDK 10.0.200 evidence: Release build completed with zero warnings/errors; two Fantasy-adapter, three protocol-adapter, and two Host tests passed; liveness/readiness/drain worked with an unavailable OTLP endpoint; Ctrl+C exited with code zero. The protocol tests include a 64-player snapshot within the fixed 1,200-byte frame budget.

[Linux candidate run 32280508631](https://github.com/rayss1/ai-native-unity-framework/actions/runs/32280508631) passed at commit `e4e636ced83ca967d90995b2bfb9ea6618629e36` on SDK `10.0.202`: warnings-as-errors build, all four candidate tests, `net10.0` publish, readiness-to-drain transition, unavailable-OTLP operation, normal SIGTERM exit within ten seconds, and direct/transitive vulnerability audit. The established parent [.NET validation run 32280508637](https://github.com/rayss1/ai-native-unity-framework/actions/runs/32280508637) also passed for the same commit.

[Release-equivalent image run 32281076432](https://github.com/rayss1/ai-native-unity-framework/actions/runs/32281076432) additionally passed the Linux x64 container gates at commit `6d28c5cbdbd879c67e73277a23aa77b7429d2edb`. It used the same immutable SDK/runtime digests recorded by WS-14, pinned Fantasy `b65e6fd60224cf264a3ee62207f0f9041e9f6d92`, protocol SHA-256 `a94af66598d7933236364ae227008c030870b25a9e7bf492a036972df82d28e0`, and produced local candidate image ID `sha256:6330e2364c77c1c5239026a5cca41c702eac73d14b49851be8865338f62914f5`. The image ran as non-root, selected `net10.0`, changed readiness to 503 within two seconds of drain, and exited normally under the ten-second SIGTERM limit. It was not pushed or distributed.

This is not a qualified 64-player or Fantasy-integrated vertical slice. The candidate does not yet route real Fantasy KCP sessions into the Battle room, execute Regional/Degraded impairment profiles, run the 60-minute soak, prove reconnect/replay over the transport, validate Unity vectors in licensed CI, or satisfy legal review. It must remain on an unmerged candidate branch until those gates pass and ADR-0012 is accepted atomically with the production Host.
