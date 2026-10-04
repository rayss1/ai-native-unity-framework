# Battle slice: final local qualification

Status: **qualified at 64 concurrent clients on the recorded Windows host**. The full sweep and highest-passing 3,600-second soak completed with exit 0. The 128-client profile failed and is not qualified. No acceptance thresholds were reduced.

The measured source is `4c7e4929f559eb977dd0bac3e5f4821d6f3b1df2`, with Fantasy `df4ad5fe5418c8855932de784c7cea6286c4b082`. The final documentation commit follows measurement; it does not change the measured code. [Machine-readable evidence](battle-slice-qualification-2026-10-05.json) records source, protocol/configuration, package and binary identities, exact metrics, and SHA-256 hashes for 30 retained reports. All 530 frozen tracked files were unchanged after the run.

## Delivered behavior

- Fantasy.Net and Fantasy.Unity set the buffer target directly, then use bounded binary fallback only after rejection, with at most 32 writes per buffer. Windows default capacity is retained. Wide arithmetic prevents overflow; observed OS clamping/scaling stops probing. Public signatures/defaults remain unchanged. Nonpositive attempts or zero step do nothing; positive attempts with a negative step now throw an argument exception.
- The local Unity package and every server consumer use the pinned fork. Versions are Fantasy-Net `2026.1.1004-ainative.1` and Fantasy.Unity `2026.1.1002-ainative.1`; old packages and third-party notices are retained. All 63 published Fantasy DLL copies across functional/capacity outputs match the adopted package DLL.
- [ADR-0018](../ADR/0018-arena-dead-input-consumption.md) consumes at most one queued command during a dead/respawn Tick without applying gameplay actions. Replay identity changed with this behavior; historical captures retain their matching old verifier.
- The integrated slice includes explicit-memory prediction sends, remote-player interpolation/visibility, foreground readmission suppression, real PostgreSQL CI gates and bounded qualification lifecycle handling. Protocol fields, MTU, authority, 60 Hz cadence and settlement-before-replacement ordering remain intact.

## Capacity result

Two Battle nodes, two Workers per node, eight players per room; seed `20261002`, 60 Hz input, 60-second warmup, 300-second short windows and a 3,600-second highest-passing window. Maximum match length is 36,000 Ticks; normal score completion and replacements remain part of occupancy accounting.

| Profile | Clients | Measured seconds | Completed matches | Occupancy | ACK P99 | Result |
|---|---:|---:|---:|---:|---:|---|
| 1 room/Worker | 32 | 300 | 26 | 95.5443% | 121.05 ms | Pass |
| 2 rooms/Worker | 64 | 300 | 54 | 95.8431% | 119.63 ms | Pass |
| 4 rooms/Worker | 128 | 300 | 104 | 94.2953% | 137.17 ms | Fail |
| Highest passing soak | 64 | 3,600 | 486 | 95.8047% | 122.55 ms | Pass |

The 128-client stage failed the existing 95% occupancy threshold and 48 player input-rate checks (58.73–59.39 Hz). Its independent replay/input and physical-headroom audits passed; those do not override native failure. Selection of 64 clients follows the pre-existing stop-at-first-failure rule.

The soak ran from `2026-10-04T15:46:31.0538134Z` to `2026-10-04T16:46:31.1473795Z` (23:46–00:46 UTC+8). All 3,888 player records passed the native checks; 3,832 overlap the measurement window and 56 have zero overlap, excluded by the existing input-audit rule. All 486 independent captures and accepted-input audits passed. There were 4,415,472 ACK samples, 13,232,860 measured sends and 13,227,621 accepted measured inputs. No measured event sequence gaps occurred; 4,623 estimated ammo-exhausted attempts were exercised.

All four Workers passed with zero measured business allocation, 215,983–215,989 Tick samples each, and business Tick P99 of 96–100 microseconds. Whole-host CPU P99 was 60%, maximum physical-memory use 72.9341%, with 1,386 samples and a maximum boundary-inclusive gap of 4.001 seconds (required at most 5 seconds). Percentiles use nearest rank. Hardware: i7-11800H, 16 logical processors, 33,963,081,728 bytes RAM, Windows build 26200. Four existing Unity-related user processes remained running; this is not dedicated-host capacity.

The workload is explicitly `staggered-ammo-cycle-v2`: stagger initial weapons and rotate after 240 accepted sends at most, or after an accepted Fire request against a magazine observed as empty. Live ammo state is inferred from reliable gameplay events; the deterministic 20-minute regression independently inspects real Arena ammo. At nominal 60 Hz this retains the four-second maximum dwell intent. Weapon distribution differs from earlier attempts; capacity changes cannot be attributed solely to the socket optimization. Gameplay and thresholds did not change for this generator adjustment; an estimated counter alone is not correctness evidence.

## Functional and identity checks

- Fantasy: 18 socket algorithm tests, including 10,000 seeded model cases, within 21 passing fork tests; full fork tooling/server build, packaging and startup/shutdown smoke. Actual Windows TCP/UDP and adapter lifecycle checks passed.
- Exact final parent candidate: 438 .NET tests across 13 reports, zero failures/skips, five named real-PostgreSQL persistence cases, architecture checks; 147 Unity EditMode and 2 actual-network PlayMode tests.
- Fresh Windows Player build and two graphics-enabled Players passed their original match, reconnect and unique-settlement checks. Two independent client captures and both-entity input audits passed.
- All 67 topology-tool and 8 PostgreSQL-gate Python checks passed during integration. Independent review found no blocking code or evidence issue and rehashed all 670 completed capacity captures.
- Parent build SDK: 10.0.202; runtime: 10.0.6; full Fantasy fork build SDK: 10.0.401; Unity: 6000.3.23f1. These toolchains are recorded separately.
- After qualification, all 45 owned process records were checked without a surviving matching process; the owned PostgreSQL instance was stopped. User Editor processes and unrelated Host launch settings were preserved.

Raw evidence remains under ignored `artifacts/battle-slice-20261004/qualified-candidate` and `qualified-capacity-sweep-01`. Earlier failures remain in their original directories and in the [execution ledger](battle-slice-completion-2026-10-04.md). Two initial post-run DLL inventory commands used a wrong filename/output directory; their diagnostic records remain, and the corrected nonempty 63-copy audit is the final evidence. No failed record is used as completion evidence.

## Limits and rollback

This closes the requested local desktop slice qualification at 64 clients. It does not qualify 128 clients, legacy 64-player rooms, Regional/Degraded wire budgets, dedicated hardware, Linux, macOS, mobile devices or public-cloud deployment. Android-related source/tooling is present but this candidate has no new device qualification. Hosted GitHub CI is separate from the local results; the online package vulnerability audit was skipped only for isolated local publication and remains enabled in CI.

For dependency rollback restore the Fantasy gitlink `f8bed0d464924f159d46498f1311206ea0694be8`, old package versions and the original Unity manifest/lock from the pre-change parent baseline `c9098be7e2a44efc42182a87aca2551648993705`, then regenerate isolated outputs. Do not mix old gameplay fingerprints with new captures; rollback of ADR-0018 behavior also requires its matching verifier. Git branch publication and PR creation are authorized; no merge or deployment is implied.
