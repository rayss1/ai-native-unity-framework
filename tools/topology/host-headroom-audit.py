#!/usr/bin/env python3
"""Offline whole-host headroom audit of every recorded qualification stage."""
import argparse
from datetime import datetime
import hashlib
import json
import math
import re
from pathlib import Path
import sys


def timestamp(value):
    parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    if parsed.tzinfo is None:
        raise ValueError("Evidence timestamp must include timezone")
    return parsed.timestamp()


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def positive_integer(value, maximum):
    return type(value) is int and 1 <= value <= maximum


def workload(stage):
    return tuple(stage[key] for key in ("Rooms", "Players", "Workers", "Density", "InputHz", "WarmupSeconds"))


def native_result(root, stage, inputs):
    name, mode = stage["Stage"]["Name"], stage["Stage"]["Mode"]
    expected_path = root / name / (mode + ".json")
    declared_path = stage.get("Report")
    if not isinstance(declared_path, str) or Path(declared_path).resolve() != expected_path.resolve():
        raise ValueError("Native report does not point to its actual stage: " + name)
    if not expected_path.is_file():
        if stage.get("Passed") is True:
            raise ValueError("Contradictory native success: missing report for " + name)
        return False
    report = json.loads(expected_path.read_text(encoding="utf-8-sig"))
    inputs.append(dict(path=str(expected_path), sha256=digest(expected_path)))
    process_exit, report_exit = stage.get("ExitCode"), report.get("exit")
    succeeded = type(process_exit) is int and process_exit == 0 and type(report_exit) is int and report_exit == 0
    if stage.get("Passed") is True and not succeeded:
        raise ValueError("Contradictory native success: process/report exit must be integer zero for " + name)
    return stage.get("Passed") is True and succeeded


def binary_inventory(directory, inputs):
    inventory = {}
    for path in sorted(directory.glob("AiNative.*.dll")):
        if not path.is_file():
            raise ValueError("Actual binary identity is not a file: " + str(path))
        inventory[path.name] = digest(path)
        inputs.append(dict(path=str(path), sha256=inventory[path.name]))
    if not inventory:
        raise ValueError("Missing actual binary identity: " + str(directory))
    return inventory


def audit(root):
    root = Path(root).resolve()
    summary_path = root / "qualification-summary.json"
    sample_path = root / "host-resource-samples.jsonl"
    summary = json.loads(summary_path.read_text(encoding="utf-8-sig"))
    native = summary.get("Passed") is True
    metadata_path = root / "host-metadata.json"
    inputs = [dict(path=str(path), sha256=digest(path)) for path in (summary_path, sample_path, metadata_path) if path.is_file()]
    reports = []
    # Read every stage after process exit; a live collector's in-memory gates are insufficient.
    for stage in summary["Stages"]:
        name = stage["Stage"]["Name"]
        if not isinstance(name, str) or Path(name).name != name or "/" in name or "\\" in name or name in (".", ".."):
            raise ValueError("Invalid stage name")
        if stage["Stage"]["Mode"] != "qualification":
            native = native_result(root, stage, inputs) and native
            continue
        detail_path = root / name / "qualification-detail.json"
        if not detail_path.is_file():
            if stage.get("Passed") is True:
                raise ValueError("Missing detail for passed stage: " + name)
            reports.append(dict(name=name, nativePassed=False, passed=False, reason="No completed measured window"))
            continue
        detail = json.loads(detail_path.read_text(encoding="utf-8-sig"))
        if stage.get("Passed") is True and detail.get("passed") is not True:
            raise ValueError("Contradictory stage success: " + name)
        inputs.append(dict(path=str(detail_path), sha256=digest(detail_path)))
        reports.append(dict(name=name, nativePassed=native_result(root, stage, inputs) and detail.get("passed") is True,
                            detail=detail, stage=stage["Stage"]))
    measured = [report for report in reports if "detail" in report]
    if not measured:
        return dict(passed=False, nativePassed=native, stages=reports,
                    reason="No completed measured windows; no capacity claim", inputHashes=inputs)
    identity_path = root / "replay-identities.json"
    expected = json.loads(identity_path.read_text(encoding="utf-8-sig"))
    metadata = json.loads(metadata_path.read_text(encoding="utf-8-sig"))
    if (not re.fullmatch(r"[0-9a-f]{40}\+source-worktree-sha256:[0-9A-Fa-f]{64}", expected["Source"])
            or not re.fullmatch(r"[0-9a-f]{40}", expected["Fantasy"])
            or not re.fullmatch(r"sha256:[0-9A-Fa-f]{64}", expected["Protocol"])
            or not positive_integer(metadata["logicalProcessors"], 4096)
            or not positive_integer(metadata["physicalRamBytes"], 1 << 60) or not metadata["cpu"]):
        raise ValueError("Invalid build or host identity")
    inputs.append(dict(path=str(identity_path), sha256=digest(identity_path)))
    expected_binaries = binary_inventory(root / "bin/battle-1", inputs)
    if binary_inventory(root / "bin/battle-2", inputs) != expected_binaries:
        raise ValueError("Actual binary identity differs between published nodes")
    points, previous = [], None
    for line in sample_path.read_text(encoding="utf-8-sig").splitlines():
        point = json.loads(line)
        when = timestamp(point["utc"])
        cpu, memory = point["cpuPercent"], point["memoryUsedPercent"]
        total, free = point["totalMemoryBytes"], point["freeMemoryBytes"]
        if (not all(isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value)
                    for value in (cpu, memory, total, free)) or not 0 <= cpu <= 100 or not 0 <= memory <= 100
                or total <= 0 or not 0 <= free <= total or abs(memory - 100*(1-free/total)) > .01
                or not metadata["physicalRamBytes"]*.8 <= total <= metadata["physicalRamBytes"]
                or (previous is not None and when <= previous)):
            raise ValueError("Invalid host sample: counters or timestamp")
        previous = when
        points.append((when, cpu, memory))
    previous_end = None
    for report in measured:
        detail = report.pop("detail")
        stage = report.pop("stage")
        options = detail["options"]
        bounds = dict(RoomCount=32, PlayersPerRoom=8, WorkerCount=16, RoomsPerWorker=16,
                      InputHz=60, WarmupSeconds=3600, DurationSeconds=7200)
        if (any(not positive_integer(options[key], maximum) for key, maximum in bounds.items())
                or options["WorkerCount"]*options["RoomsPerWorker"] > 16):
            raise ValueError("Invalid workload: " + report["name"])
        option_keys = ("RoomCount", "PlayersPerRoom", "WorkerCount", "RoomsPerWorker", "InputHz", "WarmupSeconds")
        if tuple(options[key] for key in option_keys) != workload(stage) or options["DurationSeconds"] != stage["DurationSeconds"]:
            raise ValueError("Workload differs from declared stage: " + report["name"])
        identities = detail["identities"]
        if len(identities) != 2 or detail["hardware"]["logicalProcessors"] != metadata["logicalProcessors"]:
            raise ValueError("Hardware or node identity mismatch")
        for node, identity in zip(("battle-1", "battle-2"), identities):
            node_path = root / report["name"] / (node + ".replay-identity.json")
            node_identity = json.loads(node_path.read_text(encoding="utf-8-sig"))
            if (any(identity[key] != expected[key] for key in ("Source", "Fantasy", "Protocol"))
                    or identity != node_identity or not re.fullmatch(r"sha256:[0-9A-Fa-f]{64}", identity["Configuration"])):
                raise ValueError("Replay identity differs from build/node: " + report["name"])
            inputs.append(dict(path=str(node_path), sha256=digest(node_path)))
            if binary_inventory(root / report["name"] / "bin" / node, inputs) != expected_binaries:
                raise ValueError("Actual binary identity differs from published build: " + report["name"] + "/" + node)
        binaries = detail["binaries"]
        if len({binary["name"] for binary in binaries}) != len(binaries) or {
                binary["name"]: binary["sha256"].lower() for binary in binaries} != expected_binaries:
            raise ValueError("Binary identity differs from published build")
        start, end = timestamp(detail["measurementStartUtc"]), timestamp(detail["measurementEndUtc"])
        seconds = end - start
        if previous_end is not None and start <= previous_end:
            raise ValueError("Overlapping measured stage windows")
        previous_end = end
        duration = options["DurationSeconds"]
        minimum = 3600 if report["name"].startswith("soak") or summary["Profile"] == "soak" else 300
        if (duration < minimum or seconds < duration - 2 or seconds > duration + 10
                or options["WarmupSeconds"] < 60 or options["InputHz"] != 60 or options["PlayersPerRoom"] != 8
                or options["RoomCount"] != 2*options["WorkerCount"]*options["RoomsPerWorker"]):
            raise ValueError("Declared measurement or workload not established: " + report["name"])
        window = [point for point in points if start <= point[0] <= end]
        times = [start] + [point[0] for point in window] + [end]
        max_gap = max(b-a for a, b in zip(times, times[1:]))
        cpu = sorted(point[1] for point in window)
        p99 = cpu[math.ceil(.99*len(cpu))-1] if cpu else None
        maximum_memory = max((point[2] for point in window), default=None)
        coverage = bool(window) and max_gap <= 5 and len(window) >= math.floor(seconds/5)
        report.update(measurementStartUtc=detail["measurementStartUtc"], measurementEndUtc=detail["measurementEndUtc"],
                      roomCount=options["RoomCount"], sampleCount=len(window), maximumSampleGapSeconds=max_gap,
                      cpuP99Percent=p99, memoryMaximumUsedPercent=maximum_memory, coveragePassed=coverage,
                      passed=report["nativePassed"] and coverage and p99 <= 80 and maximum_memory <= 80)
    if summary["Profile"] == "sweep" and native:
        if reports[-1]["name"] != "soak-highest-passing" or not reports[-1]["nativePassed"]:
            raise ValueError("Sweep lacks the final highest-passing soak")
        original = [stage["Stage"] for stage in summary["Stages"][:-1]
                    if stage["Stage"]["Mode"] == "qualification" and stage["Passed"] is True]
        soak = summary["Stages"][-1]["Stage"]
        if not original or workload(original[-1]) != workload(soak) or workload(summary["Selected"]) != workload(soak):
            raise ValueError("Workload differs from highest-passing selected density")
    # Failed higher density remains visible; only passing native stages need a passing host gate.
    passing = [report for report in reports if report["nativePassed"]]
    return dict(passed=native and bool(passing) and all(report["passed"] for report in passing),
                nativePassed=native, stages=reports, inputHashes=inputs, source=expected, hardware=metadata,
                percentileMethod="nearest-rank over whole-host CPU samples in each measured window",
                coverage="maximum sample gap 5 seconds, including both window boundaries",
                limits=dict(cpuP99Percent=80, memoryMaximumUsedPercent=80),
                limitations=["Does not independently verify replay, gameplay or network impairment budgets",
                             "Whole-host observation includes ambient applications; does not establish dedicated hardware capacity"])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("run_directory", type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    if args.output.exists():
        parser.error("Use a new output file; retained evidence cannot be overwritten")
    try:
        result = audit(args.run_directory)
    except (OSError, ValueError, KeyError, TypeError) as error:
        result = dict(passed=False, failureType=type(error).__name__, reason=str(error))
    args.output.write_text(json.dumps(result, indent=2, allow_nan=False)+"\n", encoding="utf-8")
    print(json.dumps(dict(passed=result["passed"], report=str(args.output))))
    return 0 if result["passed"] else 1


if __name__ == "__main__":
    sys.exit(main())
