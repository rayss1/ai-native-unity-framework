"""Prove acceptance counts actual replay records rather than sequence maxima."""
import argparse
from decimal import Decimal
import importlib.util
from pathlib import Path
import struct
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("input_audit", Path(__file__).parents[1] / "accepted-input-audit.py")
tool = importlib.util.module_from_spec(spec)
spec.loader.exec_module(tool)


def capture(path, sparse=False):
    payload = bytearray(struct.pack("<IH", 0x52414E41, 1))
    for value in ("room", "allocation", "match", "battle-1", "boot", "source", "fantasy", "protocol", "config", "gameplay", "ordering"):
        encoded = value.encode()
        payload += struct.pack("<H", len(encoded)) + encoded
    payload += struct.pack("<i", 60)
    records = 0
    for entity in (1, 2):
        payload += struct.pack("<BQI", 1, 0, entity)
        records += 1
    for tick in range(1, 61):
        for entity in (1, 2):
            if not sparse or tick == 60:
                # A huge final sequence does not establish sustained input.
                sequence = 30000 if sparse else tick
                payload += struct.pack("<BQIIQ", 2, tick, entity, sequence, tick) + bytes(21)
                records += 1
        payload += struct.pack("<BQQ", 4, tick, 123)
        records += 1
    payload += struct.pack("<BQQqB", 255, 60, 123, records, 1)
    path.write_bytes(payload)


class AcceptedInputAuditTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.path = Path(self.directory.name) / "match.anar"
        self.arguments = argparse.Namespace(
            replay=self.path, expected_input_hz=Decimal(60), expected_duration_seconds=Decimal(1),
            minimum_fraction=Decimal("0.8"), maximum_final_gap_ticks=3, expected_entities=[1, 2],
            expected_room_id="room", expected_allocation_id="allocation", expected_match_id="match",
            expected_node_id="battle-1", expected_boot_epoch="boot")

    def test_complete_record_inventory_qualifies_both_clients(self):
        capture(self.path)
        report = tool.audit(self.arguments)
        self.assertTrue(report["accepted_input_audit_passed"])
        self.assertEqual(60, report["entities"]["1"]["accepted_count"])
        self.assertEqual(60, report["entities"]["2"]["accepted_count"])
        self.assertFalse(report["state_hash_simulation_verified"])

    def test_sequence_maximum_cannot_replace_real_input_count(self):
        capture(self.path, sparse=True)
        report = tool.audit(self.arguments)
        self.assertFalse(report["accepted_input_audit_passed"])
        self.assertEqual(30000, report["entities"]["1"]["last_sequence"])
        self.assertEqual(1, report["entities"]["1"]["accepted_count"])
        self.assertIn("too-few-accepted-inputs:entity:1", report["failures"])
        self.assertIn("too-few-accepted-inputs:entity:2", report["failures"])

    def test_other_match_cannot_qualify_current_allocation(self):
        capture(self.path)
        self.arguments.expected_match_id = "other-match"
        self.assertIn("allocation-binding-mismatch:match_id", tool.audit(self.arguments)["failures"])

    def test_truncated_capture_fails(self):
        capture(self.path)
        self.path.write_bytes(self.path.read_bytes()[:-1])
        with self.assertRaisesRegex(ValueError, "Truncated"):
            tool.audit(self.arguments)


if __name__ == "__main__":
    unittest.main()
