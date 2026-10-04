"""Exercise the persistence gate with TRX reports, including false-green reports."""
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET

SCRIPT = Path(__file__).parents[1] / "verify-postgres-tests.py"
CASES = [
    ("AiNative.Server.Backend.Tests.PostgresTests", "Migrated_schema_initializes_with_only_runtime_DML_privileges"),
    ("AiNative.Server.Backend.Tests.PostgresTests", "Settlement_transaction_survives_store_restart_and_updates_once"),
    ("AiNative.Server.Rooms.Tests.PostgresAllocationStoreTests", "MigratedSchemaInitializesWithOnlyRuntimeDmlPrivileges"),
    ("AiNative.Server.Rooms.Tests.PostgresAllocationStoreTests", "TerminatedOwnershipConnectionCannotContinueAllocating"),
    ("AiNative.Server.Rooms.Tests.PostgresAllocationStoreTests", "ExclusiveOwnerDurableReloadAndConflictingIdentityAreEnforcedByPostgres"),
]


def report(path, cases, outcome="Passed", summary="Completed"):
    root = ET.Element("TestRun", xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010")
    definitions = ET.SubElement(root, "TestDefinitions")
    results = ET.SubElement(root, "Results")
    for index, (class_name, method) in enumerate(cases):
        test = ET.SubElement(definitions, "UnitTest", id=str(index), name=method)
        ET.SubElement(test, "TestMethod", className=class_name, name=method)
        ET.SubElement(results, "UnitTestResult", testId=str(index), testName=method, outcome=outcome)
    summary_node = ET.SubElement(root, "ResultSummary", outcome=summary)
    # Deliberately plausible totals: a gate must inspect identities and outcomes.
    ET.SubElement(summary_node, "Counters", total="5", executed="5", passed="5", failed="0")
    ET.ElementTree(root).write(path, encoding="utf-8", xml_declaration=True)


class PostgresGateTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.directory = Path(self.temp.name)

    def run_gate(self):
        return subprocess.run([sys.executable, str(SCRIPT), str(self.directory)], capture_output=True, text=True)

    def test_required_cases_pass_across_two_project_reports(self):
        report(self.directory / "backend.trx", CASES[:2])
        report(self.directory / "rooms.trx", CASES[2:])
        result = self.run_gate()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("5 required PostgreSQL tests passed", result.stdout)

    def test_missing_report_fails_closed(self):
        self.assertNotEqual(self.run_gate().returncode, 0)

    def test_discovered_but_unexecuted_required_case_fails(self):
        path = self.directory / "tests.trx"
        report(path, CASES)
        tree = ET.parse(path)
        results = tree.find("{*}Results")
        results.remove(results[-1])
        tree.write(path)
        self.assertNotEqual(self.run_gate().returncode, 0)

    def test_wrong_class_cannot_replace_required_case_despite_green_totals(self):
        report(self.directory / "tests.trx", CASES[:-1] + [("Other.Tests", CASES[-1][1])])
        self.assertNotEqual(self.run_gate().returncode, 0)

    def test_failed_skipped_and_unknown_outcomes_fail_despite_green_totals(self):
        for outcome in ("Failed", "NotExecuted", "Skipped", "Inconclusive", ""):
            with self.subTest(outcome=outcome):
                report(self.directory / "tests.trx", CASES, outcome=outcome)
                self.assertNotEqual(self.run_gate().returncode, 0)

    def test_aborted_run_fails_even_if_required_results_pass(self):
        report(self.directory / "tests.trx", CASES, summary="Aborted")
        self.assertNotEqual(self.run_gate().returncode, 0)

    def test_duplicate_reports_cannot_hide_stale_evidence(self):
        report(self.directory / "one.trx", CASES)
        report(self.directory / "two.trx", CASES)
        self.assertNotEqual(self.run_gate().returncode, 0)

    def test_malformed_report_fails_closed(self):
        (self.directory / "tests.trx").write_text("<broken>", encoding="utf-8")
        self.assertNotEqual(self.run_gate().returncode, 0)


if __name__ == "__main__":
    unittest.main()
