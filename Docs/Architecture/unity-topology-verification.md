# Unity topology verification

This report records observed results on 2026-10-02 for the implementation in [the execution plan](unity-cloud-topology-plan.md). The topology follows [ADR-0017](../ADR/0017-single-region-service-topology.md). Public cloud and ten-minute qualification remain incomplete.

## Implemented behavior

The application can register/login through Gate, create or join a party, invite players, set readiness, queue/cancel matching, obtain an allocation and connect directly to Battle using its short-lived admission credential. The Gate adapter owns TCP/TLS and the pinned Fantasy envelope. Its public values contain no Fantasy or server types. The original v1 launch mode remains available.

Admission refresh is restricted to the original player, room, node and boot epoch. Reconnection reauthenticates, retains the server player entity and clears stale prediction. Input timestamps derive from authoritative room time rather than a prediction clock advanced by replaying pending inputs. A finished match stops input generation and automatic Battle reconnection.

Gate disruption does not halt an active Battle. The automated verifier reconnects the original account; the interactive application offers login recovery. Missing lobby/queue state returns the user to confirmation. A lost Battle connection queries the original allocation and settlement. A successful result requires that exact MatchId's confirmed receipt and one profile increment. The authenticated Player query checks the allocation roster; a client cannot query another player's match by supplying a player ID.

## Executed checks

| Check | Observed result |
| --- | --- |
| .NET solution, fixed SDK 10.0.202, isolated PostgreSQL 17.11 on port 25432 | 302 passed; 0 failed; 0 skipped |
| Architecture validator and tracked whitespace check | Passed |
| Gate TCP/TLS integration fixtures, including independent generated protocol vectors | 16 passed, included in the solution total |
| Cloud fault and TLS preparation Python tests, using real local OpenSSL | 18 passed |
| Unity 6000.3.23f1 full EditMode suite, reachable Unity CLI beta12 | 80 passed |
| Two actual Unity PlayMode topology clients, exact settlement receipt required | 1 passed, 27.37 seconds on final flow source |
| Windows standalone build through the connected Editor | Built successfully; two independent processes passed the short local match |
| Gate restart seven seconds after launching two standalone clients | Both recovered their backend once, finished the same match, confirmed its receipt, and each recorded Played=1 |
| Continuous input replay audit | Complete 3237-record replay independently verified; both players accepted input through server tick 1294, final tick 1295 |

The Gate restart had previously failed with IOException on both real clients. After the recovery fix, match `544383e28a1a4648912ea31684562023` finished with client acknowledgements 963/960 at tick 1284. An earlier restart attempt occurred after completion and is excluded from the outage evidence.

Raw local artifacts are under ignored `artifacts/unity-topology-goal`: `final-regression`, `editmode-receipt.json`, `settling-metric-playmode-result.json`, `gate-outage-green2-a.json`, `gate-outage-green2-b.json`, the build results and owned process records. These are functional verification results, not capacity measurements or Worker-density evidence. The final statistics-only correction was validated in PlayMode after the standalone outage run.

## Cloud preparation and remaining qualification

The isolated `ainative-cloud-test` deployment keeps the previous host separate. Its dedicated native TLS terminator is running with an IP-SAN test certificate and explicit SHA256 pin. Gate's backend remains on loopback; Battle public routes have not yet been applied. Source-limited TCP 443 and UDP 32000/32001 firewall changes await the owner's specific approval.

Actual public correct/wrong-pin checks, two Unity clients over public TLS/KCP, cloud image upgrade, a real ten-minute match, controlled service/database failures and an isolated database restore still require execution. Local script tests do not establish those results. Cloud expiry, certificate renewal and source-IP changes are operational constraints; this milestone does not establish production CA trust, multiple physical machines, failover, mobile/IL2CPP or performance capacity.
