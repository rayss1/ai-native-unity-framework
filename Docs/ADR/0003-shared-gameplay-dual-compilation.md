# ADR-0003: Compile Shared Gameplay from One Source Set

Status: Accepted
Date: 2026-08-13
Decision source: WS-11

## Context

Client prediction and server replay need the same gameplay rules without copying generated or handwritten logic. Unity and server runtime APIs differ, and binary reuse from a modern server target is incompatible with Unity's managed profile.

## Decision

`shared/gameplay/Runtime/**/*.cs` is the single handwritten gameplay source set. Unity compiles it through an asmdef; `Gameplay.Shared.csproj` compiles the same files for `netstandard2.1` with C# 9. Server projects reference that assembly. A `net10.0` binary is never imported into Unity.

Shared owns pure values and ports. It may use the .NET Standard 2.1 surface but must not reference Unity, Fantasy, sockets, filesystems, databases, native bindings, wall-clock APIs, uncontrolled tasks/threads, or locale-dependent behavior on the Tick path.

Simulation time, random state, inputs, physics/navigation queries, and emitted events are explicit inputs. Cross-runtime golden vectors contain initial state, commands, Tick count, expected events, and critical-state hashes. Hash serialization is canonical and versioned.

Unity-only presentation and server-only authority/persistence live in adapters. Conditional compilation is prohibited in Shared gameplay except a documented compiler/runtime compatibility shim with equivalent tests on both sides.

## Consequences

Rules remain reusable and replayable. Adapters must translate engine-specific types and physics differences; this is not a claim of cross-platform physics determinism.

## Validation, migration, and rollback

- CI compiles Shared with `dotnet build` under .NET 10, and Unity executes the identical source and vectors through the active ADR-0014 manual path until credentialed automation is restored.
- A change passes only when event sequences and critical hashes match for every non-physics golden vector. Physics vectors use documented tolerance plus authoritative reconciliation.
- Migrate existing engine-dependent rules by extracting pure state first, then ports, then adapters; retain characterization vectors throughout.
- If dual compilation fails, revert the incompatible API use or isolate it in an adapter. Do not fork the source tree as a rollback.
