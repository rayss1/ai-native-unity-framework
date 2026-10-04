"""Offline fixtures: exact measurement sequences must carry the declared load."""
import argparse
from decimal import Decimal
import hashlib
import importlib.util
import json
from pathlib import Path
import struct
import subprocess
import sys
import tempfile
import unittest


def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).parents[1] / filename)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


parser = load("accepted_input_audit", "accepted-input-audit.py")


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def capture(path, sequences):
    data = bytearray(struct.pack("<IH", 0x52414E41, 1))
    for value in ("room", "allocation", "match", "battle-1", "boot", "source", "fantasy", "protocol", "config", "gameplay", "ordering"):
        encoded = value.encode()
        data += struct.pack("<H", len(encoded)) + encoded
    data += struct.pack("<i", 60)
    records = 0
    for entity in sequences:
        data += struct.pack("<BQI", 1, 0, entity)
        records += 1
    for tick in range(1, 61):
        for entity, inputs in sequences.items():
            for sequence in inputs.get(tick, []):
                data += struct.pack("<BQIIQ", 2, tick, entity, sequence, tick) + bytes(21)
                records += 1
        data += struct.pack("<BQQ", 4, tick, 123)
        records += 1
    data += struct.pack("<BQQqB", 255, 60, 123, records, 1)
    path.write_bytes(data)
    return records


class MeasurementWindowTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.path = Path(self.temp.name) / "match.anar"
        self.window = {"first": 21, "last": 40, "count": 20, "total": 60}
        self.args = argparse.Namespace(
            replay=self.path, expected_input_hz=Decimal(60), expected_duration_seconds=Decimal(1),
            minimum_fraction=Decimal("0.8"), maximum_final_gap_ticks=180, expected_entities=[1],
            expected_room_id="room", expected_allocation_id="allocation", expected_match_id="match",
            expected_node_id="battle-1", expected_boot_epoch="boot", measurement_windows={1: self.window})

    def test_many_inputs_outside_window_cannot_hide_empty_window(self):
        inputs = {t: [t] for t in range(1, 61) if not 21 <= t <= 40}
        inputs[60] += list(range(61, 81))
        self.window["total"] = 80
        capture(self.path, {1: inputs})
        self.assertFalse(parser.audit(self.args)["accepted_input_audit_passed"])

    def test_whole_capture_over_eighty_percent_cannot_hide_underfilled_window(self):
        capture(self.path, {1: {t: [t] for t in range(1, 61) if not 31 <= t <= 40}})
        self.assertFalse(parser.audit(self.args)["accepted_input_audit_passed"])

    def test_exact_eighty_percent_window_reports_real_gaps(self):
        capture(self.path, {1: {t: [t] for t in range(1, 61) if not 21 <= t <= 24}})
        report = parser.audit(self.args)
        self.assertTrue(report["accepted_input_audit_passed"])
        self.assertEqual(16, report["window_entities"]["1"]["accepted_count"])
        self.assertEqual([[21, 24]], report["window_entities"]["1"]["missing_sequence_ranges"])

    def test_duplicate_actual_input_is_rejected_even_outside_window(self):
        for duplicate in (5, 30):
            with self.subTest(duplicate=duplicate):
                capture(self.path, {1: {t: [t, t] if t == duplicate else [t] for t in range(1, 61)}})
                with self.assertRaisesRegex(ValueError, "Duplicate"):
                    parser.audit(self.args)

    def test_capture_sequence_cannot_exceed_total_successful_sends(self):
        capture(self.path, {1: {t: [t] for t in range(1, 61)}})
        self.window["total"] = 59
        with self.assertRaisesRegex(ValueError, "send bound"):
            parser.audit(self.args)

    def test_zero_measurement_window_explicitly_excludes_joined_player(self):
        capture(self.path, {1: {t: [t] for t in range(1, 61)}})
        self.window.update(first=0, last=0, count=0)
        report = parser.audit(self.args)
        self.assertTrue(report["accepted_input_audit_passed"])
        self.assertTrue(report["window_entities"]["1"]["excluded_zero_measurement"])


class CapacityBindingTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.capture = self.root / "replay" / "battle-1" / "match.anar"
        self.capture.parent.mkdir(parents=True)
        self.records = capture(self.capture, {1: {t: [t] for t in range(1, 61)}})
        self.identity = {"Source": "source", "Fantasy": "fantasy", "Protocol": "protocol", "Configuration": "config"}
        self.identity_path = self.root / "battle-1.replay-identity.json"
        self.identity_path.write_text(json.dumps(self.identity))
        self.verifier = self.root / "bin" / "replay-verifier" / "AiNative.ArenaReplay.dll"
        self.verifier.parent.mkdir(parents=True)
        self.verifier.write_bytes(b"isolated test verifier identity fixture")
        self.result = {"RoomId": "room", "MatchId": "match", "FinalTick": 60, "FinalHash": 123, "Records": self.records}
        self.native = {"passed": True, "options": {"InputHz": 60, "MatchTicks": 60, "PlayersPerRoom": 1},
                       "completedRooms": 1, "identities": [self.identity, self.identity],
                       "players": [{"player": "player", "roomId": "room", "slot": 0, "generation": 0,
                                    "Entity": 1, "measuredInputs": 20, "firstMeasuredInputSequence": 21,
                                    "lastMeasuredInputSequence": 40, "totalAcceptedInputs": 60}],
                       "replay": [{"capture": str(self.capture), "verified": self.result}]}
        self.independent = {"passed": True, "completedCount": 1, "expectedSource": "source", "expectedFantasy": "fantasy",
                            "verifierSha256": sha(self.verifier), "verified": [{"path": str(self.capture),
                            "sha256": sha(self.capture), "identity": self.identity,
                            "identitySha256": sha(self.identity_path), "result": self.result}], "incomplete": [], "failure": None}
        self.native_path = self.root / "qualification-detail.json"
        self.independent_path = self.root / "independent-completed-replays.json"

    def audit(self):
        self.native_path.write_text(json.dumps(self.native))
        self.independent_path.write_text(json.dumps(self.independent))
        return load("capacity_input_audit", "capacity-input-audit.py").audit_capacity(
            self.native_path, self.independent_path, Decimal("0.8"))

    def test_bound_window_inventory_passes(self):
        report = self.audit()
        self.assertTrue(report["accepted_input_audit_passed"])
        self.assertEqual(sha(self.native_path), report["native_report_sha256"])
        self.assertEqual(20, report["rooms"][0]["players"][0]["accepted_count"])

    def test_identity_swaps_reject(self):
        for field in self.identity:
            with self.subTest(field=field):
                old = self.independent["verified"][0]["identity"]
                self.independent["verified"][0]["identity"] = {**old, field: "other"}
                with self.assertRaises(ValueError):
                    self.audit()
                self.independent["verified"][0]["identity"] = old

    def test_invalid_sequence_declarations_reject(self):
        for changes in ({"firstMeasuredInputSequence": 0}, {"lastMeasuredInputSequence": 39},
                        {"totalAcceptedInputs": 39}, {"measuredInputs": True},
                        {"firstMeasuredInputSequence": "21"}, {"lastMeasuredInputSequence": 2**32}):
            with self.subTest(changes=changes):
                old = self.native["players"][0].copy()
                self.native["players"][0].update(changes)
                with self.assertRaises(ValueError):
                    self.audit()
                self.native["players"][0] = old

    def test_missing_entities_capture_and_independent_verification_reject(self):
        for field in ("players", "replay"):
            with self.subTest(field=field):
                old = self.native[field]
                self.native[field] = []
                with self.assertRaises(ValueError):
                    self.audit()
                self.native[field] = old
        self.independent["verified"] = []
        with self.assertRaises(ValueError):
            self.audit()

    def test_missing_or_wrong_artifact_hashes_reject(self):
        for field in ("sha256", "identitySha256"):
            with self.subTest(field=field):
                old = self.independent["verified"][0].pop(field)
                with self.assertRaises(ValueError):
                    self.audit()
                self.independent["verified"][0][field] = "0" * 64
                with self.assertRaises(ValueError):
                    self.audit()
                self.independent["verified"][0][field] = old

    def test_duplicate_player_or_capture_reject(self):
        for entries in (self.native["players"], self.native["replay"], self.independent["verified"]):
            entries.append(entries[0])
            with self.assertRaises(ValueError):
                self.audit()
            entries.pop()

    def test_native_result_contradiction_reject(self):
        self.native["replay"][0]["verified"] = {**self.result, "Records": self.records + 1}
        with self.assertRaises(ValueError):
            self.audit()

    def test_expected_source_dependency_and_verifier_hash_reject(self):
        for field in ("expectedSource", "expectedFantasy", "verifierSha256"):
            with self.subTest(field=field):
                old = self.independent[field]
                self.independent[field] = "other"
                with self.assertRaises(ValueError):
                    self.audit()
                self.independent[field] = old

    def test_unreported_complete_capture_and_unknown_room_reject(self):
        extra = self.capture.with_name("omitted.anar")
        extra.write_bytes(self.capture.read_bytes())
        with self.assertRaises(ValueError):
            self.audit()
        extra.unlink()
        self.native["players"][0]["roomId"] = "unreported-room"
        with self.assertRaises(ValueError):
            self.audit()

    def test_unsupported_zero_windows_cannot_qualify_entire_run(self):
        self.native["players"][0].update(measuredInputs=0, firstMeasuredInputSequence=0, lastMeasuredInputSequence=0)
        report = self.audit()
        self.assertFalse(report["accepted_input_audit_passed"])
        self.assertEqual(1, report["excluded_zero_measurement_players"])
        self.assertEqual(0, report["measured_players"])

    def test_missing_join_entity_and_capture_fail(self):
        self.native["players"][0]["Entity"] = 2
        with self.assertRaises(ValueError):
            self.audit()
        self.native["players"][0]["Entity"] = 1
        self.capture.unlink()
        with self.assertRaises((ValueError, OSError)):
            self.audit()

    def test_result_hash_accepts_exact_uint64_decimal_string_only(self):
        self.native["replay"][0]["verified"] = {**self.result, "FinalHash": "123"}
        self.assertTrue(self.audit()["accepted_input_audit_passed"])
        for value in (True, None, 123.0, "123.0", "-1", str(2**64)):
            with self.subTest(value=value):
                self.native["replay"][0]["verified"]["FinalHash"] = value
                with self.assertRaises(ValueError):
                    self.audit()

    def test_independent_failure_or_loose_counter_cannot_pass(self):
        for changes in ({"passed": 1}, {"completedCount": True}, {"completedCount": "1"}, {"failure": "failure"}):
            with self.subTest(changes=changes):
                old = self.independent.copy()
                self.independent.update(changes)
                with self.assertRaises(ValueError):
                    self.audit()
                self.independent = old

    def test_native_red_gate_remains_explicit_even_when_inputs_qualify(self):
        self.native["passed"] = False
        report = self.audit()
        self.assertTrue(report["accepted_input_audit_passed"])
        self.assertFalse(report["native_qualification_passed"])

    def test_cli_rejection_retains_exact_report_hash_provenance(self):
        del self.native["players"][0]["firstMeasuredInputSequence"]
        self.native_path.write_text(json.dumps(self.native))
        self.independent_path.write_text(json.dumps(self.independent))
        output = self.root / "rejection.json"
        result = subprocess.run([sys.executable, str(Path(__file__).parents[1] / "capacity-input-audit.py"),
                                 "--native-report", str(self.native_path), "--independent-replays", str(self.independent_path),
                                 "--output", str(output)], capture_output=True, text=True)
        self.assertEqual(2, result.returncode, result.stderr)
        report = json.loads(output.read_text())
        self.assertFalse(report["accepted_input_audit_passed"])
        self.assertEqual(sha(self.native_path), report["native_report_sha256"])
        self.assertEqual(sha(self.independent_path), report["independent_replay_report_sha256"])


if __name__ == "__main__":
    unittest.main()
