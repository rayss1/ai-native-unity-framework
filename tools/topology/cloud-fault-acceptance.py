#!/usr/bin/env python3
"""Run bounded faults against the existing isolated ainative-cloud-test Compose project.

Requires an updated acceptance image, its /reports bind mount, and private health
ports 24101..24107 reachable from this machine. Does not deploy or remove anything.
The match duration belongs to the existing Battle deployment; this tool never
changes its configuration. For a real ten-minute run configure 36000 Battle ticks
first, then pass --deadline-seconds 780 --minimum-finished-tick 36000.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import shlex
import subprocess
import time
import urllib.request

PROJECT = "ainative-cloud-test"
SERVICES = ("postgres", "gate", "player", "lobby", "match", "coordinator", "battle-1", "battle-2", "otel")
HEALTH = {name: 24101 + n for n, name in enumerate(("gate", "player", "lobby", "match", "coordinator", "battle-1", "battle-2"))}


class Compose:
    def __init__(self, files, prefix, runner=subprocess.run):
        self.prefix = list(prefix)
        self.base = self.prefix + ["compose", "-p", PROJECT]
        for file in files:
            self.base += ["-f", str(file)]
        self.runner = runner
        self.original = {}
        self.actions = []
        self.database_interrupted = False

    def call(self, command):
        # Never echo command, config, environment, stderr, or Docker inspect bodies.
        result = self.runner(command, capture_output=True, text=True, timeout=45, check=False)
        if result.returncode:
            raise RuntimeError("external-command-failed")
        return result.stdout.strip()

    def owned(self, service):
        if service not in SERVICES:
            raise ValueError("service-outside-cloud-test-allowlist")
        ids = self.call(self.base + ["ps", "-a", "-q", service]).splitlines()
        if len(ids) != 1 or not ids[0]:
            raise RuntimeError("expected-one-owned-container:" + service)
        raw = self.call(self.prefix + ["inspect", "--format", "{{json .Config.Labels}}|{{json .State}}", ids[0]])
        labels, state = map(json.loads, raw.split("|", 1))
        if labels.get("com.docker.compose.project") != PROJECT or labels.get("com.docker.compose.service") != service:
            raise RuntimeError("container-ownership-mismatch:" + service)
        return ids[0], state

    def capture(self):
        self.original = {service: self.owned(service)[1]["Running"] for service in SERVICES}

    def change(self, action, service):
        if action not in ("stop", "start", "restart"):
            raise ValueError("fault-action-outside-allowlist")
        container, _ = self.owned(service)
        self.call(self.prefix + [action, container])
        _, state = self.owned(service)
        if bool(state["Running"]) != (action != "stop"):
            raise RuntimeError("fault-state-not-observed:" + service)
        self.actions.append({"service": service, "action": action, "state": state["Status"], "utc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())})
        if service == "postgres" and action in ("stop", "restart"):
            self.database_interrupted = True
        if service == "coordinator" and action == "restart":
            self.database_interrupted = False

    def restore(self):
        failures = []
        for service, was_running in self.original.items():
            if not was_running:
                continue
            try:
                if not self.owned(service)[1]["Running"]:
                    self.change("start", service)
            except Exception:
                failures.append("restore-failed:" + service)
        if self.database_interrupted and self.original.get("coordinator"):
            try:
                until(lambda: database_ready(self), time.monotonic() + 30, "database-restore-readiness")
                self.change("restart", "coordinator")
            except Exception:
                failures.append("restore-failed:coordinator-database-lock")
        return failures

    def exec_read(self, service, argv):
        self.owned(service)
        return self.call(self.base + ["exec", "-T", service] + list(argv))

    def stop_client(self, name):
        if not re.fullmatch(r"ainative-cloud-test-acceptance-[a-z0-9-]+", name):
            raise ValueError("client-name-outside-cloud-test")
        result = self.runner(self.prefix + ["inspect", "--format", "{{json .Config.Labels}}|{{json .State}}", name], capture_output=True, text=True, timeout=45, check=False)
        if result.returncode:
            return  # --rm may already have removed this exact one-shot container.
        labels, state = map(json.loads, result.stdout.strip().split("|", 1))
        if labels.get("com.docker.compose.project") != PROJECT or labels.get("com.docker.compose.service") != "acceptance":
            raise RuntimeError("client-container-ownership-mismatch")
        if state["Running"]:
            self.call(self.prefix + ["stop", name])

    def sql(self, query):
        return self.exec_read("postgres", ["psql", "-U", "topology_test_owner", "-d", "ainative_topology", "-Atc", query])


def identifier(value):
    if not isinstance(value, str) or not re.fullmatch(r"[A-Za-z0-9_-]{1,128}", value):
        raise ValueError("invalid-evidence-identifier")
    return value


def until(predicate, deadline, label):
    while time.monotonic() < deadline:
        value = predicate()
        if value:
            return value
        time.sleep(.1)
    raise TimeoutError(label)


def health(service):
    try:
        with urllib.request.urlopen(f"http://127.0.0.1:{HEALTH[service]}/health/ready", timeout=2) as response:
            return response.status == 200
    except Exception:
        return False


def database_ready(compose):
    try:
        return bool(compose.exec_read("postgres", ["pg_isready", "-U", "topology_test_owner", "-d", "ainative_topology"]))
    except RuntimeError:
        return False


def room(compose, room_id):
    row = compose.sql("SELECT row_to_json(r) FROM (SELECT room_id,match_id,node_id,boot_epoch,state FROM coordinator.rooms WHERE room_id='" + identifier(room_id) + "') r")
    return json.loads(row) if row else None


def settlement(compose, metadata):
    match_id = identifier(metadata["MatchId"])
    players = ",".join("'" + identifier(player) + "'" for player in metadata["playerIds"])
    count = int(compose.sql("SELECT count(*) FROM player.settlements WHERE match_id='" + match_id + "'"))
    counters = json.loads(compose.sql("SELECT json_agg(r) FROM (SELECT player_id,played FROM player.accounts WHERE player_id IN (" + players + ") ORDER BY player_id) r"))
    return {"receiptCount": count, "playerCounters": counters}


def pending_result(compose, node, match_id):
    path = "/var/lib/ainative/outbox/" + hashlib.sha256(identifier(match_id).encode()).hexdigest().upper() + ".result"
    # Read only this match's result; shell arguments are positional, never interpolated.
    size = compose.exec_read(node, ["sh", "-c", 'if test -s "$1"; then wc -c < "$1"; else echo 0; fi', "_", path])
    return int(size)


def launch(compose, reports, run_id, deadline_seconds, expired=False, battle_restart=False):
    name = "cloud-" + run_id
    report = reports / (name + ".json")
    active = reports / (name + ".active.json")
    client_name = "ainative-cloud-test-acceptance-" + run_id
    argv = compose.base + ["--profile", "acceptance", "run", "--rm", "--name", client_name, "-T", "--interactive=false", "--no-deps",
                           "-e", "AINATIVE_ACCEPTANCE_REPORT=/reports/" + report.name,
                           "-e", "AINATIVE_ACCEPTANCE_ACTIVE_SIGNAL=/reports/" + active.name,
                           "-e", "AINATIVE_ACCEPTANCE_DEADLINE_SECONDS=" + str(deadline_seconds)]
    if expired:
        argv += ["-e", "AINATIVE_ACCEPTANCE_EXPIRED_TICKET=true"]
    if battle_restart:
        argv += ["-e", "AINATIVE_ACCEPTANCE_BATTLE_RESTART_SIGNAL=/reports/" + active.name + ".restart.done"]
    argv += ["acceptance"]
    # Output is discarded deliberately: client JSON supplies bounded, credential-free evidence.
    process = subprocess.Popen(argv, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    return process, report, active, client_name


def acceptance_report(process, report, end, minimum_tick):
    until(lambda: process.poll() is not None, end, "acceptance-deadline")
    if process.returncode != 0:
        raise RuntimeError("acceptance-process-failed")
    data = json.loads(report.read_text())
    if data.get("exit") != 0:
        raise RuntimeError("acceptance-report-failed")
    inputs = next((e for e in data["evidence"] if e.get("scenario") == "snapshot-driven-continuous-input"), None)
    if not inputs or not inputs.get("passed") or inputs["finishedRoomTick"] < minimum_tick or inputs.get("secondPlayerFinishedTick", -1) < minimum_tick or inputs.get("secondPlayerAcknowledgedSequence", 0) < 1 or inputs.get("replacementAcknowledgedSequence", 0) < 1:
        raise RuntimeError("finished-tick-or-continuous-input-not-qualified")
    # A final snapshot and one old acknowledgement do not establish continuous
    # traffic. Both ongoing players must send throughout the requested duration
    # and have most of those inputs acknowledged by the authoritative server.
    if inputs.get("inputHz") != 10:
        raise RuntimeError("continuous-input-rate-not-qualified")
    minimum_inputs = max(1, minimum_tick / 60 * inputs["inputHz"] * .8)
    for sent_field, ack_field in (("secondPlayerInputs", "secondPlayerAcknowledgedSequence"), ("replacementInputs", "replacementAcknowledgedSequence")):
        sent = inputs.get(sent_field, 0)
        if sent < minimum_inputs or inputs[ack_field] < sent * .8:
            raise RuntimeError("continuous-input-count-or-acknowledgements-not-qualified")
    return data


def run(args):
    compose = Compose(args.compose_file, shlex.split(args.docker_prefix))
    reports = Path(args.reports).resolve()
    if not reports.is_dir():
        raise ValueError("reports-bind-directory-must-exist")
    started = time.monotonic()
    end = started + args.deadline_seconds
    run_id = args.mode + "-" + str(time.time_ns())
    output = reports / ("fault-" + run_id + ".json")
    evidence = []
    processes = []
    success = False
    failure = None
    try:
        compose.capture()
        if not all(compose.original.get(service) for service in SERVICES if service != "otel"):
            raise RuntimeError("required-test-services-not-running")
        for service in HEALTH:
            until(lambda service=service: health(service), min(end, time.monotonic() + 30), "initial-health:" + service)
        process, report, active, client_name = launch(compose, reports, run_id, args.deadline_seconds, args.mode == "expired-ticket", args.mode == "battle-crash")
        processes.append((process, client_name))
        def active_metadata():
            if process.poll() is not None:
                raise RuntimeError("client-exited-before-active-match")
            return json.loads(active.read_text()) if active.exists() else None
        metadata = until(active_metadata, min(end, time.monotonic() + 60), "active-match-signal")
        node = metadata["NodeId"]
        if node not in ("battle-1", "battle-2"):
            raise RuntimeError("allocated-node-outside-cloud-test")
        original = room(compose, metadata["RoomId"])
        if not original or original["state"] != "Ready" or original["match_id"] != metadata["MatchId"] or original["node_id"] != node or original["boot_epoch"] != metadata["BootEpoch"]:
            raise RuntimeError("active-room-ownership-mismatch")
        evidence.append({"scenario": "actual-active-room-ownership", "passed": True, "room": original})
        if args.mode in ("player-outage", "database-outage"):
            target = "player" if args.mode == "player-outage" else "postgres"
            before = settlement(compose, metadata)
            if before["receiptCount"] != 0 or any(p["played"] != 0 for p in before["playerCounters"]):
                raise RuntimeError("unexpected-prior-settlement")
            compose.change("stop", target)
            size = until(lambda: pending_result(compose, node, metadata["MatchId"]), end, "durable-settlement-backlog")
            if args.mode == "player-outage" and settlement(compose, metadata)["receiptCount"] != 0:
                raise RuntimeError("settled-while-player-stopped")
            evidence.append({"scenario": "durable-outbox-during-" + target + "-outage", "passed": True, "bytes": size, "matchId": metadata["MatchId"]})
            compose.change("start", target)
            if target == "postgres":
                until(lambda: database_ready(compose), min(end, time.monotonic() + 30), "database-recovery-readiness")
                # ADR-0017 ownership is tied to one PostgreSQL session. A dead advisory
                # lock fails closed until an explicit Coordinator replacement reacquires it.
                compose.change("restart", "coordinator")
                until(lambda: health("coordinator"), min(end, time.monotonic() + 30), "database-coordinator-lock-recovery")
                evidence.append({"scenario": "database-recovery-explicit-coordinator-lock-reacquisition", "passed": True})
            until(lambda: health("player"), min(end, time.monotonic() + 60), "player-recovery-health")
        elif args.mode == "coordinator-restart":
            compose.change("restart", "coordinator")
            until(lambda: health("coordinator"), min(end, time.monotonic() + 30), "coordinator-recovery-health")
            rebuilt = room(compose, metadata["RoomId"])
            count = int(compose.sql("SELECT count(*) FROM coordinator.rooms WHERE match_id='" + identifier(metadata["MatchId"]) + "'"))
            if rebuilt != original or count != 1:
                raise RuntimeError("coordinator-ownership-changed-or-duplicate-allocation")
            evidence.append({"scenario": "coordinator-recovery-no-duplicate-allocation", "passed": True, "room": rebuilt, "allocationCount": count})
        elif args.mode == "battle-crash":
            # Docker restart replaces the Battle process and boot epoch; graceful shutdown
            # can run drain hooks. This is process loss/restart, not power-loss qualification.
            compose.change("restart", node)
            until(lambda: health(node), min(end, time.monotonic() + 30), "battle-recovery-health")
            lost = until(lambda: (value if (value := room(compose, metadata["RoomId"])) and value["state"] == "Lost" else None), min(end, time.monotonic() + 30), "old-room-lost")
            Path(str(active) + ".restart.done").write_text("restarted\n")
            until(lambda: process.poll() is not None, min(end, time.monotonic() + 15), "old-ticket-client-deadline")
            old_data = json.loads(report.read_text())
            fenced = next((e for e in old_data["evidence"] if e.get("scenario") == "battle-restart-old-ticket-fenced" and e.get("passed")), None)
            expected = next((e for e in old_data["evidence"] if e.get("scenario") == "run" and e.get("message") == "expected-room-lost-after-battle-restart"), None)
            if process.returncode != 1 or old_data.get("exit") != 1 or not fenced or not expected:
                raise RuntimeError("old-ticket-fencing-not-observed")
            evidence.append(fenced)
            process, report, active, client_name = launch(compose, reports, run_id + "-replacement", max(1, int(end - time.monotonic())))
            processes.append((process, client_name))
            replacement = until(lambda: json.loads(active.read_text()) if active.exists() else None, min(end, time.monotonic() + 60), "replacement-room-signal")
            if replacement["RoomId"] == metadata["RoomId"] or (replacement["NodeId"] == node and replacement["BootEpoch"] == metadata["BootEpoch"]):
                raise RuntimeError("replacement-room-or-epoch-not-fenced")
            evidence.append({"scenario": "battle-loss-old-room-lost-new-allocation", "passed": True, "oldRoom": lost, "replacement": replacement, "sameNodeEpochCompared": replacement["NodeId"] == node})
            metadata = replacement
        data = acceptance_report(process, report, end, args.minimum_finished_tick)
        evidence += data["evidence"]
        if args.mode == "expired-ticket" and not any(e.get("scenario") == "expired-player-issued-ticket-live-room" and e.get("passed") for e in evidence):
            raise RuntimeError("expired-ticket-evidence-missing")
        recovered = settlement(compose, metadata)
        if recovered["receiptCount"] != 1 or len(recovered["playerCounters"] or []) != 2 or any(p["played"] != 1 for p in recovered["playerCounters"]):
            raise RuntimeError("settlement-not-exactly-once")
        if pending_result(compose, metadata["NodeId"], metadata["MatchId"]) != 0:
            raise RuntimeError("settlement-confirmed-but-outbox-still-pending")
        time.sleep(min(1.5, max(0, end - time.monotonic())))
        if settlement(compose, metadata) != recovered:
            raise RuntimeError("duplicate-settlement-after-recovery")
        evidence.append({"scenario": "postgres-settlement-recovery-once", "passed": True, **recovered})
        success = True
    except Exception as ex:
        # Exception bodies can contain external command data; retain only controlled names.
        failure = {"type": type(ex).__name__, "reason": str(ex) if isinstance(ex, (RuntimeError, ValueError, TimeoutError)) and re.fullmatch(r"[A-Za-z0-9_:\-]+", str(ex)) else "see-local-test-state"}
    finally:
        client_cleanup_failures = []
        for process, client_name in processes:
            try:
                compose.stop_client(client_name)
            except Exception:
                client_cleanup_failures.append("client-cleanup-failed:" + client_name)
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=5)
        restore_failures = client_cleanup_failures + compose.restore()
        restored_health = {}
        for service in HEALTH:
            if compose.original.get(service):
                try:
                    until(lambda service=service: health(service), time.monotonic() + 30, "restored-health")
                    restored_health[service] = True
                except Exception:
                    restored_health[service] = False
        success = success and not restore_failures and all(restored_health.values())
        output.write_text(json.dumps({"project": PROJECT, "mode": args.mode, "passed": success, "elapsedSeconds": round(time.monotonic() - started, 3), "minimumFinishedTick": args.minimum_finished_tick, "failure": failure, "evidence": evidence, "actions": compose.actions, "restoreFailures": restore_failures, "restoredHealth": restored_health}, indent=2) + "\n")
    print(json.dumps({"passed": success, "report": str(output), "restoreFailures": restore_failures}))
    return 0 if success else 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--compose-file", action="append", required=True)
    parser.add_argument("--reports", required=True, help="Existing host directory bind-mounted at /reports by acceptance")
    parser.add_argument("--docker-prefix", default="sudo -n docker", help="Argument prefix, e.g. 'docker' or 'sudo -n docker'; no shell execution")
    parser.add_argument("--mode", choices=("long-match", "expired-ticket", "player-outage", "database-outage", "coordinator-restart", "battle-crash"), required=True)
    parser.add_argument("--deadline-seconds", type=int, default=780)
    parser.add_argument("--minimum-finished-tick", type=int, help="Defaults to 36000 for long-match, 0 for functional fault runs")
    args = parser.parse_args()
    if args.minimum_finished_tick is None:
        args.minimum_finished_tick = 36000 if args.mode == "long-match" else 0
    if not 1 <= args.deadline_seconds <= 86400 or args.minimum_finished_tick < 0:
        parser.error("deadline must be 1..86400 seconds; minimum tick must be nonnegative")
    if args.mode == "long-match" and args.minimum_finished_tick < 36000:
        parser.error("long-match requires at least 36000 finished ticks")
    return run(args)


if __name__ == "__main__":
    raise SystemExit(main())
