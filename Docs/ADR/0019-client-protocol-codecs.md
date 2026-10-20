# ADR-0019: Isolate client codecs and use generated Protobuf messages

Status: Accepted
Date: 2026-10-20
Decision source: Project owner approved client protocol migration plan
Related: ADR-0002, ADR-0005, ADR-0006, ADR-0009

## Context

Gate and prediction previously maintained handwritten Protobuf field readers. The same battle snapshot could be parsed separately by prediction, remote presentation and the application. This coupled gameplay integration to a wire representation and made schema evolution harder to verify.

## Decision

Add two Client packages, without changing the six-layer boundary:

- `com.ainative.client.protocol`: project-owned values, bounded decode storage and `IBattleProtocolCodec`; depends only on Shared Gameplay/Realtime.
- `com.ainative.client.protocol.protobuf`: generated Protobuf implementation and Gate mappings; depends on the protocol contract, never prediction.

The application explicitly constructs one codec per battle session and injects it. Prediction references only the contract. Gate uses the Protobuf implementation behind its existing business API. Fantasy TCP framing remains a separate transport concern. Generated envelope messages mirror the pinned vendor wire layout; Fantasy types do not escape the adapter.

Use the existing `shared/schemas` generation pipeline, Google.Protobuf 3.35.1 and Grpc.Tools 2.83.0. Do not hand-edit generated C#. Unity consumes reproducibly packaged assemblies, dependency hashes, licenses and stripping configuration.

Decode each received message into one generated object tree, validate it completely, then map to caller-owned bounded value buffers. Prediction, remote presentation and UI consume the same decoded data. Views expire at the next successful decode; consumers copy values they retain. Failed decoding leaves the destination unchanged. Snapshot/event parsing runs outside fixed Tick. Generated parser allocation is permitted there; stable prediction, replay and input encoding retain the zero-allocation requirement.

Reuse input message objects and caller-provided Span buffers. Encoding failure does not advance simulation or sequence. Send backpressure retries the same encoded frame. Codecs are owner-thread-only, are not shared between sessions, and own no transport. The session resets temporary codec state on reconnect and disposes its injected codec; prediction adapters borrow it.

## Alternatives and tradeoffs

Keeping handwritten field readers avoids some receive allocations but duplicates schema logic and review burden. Referencing generated types directly from prediction is simpler initially but prevents replacement without editing core consumers. A general object pool or a second production codec adds complexity without evidence of a failing budget, so neither is introduced.

Generated parsing does allocate on receive. Report it separately from mapping, prediction and input encoding; do not claim the whole network stack is allocation-free. Preserve legacy 64-entity snapshots and Arena 8-player snapshots without interpreting legacy health alone as an Arena marker.

## Migration and rollback

This is an immediate pre-release client source API migration: callers supply `IBattleProtocolCodec` explicitly, read buffer/channel requirements from the instance, and pass decoded value views to remote presentation. No implicit Protobuf constructors remain. Event/snapshot value types move to `AiNative.Client.Protocol`. See [migration guide](../Architecture/client-protocol-codecs.md).

Wire IDs, channels, schemas of existing server messages, gameplay and reconnect semantics remain compatible. Selection is at session construction, not during a match. This does not authorize an incompatible wire format: that still requires a synchronized server migration and ADR-0009 compatibility process.

Rollback restores the client packages, manifests, binary lock and callers together. Server wire format is unchanged. Platform validation and exact-source evidence are recorded separately; acceptance of this design is not a claim that device qualification passed.
