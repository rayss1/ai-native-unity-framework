"""Exercise local launch wrappers with --help, without starting services."""

import os
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


REPO = Path(__file__).resolve().parents[3]


class ManualWrapperTests(unittest.TestCase):
    @unittest.skipUnless(os.name == "nt", "Windows interpreter discovery")
    def test_cmd_python_fallbacks_and_utf8(self):
        # Use a real interpreter under an isolated PATH, not a batch-file mock:
        # invoking a nested .cmd without CALL would bypass the wrapper's exit handling.
        for location in (
            "bin/python.exe",
            "bin/python3.exe",
            "anaconda3/python.exe",
            "miniconda3/python.exe",
        ):
            with self.subTest(location=location), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                executable = root / location
                executable.parent.mkdir(parents=True)
                shutil.copy2(sys.executable, executable)
                for dll in Path(sys.base_prefix).glob("*.dll"):
                    shutil.copy2(dll, executable.parent / dll.name)
                env = os.environ.copy()
                env.update(
                    PATH=str(root / "bin") + os.pathsep + str(Path(env["SYSTEMROOT"]) / "System32"),
                    USERPROFILE=str(root),
                    PYTHONHOME=sys.base_prefix,
                    PYTHONUTF8="0",
                )
                env.pop("PYTHONIOENCODING", None)
                # The probe replaces only the service entry, so this cannot start servers.
                (root / "manual.py").write_text(
                    "import json, sys\n"
                    "print(json.dumps([sys.flags.utf8_mode, sys.argv[1:]], ensure_ascii=False))\n"
                    "sys.exit(7 if '--fail' in sys.argv else 0)\n",
                    encoding="utf-8",
                )
                for action in ("start", "stop"):
                    wrapper = root / f"{action}-manual.cmd"
                    shutil.copy2(REPO / "tools/topology" / wrapper.name, wrapper)
                    for fail in (False, True):
                        arguments = [action, "-RunDirectory", "artifacts/中文 path"]
                        if fail:
                            arguments.append("--fail")
                        result = subprocess.run(
                            [env["COMSPEC"], "/d", "/c", str(wrapper), *arguments[1:]],
                            cwd=root,
                            env=env,
                            input=b"\n",  # Dismiss the interactive failure pause.
                            capture_output=True,
                            timeout=15,
                        )
                        self.assertEqual(result.returncode, 7 if fail else 0, result.stderr)
                        probe = json.loads(result.stdout.splitlines()[0].decode("utf-8"))
                        self.assertEqual(probe, [1, arguments])

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
