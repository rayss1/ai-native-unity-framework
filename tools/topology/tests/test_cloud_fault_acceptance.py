"""Behavioral tests: the external Docker boundary is simulated, harness rules run unchanged."""
import importlib.util
import json
from pathlib import Path
import subprocess
import unittest


class DockerBoundary:
    def __init__(self):
        self.running = {name: True for name in ("gate", "player", "lobby", "match", "coordinator", "battle-1", "battle-2", "postgres", "otel")}
        self.project = "ainative-cloud-test"
        self.fail_start = False

    def __call__(self, command, **kwargs):
        if "pg_isready" in command:
            return subprocess.CompletedProcess(command, 0 if self.running["postgres"] else 1, "accepting connections", "")
        if "inspect" in command:
            service = command[-1].removeprefix("id-")
            labels = {"com.docker.compose.project": self.project, "com.docker.compose.service": service}
            state = {"Running": self.running[service], "Status": "running" if self.running[service] else "exited"}
            return subprocess.CompletedProcess(command, 0, json.dumps(labels) + "|" + json.dumps(state), "")
        if "ps" in command:
            return subprocess.CompletedProcess(command, 0, "id-" + command[-1], "")
        action = next((x for x in ("stop", "start", "restart") if x in command), None)
        if action:
            service = command[-1].removeprefix("id-")
            if action == "start" and self.fail_start:
                return subprocess.CompletedProcess(command, 1, "", "SECRET must never escape")
            self.running[service] = action != "stop"
            return subprocess.CompletedProcess(command, 0, "", "")
        raise AssertionError("Unexpected external operation")


class CloudHarnessTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        path = Path(__file__).parents[1] / "cloud-fault-acceptance.py"
        if path.exists():
            spec = importlib.util.spec_from_file_location("cloud_fault_acceptance", path)
            cls.module = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(cls.module)
        else:
            cls.module = None

    def compose(self, boundary=None):
        self.assertIsNotNone(self.module, "Missing bounded cloud fault harness")
        return self.module.Compose(["compose.json"], ["sudo", "-n", "docker"], runner=boundary or DockerBoundary())

    def test_other_project_container_is_rejected_before_any_mutation(self):
        boundary = DockerBoundary()
        boundary.project = "some-production-stack"
        compose = self.compose(boundary)
        with self.assertRaisesRegex(RuntimeError, "ownership"):
            compose.change("stop", "player")
        self.assertTrue(boundary.running["player"])

    def test_unknown_service_and_remove_actions_are_rejected(self):
        compose = self.compose()
        for action, service in [("stop", "acceptance"), ("stop", "production"), ("rm", "player"), ("down", "postgres")]:
            with self.assertRaises(ValueError):
                compose.change(action, service)

    def test_cleanup_rejects_a_client_name_outside_test_project(self):
        compose = self.compose()
        self.assertTrue(hasattr(compose, "stop_client"), "Client container cleanup must not leave an orphan")
        with self.assertRaises(ValueError):
            compose.stop_client("production-acceptance")

    def test_finally_restores_only_services_that_were_running(self):
        boundary = DockerBoundary()
        boundary.running["otel"] = False
        compose = self.compose(boundary)
        compose.capture()
        try:
            compose.change("stop", "player")
            compose.change("stop", "postgres")
            raise RuntimeError("acceptance failed")
        except RuntimeError:
            pass
        finally:
            failures = compose.restore()
        self.assertEqual(failures, [])
        self.assertTrue(boundary.running["player"])
        self.assertTrue(boundary.running["postgres"])
        self.assertFalse(boundary.running["otel"])

    def test_failed_restore_is_reported_without_external_stderr_secrets(self):
        boundary = DockerBoundary()
        compose = self.compose(boundary)
        compose.capture()
        compose.change("stop", "player")
        boundary.fail_start = True
        failures = compose.restore()
        self.assertEqual(len(failures), 1)
        self.assertIn("player", failures[0])
        self.assertNotIn("SECRET", str(failures))

    def test_short_successful_client_cannot_qualify_ten_minute_match(self):
        self.assertIsNotNone(self.module)
        import tempfile
        import time
        with tempfile.TemporaryDirectory() as directory:
            report = Path(directory) / "acceptance.json"
            report.write_text(json.dumps({"exit": 0, "evidence": [{"scenario": "snapshot-driven-continuous-input", "passed": True, "finishedRoomTick": 1200}]}))
            class Completed:
                returncode = 0
                def poll(self):
                    return 0
            with self.assertRaisesRegex(RuntimeError, "not-qualified"):
                self.module.acceptance_report(Completed(), report, time.monotonic() + 1, 36000)

    def test_missing_second_player_finish_cannot_qualify_long_match(self):
        self.assertIsNotNone(self.module)
        import tempfile
        import time
        with tempfile.TemporaryDirectory() as directory:
            report = Path(directory) / "acceptance.json"
            report.write_text(json.dumps({"exit": 0, "evidence": [{"scenario": "snapshot-driven-continuous-input", "passed": True, "finishedRoomTick": 36000}]}))
            class Completed:
                returncode = 0
                def poll(self):
                    return 0
            with self.assertRaisesRegex(RuntimeError, "not-qualified"):
                self.module.acceptance_report(Completed(), report, time.monotonic() + 1, 36000)

    def test_two_players_finished_and_acknowledged_qualify_long_match(self):
        self.assertIsNotNone(self.module)
        import tempfile
        import time
        with tempfile.TemporaryDirectory() as directory:
            report = Path(directory) / "acceptance.json"
            report.write_text(json.dumps({"exit": 0, "evidence": [{"scenario": "snapshot-driven-continuous-input", "passed": True, "finishedRoomTick": 36040, "secondPlayerFinishedTick": 36040, "secondPlayerInputs": 5950, "replacementInputs": 5930, "inputHz": 10, "secondPlayerAcknowledgedSequence": 5900, "replacementAcknowledgedSequence": 5870}]}))
            class Completed:
                returncode = 0
                def poll(self):
                    return 0
            qualified = self.module.acceptance_report(Completed(), report, time.monotonic() + 1, 36000)
            self.assertEqual(qualified["exit"], 0)

    def test_finished_match_with_stalled_acknowledgements_cannot_qualify(self):
        import tempfile
        import time
        class Completed:
            returncode = 0
            def poll(self):
                return 0
        with tempfile.TemporaryDirectory() as directory:
            report = Path(directory) / "acceptance.json"
            baseline = {"scenario": "snapshot-driven-continuous-input", "passed": True, "finishedRoomTick": 36040, "secondPlayerFinishedTick": 36040, "secondPlayerInputs": 6000, "replacementInputs": 6000, "inputHz": 10, "secondPlayerAcknowledgedSequence": 5990, "replacementAcknowledgedSequence": 5990}
            for field in ("secondPlayerAcknowledgedSequence", "replacementAcknowledgedSequence"):
                with self.subTest(stalled=field):
                    inputs = dict(baseline, **{field: 1})
                    report.write_text(json.dumps({"exit": 0, "evidence": [inputs]}))
                    with self.assertRaisesRegex(RuntimeError, "not-qualified"):
                        self.module.acceptance_report(Completed(), report, time.monotonic() + 1, 36000)

    def test_long_match_with_only_brief_input_burst_cannot_qualify(self):
        import tempfile
        import time
        class Completed:
            returncode = 0
            def poll(self):
                return 0
        with tempfile.TemporaryDirectory() as directory:
            report = Path(directory) / "acceptance.json"
            report.write_text(json.dumps({"exit": 0, "evidence": [{"scenario": "snapshot-driven-continuous-input", "passed": True, "finishedRoomTick": 36040, "secondPlayerFinishedTick": 36040, "secondPlayerInputs": 20, "replacementInputs": 20, "inputHz": 10, "secondPlayerAcknowledgedSequence": 20, "replacementAcknowledgedSequence": 20}]}))
            with self.assertRaisesRegex(RuntimeError, "not-qualified"):
                self.module.acceptance_report(Completed(), report, time.monotonic() + 1, 36000)

    def test_continuous_loop_disconnected_failure_cannot_qualify_passed_report(self):
        self.assertIsNotNone(self.module)
        import tempfile
        import time
        with tempfile.TemporaryDirectory() as directory:
            report = Path(directory) / "acceptance.json"
            report.write_text(json.dumps({"exit": 0, "evidence": [{"scenario": "finished-postgres-profile", "passed": True}]}))
            class Completed:
                returncode = 0
                def poll(self):
                    return 0
            with self.assertRaisesRegex(RuntimeError, "not-qualified"):
                self.module.acceptance_report(Completed(), report, time.monotonic() + 1, 0)


if __name__ == "__main__":
    unittest.main()
