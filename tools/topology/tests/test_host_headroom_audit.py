"""Host qualification must include the final measured stage and sustained samples."""
import importlib.util
import copy
from datetime import datetime, timedelta, timezone
import hashlib
import json
from pathlib import Path
import tempfile
import unittest

SPEC = importlib.util.spec_from_file_location("host_audit", Path(__file__).parents[1] / "host-headroom-audit.py")
audit = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(audit)


class HostAuditTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.options = dict(RoomCount=4, PlayersPerRoom=8, WorkerCount=2, RoomsPerWorker=1,
                            InputHz=60, WarmupSeconds=60, DurationSeconds=300)
        self.identity = dict(Source="c9098be7e2a44efc42182a87aca2551648993705+source-worktree-sha256:"+"A"*64,
                             Fantasy="f8bed0d464924f159d46498f1311206ea0694be8", Protocol="sha256:"+"B"*64)
        self.write("replay-identities.json", self.identity)
        self.write("host-metadata.json", dict(cpu=["fixture CPU"], logicalProcessors=16, physicalRamBytes=1000000))
        binary = self.root / "bin/battle-1/AiNative.BattleHost.dll"
        binary.parent.mkdir(parents=True)
        binary.write_bytes(b"fixture")
        self.binary = dict(name=binary.name, sha256=hashlib.sha256(b"fixture").hexdigest())
        binary_two = self.root / "bin/battle-2" / binary.name
        binary_two.parent.mkdir(parents=True)
        binary_two.write_bytes(b"fixture")
        self.stage = dict(Name="single", Mode="qualification", Rooms=4, Players=8, Workers=2, Density=1,
                          InputHz=60, WarmupSeconds=60, DurationSeconds=300)
        self.write("qualification-summary.json", dict(Passed=True, Profile="single", Stages=[
            dict(Passed=True, ExitCode=0, Report=str(self.root / "single/qualification.json"), Stage=self.stage)]))
        self.detail = dict(passed=True, options=self.options,
            identities=[dict(self.identity, Configuration="sha256:"+"C"*64) for _ in range(2)],
            binaries=[self.binary], hardware=dict(logicalProcessors=16),
            measurementStartUtc="2026-10-03T00:00:00Z", measurementEndUtc="2026-10-03T00:05:00Z")
        self.write_detail("single", self.detail)
        self.points = [dict(utc=f"2026-10-03T00:{second//60:02d}:{second%60:02d}Z",
                            cpuPercent=30, memoryUsedPercent=60, totalMemoryBytes=1000000,
                            freeMemoryBytes=400000) for second in range(0, 301, 2)]
        self.save_samples()

    def write(self, name, data):
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(data), encoding="utf-8")

    def save_samples(self):
        (self.root / "host-resource-samples.jsonl").write_text(
            "".join(json.dumps(point)+"\n" for point in self.points), encoding="utf-8")

    def write_detail(self, name, detail):
        self.write(name+"/qualification-detail.json", detail)
        self.write(name+"/qualification.json", dict(exit=0))
        for node in ("battle-1", "battle-2"):
            self.write(name+"/"+node+".replay-identity.json", dict(self.identity, Configuration="sha256:"+"C"*64))
            binary = self.root / name / "bin" / node / self.binary["name"]
            binary.parent.mkdir(parents=True, exist_ok=True)
            binary.write_bytes(b"fixture")

    def sweep(self):
        summary = dict(Passed=True, Profile="sweep", Selected=dict(self.stage, Name="soak-highest-passing", DurationSeconds=3600),
                       Stages=[dict(Passed=True, ExitCode=0, Report=str(self.root / "single/qualification.json"), Stage=self.stage),
                               dict(Passed=True, ExitCode=0, Report=str(self.root / "soak-highest-passing/qualification.json"), Stage=dict(self.stage, Name="soak-highest-passing", DurationSeconds=3600))])
        detail = copy.deepcopy(self.detail)
        detail["options"]["DurationSeconds"] = 3600
        detail.update(measurementStartUtc="2026-10-03T00:05:20Z", measurementEndUtc="2026-10-03T01:05:20Z")
        self.write_detail("soak-highest-passing", detail)
        self.write("qualification-summary.json", summary)
        start = datetime(2026, 10, 3, tzinfo=timezone.utc)
        point = self.points[0]
        self.points = [dict(point, utc=(start+timedelta(seconds=second)).isoformat()) for second in range(0, 3921, 2)]
        self.save_samples()
        return summary, detail

    def test_complete_window_passes_with_nearest_rank(self):
        result = audit.audit(self.root)
        self.assertTrue(result["passed"])
        self.assertEqual(result["stages"][0]["sampleCount"], 151)
        self.assertEqual(result["stages"][0]["cpuP99Percent"], 30)
        hashes = {item["path"]: item["sha256"] for item in result["inputHashes"]}
        for prefix in ("bin/battle-1", "bin/battle-2", "single/bin/battle-1", "single/bin/battle-2"):
            self.assertEqual(hashes[str(self.root / prefix / self.binary["name"])], self.binary["sha256"])
        self.assertIn(str(self.root / "single/qualification.json"), hashes)

    def test_final_soak_cannot_be_omitted_after_child_exit(self):
        summary = json.loads((self.root / "qualification-summary.json").read_text())
        summary["Profile"] = "sweep"
        summary["Stages"].append(dict(Passed=True, Stage=dict(Name="soak-highest-passing", Mode="qualification")))
        self.write("qualification-summary.json", summary)
        with self.assertRaisesRegex(ValueError, "Missing detail.*soak"):
            audit.audit(self.root)

    def test_many_early_samples_do_not_cover_later_gap(self):
        self.points = self.points[:75]
        self.save_samples()
        self.assertFalse(audit.audit(self.root)["passed"])

    def test_host_cpu_or_memory_budget_failure_is_retained(self):
        for point in self.points: point["cpuPercent"] = 81
        self.save_samples()
        self.assertFalse(audit.audit(self.root)["passed"])
        for point in self.points: point.update(cpuPercent=30, memoryUsedPercent=81, freeMemoryBytes=190000)
        self.save_samples()
        self.assertFalse(audit.audit(self.root)["passed"])

    def test_nonfinite_counter_is_rejected(self):
        self.points[1]["cpuPercent"] = float("nan")
        self.save_samples()
        with self.assertRaisesRegex(ValueError, "Invalid host sample"):
            audit.audit(self.root)

    def test_short_window_cannot_qualify_declared_measurement(self):
        self.detail["measurementEndUtc"] = "2026-10-03T00:00:30Z"
        self.write_detail("single", self.detail)
        with self.assertRaisesRegex(ValueError, "Declared measurement"):
            audit.audit(self.root)

    def test_soak_must_match_selected_density(self):
        _, detail = self.sweep()
        detail["options"].update(RoomCount=8, RoomsPerWorker=2)
        self.write_detail("soak-highest-passing", detail)
        with self.assertRaisesRegex(ValueError, "Workload differs"):
            audit.audit(self.root)

    def test_success_summary_cannot_discard_failed_detail(self):
        self.sweep()
        self.detail["passed"] = False
        self.write_detail("single", self.detail)
        with self.assertRaisesRegex(ValueError, "Contradictory stage"):
            audit.audit(self.root)

    def test_measured_stages_cannot_overlap(self):
        _, detail = self.sweep()
        detail.update(measurementStartUtc="2026-10-03T00:00:00Z", measurementEndUtc="2026-10-03T01:00:00Z")
        self.write_detail("soak-highest-passing", detail)
        with self.assertRaisesRegex(ValueError, "Overlapping"):
            audit.audit(self.root)

    def test_zero_workers_cannot_establish_workload(self):
        self.detail["options"].update(WorkerCount=0, RoomCount=0)
        self.write_detail("single", self.detail)
        with self.assertRaisesRegex(ValueError, "Invalid workload"):
            audit.audit(self.root)

    def test_source_and_hardware_and_binary_identity_are_bound(self):
        for field in ("source", "hardware", "binary"):
            detail = copy.deepcopy(self.detail)
            if field == "source": detail["identities"][0]["Source"] = "unrelated"
            if field == "hardware": detail["hardware"]["logicalProcessors"] = 999
            if field == "binary": detail["binaries"][0]["sha256"] = "F"*64
            self.write_detail("single", detail)
            with self.assertRaisesRegex(ValueError, "identity"):
                audit.audit(self.root)

    def test_native_exit_failure_cannot_be_masked_by_success_flag(self):
        self.write("single/qualification.json", dict(exit=1))
        with self.assertRaisesRegex(ValueError, "Contradictory native"):
            audit.audit(self.root)

    def test_second_node_actual_binary_cannot_borrow_first_node_hash(self):
        (self.root / "single/bin/battle-2" / self.binary["name"]).write_bytes(b"different build")
        with self.assertRaisesRegex(ValueError, "binary identity"):
            audit.audit(self.root)

    def test_native_process_exit_must_be_an_explicit_integer_zero(self):
        summary = json.loads((self.root / "qualification-summary.json").read_text())
        for value in (1, False, "0", None):
            with self.subTest(exit_code=value):
                summary["Stages"][0]["ExitCode"] = value
                self.write("qualification-summary.json", summary)
                with self.assertRaisesRegex(ValueError, "Contradictory native"):
                    audit.audit(self.root)

    def test_native_report_exit_must_be_an_explicit_integer_zero(self):
        for value in (False, "0", None):
            with self.subTest(exit_code=value):
                self.write("single/qualification.json", dict(exit=value))
                with self.assertRaisesRegex(ValueError, "Contradictory native"):
                    audit.audit(self.root)
        self.write("single/qualification.json", {})
        with self.assertRaisesRegex(ValueError, "Contradictory native"):
            audit.audit(self.root)

    def test_native_report_cannot_borrow_another_stage_success(self):
        self.write("other/qualification.json", dict(exit=0))
        summary = json.loads((self.root / "qualification-summary.json").read_text())
        summary["Stages"][0]["Report"] = str(self.root / "other/qualification.json")
        self.write("qualification-summary.json", summary)
        with self.assertRaisesRegex(ValueError, "Native report.*stage"):
            audit.audit(self.root)

    def test_failed_higher_density_is_retained_before_successful_selected_soak(self):
        summary, _ = self.sweep()
        summary["Stages"].insert(1, dict(Passed=False, ExitCode=1,
            Report=str(self.root / "capacity-2/qualification.json"),
            Stage=dict(self.stage, Name="capacity-2", Rooms=8, Density=2)))
        self.write("qualification-summary.json", summary)
        result = audit.audit(self.root)
        self.assertTrue(result["passed"])
        self.assertEqual([stage["passed"] for stage in result["stages"]], [True, False, True])

    def test_each_node_actual_binary_hash_is_bound_to_publication(self):
        for prefix in ("bin/battle-2", "single/bin/battle-1", "single/bin/battle-2"):
            with self.subTest(node=prefix):
                binary = self.root / prefix / self.binary["name"]
                binary.write_bytes(b"different build")
                with self.assertRaisesRegex(ValueError, "binary identity"):
                    audit.audit(self.root)
                binary.write_bytes(b"fixture")

    def test_each_node_actual_binary_inventory_must_be_complete_and_exact(self):
        for prefix in ("bin/battle-2", "single/bin/battle-1", "single/bin/battle-2"):
            for mutation in ("missing", "extra"):
                with self.subTest(node=prefix, mutation=mutation):
                    binary = self.root / prefix / self.binary["name"]
                    extra = binary.parent / "AiNative.Unexpected.dll"
                    if mutation == "missing":
                        binary.unlink()
                    else:
                        extra.write_bytes(b"unexpected")
                    with self.assertRaisesRegex(ValueError, "binary identity"):
                        audit.audit(self.root)
                    if mutation == "missing":
                        binary.write_bytes(b"fixture")
                    else:
                        extra.unlink()


if __name__ == "__main__":
    unittest.main()
