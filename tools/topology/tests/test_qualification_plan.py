"""Execute only the pure qualification planning boundary; no service or credential access."""
import json
from pathlib import Path
import subprocess
import unittest


class QualificationPlanTests(unittest.TestCase):
    script = Path(__file__).parents[1] / "qualify-capacity.ps1"

    def invoke(self, *args):
        return subprocess.run(["pwsh", "-NoProfile", "-File", str(self.script), "-PlanOnly", *args],
                              capture_output=True, text=True, encoding="utf-8", timeout=30)

    def test_sweep_preserves_compatibility_then_qualifies_actual_density(self):
        result = self.invoke()
        self.assertEqual(result.returncode, 0, result.stderr)
        plan = json.loads(result.stdout)
        stages = plan["Stages"]
        self.assertEqual((stages[0]["Rooms"], stages[0]["Players"], stages[0]["InputHz"]), (8, 2, 20))
        self.assertEqual([s["Rooms"] for s in stages[1:]], [4, 8, 16])
        self.assertEqual([s["Density"] for s in stages[1:]], [1, 2, 4])
        self.assertTrue(all(s["Workers"] == 2 and s["Players"] == 8 and s["InputHz"] == 60
                            and s["WarmupSeconds"] == 60 and s["DurationSeconds"] == 300 for s in stages[1:]))
        self.assertIn("first failure", plan["AfterSweep"])
        self.assertIn("3600", plan["AfterSweep"])

    def test_soak_cannot_silently_become_short_diagnostic(self):
        result = self.invoke("-Profile", "soak", "-DurationSeconds", "300")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(json.loads(result.stdout)["Stages"][0]["DurationSeconds"], 3600)

    def test_capacity_mismatch_is_rejected_before_any_process(self):
        result = self.invoke("-Profile", "single", "-RoomCount", "7")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("exact two-node", result.stderr)


if __name__ == "__main__":
    unittest.main()
