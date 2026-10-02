# Isolated cloud fault acceptance

`cloud-fault-acceptance.py` operates only containers whose inspected Compose project label is `ainative-cloud-test`. It never removes services or volumes, modifies deployment configuration, prints secrets, or deploys images. Each mutated container must match its expected service label. All previously running services are restored in `finally`; the JSON report also records restored health and any recovery failure. One-shot acceptance containers receive unique names and are stopped during cleanup.

Prerequisites: Python 3; noninteractive Docker permission; the existing Compose file; private readiness endpoints on host loopback ports 24101–24107; the host directory bound at `/reports` by acceptance; and an acceptance image built from the current `Program.cs`. The script runs `compose --profile acceptance run --rm --no-deps acceptance` with report/deadline environment overrides. It expects the acceptance service to share Gate's network namespace, as in the isolated cloud deployment. PostgreSQL reads use the test-only `topology_test_owner` role and `ainative_topology` database inside this project's PostgreSQL container. Do not point it at a production configuration.

From the cloud checkout, with the existing Battle deployment configured for 36000 ticks:

```sh
python3 tools/topology/cloud-fault-acceptance.py \
  --compose-file artifacts/cloud-deploy/compose.json \
  --reports artifacts/cloud-deploy/reports \
  --mode long-match --deadline-seconds 780
```

`long-match` requires at least 36000 authoritative finished ticks. A successful short run cannot qualify it. The script does not change match duration: configure the Battle deployment separately under the deployment workflow. Ordinary Windows functional runs remain at 1200 ticks and a 240-second deadline. A local long run can use `testserver.ps1 -MatchTicks 36000 -DeadlineSeconds 780`; this is still local evidence.

Use the same cloud command with these `--mode` values:

| Mode | Actual assertion |
| --- | --- |
| `expired-ticket` | The Player-issued credential expires while the authoritative match remains active, then Battle rejects it. Requires match duration beyond ticket expiry. |
| `player-outage` | Stop Player after admission; observe this exact match's durable `.result` file with no receipt; restore Player; require one PostgreSQL receipt, both counters equal one, no pending file and stable counters. |
| `database-outage` | Stop PostgreSQL after admission; observe the exact durable result; restore PostgreSQL, explicitly replace Coordinator to reacquire its lost advisory lock, and verify settlement recovery exactly once. |
| `coordinator-restart` | Compare persisted room/match/node/boot identity before and after readiness recovery and require exactly one allocation for the match. |
| `battle-crash` | Restart the allocated Battle process; require the old room to become Lost and its ticket to fail admission, then finish a fresh match. New boot epoch comparison is recorded when the replacement is allocated on the same node. Docker restart runs graceful shutdown hooks; this is not power-loss qualification. |

Each run writes unique `fault-*.json`, client report and active-room metadata files in the reports directory. Reports contain identifiers, counters, tick/input evidence, observed service actions and health, never tickets or database connection strings. Failure to restore a service makes the run fail. Restore can take additional bounded time beyond the client deadline. PostgreSQL outages remove the Coordinator's session-bound lock; recovery explicitly restarts Coordinator rather than claiming automatic HA.

Behavior tests:

```powershell
. ./artifacts/topology-bootstrap/environment.ps1
./artifacts/dotnet/dotnet.exe test server/tests/AiNative.TopologyAcceptance.Tests/AiNative.TopologyAcceptance.Tests.csproj -p:NuGetAudit=false
python -m unittest discover -s tools/topology/tests -p test_cloud_fault_acceptance.py
```

The Docker boundary tests use a stateful external-command fixture. They establish ownership checks, action allowlists, recovery and report rejection rules; they do not establish real cloud fault results. Run every intended mode on the isolated cloud project before marking that mode qualified.
