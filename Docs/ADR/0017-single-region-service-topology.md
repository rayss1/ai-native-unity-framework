# ADR-0017: Single-region independent services and fixed Battle Workers

Status: Accepted
Date: 2026-10-01
Decision source: Project owner requested implementation of the six-process plan.
Extends: [ADR-0001](0001-fantasy-server-foundation.md), [ADR-0002](0002-repository-layout-and-module-dependencies.md), [ADR-0010](0010-observability-and-deployment.md).

## Context and decision

Deploy Gate, Player, Lobby, Match, Room Coordinator and Battle as independent processes in one region. Initially each backend role has one active instance; Battle has multiple configured instances. Automatic takeover, dynamic node agents, live battle migration, payment and a general economy are outside this release.

Hosts are composition roots. Product services live in Server modules. Fantasy's pinned revision owns backend transport and process routing behind the Fantasy adapter. Clients use Gate for account/party/match requests and connect directly to Battle through KCP for gameplay. The existing v1 evaluation path remains explicitly selectable and must not bypass authentication in topology mode.

Player owns accounts, profiles and idempotent settlements. Lobby owns transient parties, invitations and readiness. Match owns transient queue membership and matching attempts. Coordinator owns durable global room allocation/ownership records. Battle owns its live rooms; a room is assigned once to a fixed dedicated Worker for its lifetime. Each Worker advances its rooms synchronously at 60 Hz and has a bounded mailbox and fixed room budget. I/O, persistence, discovery, credential verification and result retry run outside Tick. One slow Worker cannot block another Worker; rooms on the same Worker share its budget and crash boundary.

Global match/room IDs are separate from local slots. Every Battle start generates a fresh boot epoch. Allocation is a durable, idempotent state machine: Reserved -> Ready -> Released or Lost. Uncertain creation remains reserved until reconciled; a timeout never authorizes another instance of the same match. Restarted Coordinator blocks new allocation until persisted allocations are reconciled against registered nodes' inventories. Unavailable or replaced epochs terminate their allocations instead of migrating them. Conflicting inventories fail closed.

Player issues short-lived offline-verifiable signed entry tickets bound to player, global room, Battle node and boot epoch. Battle binds an admitted player to one local session; reconnection requires a valid ticket and supersedes the previous connection. Internal control peers authenticate separately from client sessions. Transport encryption/peer admission must be configured for exposed interfaces; health endpoints do not grant administrative authority.

PostgreSQL may be shared, with owned `player` and `coordinator` schemas and separately applied migrations. Services access foreign state through service ports, never foreign tables. Battle writes immutable pending results to a bounded durable volume outside Tick and retries until Player confirms the exact result. Player atomically inserts a result and updates player statistics, rejecting conflicting duplicate payloads. Result persistence failure closes new admission, and does not acknowledge a result.

## Faults and operational limits

Gate restart rebuilds backend sessions; direct battles continue. Lobby/Match restart invalidates transient state and clients reconfirm/requeue. Player/DB outage pauses operations requiring them and Battle retains results. Coordinator restart pauses allocation and rebuilds ownership. Battle loss ends its rooms. Queue, mailbox, connection, room and outbox bounds are configuration limits, not measured production capacity.

## Alternatives and migration

A modular backend would reduce deployment work but does not meet the owner's independent-process requirement. A process per room improves isolation but conflicts with the selected fixed-Worker grouping and adds process overhead. Automatic HA and room transfer require fencing, replicated state and recovery evidence beyond this release.

Add control contracts without changing existing message numbers or removing fields. Roll out the new backend and topology Battle mode separately; retain the old evaluation runner for regression qualification. Drain Battle before changing its worker layout or image. Roll back by draining new rooms and deploying the previous binaries/configuration; retain database migrations and pending results until compatible readers have confirmed them. Never recreate an uncertain match during rollback.

## Evidence

Implementation and verification ledger: [server topology plan](../Architecture/server-topology-plan.md). Existing synthetic capacity reports do not qualify the new topology. Release requires actual cross-process Fantasy routing, durable recovery/failure tests, Unity/.NET vectors and representative gameplay performance evidence with hardware, source, seed, warmup, duration and percentile method.
