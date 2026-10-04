# AI-Native Client Prediction Adapter

This Unity-ready package connects the project-owned `IRealtimeTransport` and
`ClientPredictionHistory` contracts without exposing Unity, Fantasy, or generated
Protobuf types. It targets protocol v1 InputCommand, Snapshot, and
ReconnectResponse frames.

Create the adapter after JoinRoom supplies the local entity ID. Route complete
Snapshot-channel and ReconnectResponse control frames into `ApplyPacket`. Call
`PrepareInput` on the single prediction owner thread for the allocation-free
predict/encode path, or use `SendInputAsync` outside the fixed-Tick critical path
when the adapter should forward the prepared frame through its transport.

The caller owns packet routing and the concrete transport. Arena remote players
can use the optional `ArenaRemotePresentation` described below.
`PresentationCorrectionSmoother` is an optional presentation-only primitive: it
preserves visual continuity for corrections at or below 250 mm and linearly
decays the residual over 100 ms. Larger or untrusted corrections snap, and a
reconnect resets residual state. It never delays or mutates authoritative
prediction. Concurrent input sends are rejected, history is bounded, and
transport backpressure remains visible. `DisposeAsync` disposes the supplied
transport only when `ownsTransport` was selected at construction.

`PredictionDiagnostics` exposes a fixed-size millimetre correction histogram,
P95/P99 correction values, threshold counts, history misses, and bounded-input
drops. `ResetDiagnostics` starts a new measurement window without resetting the
session, transport, connection epoch, input sequence, or prediction history.
Histogram storage is allocated with the adapter and does not grow per Snapshot.

Runtime code contains no Google.Protobuf dependency. The .NET-only compatibility
tests compare emitted and accepted bytes with the tracked generated Protobuf
types; Unity EditMode tests use fixed protocol fixtures.

## Arena input sending and migration

`ArenaClientPredictionAdapter.PrepareInput(..., destination)` predicts once and
encodes into caller-owned storage. Send precisely that written memory with
`SendPreparedAsync(buffer.AsMemory(0, prepared.WrittenBytes), cancellationToken)`
outside fixed Tick, and await its `SendResult`. The buffer must remain unchanged
until completion. `WouldBlock` can retry the same bytes without another prediction
step. Cancellation and transport exceptions are observable by the caller.

The 2026-10-04 pre-release correction removes the unused
`SendPreparedAsync(int)` and fire-and-forget `PrepareAndSendInput` signatures.
The former sent a different, uninitialized buffer; the latter discarded send
failures. There are no repository consumers of those signatures. External source
consumers must migrate immediately to prepare plus explicit-memory send; there is
no silent compatibility shim. The Unity application already owns a bounded input
ring and sends that ring directly, so its runtime path and wire format do not
change. Rollback must restore both API and consumers together; retaining the
regression test makes the old broken behavior visible.

## Arena remote players

`ArenaRemotePresentation.ApplySnapshot` decodes the existing full snapshot after
the application checks admission and connection epoch. It excludes the local
entity, rejects stale/invalid frames, and maintains at most eight entities with
sixteen historical samples each. Call `Advance` once per rendered frame with an
eight-element output buffer. Poses use world-space millimetres and millidegree yaw.
The six-Tick render delay interpolates position and shortest-path yaw; starvation
holds and then rebuilds the delay, never extrapolating or rewinding render time.
Alive transitions and jumps over five metres snap; absent members disappear.
Call `Reset` whenever the connection or room changes. All methods share one owner
thread and remain allocation-free after construction. The Unity application owns
the corresponding scene objects; presentation never changes gameplay authority.
