# Terminal delivery before room release — approved bounded drain

Status: approved by the project owner on2026-10-05 ("允许最长5秒有界排空方案"), implemented, and qualified on Windows with the original32/64/128 ladder and128-client3,600-second soak. See [final evidence](battle-slice-qualification-2026-10-06.md) and [retained failures](arena-128-path-diagnosis-2026-10-05.md). This document does not supersede an accepted ADR.

## Evidence and constraint

Formal `qualified-capacity-sweep-02` passed32/64/128 short stages but the128-client soak failed after approximately33 minutes29 seconds. All eight clients in one room observed Active tick3582 and then Closed; the room's completed replay independently verifies Finished tick3584. The server had released that room. All546 frozen source and1,057 runtime file hashes were unchanged before investigation edits.

`TopologyBattleEngineTests.RejectedFinalSnapshotDoesNotAuthorizeRoomRelease` reproduced the old behavior as RED: a rejected final snapshot still authorized room release. It now passes with retained retry state. Separately, transport Accepted means queued, not delivered; both adapter disposal and KCP disposal can discard pending terminal data. The recorded failure does not distinguish the exact queue or network boundary where the final state was lost.

The original plan said: “不改变连接生命周期、协议、MTU、战斗规则或结算顺序。” The owner explicitly approved an exception for the following bounded normal Finished room/connection release condition. Protocol, MTU, gameplay, settlement ordering and acceptance thresholds remain unchanged.

## Implementation contract

1. Retain a terminal frame until it is accepted for each current joined connection. WouldBlock keeps it pending for retry; an accepted terminal frame is not regenerated continuously. Terminal event publication must finish before requesting the transmission barrier. Closed/Faulted is delivery failure, not successful completion.
2. Preserve the ordering of terminal publication, durable outbox storage, room release, and connection release. After durable storage, defer room release until terminal sends have passed a Scene-owned adapter barrier and the corresponding KCP outstanding send count is zero. Poll asynchronously outside the Worker Tick; do not wait inside fixed Tick or block the Scene thread. Queue counters alone are insufficient because they decrement before the underlying send.
3. Apply an absolute five-second bound from the first terminal publication attempt, not a renewable per-retry timeout. The next network pump after expiry records failure; scheduling and the existing durable-result/release loop can add delay before physical closure. The bound limits delivery waiting, not persistence: unavailable durable storage continues to retain the result/room under the original contract. A disconnected peer, incompatible vendor contract, or deadline expiry records an explicit terminal-delivery failure. Publish failure counters before authorizing release, so tail capacity audits cannot miss a failure. Finalization state and pending work remain bounded by the existing room/connection limits.
4. Keep vendor inspection inside the server Fantasy adapter, using the fixed dependency contract and the existing reflection-isolation precedent in `FantasyOuterSendBudget`. Read KCP state only on its owning Scene thread. Contract mismatch fails explicitly. The fixed fork's `WaitSendCount` contract is covered by tests and actual Windows KCP delayed-acknowledgement/timeout runs; no vendor package overwrite or fixed-fork change is required.
5. No wire messages, protocol numbering, MTU, gameplay, input accounting, or performance thresholds change. KCP acknowledgement establishes transport reception, not application consumption; do not claim an application-level acknowledgement guarantee. A new application handshake is outside this proposal.
6. A remotely closed Unity transport retains already-admitted inbound packets until the application consumes them or explicitly disposes it. The application drains within its existing256-packet update budget before reconnecting. Settling/AwaitingResult/Completed use receive-only draining, without new input or reconnect attempts; a confirmed settlement remains complete immediately. Automated Player success/exit separately waits at most five seconds for a received Finished state and closed, drained transport, and reports failure if that evidence is absent.

## Required validation

- Turn the retained rejection regression GREEN and cover retry without duplicate terminal regeneration.
- Reproduce Accepted-but-not-drained with a controllable dispatcher; prove release waits across the adapter barrier and nonzero KCP pending count.
- Cover peer closure, vendor contract mismatch, absolute timeout, cancellation, duplicate checks and exactly-once resource release with deterministic signals/time providers.
- Check actual terminal snapshot reception before close over the fixed Windows Fantasy transport, including delayed send/acknowledgement conditions. Do not replace this with timing assertions on mocks.
- Rebuild and repeat applicable .NET/real PostgreSQL, Unity EditMode/PlayMode and dual Windows Player regressions. Freeze the resulting candidate and rerun the full original32/64/128 ladder plus a3,600-second128-client soak. Retain this failed run and the RED evidence.
- Keep unsupported platforms explicitly unverified. Commit and create the follow-up PR only after the original full acceptance criteria are proven.

## Rollback

Revert only the new terminal-delivery state/adapter inspection and associated configuration, rebuild isolated outputs, and retain evidence. This restores prior immediate-release behavior and its known delivery risk; it does not produce a passing128-client capacity claim. The existing Fantasy fixed commit/package identities and unrelated user files remain unchanged.
