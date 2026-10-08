# Server topology implementation and verification

Authority: [ADR-0017](../ADR/0017-single-region-service-topology.md) and the owner's implementation request. Existing [module boundaries](dependency-matrix.md), [public ports](public-api-contracts.md) and [performance budgets](performance-budgets.md) remain applicable.

**Current status — 2026-10-08:** this page retains the 2026-10-02 implementation ledger. The later [2026-10-06 Windows report](battle-slice-qualification-2026-10-06.md) passed 32/64/128 total clients and a 128-client one-hour soak, with eight players per room. The earlier open Task 8 statements below are historical, not the current Windows result. Current platform/device/deployment gaps are in [current progress](current-status.md); historical cloud evidence does not qualify the changed source.

## Frozen integration boundary

Shared control messages are generated from `shared/schemas/ainative/v1/backend.proto`. The Server topology domain consumes these messages without Fantasy types. The Fantasy adapter exposes a request/response port with method, correlation ID and Protobuf body, a peer identity and cancellation. It provides bounded requests, timeouts and explicit unavailable errors. Internal caller identity comes from the authenticated transport, never a client-supplied role. Hosts configure exactly one service role; a Gate client cannot invoke Coordinator, Battle creation or settlement operations.

Room allocation carries match ID, global room ID, node ID, boot epoch, allocation ID and players. Repeating a match ID returns the same allocation; changing the roster is a conflict. Local Worker/slot selection is made only by Battle. Reserve acknowledgement means local capacity is actually held. Ready acknowledgement means the room is installed on its Worker. Heartbeats report complete inventories, drain and outbox health. Backend restart creates a new service epoch and transient parties/queues must be re-confirmed.

## Tasks and ownership

| Task | Owner | Depends on | Deliverable and acceptance |
| --- | --- | --- | --- |
| 1. Restore toolchain and pinned Fantasy | Environment executor | None | Local SDK10.0.202 hash verified; exact submodule; baseline build/tests; actual API investigation |
| 2. Freeze topology, Protobuf contracts, enforcement | Primary architect | None | ADR, schemas, compatibility and architecture checks, no forbidden dependencies |
| 3. Identity/Player, Lobby, Match domains | Backend executor | 2 | Owned schema/migrations, login/tickets, party readiness, unique queue membership, cancellation and settlement transactions; behavior tests |
| 4. Coordinator and fixed Workers | Primary architect | 2 | Durable ownership, reboot reconciliation, bounded admission/mailboxes, epoch fencing, fixed room ownership; race/restart/isolation tests |
| 5. Fantasy transport and six compositions | Primary architect | 1–4 | Pinned cross-process request/routing proof; six independently runnable hosts; backend-to-KCP flow |
| 6. Input/prediction/lifecycle repairs | Gameplay executor | 4 | Actual processed-input acknowledgement, bounded ordered input, correct room-relative lifetime, safe malformed input, matching Arena client prediction; v1 regressions |
| 7. Deployment and failure acceptance | Primary architect/reviewer | 3–6 | TLS/internal credentials, PostgreSQL, durable outbox volumes, health/drain, multi-process end-to-end plus outages/conflicts tests |
| 8. Capacity qualification | Primary architect | 7 | Reproducible representative real-gameplay runner; report measured limits only when run on declared hardware |

Implementation was kept in the shared checkout on `codex/server-topology` without commits or pushes until the owner explicitly requested a commit and PR on 2026-10-02. Windows Unity Editor validation is recorded below; Docker, Linux and platform release checks remain external qualification gates. Tests start with failing behavioral regressions, not source-text assertions.

## Preflight interface/conflict scan

| Tasks | Boundary | Resolution |
| --- | --- | --- |
| 2/3/4/5 | Backend messages and request port | Freeze schema before service delegation; only architect edits contracts |
| 3/4 | Player ticket issuance and settlement | Player owns signing/storage; Battle validates public key and retries immutable results |
| 4/5/6 | Battle lifecycle and KCP dispatch | Worker owns room mutation; ingress only enqueues; gameplay executor owns rules/prediction, architect integration |
| 3/7 | Persistence | Test real transactions with PostgreSQL when available; in-memory behavior is not restart evidence |
| 5/7 | Fantasy config/deployment | Static complete scene directory, process selection at startup, no invented vendor API |
| 1–8 | Task internal consistency | Environment fixes stay in artifacts/vendor; v1 tests remain; no synthetic capacity claims; unavailable checks must be recorded |

## Execution ledger

- Initial: no topology services implemented. SDK restore and pinned Fantasy investigation in progress. Docker daemon unavailable. No runtime validation claimed.
- Local branch creation required filesystem escalation and succeeded. No commit or remote operation performed.
- SDK10.0.202 restored and official SHA512 checked; pinned Fantasy source and 2026.1.1003 package available. PostgreSQL17.11 runs in an isolated local test instance. No Docker daemon, Unity editor or IL2CPP toolchain available.
- Six role compositions, static Fantasy routing, per-peer RSA authentication, offline entry tickets, owned PostgreSQL schemas, Gate routing, parties, matching, durable Coordinator and fixed Worker Battle are implemented. v1 evaluation mode remains the default; topology mode is explicitly selected.
- Actual loopback Fantasy TCP/KCP + PostgreSQL acceptance passed: login/party/queue/allocation/direct battle/reconnect/settlement. Gate restart left Battle progressing. Two real duplicate settlement submissions preserved one statistic update per player. Evidence: `artifacts/topology-work/acceptance-final.json` and `duplicate-settlement-final.json` (local run outputs, not production qualification).
- Advance result reservations, distinct Arena deterministic replay, legacy acknowledgement integration, Coordinator partition fencing/database ownership liveness, bounded telemetry, actual process failure scenarios and the representative KCP diagnostic runner are implemented. Final local verification follows below; external deployment/capacity gates remain open.

## Final local verification — 2026-10-02

The current source builds in Release with **zero warnings/errors**. The whole solution test run passed **251 tests, zero failures, zero skips**, including isolated real PostgreSQL tests. Evidence: `artifacts/topology-work/final-build.log`, `final-tests.log` and `completion-final-tests/`.

| Suite | Passed |
| --- | ---: |
| Client prediction / Shared Gameplay | 19 / 20 |
| Shared Protocol / Server Protocol / Realtime | 8 / 4 / 2 |
| Fantasy adapter / architecture rules | 8 / 37 |
| Legacy Battle Host / topology Battle | 49 / 22 |
| Coordinator, Workers and durable outbox | 26 |
| Player, Gate, Lobby and Match | 56 |

The full repository architecture scan passed (`architecture-repository.txt`). Explicit protocol generation compared both tracked generated files with SHA256 and changed neither (`protocol-drift-final.json`). Updated document links, PowerShell parsing and `git diff --check` passed. An enabled-audit whole-solution restore succeeded; local acceptance publishes separately used explicit `SkipAudit`, so they do not provide an independent audit result.

Independent final review found no remaining Critical/Important issue after inspecting the source, final tests and actual process evidence; the reviewer independently ran the repository architecture check. Review: `artifacts/topology-work/topology-review-completion.md`. Its acceptance applies to the local implementation, with the external qualification limits below.

Actual loopback tests use seven independent business processes, real Fantasy TCP/KCP, PostgreSQL 17.11 and durable local directories:

| Case | Evidence under `artifacts/topology-work/` |
| --- | --- |
| Login → party → queue → allocation → KCP battle → reconnect → settlement; Gate restart; exact duplicate settlement | `acceptance-final.json`, `duplicate-settlement-final.json`, `acceptance-replay-final.json` |
| Both members receive individual ready tickets after only the leader queues | `party-notifications-final.json` |
| Independent Coordinator, Match and Lobby restart; ongoing battle finishes | `backend-restarts-final.json`, `backend-restarts-final-actions.json` |
| Player unavailable across completion; durable pending result survives and retries once after recovery | `player-outage-final.json`, `player-outage-final-actions.json`, `player-outage-final-replay.json` |
| Battle crash, terminal Lost, different boot epoch, rejection of old ticket and successful new match | `battle-crash-final.json`, `battle-crash-final-actions.json`, `battle-crash-final-replay.json` |
| Actual expired ticket rejected while the original room remains active | `expired-ticket-final.json` and its replay/actions reports |
| Eight rooms, sixteen moving/firing clients; ninth request has no allocation and can cancel | `bots-final.json` and raw latency/Worker/hardware reports |
| Latest Coordinator binary accepts a new real allocation after quota routing correction | `latest-coordinator-allocation-final.json` |

Complete ANAR captures were independently replayed with their source-worktree, Fantasy, protocol and configuration identities. Backend-restart gameplay assertions passed, but that run's automatic replay step was interrupted by the CLI path migration; separate final primary, Player-outage, Battle-crash, expired-ticket and bot captures passed the new CLI. The verifier resides under `server/acceptance/AiNative.ArenaReplay`, preserving the Tools→Server prohibition.

Final review fixes have behavioral evidence: bounded cancellation tombstones prevent delayed joins after uncertain cancellation; unjoined connections expire and closed transports return capacity; replay quota rejection releases definite pre-creation reservations; a fresh inventory cannot discard a heartbeat's negative admission gate; confirming an older result cannot reopen admission after another durable write failed. The latter two have retained failing/passing regressions (`quota-routing-red/green.log`, `outbox-confirm-red/green.log`). Final full tests include both fixes. Process tests preceded the final outbox health-latch change, which was validated by real filesystem failure/retry tests rather than repeating unrelated long bot runs.

The bot observation is a **diagnostic, not production density qualification**: Intel Core i7-11800H, sixteen logical processors, Windows 10.0.26200, .NET runtime 10.0.6/SDK10.0.202; two nodes × two Workers × two rooms; seed 20261002; three-second warmup and ten-second observation. At 20 Hz moving/firing input, 2,989 observed input-to-snapshot acknowledgements gave nearest-rank p50/p95/p99 **47.9863/73.9133/81.2453 ms**. Four Workers retained 591–593 deduplicated Tick samples each, with p99 **62–72 μs**, zero sampled over-budget ticks and zero measured Worker allocation increment. Process-wide GC, memory and bandwidth are recorded separately. Loopback, short duration and possible concurrent repository work prevent extrapolation to production rooms/core or sustained tail latency.

Owned service processes were stopped, with `final-owned-process-cleanup.json` reporting no remaining owned PID. The owned portable PostgreSQL instance was also stopped after final tests, checking its executable and startup identity first (`postgres-cleanup-final.json`). SDK, local database files and run evidence remain under ignored artifacts for reproducibility. No changes were staged, committed, pushed or published.

Local implementation/functional acceptance for tasks 1–7 is complete. Task 8 has a reproducible real-gameplay diagnostic runner and local evidence; target-machine sustained capacity qualification remains open. Linux containers, TLS client routing, multi-machine partitions, actual OTLP collector delivery, Unity Editor/IL2CPP/mobile and filesystem power-loss durability were not run. Worker count/room density stays provisional until those relevant gates are satisfied; automatic backend takeover and Battle migration remain explicitly out of scope.

## Implementation limits and recovery contract

Follow-up Unity validation on **2026-10-02** closed the local Windows Editor/Mono development check for the current worktree: CLI 1.0.0-beta.12 with Unity 6000.3.23f1 passed **56 EditMode and 2 real-KCP PlayMode tests**, built the Windows Player and passed its reconnect smoke. The **20 Shared Gameplay tests** also passed on .NET SDK 10.0.401 with identical test identities on both runtimes. See [Unity CLI validation](unity-cli-validation.md). This uses the retained v1 client/Host path and uncommitted-source evidence; it does not close exact-commit release, the new topology's Unity frontend, IL2CPP/mobile, Linux or capacity qualification.

Loss of a heartbeat makes the allocation terminal `Lost`; its roster remains quarantined until an authenticated node report proves the old epoch replaced or an authenticated release stops the old room. The resulting `authority_fenced` proof is persisted, so another Coordinator restart cannot resurrect a cleared quarantine. A partition alone cannot authorize a second authority for the same players. This intentionally reduces rematch availability during an unresolved partition. A returning node must release terminal rooms before becoming selectable. A repeated match identity always retains its old terminal allocation.

Coordinator retains at most 100,000 allocation records in its in-memory catalog and refuses new identities at the limit; it does not discard tombstones or silently recreate a match. The PostgreSQL loader reads at most 100,001 records and fails startup on overflow. Room-list responses page at most 16 records using an additive cursor. An archival/read-through catalog is a future explicitly versioned migration, not an implicit deletion policy.

Coordinator owns an advisory lock on the same database connection used for all writes. Its periodic ownership probe checks that connection; once lost, allocation stays disabled until an operator restarts the Coordinator. It never reconnects and assumes it still owns the lock. PostgreSQL outage may require manual Coordinator restart even after the database returns.

Control telemetry uses fixed method/role dimensions, bounded OTLP export outside Tick, and signed trace context. It does not include credentials, player IDs or room IDs in metric dimensions. Backend pump failures emit throttled sanitized records. Real multi-machine routing/TLS, Unity vectors and production density remain external release gates.

## Dependency pins and upgrade boundary

Npgsql **10.0.3** is the topology's direct ADO.NET provider (no EF Core runtime dependency). Its signed NuGet metadata declares the **PostgreSQL** license and source commit `d3768398c17877b3a916c3c4d87e8e11698991fc`. Preserve that license notice in distributions; the package is pinned in both owning Server module manifests. PostgreSQL **17.11** and OpenTelemetry Collector **0.123.0** are pinned in deployment configuration; their license/operational references are in [operations](server-topology-operations.md). New Hosts reuse OpenTelemetry **1.17.0** (Apache-2.0) from the existing Battle baseline. Fantasy stays at the accepted fork commit/package and its entity-specific license approval.

Upgrade one fixed dependency at a time after release-note/license review, successful vulnerability audit, exact protocol generation, real service-routing/restart/settlement tests and target-platform builds. Retain previous package/image identities, schema compatibility and durable results for rollback. Never downgrade by deleting room tombstones, settlements or pending results; restore compatible readers or use a separately reviewed migration.
