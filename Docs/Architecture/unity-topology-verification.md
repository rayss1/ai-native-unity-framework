# Unity topology verification

This report records observed results on 2026-10-02 and 2026-10-03 for the implementation in [the execution plan](unity-cloud-topology-plan.md). The topology follows [ADR-0017](../ADR/0017-single-region-service-topology.md). Private cloud qualification, the public Unity TLS/KCP short match and two public ten-minute Unity matches passed.

## Implemented behavior

The application can register/login through Gate, create or join a party, invite players, set readiness, queue/cancel matching, obtain an allocation and connect directly to Battle using its short-lived admission credential. The Gate adapter owns TCP/TLS and the pinned Fantasy envelope. Its public values contain no Fantasy or server types. The original v1 launch mode remains available.

Admission refresh is restricted to the original player, room, node and boot epoch. Reconnection reauthenticates, retains the server player entity and clears stale prediction. Input timestamps derive from authoritative room time rather than a prediction clock advanced by replaying pending inputs. A finished match stops input generation and automatic Battle reconnection.

Gate disruption does not halt an active Battle. The automated verifier reconnects the original account; the interactive application offers login recovery. Missing lobby/queue state returns the user to confirmation. A lost Battle connection queries the original allocation and settlement. A successful result requires that exact MatchId's confirmed receipt and one profile increment. The authenticated Player query checks the allocation roster; a client cannot query another player's match by supplying a player ID.

## Executed checks

| Check | Observed result |
| --- | --- |
| .NET solution, fixed SDK 10.0.202, isolated PostgreSQL 17.11 on port 25432 | Final aggregate: 311 passed, 0 skipped. The sandbox run passed 295 non-Gate tests but failed two Gate TLS tests at Windows key import; all 16 Gate tests passed on the normal-user rerun |
| Architecture validator and tracked whitespace check | Passed |
| Gate TCP/TLS integration fixtures, including independent generated protocol vectors | 16 passed, included in the solution total |
| Cloud fault and TLS preparation Python tests, using real local OpenSSL | 20 passed, including rejection of stalled acknowledgements and brief input bursts |
| Acceptance cleanup regressions after observed Battle-restart failure | 17 passed with fixed SDK 10.0.202 |
| Unity 6000.3.23f1 full EditMode suite, reachable Unity CLI beta12 | 80 passed |
| Two actual Unity PlayMode topology clients, exact settlement receipt required | 1 passed, 27.37 seconds on final flow source |
| Windows standalone build through the connected Editor | Built successfully; two independent processes passed the short local match |
| Gate restart seven seconds after launching two standalone clients | Both recovered their backend once, finished the same match, confirmed its receipt, and each recorded Played=1 |
| Player paused across the end of a standalone Unity match | Both clients continued input through finish, waited for recovery, confirmed the original match receipt, and recorded Played=1 |
| Legacy v1 real KCP PlayMode tests on an independent Host | 2 passed, 0 skipped, 1.48 seconds; temporary Host stopped and Editor environment restored |
| Idle KCP heartbeat regression and real topology after the runtime fix | RED: idle disconnect at 35.192 seconds. GREEN: 3/3 v1 tests, idle 45 seconds with unchanged identity/ACK=0 and advancing snapshots; EditMode 80/80; topology 1/1 in 27.45 seconds |
| Continuous input replay audit | Complete 3237-record replay independently verified; both players accepted input through server tick 1294, final tick 1295 |

The Gate restart had previously failed with IOException on both real clients. After the recovery fix, match `544383e28a1a4648912ea31684562023` finished with client acknowledgements 963/960 at tick 1284. An earlier restart attempt occurred after completion and is excluded from the outage evidence.

Raw local artifacts are under ignored `artifacts/unity-topology-goal`: `final-regression`, `editmode-receipt.json`, `settling-metric-playmode-result.json`, `gate-outage-green2-a.json`, `gate-outage-green2-b.json`, the build results and owned process records. These are functional verification results, not capacity measurements or Worker-density evidence. The final statistics-only correction was validated in PlayMode after the standalone outage run.

The pinned Fantasy `Scene.Connect` path bypassed automatic heartbeat setup. The private adapter now attaches Fantasy's default heartbeat to the connected Session on its owning Scene thread; Session disposal removes both timers. The idle regression sends no gameplay input. Its isolated Host was published with installed SDK 10.0.401; the separate .NET regression above uses the fixed 10.0.202 SDK. Evidence is under `idle-keepalive`, `heartbeat-final-regression` and `heartbeat-final-gate-normal-user`. The final runtime was rebuilt as a Windows standalone; both independent processes finished match `39a038aca0a4408eacf2f93e26def96c` at tick 1289 with acknowledgements 963/961, same room/boot, successful reconnect and confirmed receipt, each Played=1 (`heartbeat-final-client-a.json`/`-b.json`). No mobile or IL2CPP claim is made.

Two independent standalone clients also passed Match and Lobby restarts during their live match: both completed the same match, confirmed its receipt and recorded Played=1. Evidence: `backend-restarts-unity-a.json` and `backend-restarts-unity-b.json`.

## Cloud deployment and qualification

The isolated `ainative-cloud-test` deployment keeps the previous host separate. Its dedicated native TLS terminator is running with an IP-SAN test certificate and explicit SHA256 pin. An actual loopback handshake against native stunnel verified the IP identity and expected DER fingerprint. Gate's backend remains on loopback; the applied public routes are TLS TCP 443 and Battle UDP 32000/32001. The owner specifically approved the three source-limited firewall rules and their source correction. All seven business roles were observed ready after deployment; internal RPC, PostgreSQL and health endpoints remain private.

The cloud business/acceptance images were built from `f94d45a`, then upgraded to clean `e995e404c395338a4b858015d3da713adb3aa183` using fixed SDK 10.0.202 and the verified fixed Fantasy package, without changing ports, credentials or persistent volumes. Actual private-network cloud checks observed:

| Scenario | Observed result |
| --- | --- |
| Player interruption | Passed in 36.678 seconds; 196-byte durable result observed, one receipt, both players Played=1, all roles restored ready |
| PostgreSQL interruption | Passed in 39.398 seconds; durable result preserved, explicit Coordinator restart reacquired ownership, single settlement |
| Coordinator restart | Passed in 29.941 seconds; room/match/node/boot identity unchanged and allocation count remained one |
| Backup restore | Passed into new `ainative_restore_20261002_f94d45a`; exact match receipt/hash, both played/won/kills and both schema versions matched; source database unmodified |
| Battle restart after acceptance cleanup fix | Passed in 46.024 seconds; original room became Lost, old boot ticket rejected, replacement used a different boot epoch and settled once |
| Expired admission credential | Passed in 165.233 seconds; expired ticket rejected while original room was live at tick 7266; both clients completed and settled once |
| Ten-minute continuous-input match | Passed in 615.379 seconds at 10 Hz; finished tick 36029, one receipt, both Played=1; independent replay audit and simulation described below |

The first Player wrapper attempt could not write its final report because the reports directory belonged solely to container UID 1654. Only that directory was changed to `ubuntu:1654`/`2770`, then the actual scenario was rerun successfully. Battle restart initially demonstrated old-ticket rejection but failed the harness verdict because asynchronous input-loop disposal masked the deliberate room-loss termination. Regression tests reproduce and fix that cleanup; the actual cloud rerun subsequently passed.

The ten-minute match was `c5dc2bb4d4374753b9a39b7f30ebeb87`, room `f4af5de81a644a37aaadf7d4d9cf54f4`, on battle-1 boot `4a2c847f830242ae8a3c18dac2ef85cb`. The complete 47396-record ANAR replay contained 5684 and 5678 accepted inputs for entities 1 and 2, with last accepted server ticks 36026 and 36023 (3 and 6 ticks before finish). Maximum inter-input gaps were 24 and 30 ticks. This exceeds the explicit 4800-input lower bound per player and ends within 180 ticks. Independent `AiNative.ArenaReplay` re-simulated every state hash, producing final hash `10568620101839048260` at tick 36029. Source, fixed Fantasy, protocol and configuration header identities were checked against the separately recorded deployment provenance. Input ACK maxima alone were not used as accepted-input evidence.

Remote evidence remains in `/home/ubuntu/ainative-cloud-test/repository/artifacts/cloud-deploy/reports`: `fault-long-match-1790934116318547103.json`, `provenance-private-e995e40-36000.json` (preserved before changing public routes), `long-match-e995e40-accepted-input-audit.json`, `long-match-e995e40-independent-verifier.json` and `long-match-e995e40-final-proof.json`. These cloud clients are the .NET acceptance clients, not public Unity clients.

HTTP-based IP discovery initially returned a different source from direct TCP. The original source restriction therefore caused a timeout. A uniquely marked direct packet proved the actual client source, which the owner then approved. Only the three new rules were corrected; the existing rules were preserved. The earlier wrong-pin timeout is excluded from certificate rejection evidence.

On 2026-10-03, a fresh direct TCP 443 connection succeeded. The final Windows Unity build rejected an incorrect SHA256 pin with `AuthenticationException`, before acquiring any player or allocation identity. With the correct pin, two independent Unity processes completed public match `5cd1e33031f44647bfd64b5bd3265e43`, room `0b9b14fa14034a2da585483be2cd1664`, boot `24bfe2ee5fce40d59e848a089ab021dd`. Both reached tick 1297, retained entity/session identity through reconnect, confirmed the exact settlement receipt and recorded Played=1. Input acknowledgements reached 958/955; maximum observed acknowledgement stalls were approximately 0.151 seconds. Evidence: `public-tcp-correct-source.json`, `public-wrong-pin-correct-source.json`, `public-client-a.json` and `public-client-b.json` under ignored `artifacts/unity-topology-goal`.

Four final-build Unity processes then ran two concurrent matches on separate Battle processes with 36000 active match ticks. Actual server captures observed the approved client source entering both public UDP 32000 and 32001. Each pair reported matching room/boot/match identities, successful identity-preserving reconnect, the original receipt and Played=1. Process launch to successful evidence took 608.56–608.98 seconds. No backend recovery was required in these uninterrupted public runs.

The independent audit counted accepted ANAR input records rather than inferring acceptance from ACK maxima. Each player had to exceed 24000 accepted inputs (80% of 50 Hz for 600 seconds) and finish within 180 ticks of its last input. A separate `AiNative.ArenaReplay` executable re-simulated every state hash with source/Fantasy/protocol/configuration identities checked against `provenance-public-e995e40-36000.json`. Read-only PostgreSQL checks found exactly one allocation and one settlement per match, with the expected node/boot and each player's Played=1.

| Public Unity match | Battle node / UDP port | Final tick / complete records | Accepted inputs, entities 1 / 2 | Final input gaps / maximum inter-input gaps | Independently verified final hash |
| --- | --- | --- | --- | --- | --- |
| `2ce1a23f685e40ddbb280ec9087cfa7c` | battle-1 / 32000 | 36061 / 95947 | 29940 / 29940 | 1 / 1 ticks; 51 / 53 ticks maximum | `8128658296714165684` |
| `4738ce685ada4f7faedaaf40c62b2d97` | battle-2 / 32001 | 36087 / 95991 | 29949 / 29949 | 1 / 1 ticks; 49 / 50 ticks maximum | `6245273737489585266` |

Local evidence: `public-long-client-a.json` through `public-long-client-d.json`, `public-long-client-validation.json`, managed assembly SHA256 hashes and the two `public-long-battle*-proof.png` captures. Remote reports retain `public-unity-long-<MatchId>-input-audit.json` and `public-unity-long-<MatchId>-final-proof.json`, including independent verifier output and database observations. All owned standalone processes exited. After checking there were no active rooms, the two Battle services were restored to the 1200-tick short-test configuration; database volumes and replays were retained.

The client runtime source is `a1ee08dbce7c1e2ad28853119290e743d0ad99da`; cloud server images use clean `e995e404c395338a4b858015d3da713adb3aa183`. The subsequent client heartbeat/test changes do not alter server runtime. Final-head PR31 validation passed in 1m51s and production validation in 27m1s; PR31 was merged by the owner or another actor before this report update.

Cloud expiry, certificate renewal and source-IP changes are operational constraints; this milestone does not establish production CA trust, multiple physical machines, failover, mobile/IL2CPP or performance capacity.
