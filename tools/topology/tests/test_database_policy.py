"""Database qualification evidence must be accurate without exposing credentials."""
import json
import hashlib
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[3]
SCRIPT = ROOT / "tools/topology/database-policy.ps1"
SECRET = "fake-password-never-export-938172"


@unittest.skipUnless(shutil.which("pwsh"), "PowerShell 7 required")
class DatabasePolicyTests(unittest.TestCase):
    def invoke(self, connection):
        env = os.environ.copy()
        if connection is None:
            env.pop("AINATIVE_TEST_POSTGRES", None)
        else:
            env["AINATIVE_TEST_POSTGRES"] = connection
        result = subprocess.run(
            ["pwsh", "-NoProfile", "-Command",
             "$ErrorActionPreference='Stop'; & '" + str(SCRIPT) + "' | ConvertTo-Json -Compress"],
            env=env, capture_output=True, text=True, encoding="utf-8", timeout=30,
        )
        self.assertNotIn(SECRET, result.stdout + result.stderr)
        return result

    def test_effective_pooling_and_explicitness_without_secret_fields(self):
        for suffix, enabled, explicit in [
            ("", True, False), (";Pooling=true", True, True),
            (";Pooling=false", False, True), (";pOoLiNg=FaLsE", False, True),
            (';Pooling=" true "', True, True),
            (';Password="embedded;Pooling=;value"', True, False),
        ]:
            with self.subTest(suffix=suffix):
                result = self.invoke(f'Host=localhost;Password="{SECRET};quoted"' + suffix)
                self.assertEqual(0, result.returncode, result.stderr)
                self.assertEqual({"Provider": "Npgsql", "PoolingEnabled": enabled,
                                  "PoolingExplicitlyConfigured": explicit}, json.loads(result.stdout))

    def test_invalid_policy_fails_closed_and_sanitizes_parser_exceptions(self):
        for connection in [None, "", f"Password={SECRET};Pooling=perhaps",
                           f"Password={SECRET};Pooling=1", f'Password="{SECRET}',
                           f"Password={SECRET};Pooling="]:
            with self.subTest(present=bool(connection)):
                result = self.invoke(connection)
                self.assertNotEqual(0, result.returncode)
                self.assertEqual("", result.stdout.strip())

    def test_does_not_mutate_original_environment(self):
        env = os.environ.copy()
        env["AINATIVE_TEST_POSTGRES"] = f"Password={SECRET};Pooling=false"
        result = subprocess.run(
            ["pwsh", "-NoProfile", "-Command", "$before=$env:AINATIVE_TEST_POSTGRES; & '"
             + str(SCRIPT) + "' | Out-Null; if($before -cne $env:AINATIVE_TEST_POSTGRES){exit 7}"],
            env=env, capture_output=True, text=True, encoding="utf-8", timeout=30)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertNotIn(SECRET, result.stdout + result.stderr)

    def test_summary_binds_policy_file_hash_and_values_even_when_stage_fails(self):
        # Replace only external build/service orchestration; exercise the real
        # qualification wrapper through its final evidence writer.
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            run = root / "run"
            (run / "bin").mkdir(parents=True)
            (run / "replay-identities.json").write_text("{}")
            for name in ["qualify-capacity.ps1", "database-policy.ps1"]:
                shutil.copyfile(SCRIPT.parent / name, root / name)
            (root / "common.ps1").write_text(
                "param($SdkPath,$RunDirectory)\n$Run=$RunDirectory;$Repo=$PSScriptRoot\n")
            (root / "faultserver.ps1").write_text(
                "$global:LASTEXITCODE=1\n")
            env = os.environ.copy()
            env["AINATIVE_TEST_POSTGRES"] = f"Host=localhost;Password={SECRET};Pooling=false"
            result = subprocess.run(
                ["pwsh", "-NoProfile", "-File", str(root / "qualify-capacity.ps1"),
                 "-RunDirectory", str(run), "-Profile", "single", "-SkipBuild"],
                env=env, capture_output=True, text=True, encoding="utf-8", timeout=30)
            self.assertEqual(1, result.returncode)
            summary = json.loads((run / "qualification-summary.json").read_text(encoding="utf-8-sig"))
            self.assertIn("DatabasePolicy", summary)
            evidence = summary["DatabasePolicy"]
            path = Path(evidence["Path"])
            self.assertEqual(run / "database-policy.json", path)
            self.assertEqual(hashlib.sha256(path.read_bytes()).hexdigest().upper(), evidence["Sha256"])
            self.assertEqual(json.loads(path.read_text(encoding="utf-8-sig")), evidence["Policy"])
            self.assertFalse(evidence["Policy"]["PoolingEnabled"])
            self.assertNotIn(SECRET, result.stdout + result.stderr + json.dumps(summary) + path.read_text())


if __name__ == "__main__":
    unittest.main()
