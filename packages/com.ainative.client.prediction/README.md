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

The caller owns packet routing, visual smoothing, remote interpolation, and the
concrete transport. Concurrent input sends are rejected, history is bounded, and
transport backpressure remains visible. `DisposeAsync` disposes the supplied
transport only when `ownsTransport` was selected at construction.

Runtime code contains no Google.Protobuf dependency. The .NET-only compatibility
tests compare emitted and accepted bytes with the tracked generated Protobuf
types; Unity EditMode tests use fixed protocol fixtures.
