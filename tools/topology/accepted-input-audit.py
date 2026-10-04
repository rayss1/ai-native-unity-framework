#!/usr/bin/env python3
"""Offline ANAR v1 accepted-input audit. No service, credentials, or network access."""
import argparse
from decimal import Decimal, ROUND_CEILING
import hashlib
import json
from pathlib import Path
import struct
import sys


HEADER_FIELDS = (
    "room_id", "allocation_id", "match_id", "node_id", "boot_epoch",
    "source_identity", "fantasy_identity", "protocol_identity", "configuration_identity",
    "gameplay_fingerprint", "ordering",
)
STATUS_NAMES = {0: "Capturing", 1: "Complete", 2: "Overflow", 3: "PersistenceFailure", 4: "Aborted"}


def validate_window(window):
    if not isinstance(window, dict):
        raise ValueError("Measurement window must be an object")
    for key in ("first", "last", "count", "total"):
        if type(window.get(key)) is not int or not 0 <= window[key] <= 0xFFFFFFFF:
            raise ValueError("Measurement window fields must be bounded integers: " + key)
    first, last, count = window["first"], window["last"], window["count"]
    if count == 0:
        if first != 0 or last != 0:
            raise ValueError("Zero measurement requires zero sequence bounds")
    elif first < 1 or last < first or last - first + 1 != count:
        raise ValueError("Measurement sequence interval does not match input count")
    if last > window["total"]:
        raise ValueError("Measurement sequence exceeds successful send bound")


def audit(args):
    windows = getattr(args, "measurement_windows", None)
    seen_sequences = {}
    window_sequences = {}
    if windows is not None:
        if not windows or any(type(entity) is not int or not 1 <= entity <= 0xFFFFFFFF for entity in windows):
            raise ValueError("Measurement windows require positive entity IDs")
        for entity, window in windows.items():
            validate_window(window)
            seen_sequences[entity] = set()
            window_sequences[entity] = set()
    replay_path = Path(args.replay)
    if replay_path.suffix.lower() != ".anar":
        raise ValueError("Expected a published .anar replay")
    digest = hashlib.sha256()
    with replay_path.open("rb") as source:
        for chunk in iter(lambda: source.read(65536), b""):
            digest.update(chunk)

    entity_stats = {}
    joins = {}
    clears = []
    record_count = 0
    tick_records = 0
    last_tick_record = 0
    last_tick_hash = 0
    previous_record_tick = 0
    kinds = {1: 0, 2: 0, 3: 0, 4: 0}
    with replay_path.open("rb") as stream:
        def read_bytes(count):
            data = stream.read(count)
            if len(data) != count:
                raise ValueError("Truncated ANAR record or header")
            return data

        def read(fmt):
            return struct.unpack("<" + fmt, read_bytes(struct.calcsize("<" + fmt)))

        magic, version = read("IH")
        if magic != 0x52414E41 or version != 1:
            raise ValueError("Unsupported ANAR magic or version")
        header = {}
        for field in HEADER_FIELDS:
            length, = read("H")
            if not 1 <= length <= 4096:
                raise ValueError("Invalid ANAR header string length")
            header[field] = read_bytes(length).decode("utf-8", errors="strict")
        match_length_ticks, = read("i")
        if match_length_ticks < 1:
            raise ValueError("Invalid declared match length")
        header.update(magic="ANAR", version=version, match_length_ticks=match_length_ticks)

        while True:
            kind, server_tick = read("BQ")
            if kind == 255:
                final_hash, declared_records, status = read("QqB")
                footer = {
                    "kind": 255, "final_tick": server_tick, "final_hash": str(final_hash),
                    "record_count": declared_records, "status_code": status,
                    "status": STATUS_NAMES.get(status, "Unknown"),
                }
                if stream.read(1):
                    raise ValueError("Unexpected bytes after ANAR footer")
                break
            if kind not in kinds:
                raise ValueError("Unknown ANAR record kind")
            if server_tick < previous_record_tick:
                raise ValueError("ANAR mutation tick ordering regressed")
            previous_record_tick = server_tick
            record_count += 1
            if record_count > 100_000_000:
                raise ValueError("ANAR record limit exceeded")
            kinds[kind] += 1
            if kind == 4:
                last_tick_hash, = read("Q")
                if server_tick != last_tick_record + 1:
                    raise ValueError("ANAR Tick records are not consecutive")
                last_tick_record = server_tick
                tick_records += 1
                continue
            entity, = read("I")
            if entity == 0:
                raise ValueError("ANAR mutation has entity zero")
            if kind == 1:
                if entity in joins:
                    raise ValueError("Duplicate join entity")
                joins[entity] = server_tick
                continue
            if entity not in joins:
                raise ValueError("ANAR mutation refers to an entity without a join")
            if kind == 3:
                clears.append({"entity_id": entity, "server_tick": server_tick})
                continue
            sequence, client_tick = read("IQ")
            read_bytes(21)  # move X/Z, look yaw/pitch, buttons, weapon; no credentials.
            if sequence == 0:
                raise ValueError("ANAR input sequence is zero")
            if windows is not None:
                if entity not in windows:
                    raise ValueError("ANAR input entity missing from declared measurement inventory")
                if sequence > windows[entity]["total"]:
                    raise ValueError("ANAR sequence exceeds successful send bound")
                if sequence in seen_sequences[entity]:
                    raise ValueError("Duplicate ANAR accepted input sequence")
                seen_sequences[entity].add(sequence)
                if windows[entity]["first"] <= sequence <= windows[entity]["last"]:
                    window_sequences[entity].add(sequence)
            stats = entity_stats.setdefault(entity, {
                "accepted_count": 0, "first_server_tick": server_tick,
                "last_server_tick": server_tick, "max_gap_ticks": 0,
                "first_sequence": sequence, "last_sequence": sequence,
                "last_client_tick": client_tick, "min_client_ahead_ticks": client_tick - server_tick,
                "max_client_ahead_ticks": client_tick - server_tick,
            })
            if stats["accepted_count"]:
                stats["max_gap_ticks"] = max(stats["max_gap_ticks"], server_tick - stats["last_server_tick"])
            stats["accepted_count"] += 1  # Real kind=2 records, never inferred from maximum sequence.
            stats["last_server_tick"] = server_tick
            stats["last_sequence"] = sequence
            stats["last_client_tick"] = client_tick
            stats["min_client_ahead_ticks"] = min(stats["min_client_ahead_ticks"], client_tick - server_tick)
            stats["max_client_ahead_ticks"] = max(stats["max_client_ahead_ticks"], client_tick - server_tick)

    expected = args.expected_input_hz * args.expected_duration_seconds
    minimum = int((expected * args.minimum_fraction).to_integral_value(rounding=ROUND_CEILING))
    required_entities = set(args.expected_entities or joins)
    failures = []
    if windows is not None and (set(joins) != set(windows) or required_entities != set(windows)):
        raise ValueError("Joined entities do not match declared measurement inventory")
    if not required_entities:
        failures.append("no-required-entities")
    if status != 1:
        failures.append("footer-not-Complete")
    if declared_records != record_count:
        failures.append("footer-record-count-mismatch")
    if footer["final_tick"] != last_tick_record or final_hash != last_tick_hash:
        failures.append("footer-does-not-match-last-Tick-record")
    expected_match_ticks = args.expected_duration_seconds * 60
    if Decimal(match_length_ticks) != expected_match_ticks:
        failures.append("declared-match-length-does-not-match-requested-duration-at-60Hz")
    for field in ("room_id", "allocation_id", "match_id", "node_id", "boot_epoch"):
        wanted = getattr(args, "expected_" + field)
        if wanted is not None and header[field] != wanted:
            failures.append("allocation-binding-mismatch:" + field)

    entities = {}
    for entity in sorted(set(joins) | required_entities):
        stats = dict(entity_stats.get(entity, {
            "accepted_count": 0, "first_server_tick": None, "last_server_tick": None,
            "max_gap_ticks": None, "first_sequence": None, "last_sequence": None,
            "last_client_tick": None, "min_client_ahead_ticks": None, "max_client_ahead_ticks": None,
        }))
        gap = None if stats["last_server_tick"] is None else footer["final_tick"] - stats["last_server_tick"]
        stats.update(
            join_server_tick=joins.get(entity), final_tick_gap=gap,
            expected_count=str(expected), minimum_accepted_count=minimum,
            accepted_fraction=float(Decimal(stats["accepted_count"]) / expected),
            accepted_count_pass=stats["accepted_count"] >= minimum,
            final_tick_gap_pass=gap is not None and 0 <= gap <= args.maximum_final_gap_ticks,
            required=entity in required_entities,
        )
        if entity in required_entities and windows is None:
            if not stats["accepted_count_pass"]:
                failures.append("too-few-accepted-inputs:entity:" + str(entity))
            if not stats["final_tick_gap_pass"]:
                failures.append("last-accepted-input-too-far-from-final:entity:" + str(entity))
        entities[str(entity)] = stats

    report = {
        "audit_version": 1, "replay": str(replay_path.resolve()), "sha256": digest.hexdigest(),
        "header": header, "footer": footer, "parsed_record_count": record_count,
        "tick_record_count": tick_records, "record_kind_counts": {str(k): v for k, v in kinds.items()},
        "requirements": {
            "expected_input_hz": str(args.expected_input_hz),
            "expected_duration_seconds": str(args.expected_duration_seconds),
            "minimum_fraction": str(args.minimum_fraction), "minimum_accepted_count_per_entity": minimum,
            "maximum_final_gap_ticks": args.maximum_final_gap_ticks,
            "expected_entities": sorted(required_entities),
        },
        "entities": entities, "connection_clears": clears,
        "accepted_input_audit_passed": not failures, "failures": failures,
        "state_hash_simulation_verified": False,
        "verification_note": "Run AiNative.ArenaReplay independently to re-simulate and verify every state hash.",
    }
    if windows is not None:
        measured = {}
        for entity, window in sorted(windows.items()):
            accepted = sorted(window_sequences[entity])
            missing = []
            cursor = window["first"]
            if window["count"]:
                for sequence in accepted:
                    if sequence > cursor:
                        missing.append([cursor, sequence - 1])
                    cursor = sequence + 1
                if cursor <= window["last"]:
                    missing.append([cursor, window["last"]])
            minimum = int((Decimal(window["count"]) * args.minimum_fraction).to_integral_value(rounding=ROUND_CEILING))
            passed = len(accepted) >= minimum
            if not passed:
                failures.append("too-few-measured-accepted-inputs:entity:" + str(entity))
            measured[str(entity)] = {
                "entity_id": entity, "first_sequence": window["first"], "last_sequence": window["last"],
                "measured_input_count": window["count"], "total_successful_send_count": window["total"],
                "whole_capture_accepted_count": entity_stats.get(entity, {}).get("accepted_count", 0),
                "accepted_count": len(accepted), "accepted_fraction": float(Decimal(len(accepted)) / window["count"]) if window["count"] else None,
                "minimum_accepted_count": minimum, "missing_sequence_count": window["count"] - len(accepted),
                "missing_sequence_ranges": missing, "excluded_zero_measurement": window["count"] == 0, "passed": passed,
            }
        report["window_entities"] = measured
        report["accepted_input_audit_passed"] = not failures
        report["requirements"]["coverage_scope"] = "Actual kind=2 records in each declared measurement sequence interval"
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("replay")
    parser.add_argument("--expected-input-hz", type=Decimal, required=True)
    parser.add_argument("--expected-duration-seconds", type=Decimal, required=True)
    parser.add_argument("--minimum-fraction", type=Decimal, default=Decimal("0.8"))
    parser.add_argument("--maximum-final-gap-ticks", type=int, default=180)
    parser.add_argument("--expected-entities", type=lambda text: [int(item) for item in text.split(",")])
    for field in ("room_id", "allocation_id", "match_id", "node_id", "boot_epoch"):
        parser.add_argument("--expected-" + field.replace("_", "-"))
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    if args.expected_input_hz <= 0 or args.expected_duration_seconds <= 0 or not 0 < args.minimum_fraction <= 1 or args.maximum_final_gap_ticks < 0:
        parser.error("Rate/duration must be positive, fraction in (0,1], final gap nonnegative")
    try:
        report = audit(args)
        code = 0 if report["accepted_input_audit_passed"] else 1
    except (ValueError, OSError, UnicodeError, struct.error) as error:
        report = {"accepted_input_audit_passed": False, "parse_error": str(error)}
        code = 2
    rendered = json.dumps(report, indent=2, ensure_ascii=True) + "\n"
    if args.output:
        args.output.write_text(rendered, encoding="utf-8")
    sys.stdout.write(rendered)
    return code


if __name__ == "__main__":
    raise SystemExit(main())
