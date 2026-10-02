# Static server topology operations

ADR-0017 fixes Gate, Player, Lobby, Match, RoomCoordinator and Battle roles. The local test runs seven processes because Battle has two nodes. Each Battle has two workers and two rooms per worker. These settings are provisional functional-test values, not measured capacity.

## Windows local acceptance

Use PowerShell 7 and the SDK pinned in global.json. Every wrapper accepts `-SdkPath` and `-RunDirectory`; run directories must remain inside repository artifacts.

```powershell
. ./artifacts/topology-bootstrap/environment.ps1
. ./artifacts/topology-postgres/environment.ps1
./tools/topology/testserver.ps1
```

The testserver builds each Host and the acceptance client, creates local RSA keys, launches a hidden supervisor, checks HTTP liveness, runs acceptance and stops its recorded children in finally. To inspect a running deployment separately, use build.ps1, initialize.ps1, start.ps1, acceptance.ps1 and stop.ps1. start.ps1 remains alive to drain child output. Stop verifies each recorded executable path and UTC process creation time before touching a PID. No process-name kill is used. Child environments are isolated, with unique health ports, Battle endpoints and outbox paths. Logs, ownership records and JSON evidence live under the selected run directory.

`AINATIVE_TEST_POSTGRES` must identify an isolated test database; no production connection fallback exists. Keys are generated only under artifacts, with a distinct service RSA key and a separate Player ticket-signing key. Never commit or attach that directory. Restrict its ACL to the service accounts before production use.

The normal acceptance runner uses two actual Fantasy TCP connections for account registration/login, party readiness, queuing and tickets. It tests denied foreign-profile/private-settlement calls, then uses adapter-owned Fantasy KCP connections for admission, snapshot-based input acknowledgements, reconnect, wrong-room rejection and completed-match Profile counters. Stable counters observe the real delivery path; they do not replace an explicit duplicate-settlement fault test. BackendOnly explicitly skips realtime/settlement but still requires a real room allocation. The normal runner has a 120-second deadline and writes errors/skips without credential bodies.

## Linux and static machines

The committed Fantasy.config is the loopback baseline. initialize-compose.py derives a complete per-machine config under artifacts/topology-compose using fixed private addresses 172.28.0.11–17. Fantasy innerBindIP is also the advertised routing endpoint: use the actual reachable private address, never 0.0.0.0 or cross-machine loopback. All processes share the complete process/scene tables, and each starts only its own `--pid N -m Release`. No dynamic NodeAgent is introduced.

Install Python 3, OpenSSL and Docker Compose, then run `sh tools/topology/linux.sh init`. The generated public directory contains configuration and public keys only. Each service mounts its own private key as one read-only secret file; only Player mounts the ticket signing key. Private directories use mode 0700 and key files 0600. Provision ownership/read access for container UID 1654 separately for each secret and Battle volume; do not grant every service access to the whole secrets directory. Supply artifacts/topology-compose/postgres-password through the secret manager. Set AINATIVE_PLAYER_POSTGRES, AINATIVE_COORDINATOR_POSTGRES, AINATIVE_BATTLE_1_PUBLIC_ADDRESS and AINATIVE_BATTLE_2_PUBLIC_ADDRESS, then run linux.sh build/start. Compose maps the two database inputs to the actual per-process AINATIVE_POSTGRES_CONNECTION_STRING setting. Battle public addresses must be reachable UDP endpoints forwarded to their fixed nodes. Dockerfile builds one selected Host via HOST and runs as nonroot UID 1654. Only the fixed Fantasy.config destination is writable by this UID for startup configuration copying; published binaries remain root-owned. The Compose template needs target-platform validation; Windows socket acceptance does not establish Linux behavior.

Replay capture requires AINATIVE_SOURCE_COMMIT, AINATIVE_FANTASY_COMMIT, AINATIVE_PROTOCOL_IDENTITY and AINATIVE_CONFIGURATION_IDENTITY. Use exact source/worktree, vendor revision, schema hash and deployment configuration identities. Battle volumes persist both outbox and ANAR capture. Capture defaults reserve storage within 1024 files and 2 GiB per node; completed files are not automatically deleted. Quota exhaustion closes new-room admission while existing rooms continue. Archive and remove verified completed captures through an explicit operator retention policy before capacity returns. Filesystem power-loss durability and remote retention remain unqualified.

Each Battle owns a separate durable volume at /var/lib/ainative/outbox. Keep it through replacement and rollback. Do not erase pending results to restore readiness. Back up PostgreSQL and outboxes before upgrades, and retain compatible keys, schema versions and previous image digests.

## Exposure and TLS

Only Gate outer TCP and Battle outer UDP should be public. Inner TCP 23101–23107, PostgreSQL, OTLP and HTTP health remain private. Compose publishes only Battle UDP; supply an external stream TLS terminator for Gate using stunnel.conf. That example accepts TLS on 443 and forwards the Fantasy TCP byte stream to Gate 23001. Mount certificate/key files from the secret manager. An HTTP proxy is not an equivalent stream endpoint. Frontend clients must explicitly use TLS; local acceptance uses plain loopback TCP. TLS has not been exercised by local acceptance.

## PostgreSQL ownership and recovery

Player owns schema player; RoomCoordinator owns coordinator. Other roles receive no database credential. least-privilege.sql documents separate runtime users and a non-login migration owner. Supply passwords as psql variables outside source control. Provision the actual schema/tables through a controlled migration before granting runtime DML. Stores first read schema_version and accept the implemented version (1) without running DDL; an empty or unsupported version fails startup. A missing version table retains development bootstrap using the caller's own migration privileges. Production runtime accounts cannot perform that bootstrap and must not receive database CREATE or schema ownership to bypass missing migrations. Sharing one database does not permit either runtime user to access the other schema.

Coordinator holds an exclusive PostgreSQL advisory lock. A replacement must acquire it before readiness, issue a new coordinator epoch and fence/rebuild Battle inventories. Losing lock ownership removes allocation readiness. This is single-coordinator recovery, not an HA claim. Gate restart must not stop Battle simulation. Node crashes invalidate node boot epochs and tickets; draining rejects new placements and lets active rooms finish.

## Health, telemetry and qualification

Local health ports are 24101–24107. /health/live means process liveness; /health/ready adds runtime/domain readiness. Health exposes no mutation endpoint. Management uses authenticated internal RPC. The collector accepts OTLP privately and prints diagnostic records. OTEL_EXPORTER_OTLP_ENDPOINT configures export only when host instrumentation exists; collector availability does not prove emitted spans/metrics.

Versions are pinned: [PostgreSQL 17.11 official tags](https://hub.docker.com/_/postgres/tags?name=17.&page=1) and [OpenTelemetry Collector 0.123.0 official release](https://github.com/open-telemetry/opentelemetry-collector-releases/releases/tag/v0.123.0). PostgreSQL uses the PostgreSQL license; Collector uses Apache-2.0. Record image digests after pulling, review upstream release notes before upgrades and preserve previous digests for rollback. SDK follows global.json; runtime 10.0.4 must be reviewed alongside that SDK before production.

Functional acceptance does not qualify Unity client compatibility, IL2CPP/AOT, mobile, Linux containers, TLS, multi-machine routing or real capacity. Capacity evidence must state hardware, build, configuration, seed, warmup, duration, sample count and percentile method.

Hosting exports logs, metrics and traces through OTLP when configured, with a two-second exporter timeout and bounded default trace/log queues of 2048. Export stays off the fixed Tick path. The collector template includes all three pipelines. Pump failures are also written to process stdout; inspect the local stdout logs when diagnosing absent export or failed domain pumps. Signed internal traceparent propagation prevents accepting unauthenticated service context. Collector pipeline configuration still requires actual export verification before claiming emitted telemetry.

WorkerCapacity.last_tick_micros is the latest sampled worker Tick duration, not a complete per-Tick distribution. Periodic inventory samples can be diagnostic evidence with timestamps and sample counts, but their p99 must not be described as a true per-Tick p99. The bot runner measures actual KCP input-to-snapshot acknowledgement latency, which includes scheduling, transport and server processing; it does not derive worker execution percentiles from that latency.

For the extended local run, use testserver.ps1 -GateRestart -DuplicateSettlement. GateRestart stops and replaces only the owned Gate process while asserting Battle ticks continue. DuplicateSettlement retires the selected owned Battle process, uses that node's own signing identity in a separate acceptance process for two exact internal settlement retries, checks the prior canonical receipt and reads PostgreSQL counters, then restores the original node and waits for readiness. These administrative credentials stay inside the test run directory. With network vulnerability-audit endpoints unavailable, -SkipAudit explicitly disables that restore check for local functional tests; this does not count as a security audit and must be recorded.
