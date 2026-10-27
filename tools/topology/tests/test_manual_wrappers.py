"""Exercise local launch wrappers with --help, without starting services."""

import os
from pathlib import Path
import subprocess
import unittest


REPO = Path(__file__).resolve().parents[3]


class ManualWrapperTests(unittest.TestCase):
    def assert_help(self, command, action):
        # --help exits in argparse before run-directory validation or any service work.
        result = subprocess.run(
            [*command, "-RunDirectory", "artifacts/wrapper help test", "--help"],
            cwd=REPO,
            capture_output=True,
            text=True,
            timeout=15,
        )
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn(f"manual.py {action}", result.stdout)
        self.assertIn("--run-directory", result.stdout)

    @unittest.skipUnless(os.name == "nt", "Windows CMD wrapper")
    def test_cmd_start_and_stop_help(self):
        for action in ("start", "stop"):
            with self.subTest(action=action):
                self.assert_help(
                    ["cmd.exe", "/d", "/c", f"tools\\topology\\{action}-manual.cmd"], action
                )

    @unittest.skipIf(os.name == "nt", "POSIX shell wrapper")
    def test_shell_start_and_stop_help(self):
        for action in ("start", "stop"):
            with self.subTest(action=action):
                self.assert_help(["sh", f"tools/topology/{action}-manual.sh"], action)


if __name__ == "__main__":
    unittest.main()
