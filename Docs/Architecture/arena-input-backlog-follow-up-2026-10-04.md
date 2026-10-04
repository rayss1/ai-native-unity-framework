# Arena input backlog: decision required

Status: owner approved the proposed follow-up and commit/PR publication on2026-10-04; implementation and fresh qualification in progress under [ADR-0018](../ADR/0018-arena-dead-input-consumption.md). All acceptance gates remain unchanged. Fantasy remains at `df4ad5fe5418c8855932de784c7cea6286c4b082`.

## Evidence

`artifacts/battle-slice-20261004/socket-capacity-sweep-01` passed the32/64/128-client short stages, then failed the full128-client3600-second soak solely at three player consumed-ACK checks. Occupancy95.4344%, all four Worker gates, both node gates,875 independent replays, accepted-input audits and physical-host headroom met their thresholds. The original failure remains authoritative.

In room `7996cde8eb60496abbe3a5f4451bdfde`, entities5,6,8 sent measured sequences4194–4225; replay accepted4194–4224, but final consumed sequence remained4193. The window overlaps the last0.5271575s of this generation. No Clear mutation occurred in this window. The harness waits for all final snapshots and associates ACK samples with input send time, so this is not an early receiver exit or missing late-ACK accounting.

`socket-fix/ack-diagnostic-result.json` replays the unchanged linked ArenaRoom/Gameplay implementation, verifies all38058 records and every Tick hash, and confirms31 queued inputs per affected player at finalTick4261. Dead/respawn ticks skipped consumption21/20/18 times respectively. Those skips contribute permanent backlog when both client input and normal server consumption run at60Hz; scheduling/arrival variation can contribute further backlog. Do not infer that every queued input was caused by respawn alone.

Source: [ArenaRoom.TickOnce](../../server/src/Modules/AiNative.Server.Battle/ArenaRoom.cs) skips the input dequeue on its dead/respawn branch. [ArenaCapacityRunner](../../server/acceptance/AiNative.TopologyAcceptance/ArenaCapacityRunner.cs) correctly retains the strict measured-ACK requirement. Accepted replay inputs and consumed acknowledgements are distinct evidence.

## Proposed additional scope

Approve a separate input-lifecycle correction: on a dead/respawn Tick, process at most one queued input as having no movement, aiming, weapon-switch or firing effect, advance its consumed sequence and remove it from the queue. Preserve respawn time/location/protection, damage rules, fixed60Hz, bounded queues, at most one consumed command per player per Tick, and settlement-before-replacement ordering. Empty queues must not invent ACKs; finished matches must not consume or falsely ACK remaining inputs.

This changes cross-life input semantics and state hashes; it is not a pure Socket optimization. Record the accepted decision and update the gameplay fingerprint/replay identity. Old captures must keep their old verifier/source identity. Review adjacent death-in-the-same-Tick behavior before implementation; do not promise this alone eliminates all backlog.

Required regression: repeated deaths with continuous input, no combat/movement from dead inputs, exact next-Tick respawn, no empty/terminal pseudo-ACK, deterministic new-version replay, and zero Tick allocation. Run the affected .NET/Unity functional matrix, freeze a new candidate and repeat the unchanged full capacity ladder plus highest-passing3600-second soak. Retain all failed runs and continue diagnosis if any gate still fails.

Rejected shortcuts: counting accepted inputs as consumed ACKs; skipping short measured player windows; reducing input rate; increasing queue size; extending the match; relaxing occupancy or other budgets; repeating unchanged runs solely to select a passing measurement boundary.

Rollback: restore the prior ArenaRoom implementation and gameplay fingerprint together and rebuild into a new isolated output. The independent Socket fork can remain adopted. The owner's subsequent instruction authorizes completed-work commits, dependency branch publication and PR creation; no deployment or merge is implied.

The required scope approval was granted. Continue through regression, source/binary freeze, full qualification and PR publication; preserve earlier failed evidence.
