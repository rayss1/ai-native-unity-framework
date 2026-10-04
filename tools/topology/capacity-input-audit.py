#!/usr/bin/env python3
"""Offline Arena capacity input-window gate, bound to independent replay verification."""
import argparse
from decimal import Decimal
import hashlib
import importlib.util
import json
from pathlib import Path
import re
import struct
import sys

_parser_path = Path(__file__).with_name("accepted-input-audit.py")
_spec = importlib.util.spec_from_file_location("accepted_input_audit", _parser_path)
_parser = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_parser)
IDENTITY_FIELDS = {"Source": "source_identity", "Fantasy": "fantasy_identity",
                   "Protocol": "protocol_identity", "Configuration": "configuration_identity"}


def sha256(path):
    digest = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for block in iter(lambda: stream.read(65536), b""):
            digest.update(block)
    return digest.hexdigest()


def load_json(path):
    def unique(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError("Duplicate JSON field: " + key)
            result[key] = value
        return result
    return json.loads(Path(path).read_text(encoding="utf-8-sig"), object_pairs_hook=unique)


def integer(value, name, minimum=0, maximum=0xFFFFFFFFFFFFFFFF):
    if type(value) is not int or not minimum <= value <= maximum:
        raise ValueError("Expected bounded integer: " + name)
    return value


def text(value, name):
    if not isinstance(value, str) or not value.strip():
        raise ValueError("Expected nonempty string: " + name)
    return value


def identity(value):
    if not isinstance(value, dict):
        raise ValueError("Missing replay identity")
    return {field: text(value.get(field), field) for field in IDENTITY_FIELDS}


def verify_hash(value, path, name):
    if not isinstance(value, str) or not re.fullmatch(r"[0-9a-fA-F]{64}", value) or sha256(path) != value.lower():
        raise ValueError("Missing or mismatched hash: " + name)


def result_binding(value):
    if not isinstance(value, dict):
        raise ValueError("Missing verified replay result")
    final_hash = value.get("FinalHash")
    # Replay UInt64 output may be a JSON integer or an exact decimal string.
    if isinstance(final_hash, str) and re.fullmatch(r"[0-9]+", final_hash):
        final_hash = int(final_hash)
    return (text(value.get("RoomId"), "RoomId"), text(value.get("MatchId"), "MatchId"),
            integer(value.get("FinalTick"), "FinalTick", 1), integer(final_hash, "FinalHash"),
            integer(value.get("Records"), "Records", 1, 100_000_000))


def index_captures(entries, path_key):
    if not isinstance(entries, list) or not entries:
        raise ValueError("Missing capture inventory")
    indexed = {}
    for entry in entries:
        if not isinstance(entry, dict):
            raise ValueError("Invalid capture inventory entry")
        path = Path(text(entry.get(path_key), path_key)).resolve()
        if path.suffix.lower() != ".anar" or path in indexed:
            raise ValueError("Invalid or duplicate capture path")
        indexed[path] = entry
    return indexed


def audit_capacity(native_path, independent_path, minimum_fraction=Decimal("0.8")):
    if not minimum_fraction.is_finite() or not 0 < minimum_fraction <= 1:
        raise ValueError("minimum_fraction must be in (0,1]")
    native_path, independent_path = Path(native_path).resolve(), Path(independent_path).resolve()
    native_hash, independent_hash = sha256(native_path), sha256(independent_path)
    native, independent = load_json(native_path), load_json(independent_path)
    if not isinstance(native, dict) or type(native.get("passed")) is not bool:
        raise ValueError("Native qualification outcome missing")
    if not isinstance(independent, dict) or independent.get("passed") is not True or independent.get("failure") is not None:
        raise ValueError("Independent state-hash verification did not pass")
    identities = native.get("identities")
    if not isinstance(identities, list) or len(identities) != 2:
        raise ValueError("Native two-node identities missing")
    identities = [identity(item) for item in identities]
    source, fantasy = identities[0]["Source"], identities[0]["Fantasy"]
    if any(item["Source"] != source or item["Fantasy"] != fantasy for item in identities):
        raise ValueError("Native source/dependency identities differ")
    if independent.get("expectedSource") != source or independent.get("expectedFantasy") != fantasy:
        raise ValueError("Independent declared expected source/dependency mismatch")
    verifier = native_path.parent / "bin" / "replay-verifier" / "AiNative.ArenaReplay.dll"
    verify_hash(independent.get("verifierSha256"), verifier, "verifier")
    captures = index_captures(native.get("replay"), "capture")
    verified = index_captures(independent.get("verified"), "path")
    completed = integer(native.get("completedRooms"), "completedRooms", 1)
    if completed != len(captures) or integer(independent.get("completedCount"), "completedCount", 1) != completed or set(captures) != set(verified):
        raise ValueError("Native and independently verified completed inventories differ")
    # An omitted published Complete capture cannot disappear from both reports.
    on_disk_complete = set()
    replay_root = (native_path.parent / "replay").resolve()
    for path in replay_root.rglob("*.anar"):
        with path.open("rb") as stream:
            if stream.seek(0, 2) < 26:
                raise ValueError("Truncated published replay")
            stream.seek(-26, 2)
            marker, _, _, _, status = struct.unpack("<BQQqB", stream.read(26))
        if marker != 255 or status not in (1, 2, 3, 4):
            raise ValueError("Invalid published replay footer")
        if status == 1:
            on_disk_complete.add(path.resolve())
    if on_disk_complete != set(captures):
        raise ValueError("Complete captures missing from declared inventory or outside run")
    options = native.get("options")
    if not isinstance(options, dict):
        raise ValueError("Native qualification options missing")
    input_hz = integer(options.get("InputHz"), "InputHz", 1, 60)
    match_ticks = integer(options.get("MatchTicks"), "MatchTicks", 1, 216000)
    players_per_room = integer(options.get("PlayersPerRoom"), "PlayersPerRoom", 1, 8)
    players = native.get("players")
    if not isinstance(players, list) or not players:
        raise ValueError("Native player window inventory missing")
    by_room, player_ids, room_generations = {}, set(), {}
    for player in players:
        if not isinstance(player, dict):
            raise ValueError("Invalid native player")
        room = text(player.get("roomId"), "roomId")
        entity = integer(player.get("Entity"), "Entity", 1, 0xFFFFFFFF)
        player_id = text(player.get("player"), "player")
        slot = integer(player.get("slot"), "slot", 0, 0x7FFFFFFF)
        generation = integer(player.get("generation"), "generation", 0, 0x7FFFFFFF)
        if player_id in player_ids or entity in by_room.get(room, {}):
            raise ValueError("Duplicate native player or room/entity")
        if room in room_generations and room_generations[room] != (slot, generation):
            raise ValueError("Native room has contradictory slot/generation")
        room_generations[room] = slot, generation
        player_ids.add(player_id)
        window = {"first": player.get("firstMeasuredInputSequence"), "last": player.get("lastMeasuredInputSequence"),
                  "count": player.get("measuredInputs"), "total": player.get("totalAcceptedInputs")}
        _parser.validate_window(window)
        by_room.setdefault(room, {})[entity] = {"player": player_id, "window": window}
    if len(set(room_generations.values())) != len(room_generations):
        raise ValueError("Different rooms reuse a slot/generation")
    rooms, observed_rooms, failures = [], set(), []
    for path, native_capture in captures.items():
        bound = verified[path]
        verify_hash(bound.get("sha256"), path, "capture")
        bound_identity = identity(bound.get("identity"))
        identity_path = native_path.parent / (path.parent.name + ".replay-identity.json")
        verify_hash(bound.get("identitySha256"), identity_path, "identity")
        if identity(load_json(identity_path)) != bound_identity or bound_identity not in identities:
            raise ValueError("Capture identity differs from independent/native identity")
        result = result_binding(native_capture.get("verified"))
        if result != result_binding(bound.get("result")):
            raise ValueError("Native and independent results contradict")
        room_id = result[0]
        if room_id in observed_rooms or room_id not in by_room or len(by_room[room_id]) != players_per_room:
            raise ValueError("Missing, duplicate or incomplete room/player inventory")
        observed_rooms.add(room_id)
        windows = {entity: entry["window"] for entity, entry in by_room[room_id].items()}
        args = argparse.Namespace(replay=path, expected_input_hz=Decimal(input_hz),
            expected_duration_seconds=Decimal(match_ticks) / 60, minimum_fraction=minimum_fraction,
            maximum_final_gap_ticks=180, expected_entities=list(windows), measurement_windows=windows,
            expected_room_id=room_id, expected_match_id=result[1], expected_allocation_id=None,
            expected_node_id=path.parent.name, expected_boot_epoch=None)
        parsed = _parser.audit(args)
        if parsed["sha256"] != bound["sha256"].lower():
            raise ValueError("Capture changed during audit")
        if any(parsed["header"][header_field] != bound_identity[field] for field, header_field in IDENTITY_FIELDS.items()):
            raise ValueError("ANAR header identity mismatch")
        if (parsed["footer"]["final_tick"], int(parsed["footer"]["final_hash"]), parsed["parsed_record_count"]) != result[2:]:
            raise ValueError("ANAR footer contradicts verified results")
        room_players = [{"player": by_room[room_id][int(entity)]["player"], **stats}
                        for entity, stats in parsed["window_entities"].items()]
        for reason in parsed["failures"]:
            failures.append(room_id + ":" + reason)
        rooms.append({"room_id": room_id, "match_id": result[1], "slot": room_generations[room_id][0],
                      "generation": room_generations[room_id][1], "capture": str(path), "capture_sha256": parsed["sha256"],
                      "identity": bound_identity, "identity_path": str(identity_path), "identity_sha256": bound["identitySha256"].lower(),
                      "final_tick": result[2], "final_hash": str(result[3]), "record_count": result[4], "players": room_players,
                      "accepted_input_audit_passed": parsed["accepted_input_audit_passed"], "state_hash_simulation_verified": True})
    if observed_rooms != set(by_room):
        raise ValueError("Native player room has no completed capture")
    measured = [player for room in rooms for player in room["players"] if not player["excluded_zero_measurement"]]
    if not measured:
        failures.append("no-measured-players")
    if sha256(native_path) != native_hash or sha256(independent_path) != independent_hash:
        raise ValueError("Qualification reports changed during audit")
    return {"audit_version": 1, "accepted_input_audit_passed": not failures, "failures": failures,
            "native_report": str(native_path), "native_report_sha256": native_hash,
            "native_qualification_passed": native["passed"], "independent_replay_report": str(independent_path),
            "independent_replay_report_sha256": independent_hash, "verifier": str(verifier),
            "verifier_sha256": independent["verifierSha256"].lower(), "audit_tool_sha256": sha256(__file__),
            "parser_sha256": sha256(_parser_path), "minimum_fraction": str(minimum_fraction),
            "completed_rooms": completed, "measured_players": len(measured), "excluded_zero_measurement_players": len(players) - len(measured),
            "measured_inputs": sum(player["measured_input_count"] for player in measured),
            "accepted_measured_inputs": sum(player["accepted_count"] for player in measured), "rooms": rooms,
            "scope": "Actual ANAR kind=2 counts in declared measurement sequence intervals; each nonzero player window requires the existing 0.8 minimum fraction. Zero windows are explicitly excluded; whole-match and ACK counts cannot substitute."}


def main():
    cli = argparse.ArgumentParser(description=__doc__)
    cli.add_argument("--native-report", type=Path, required=True)
    cli.add_argument("--independent-replays", type=Path, required=True)
    cli.add_argument("--minimum-fraction", type=Decimal, default=Decimal("0.8"))
    cli.add_argument("--output", type=Path, required=True)
    args = cli.parse_args()
    if args.output.exists() or args.output.resolve() in (args.native_report.resolve(), args.independent_replays.resolve()):
        cli.error("Use a new output path; retained evidence cannot be overwritten")
    provenance = {"native_report": str(args.native_report.resolve()),
                  "independent_replay_report": str(args.independent_replays.resolve()),
                  "audit_tool_sha256": sha256(__file__), "parser_sha256": sha256(_parser_path)}
    for field, path in (("native_report_sha256", args.native_report), ("independent_replay_report_sha256", args.independent_replays)):
        try:
            provenance[field] = sha256(path)
        except OSError:
            provenance[field] = None
    try:
        report = audit_capacity(args.native_report, args.independent_replays, args.minimum_fraction)
        code = 0 if report["accepted_input_audit_passed"] else 1
    except (ValueError, OSError, UnicodeError, struct.error) as error:
        report = {"accepted_input_audit_passed": False, "parse_error": str(error), **provenance}
        code = 2
    rendered = json.dumps(report, indent=2, ensure_ascii=True) + "\n"
    args.output.write_text(rendered, encoding="utf-8")
    sys.stdout.write(rendered)
    return code


if __name__ == "__main__":
    raise SystemExit(main())
