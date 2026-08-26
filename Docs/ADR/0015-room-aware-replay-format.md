# ADR-0015: Adopt a Room-Aware Replay Format

Status: Accepted
Date: 2026-08-26
Decision source: WS-22

## Context

Replay format version 1 records one Bot count, one ordered input stream, and one final state hash without a room identity. It is deterministic for the accepted one-room Host, but interpreting a two-room capture as version 1 would merge equal entity indices and per-entity input sequences from different rooms. Rejecting that ambiguous combination was correct, but it left room density unable to retain the replay evidence required by ADR-0005 and ADR-0012.

## Decision

New captures use replay format version 2. The header records the room count and Bots per room before the existing RNG, source, Fantasy, protocol, and configuration identities. Every Input record records a zero-based room index before the shared authoritative Tick, entity index, frame length, and unchanged production protocol bytes. The footer retains the final shared Tick, the ordered combined room-state hash, and the dropped-record count.

All rooms advance on the same fixed 60 Hz scheduler. Verification maintains independent simulation state and input-sequence history per room, applies each record only to its declared room, advances every room to the next recorded Tick, and compares the final combined hash in stable room order. Invalid room indices, unsupported topology, reordered inputs, dropped records, identity drift, truncation, trailing bytes, and hash drift fail closed.

The writer emits only version 2. The verifier continues to read version 1 as a one-room stream and reports the detected version/topology. Unknown versions are rejected. Version 2 is not down-converted because removing room identity would create ambiguous evidence.

Capture remains bounded and asynchronous. The room Tick only transfers an owned frame into the existing bounded channel; file creation, serialization, and flushing remain outside the Tick. A full channel increments the existing dropped-record metric and makes verification fail.

## Consequences

One reader can inspect retained one-room version 1 artifacts and new one- or two-room version 2 artifacts. The additional four-byte room index per Input record and one extra topology field in the header increase capture size slightly. Replay files remain internal evidence rather than a public gameplay protocol.

An older Host can still run or be selected as a deployment rollback, but its version 1 verifier cannot inspect new version 2 files. Operational evidence tooling must therefore use the current reader, while retained version 1 artifacts remain available for historical comparison.

## Validation, migration, and rollback

- Unit tests generate and verify independent two-room inputs and their combined hash, retain a handcrafted version 1 compatibility fixture, and reject invalid topology/order/hash/drop conditions.
- The release-equivalent two-room/128-client capacity profile enables version 2 capture, verifies exact source/Fantasy/protocol/configuration identities, requires the replay to cover every load-reported Input with no more than one second of accepted setup/tail Input, and matches final Tick/hash against the Host report.
- The repository capacity verifier treats the replay JSON and retained binary file as mandatory evidence; a replay failure blocks the density candidate without weakening performance budgets.
- Exact-`main` [run 32976156874](https://github.com/rayss1/ai-native-unity-framework/actions/runs/32976156874) passed the replay-enabled two-room matrix for source `ee080b4a2f1af218d909e733b6cc5f3c5e274167` and Fantasy `f8bed0d464924f159d46498f1311206ea0694be8`. Its retained version 2 capture verified two 64-Bot rooms, final Tick `21,212`, combined hash `3683eb7143bc7f01`, complete bounded Input coverage, and zero dropped records.
- Production remains one room per process until the independent environment canary/rollback gate is approved.
- Rollback returns density to one room and may continue reading either format with the version 2 reader. No tool rewrites or silently discards room identity.
