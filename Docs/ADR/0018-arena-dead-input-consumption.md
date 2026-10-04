# ADR-0018: Consume dead-player inputs without actions

Status: Accepted
Date: 2026-10-04
Decision source: the owner approved fixing the diagnosed input backlog, repeating the full capacity qualification, and committing/publishing PRs after completion.

## Context

After the bounded Fantasy Socket fix, the 128-client one-hour Arena qualification reached95.4344% occupancy but failed three consumed-ACK checks. Offline replay matched every state hash and found31 accepted inputs still queued for each affected player. The dead/respawn branch skipped consumption; repeated skipped Ticks create persistent delay when both incoming input and consumption run at60Hz. Arrival/scheduling variation can also contribute backlog.

## Decision

- An occupied dead player consumes at most one queued command during its ordinary position in the fixed Tick loop. It is processed as a no-op: only the consumed sequence and queue head/count advance. Do not move, turn, fire, switch weapon or collect pickups from this command.
- Keep the existing next-Tick respawn condition, spawn selection, protection interval and health/armor rules. A player killed earlier in the same Tick consumes its command but does not respawn early. A player killed after its own turn consumes nothing additional during that Tick.
- Empty queues do not invent acknowledgements. Finished matches do not process remaining inputs. Accepted/enqueued inputs remain distinct from consumed ACKs.
- Preserve the60Hz simulation, queue capacity, wire messages, match duration and scoring, settlement ordering, zero-allocation Tick contract and all capacity thresholds.
- Record the changed ArenaRoom source in the gameplay fingerprint. Old captures require the retained verifier built from their exact source; a new verifier rejects old gameplay fingerprints. No wire-version migration is needed.

## Alternatives and consequences

Counting enqueue as ACK would misrepresent authoritative processing. Dropping short measured windows or lowering input rate would weaken qualification. Increasing queue capacity would hide latency. Processing multiple active commands in one Tick would alter movement/combat rate. Applying a command after respawning would let dead-life input affect a new life.

The selected behavior changes cross-life command timing and state hashes. It removes the known dead-Tick backlog contributor but does not prove all input delay has disappeared. Functional regression, independently verified replay and a fresh unchanged32/64/128-client ladder plus the highest-passing3600-second soak must provide that evidence.

## Rollback

Restore ArenaRoom and its gameplay fingerprint together, then rebuild into a new isolated directory. Keep captures paired with their original source and verifier. The bounded Socket fork is independent and can remain adopted. See the [diagnosis and evidence](../Architecture/arena-input-backlog-follow-up-2026-10-04.md).
