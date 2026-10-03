# Audit remediation implementation and evidence

Scope: the owner's request to resolve audit findings 1–5. Accepted ADR-0005,
ADR-0017 and the public API catalog remain binding. No dependency upgrade,
public wire change or deployment is part of this work. The owner subsequently
authorized a commit and pull request for these fixes.

## Design and acceptance

| Item | Owner and files | Required behavior and verification |
| --- | --- | --- |
| 1. Simulation cadence | Primary; Unity TimeManager and application tests | Persist 1/60 second fixed updates through the running Editor. A regression reads the actual Unity clock; Shared movement over 60 updates must match the 60 Hz model. Re-run application EditMode and real-KCP PlayMode. |
| 2. Lag compensation | Primary; Server ArenaRoom, replay and Battle tests | Retain 15 committed historical ticks (250 ms) plus the current boundary in preallocated storage. Hitscan uses the bounded client tick, historical target position and lifetime, with current authority for applying damage. Reject invalid or unavailable history without inventing a hit; never hit a respawned/reused entity from its prior lifetime. Rockets continue forward simulation. Record rejection diagnostics, hash history that affects future simulation and update the gameplay fingerprint. Reproduce delayed moving-target hits, window edges, stale history, lifecycle changes, deterministic replay and warmed zero allocation. |
| 3. Disconnect disposal | Client recovery delegate; Fantasy transport/session tests | Closed network state must not suppress owned resource disposal. Disconnect followed by disposal, repeated disposal and concurrent disposal release the owned session once, leave Closed and ignore late callbacks. Unity tests first fail, then pass. |
| 4. Worker termination | Worker delegate; BattleWorkerPool and Rooms tests | Fault, stop and disposal complete pending create/release operations with explicit terminal results. Uninstalled and installed rooms dispose exactly once. Cover fault while mailbox work waits, cancellation races and isolation; retain allocation fencing and bounded mailboxes. |
| 5. Expired login recovery | Client recovery delegate; Gate session, topology flow and tests | Separate valid authentication from connected TCP. Expiry exposes interactive login recovery; automated flow reauthenticates the same account while an active Battle continues. Preserve original allocation/settlement identity, reject a changed account and test with controlled expiry instead of an eight-hour wait. |

Items 3–5 have disjoint implementation ownership. Primary coordinates every
Unity Editor call, reviews all changes and runs the combined validation. Each
task follows failing behavior test → minimal implementation → targeted suite.

## Integration gates

- Release solution build and tests; record genuine skips and environment errors.
- Unity EditMode, simulation cadence and transport lifecycle regressions.
- Real local topology login, admission, reconnect, continuous input and settlement.
- Arena delayed hits, bounded history, replay identity/hash and warmed allocation.
- Architecture validation, whitespace and document consistency.
- Preserve unrelated untracked launch settings and all prior evidence.

## Compatibility and rollback

No protocol field or accepted public port changes. New hit validation implements
the existing authority contract. Arena captures identify their exact gameplay
fingerprint; old captures require the retained verifier built from their source,
and a new verifier must fail closed on an old fingerprint. Drain before adopting
new simulation binaries; rollback selects prior binaries/configuration and keeps
database records, outbox results and replay files. Capacity and platform claims
require fresh qualification and are not inferred from correctness tests.

## Evidence ledger

- Baseline: head 39f2641; only unrelated untracked Host launch settings.
- Unity 6000.3.23f1 / CLI beta12 Editor discovered ready on local port 7800.
- Release solution build: 0 warnings / 0 errors; `release-build.log`.
- Full .NET suite: 332 passed / 0 failed / 0 skipped; 13 TRX reports in
  `artifacts/audit-fixes-20261003/full-dotnet-final`. Includes real PostgreSQL
  (isolated loopback port 15432) and normal-user TLS certificate import.
  The earlier run with the retained 55432 port failed because Windows refused
  that listener; it is diagnostic evidence, not the final result.
- Lag compensation: initial delayed-target and 15-tick-window tests failed;
  final Battle suite 31 passed, including 10 historical-hit regressions,
  deterministic delayed-hit replay and warmed zero managed allocation.
- Local seven-process topology: all 8 acceptance scenarios passed, including
  actual TCP registration/login, two players, KCP input, ticket fencing,
  reconnection, continuous input and PostgreSQL settlement.
- Actual completed ANAR replay verified at tick 1247 / 1627 records:
  `artifacts/audit-fixes-20261003/topology/replay-verification.json`.
- Worker termination: 11 new regression cases; Rooms suite 38 passed with
  PostgreSQL configured. Worker evidence is in `worker/evidence.json`.
- Transport: initial disconnect/repeated/concurrent disposal 3 regressions
  failed, then 11/11 passed in the original Unity Editor. Independent review
  subsequently found in-flight callback races and a Fantasy RuntimeId reset;
  five additional regressions reproduced those races (16 tests: 11 passed,
  5 failed), then all 16 passed after synchronization and stable-ID fixes.
  The independent reviewer rechecked the exact diff and actual XML and closed
  both findings without identifying another serious race or lock cycle.
- Unity fixed timestep is persisted as `2352000 / 141120000 = 1/60`.
  CLI time mutation and native save APIs changed the live value but did not
  persist the file; a one-value YAML patch followed failed save attempts.
  Original and backup settings remain recoverable in Git/evidence. Fresh
  Editor reload in the isolated test project retained the value; both the
  persisted-file and loaded-clock regressions passed. The file survived the
  fresh Editor's normal shutdown unchanged.
- Unity final: 95/95 project EditMode tests passed, including 16 transport,
  5 authentication recovery and 2 clock regressions; topology PlayMode 1/1
  passed with two real clients; legacy KCP PlayMode 3/3 passed, including
  45-second idle keepalive. No skips. XML and full logs are in
  `artifacts/audit-fixes-20261003/client`.
- An original-Editor recovery test used nested NUnit synchronous waiting on
  real TCP and stalled the main thread. The test now uses direct async awaits;
  all final tests ran in an isolated Assets/ProjectSettings copy with the same
  pinned installed package sources. The tool environment also lacked
  ALLUSERSPROFILE; supplying ProgramData to the child fixed UPM startup.
  The existing Editor was not force-closed; restart awaits the owner's choice
  because unsaved editor state cannot be inspected or saved while blocked.
- Architecture check passed; no dependency or public wire changes.
- Before the owner's requested commit/PR, the branch was based on main
  765e722 (the same source tree as 39f2641). Release build again passed with
  0 warnings/errors, the full .NET suite passed 332/332 with PostgreSQL,
  architecture validation passed and isolated Unity EditMode passed 95/95.
  Fresh logs/TRX are in `artifacts/audit-fixes-20261003/pr-validation`;
  Unity XML/log are `client/editmode-pr.xml` and `client/editmode-pr.log`
  under the same evidence directory. PlayMode/topology/replay evidence above
  comes from the implementation run; those checks were not repeated for PR
  creation. The temporary database was stopped after the repeated tests.
- PR #33's initial production CI run 37103655259 failed in
  `TripleFramesDoNotAliasWhileReaderIsDelayed`: the producer could complete
  before the consumer loop started, making the test observe zero frames.
  The other .NET workflow passed the same commit. A forced producer-first
  test case reproduced the identical assertion locally (1 passed / 1 failed).
  The consumer now drains published frames after observing producer completion,
  retaining payload-consistency and nonzero-observation assertions and checking
  all slots are released. Both scheduling cases and all 32 Battle tests passed.
  Red/green TRX are in `ci-triple-frame-red` and `ci-triple-frame-green` under
  the evidence directory. This correction changes tests only; production
  triple-frame ownership and gameplay fingerprint are unchanged.

## Validation limits and next gates

The evidence above was collected from the Windows x64 local worktree using
Unity 6000.3.23f1 and .NET SDK 10.0.401. `-SkipAudit` was used for the local
topology publish; that run does not constitute a fresh dependency audit.
IL2CPP/AOT, mobile, macOS, Regional impairment, capacity/soak, public cloud TLS
and exact-commit release qualification were not rerun. Existing evidence for
older binaries does not qualify the new gameplay fingerprint. Review and
integrate the final diff through the owner's authorized pull request, then rerun
the relevant clean-commit gates before any deployment. Committing and opening
the pull request do not qualify the new binaries for deployment.
Synchronous external room factory/Tick/Dispose implementations cannot be
forcibly interrupted; the pool terminates pending operations and reclaims late
factory results when those external calls return. This is an explicit limit,
not proof of shutdown completion for a permanently blocking external callback.

## Historical-hit execution details

Capture the complete committed state at boundary T before simulating T+1.
The preallocated 16-frame ring covers T through T-15 inclusively. Intake admits
at most one future tick and at most 15 past ticks. At execution, queued hitscan
inputs are checked again against that step's committed boundary; stale or
missing history rejects the shot before ammunition/cooldown/event changes.
Current-step shots retain current simulation behavior. Delayed hits use both
historical shooter origin and historical target position, current validated
aim, historical spawn protection, current alive/protection checks and matching
join/respawn lifetimes. Damage and score apply to current authority only.
Rockets retain forward simulation. Rejection counters are room diagnostics,
excluded from replay state because rejected inputs are not capture records.
History tags, validity, positions, protection and lifetimes enter state hashing.
The new gameplay fingerprint is
`ce3034f62374b59b906abfb31774454f177b126aca2a5c27b9dfb715ba8a8a9f`.
