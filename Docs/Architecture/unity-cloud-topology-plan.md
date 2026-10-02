# Unity topology and cloud verification implementation plan

Authority: owner approved completing items 1–4 of the follow-up plan on 2026-10-02. [ADR-0017](../ADR/0017-single-region-service-topology.md) and the [dependency matrix](dependency-matrix.md) remain binding. This plan uses writing-plans and executing-plans, with independent tasks delegated under the repository agent guide.

## Goal and constraints

Two actual Unity clients register/login through Gate, create/ready parties, match, obtain offline entry credentials, connect directly to Battle over KCP, reconnect and display the persisted settlement. Local EditMode/PlayMode and cloud public TLS/KCP evidence must cover this final behavior. Preserve the explicit v1 mode and its tests.

Runtime credentials are never logged or committed. Fantasy, TCP/TLS and envelope framing stay in the client Fantasy adapter. Product orchestration stays in the Unity application. Shared Gameplay and schemas do not gain I/O or vendor types. All networking, ticket renewal and settlement queries run outside fixed Tick.

The cloud remains one static machine for this milestone. Public exposure is only Gate TLS and Battle UDP, limited to verified test sources. The owner has no domain; test TLS uses a certificate with the server IP SAN and explicit SHA256 certificate pin. A wrong pin, invalid date or wrong server identity must fail. This is a test trust configuration, not public-CA/domain production qualification.

No capacity claim is made. Existing Worker density remains provisional. No live battle migration, HA, NodeAgent, mobile or IL2CPP implementation is added.

## Interfaces and ownership

| Producer → consumer | Frozen boundary | Owner |
| --- | --- | --- |
| Gate adapter → application | Project-owned login, party, match admission and profile values; asynchronous bounded calls; cancellation/timeouts; TLS options | Primary |
| Match admission → Battle client | `BattleAdmissionInfo(roomId, bootEpoch, entryTicket, nodeId = "", playerId = "")`; optional topology constructor and same-allocation credential refresh | Battle delegate / Primary |
| Client → Gate | Existing backend.proto methods and Fantasy outer RPC envelope/opcodes from pinned vendor; no server assembly references | Primary |
| Client → Battle | Additive v1 Login fields and response allocation validation; topology reconnect reauthenticates instead of trusting old session ID | Battle delegate |
| Acceptance → deployment | Explicit Gate endpoint/deadline; continuous valid input; owned Compose actions; JSON evidence without secrets | Failure delegate / Primary |

## Task 1: code and CI closure

Files: the two Postgres stores, their two regression files, server-topology-operations.md, existing Unity Pipeline manifest/lock changes. Exclude unrelated ShaderGraph line-ending drift and local launch settings.

1. Inspect actual PR/main state. PR30 is merged; production and validate checks passed. Start `codex/unity-cloud-topology` from origin/main, carrying existing changes.
2. Run whole solution with an isolated real PostgreSQL 17.11 instance and fixed SDK, then architecture check and whitespace validation. Expected: all tests pass without PostgreSQL skips.
3. Review/stage exact owned changes and commit. Publish a new follow-up PR once reviewable implementation is ready; never force-push or merge automatically.
4. After publication inspect the actual new-head CI. Fix real failures and retain evidence; an old-head green run is insufficient.

## Task 2: Unity topology flow

Files: client Fantasy adapter Gate protocol/transport/types and tests; Unity application topology composition, launch options and Battle admission code/tests. Existing scene files are edited only through the reachable editor if necessary.

1. RED: literal/independent protocol vectors and actual local TCP/TLS fixture tests for bounded framing, correlation, errors, cancellation and certificate rejection; add topology admission/reconnect tests.
2. GREEN: implement the bounded Gate adapter using the existing Fantasy wire protocol; TLS framing is needed because the pinned vendor TCP implementation does not encrypt TCP. Test against the actual Fantasy Gate before claiming compatibility.
3. Add product flow with register/login, party create/invite/accept/ready/leave, queue/cancel/status, admission renewal and result/profile display. Backend reconnect recreates sessions; transient lobby/match recovery is explicit. No plaintext secret persistence.
4. Run all affected EditMode tests, actual KCP PlayMode tests and two-client topology flow. Build two standalone instances from exact source and test them, not only protocol probes.
5. Assert that two clients share a global room, receive input acknowledgements, reconnect with the same player entity, observe match completion, and each profile changes once.

## Task 3: public test deployment

Files: versioned deployment support under infrastructure/topology and tools/topology; host-specific paths and addresses in ignored artifacts.

1. Verify source, image identities, current server state and the actual test egress IP. Keep old service running.
2. Prepare IP-SAN certificate remotely, private key mode 0600, a private TLS terminator and public Battle routes. Export public certificate fingerprint only.
3. Present exact firewall changes at the browser action confirmation gate before mutation. No DB, inner RPC, OTLP or health public exposure.
4. Validate TLS handshake rejection and acceptance, then two actual Unity clients over public Gate/KCP. Record source/config/protocol identity and endpoint evidence.

## Task 4: full-duration and failures

1. Extend the probe's 240-second deadline and single-input behavior through explicit options and a bounded continuous input loop. Default short verification remains compatible.
2. Run a real ten-minute match with continuous traffic and separately verify idle client keepalive behavior. Observe completion and durable settlement.
3. Exercise repeated queue/allocation and expired/incorrect credentials; assert no extra authoritative rooms or rewards.
4. During a live battle restart Gate and backend roles: Battle continues; Coordinator rebuilds ownership without duplicating rooms; Lobby/Match recovery asks for re-confirmation.
5. Stop Player and then PostgreSQL in controlled separate runs: existing battles finish, results remain on durable outbox, restore the service and observe eventual single settlement and outbox acknowledgement.
6. Lose a selected Battle process: rooms become Lost, old tickets/epochs fail, replacement starts a different epoch. No migration or duplicate match creation.
7. Verify slow Worker/full mailbox isolation with targeted tests and preserve bounded storage admission behavior. Perform database restore into an isolated database and compare known results; retain original data.
8. Record exact observed evidence and limitations in the ledger/run report. Missing cloud/client proof keeps the corresponding task incomplete.

## Review focus

- TLS wrong pin/name/date must fail; no permissive validation callback.
- Partial TCP frames and malicious lengths cannot allocate unbounded memory; timed-out partial streams cannot be reused.
- Multiple Gate/KCP connections cannot destroy another client scene or retain stale callbacks.
- Credential refresh cannot silently move an active client to another allocation; reconnect reauthenticates and resets prediction safely.
- Fault scripts target only recorded deployment ownership, restore stopped processes in finally, preserve pending results and never emit private material.

Progress and raw evidence: `artifacts/unity-topology-goal/progress.md` and sibling run outputs. These are local verification artifacts, not deployable credentials.
