# ADR-0001: Use Fantasy as the Server Foundation

Status: Accepted
Date: 2026-08-12
Decision source: WS-9

## Context

The framework targets a self-hosted, cloud-neutral, authoritative multiplayer FPS/action stack with an initial 64-player, 60 Tick vertical slice. The server must run independently of Unity, support deep optimization, and allow client and server to compile the same Unity-independent gameplay source. The original .NET 8/.NET 9 runtime assumption was superseded by ADR-0012.

Building sessions, routing, process topology, service discovery, server ECS infrastructure, and protocol tooling from scratch would delay validation of replication and gameplay risks. Fantasy provides relevant C# game-server infrastructure and source access for deep customization; the accepted fork now targets .NET 10.

## Decision

The server platform uses the project-maintained [rayss1/Fantasy fork](https://github.com/rayss1/Fantasy) as its infrastructure foundation. The controlled production source is embedded at `server/vendor/Fantasy` as an exact gitlink without a floating branch.

Fantasy owns the initial implementation of network sessions, TCP/KCP/WebSocket/HTTP transports, Scene/Entity lifecycle, Gate and routing/Roaming, service discovery, server-to-server communication, and related code-generation/bootstrap tooling.

The project owns its fork and will:

- Pin the upstream source to an explicit commit or tag.
- Retain an upstream remote and a documented fork change log.
- Keep customizations as focused, reviewable commits.
- Validate upstream integrations before adoption rather than tracking a floating release.
- Preserve all required copyright and license notices.

Deep customization may cover the fixed-Tick scheduler, KCP/transport behavior, replication, backpressure isolation, generated code, observability, process lifecycle, and deployment integration.

`Gameplay.Shared` remains independent of Fantasy. It targets `.NET Standard 2.1`/C# 9 and is compiled from the same source by Unity and the server. Fantasy Entity, Scene, Session, timer, transport, persistence, and configuration types are confined to server/client adapters and composition roots. Product gameplay rules must not be implemented inside the fork.

[ADR-0012](0012-server-runtime-successor.md) accepts the single `net10.0` product lane. `AiNative.Server.Fantasy` owns Fantasy runtime types; the Battle Host composition root may reference the tracked package for generated startup metadata, while Shared and other product modules remain Fantasy-free.

Fantasy's repository license uses the MIT license text with an additional entity-specific restriction. The project owner accepted that license and project risk for the pinned baseline; any license, ownership, entity relationship, distribution model, or adopted baseline change reopens review.

## Consequences

Benefits:

- The first vertical slice can start from an existing game-server network and process model.
- Source availability permits optimization of the 60 Tick hot path and protocol stack.
- Built-in routing and service discovery provide an evolution path without forcing an early microservice topology.
- The direct .NET 10 target matches the selected Server runtime.

Costs and risks:

- The project assumes ongoing ownership of its fork, upstream synchronization, security review, and migration work.
- Deep modifications can make upstream upgrades expensive unless patches remain isolated and tested.
- Fantasy abstractions can leak into gameplay unless dependency checks enforce the boundary.
- The non-standard license addition requires explicit review before release.

## Validation gates

Fantasy remains the accepted foundation only if the 64-player vertical slice demonstrates:

- Stable 60 Hz simulation with Tick P99 at or below 16.67 ms and core simulation P99 targeting 8 ms or less.
- Zero steady-state managed allocation in the Tick hot path.
- Correct behavior at 100/200 ms RTT, 1%/5% loss, jitter, duplication, and reordering.
- Backpressure isolation so one client cannot delay a Battle room.
- Measured AOI, snapshot bandwidth, correction rate, reconnect, packet capture, and replay behavior.
- Successful .NET 10 build, test, publish, and release-equivalent image validation.
- Successful Unity and .NET execution of shared gameplay test vectors without Fantasy dependencies in `Gameplay.Shared`.

If these gates fail, the project will first replace or redesign the failing Fantasy subsystem behind its adapter boundary. Reconsidering the whole foundation requires a superseding ADR with benchmark evidence.

## References

- [Project Fantasy fork](https://github.com/rayss1/Fantasy)
- [Reviewed public baseline](https://github.com/rayss1/Fantasy/commit/493d5d4dd1dd009cdfcd2846b88ebab9746d4504)
- [Pinned production fork commit](https://github.com/rayss1/Fantasy/commit/f8bed0d464924f159d46498f1311206ea0694be8)
- [Accepted .NET 10 runtime](0012-server-runtime-successor.md)
- [Project-owner license approval](../Architecture/fantasy-license-approval-2026-08-24.md)
- [Fantasy license](https://github.com/rayss1/Fantasy/blob/main/LICENSE)
- [Architecture and Technology Baseline](../Architecture/technology-baseline.md)
