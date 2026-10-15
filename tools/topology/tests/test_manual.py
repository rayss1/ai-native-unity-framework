"""Run: python3 -m unittest discover -s tools/topology/tests -p test_manual.py -v."""
import argparse
import contextlib
import io
import json
import os
from pathlib import Path
import re
import signal
import socket
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import manual
import manual_runtime as runtime


class ManualTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="本机 test ")
        self.root = Path(self.temp.name).resolve()
        self.run = self.root / "artifacts/manual-test"
        self.run.mkdir(parents=True)
        self.repo = patch.object(runtime, "REPO", self.root)
        self.repo.start()
        self.addCleanup(self.repo.stop)
        self.addCleanup(self.temp.cleanup)

    def record(self, **changes):
        return dict(Pid=os.getpid(), StartUtc="2026-10-15T00:00:00.1234567Z", Executable=sys.executable,
                    Session="first", **changes)

    def config(self):
        path = self.root / "infrastructure/topology/Fantasy.config"
        path.parent.mkdir(parents=True)
        path.write_text("test-config")
        return path

    def args(self, **changes):
        values = dict(debug_service=None, sdk_path=None, postgres_bin_directory=None, postgres_port=25439,
                      match_ticks=36000, players_per_room=2)
        values.update(changes)
        return argparse.Namespace(**values)

    def test_defaults_and_all_debug_roles(self):
        args = manual.parser().parse_args(["start"])
        self.assertEqual((args.postgres_port, args.match_ticks, args.players_per_room), (25439, 36000, 2))
        for service in runtime.SERVICES[:-1]:
            self.assertEqual(manual.parser().parse_args(["start", "--debug-service", service["Id"]]).debug_service, service["Id"])

    def test_legacy_cmd_argument_names(self):
        args = manual.parser().parse_args(["start", "-PlayersPerRoom", "4", "-MatchTicks", "7200",
                                          "-PostgresBinDirectory", "path with spaces", "-DebugService", "player"])
        self.assertEqual((args.players_per_room, args.match_ticks, args.debug_service), (4, 7200, "player"))
        self.assertEqual(args.postgres_bin_directory, "path with spaces")

    def test_invalid_parameters(self):
        for arguments in (["--players-per-room", "9"], ["--match-ticks", "0"], ["--postgres-port", "80"], ["--debug-service", "acceptance"]):
            with self.subTest(arguments=arguments), contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
                manual.parser().parse_args(["start", *arguments])

    def test_platform_executable(self):
        for windows, expected in ((True, "AiNative.PlayerHost.exe"), (False, "AiNative.PlayerHost")):
            with patch.object(runtime, "WINDOWS", windows):
                self.assertEqual(runtime.executable("AiNative.PlayerHost"), expected)

    def test_run_path_and_escape(self):
        self.assertEqual(runtime.resolve_run("artifacts/manual-test"), self.run)
        for value in ("artifacts", "../outside", "artifacts/../../outside"):
            with self.assertRaises(runtime.ManualError):
                runtime.resolve_run(value)

    @unittest.skipIf(os.name == "nt", "Symlink permission varies on Windows")
    def test_symlink_escape_rejected(self):
        (self.root / "artifacts/link").symlink_to(self.root, target_is_directory=True)
        with self.assertRaises(runtime.ManualError):
            runtime.resolve_run("artifacts/link/escaped")

    def test_atomic_json_and_unicode(self):
        path = self.run / "配置.json"
        runtime.write_json(path, {"中文": "one"})
        runtime.write_json(path, {"中文": "two"})
        self.assertEqual(runtime.read_json(path), {"中文": "two"})
        self.assertEqual(list(self.run.glob("*.tmp")), [])
        if os.name != "nt":
            self.assertEqual(path.stat().st_mode & 0o777, 0o600)

    def test_lock_excludes_other_process_and_releases(self):
        script = "import sys; from pathlib import Path; sys.path.insert(0, sys.argv[1]); from manual_runtime import RunLock; lock=RunLock(Path(sys.argv[2])); lock.__enter__()"
        args = [sys.executable, "-c", script, str(Path(runtime.__file__).parent), str(self.run)]
        with runtime.RunLock(self.run):
            child = subprocess.run(args, capture_output=True)
            self.assertNotEqual(child.returncode, 0)
        self.assertEqual(subprocess.run(args, capture_output=True).returncode, 0)

    def test_identity_rejects_pid_reuse_and_path_change(self):
        original = self.record()
        self.assertTrue(runtime.same_identity(original, original))
        self.assertFalse(runtime.same_identity(None, original))
        for key, value in (("Pid", 1), ("StartUtc", "different"), ("Executable", "/other")):
            self.assertFalse(runtime.same_identity(dict(original, **{key: value}), original))

    def test_stop_request_binds_session_not_only_pid(self):
        supervisor = self.record()
        for session in ("old", "first"):
            runtime.write_json(self.run / "stop-request.json", dict(supervisor, Session=session))
            self.assertEqual(runtime.stop_requested(self.run, supervisor), session == "first")
        (self.run / "stop-request.json").write_text("invalid json")
        self.assertFalse(runtime.stop_requested(self.run, supervisor))

    def test_duplicate_database_port_rejected(self):
        with self.assertRaisesRegex(runtime.ManualError, "conflicts"):
            runtime.assert_free_ports(23001)

    def test_occupied_port_rejected(self):
        with socket.socket() as listener:
            listener.bind(("127.0.0.1", 0))
            listener.listen()
            with self.assertRaisesRegex(runtime.ManualError, "occupied"):
                runtime.assert_ports_available([listener.getsockname()[1]], [])

    @unittest.skipIf(os.name == "nt", "Windows uses exclusive bind instead of Unix reuseaddr")
    def test_closed_listener_time_wait_does_not_block_restart(self):
        with socket.socket() as listener:
            listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            listener.bind(("127.0.0.1", 0))
            port = listener.getsockname()[1]
            listener.listen()
            # A live REUSEADDR listener must still be rejected.
            with self.assertRaisesRegex(runtime.ManualError, "occupied"):
                runtime.assert_ports_available([port], [])
            with socket.create_connection(("127.0.0.1", port)) as client:
                accepted, _ = listener.accept()
                accepted.close()  # server actively closes; its endpoint enters TIME_WAIT
                self.assertEqual(client.recv(1), b"")
        runtime.assert_ports_available([port], [])

    def test_topology_port_inventory_is_preserved(self):
        with patch.object(runtime, "assert_ports_available") as probe:
            runtime.assert_free_ports(25439)
        probe.assert_called_once_with(
            [23001, *range(24101, 24108), *range(23101, 23108), 25439], [22000, 22001])

    def test_occupied_udp_port_rejected(self):
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as listener:
            listener.bind(("127.0.0.1", 0))
            with self.assertRaisesRegex(runtime.ManualError, "occupied"):
                runtime.assert_ports_available([], [listener.getsockname()[1]])

    def test_postgres_requires_exact_version_and_complete_tools(self):
        directory = self.root / "pg tools"
        directory.mkdir()
        for name in ("pg_ctl", "postgres", "initdb", "createdb", "psql"):
            (directory / runtime.executable(name)).touch()
        with patch.object(runtime, "invoke", return_value=Mock(stdout="pg_ctl (PostgreSQL) 17.11\n")):
            self.assertEqual(runtime.find_postgres(str(directory)), directory)
        with patch.object(runtime, "invoke", return_value=Mock(stdout="pg_ctl (PostgreSQL) 17.12\n")):
            with self.assertRaises(runtime.ManualError):
                runtime.find_postgres(str(directory))
        (directory / runtime.executable("psql")).unlink()
        with self.assertRaises(runtime.ManualError):
            runtime.find_postgres(str(directory))

    def test_repository_postgres_is_discovered_without_path_changes(self):
        directory = self.root / "artifacts/postgresql/17.11/bin"
        directory.mkdir(parents=True)
        for name in ("pg_ctl", "postgres", "initdb", "createdb", "psql"):
            (directory / runtime.executable(name)).touch()
        with patch.object(runtime, "invoke", return_value=Mock(stdout="pg_ctl (PostgreSQL) 17.11\n")), \
             patch.object(runtime.shutil, "which", return_value=None):
            self.assertEqual(runtime.find_postgres(), directory)

    def test_sdk_checks_root_and_reports_policy_failure(self):
        sdk = self.root / "dotnet"
        sdk.touch()
        with patch.object(runtime, "invoke", return_value=Mock(returncode=155, stdout="")) as invoke:
            with self.assertRaisesRegex(runtime.ManualError, "root global.json"):
                runtime.find_sdk(str(sdk))
            self.assertEqual(invoke.call_args.args[0], [sdk, "--version"])

    def test_environment_and_replay_configuration(self):
        self.config()
        identity = dict(Source="source", Fantasy="fantasy", Protocol="protocol")
        env = runtime.service_environment(runtime.SERVICES[5], self.run, self.root / "dotnet", "database", 7200, 4, identity)
        self.assertEqual(env["AINATIVE_BATTLE_ADDRESS"], "127.0.0.1:22000")
        self.assertEqual(env["AINATIVE_PLAYERS_PER_MATCH"], "4")
        configuration = runtime.digest(self.root / "infrastructure/topology/Fantasy.config")
        configuration += "".join("|" + key + "=" + env[key] for key in runtime.CONFIG_SETTINGS)
        self.assertEqual(env["AINATIVE_CONFIGURATION_IDENTITY"], "sha256:" + runtime.sha(configuration))
        changed = runtime.service_environment(runtime.SERVICES[5], self.run, self.root / "dotnet", "database", 7201, 4, identity)
        self.assertNotEqual(changed["AINATIVE_CONFIGURATION_IDENTITY"], env["AINATIVE_CONFIGURATION_IDENTITY"])

    def test_debug_profile_inventory_and_timestamp_precision(self):
        config = self.config()
        env = runtime.service_environment(runtime.SERVICES[1], self.run, self.root / "dotnet", "database", 7200, 4, {})
        peers = []
        for service in runtime.SERVICES:
            public = self.run / (service["Id"] + ".public.pem")
            public.write_text("fake public")
            peers.append(dict(PublicKeyFile=str(public)))
        for name in ("player.private.pem", "player-ticket.private.pem", "player-ticket.public.pem"):
            (self.run / name).write_text("test fixture only")
        runtime.write_json(self.run / "peers.json", peers)
        profile = runtime.debug_profile(runtime.SERVICES[1], env, self.record())
        self.assertEqual(len(profile["RequiredFiles"]), 13)
        self.assertEqual(profile["SupervisorStartUtc"], self.record()["StartUtc"])
        self.assertEqual(profile["RequiredFiles"][str(config)], runtime.digest(config))

    def test_environment_does_not_inherit_debug_selectors(self):
        with patch.dict(os.environ, {"AINATIVE_LOCAL_DEBUG_PROFILE": "stale", "AINATIVE_LOCAL_DEBUG_SERVICE": "player"}):
            env = runtime.child_environment({"AINATIVE_SERVICE_ID": "gate"})
            self.assertNotIn("AINATIVE_LOCAL_DEBUG_PROFILE", env)
            self.assertNotIn("AINATIVE_LOCAL_DEBUG_SERVICE", env)
            self.assertEqual(os.environ["AINATIVE_LOCAL_DEBUG_PROFILE"], "stale")

    def test_start_failure_cleans_child_after_pid_write_error(self):
        service = runtime.SERVICES[1]
        directory = self.run / "bin/player"
        directory.mkdir(parents=True)
        (directory / runtime.executable("AiNative.PlayerHost")).touch()
        child = Mock(pid=123)
        with patch.object(runtime.subprocess, "Popen", return_value=child), patch.object(runtime, "write_json", side_effect=OSError("fail")), patch.object(runtime, "terminate_child") as terminate:
            with self.assertRaises(OSError):
                runtime.start_child(service, self.run, {}, Mock(identity=Mock(return_value=self.record())))
            terminate.assert_called_once_with(child)

    def test_log_failure_does_not_start_child(self):
        directory = self.run / "bin/player"
        directory.mkdir(parents=True)
        (directory / runtime.executable("AiNative.PlayerHost")).touch()
        (self.run / "player.stderr.log").mkdir()
        with patch.object(runtime.subprocess, "Popen") as start:
            with self.assertRaises(OSError):
                runtime.start_child(runtime.SERVICES[1], self.run, {}, Mock())
            start.assert_not_called()

    def test_termination_graceful_then_bounded_force(self):
        child = Mock()
        child.poll.return_value = None
        child.wait.side_effect = [subprocess.TimeoutExpired("probe", 1), 0]
        runtime.terminate_child(child, timeout=1)
        child.send_signal.assert_called_once()
        child.kill.assert_called_once()
        child.wait.assert_any_call(timeout=10)

    def test_already_exited_child_not_signalled(self):
        child = Mock()
        child.poll.return_value = 0
        runtime.terminate_child(child)
        child.send_signal.assert_not_called()

    def test_cleanup_continues_after_one_failure_and_preserves_database(self):
        session = runtime.Session(self.args(), self.run)
        session.children = [Mock(), Mock()]
        session.database_attempt = True
        session.database_identity = self.record()
        session.helper = Mock(identity=Mock(return_value=dict(self.record(), StartUtc="reused")))
        session.pg_control = Mock()
        with patch.object(runtime, "terminate_child", side_effect=[OSError("fail"), None]) as terminate:
            with self.assertRaises(runtime.ManualError):
                session.cleanup()
            self.assertEqual(terminate.call_count, 2)
        session.pg_control.assert_not_called()

    def test_cleanup_stops_owned_database_only(self):
        session = runtime.Session(self.args(debug_service="player"), self.run)
        session.database_attempt = True
        session.database_identity = self.record()
        session.helper = Mock(identity=Mock(return_value=self.record()))
        session.pg_control = Mock(return_value=0)
        with contextlib.redirect_stdout(io.StringIO()):
            session.cleanup()
        self.assertEqual(session.pg_control.call_args.args[0], "stop")
        self.assertEqual(session.children, [])

    def test_free_lock_means_stopped_even_with_current_pid(self):
        runtime.write_json(self.run / "supervisor.json", self.record())
        with self.assertRaisesRegex(runtime.ManualError, "No active supervisor"):
            runtime.control("stop", self.run)

    @unittest.skipIf(os.name == "nt", "Real Unix process lifecycle; Windows has its own execution gate")
    def test_real_child_receives_graceful_signal_and_keeps_logs(self):
        service = runtime.SERVICES[1]
        directory = self.run / "bin/player"
        directory.mkdir(parents=True)
        app = directory / runtime.executable("AiNative.PlayerHost")
        # /bin/sh exec preserves the Popen PID and avoids a long shebang containing spaces.
        probe = directory / "probe.py"
        probe.write_text("import signal,time\nsignal.signal(signal.SIGTERM, lambda *_: exit(0))\nprint('ready',flush=True)\nwhile True: time.sleep(.01)\n")
        import shlex
        app.write_text("#!/bin/sh\nexec " + shlex.quote(sys.executable) + " " + shlex.quote(str(probe)) + "\n")
        app.chmod(0o700)
        helper = Mock(identity=lambda pid: dict(self.record(), Pid=pid))
        child = runtime.start_child(service, self.run, {}, helper)
        try:
            import time
            deadline = time.monotonic() + 5
            while not (self.run / "player.stdout.log").read_text() and time.monotonic() < deadline:
                time.sleep(.01)
            self.assertIn("ready", (self.run / "player.stdout.log").read_text())
            runtime.terminate_child(child, timeout=2)
            self.assertEqual(child.returncode, 0)
            self.assertEqual(runtime.read_json(self.run / "player.pid.json")["Pid"], child.pid)
        finally:
            runtime.terminate_child(child)

    def test_active_stop_writes_session_bound_request(self):
        runtime.write_json(self.run / "supervisor.json", dict(self.record(), SdkPath=sys.executable))
        with patch.object(runtime, "RunLock", side_effect=runtime.ManualError("locked")), \
             patch.object(runtime, "Helper", return_value=Mock(identity=Mock(return_value=self.record()))), \
             contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(runtime.control("stop", self.run), 0)
        self.assertTrue(runtime.stop_requested(self.run, self.record()))

    def test_stale_supervisor_is_not_stopped(self):
        runtime.write_json(self.run / "supervisor.json", dict(self.record(), SdkPath=sys.executable))
        with patch.object(runtime, "RunLock", side_effect=runtime.ManualError("locked")), \
             patch.object(runtime, "Helper", return_value=Mock(identity=Mock(return_value=dict(self.record(), StartUtc="reused")))):
            with self.assertRaisesRegex(runtime.ManualError, "stale"):
                runtime.control("stop", self.run)
        self.assertFalse((self.run / "stop-request.json").exists())

    def run_simulated_session(self, debug=None, fail_at=None):
        self.config()
        session = runtime.Session(self.args(debug_service=debug), self.run)
        helper = Mock()
        helper.identity.return_value = self.record()
        def keys(private, public):
            private.write_text("fake private")
            public.write_text("fake public")
        helper.keys.side_effect = keys
        started = []
        def start(service, *_):
            if service["Id"] == fail_at:
                raise runtime.ManualError("injected child failure")
            child = Mock()
            child.poll.return_value = None
            started.append((service["Id"], child))
            return child
        def ready():
            session.request_stop()
            return [debug] if debug else []
        with contextlib.ExitStack() as stack:
            stack.enter_context(patch.object(runtime.sys, "platform", "darwin"))
            stack.enter_context(patch.object(runtime, "find_sdk", return_value=self.root / "dotnet"))
            stack.enter_context(patch.object(runtime, "find_postgres", return_value=self.root / "pg"))
            stack.enter_context(patch.object(runtime, "assert_free_ports"))
            stack.enter_context(patch.object(runtime, "Helper", return_value=helper))
            stack.enter_context(patch.object(runtime, "invoke"))
            stack.enter_context(patch.object(session, "start_database"))
            stack.enter_context(patch.object(runtime, "source_identity", return_value=dict(Source="s", Fantasy="f", Protocol="p")))
            stack.enter_context(patch.object(runtime, "start_child", side_effect=start))
            stack.enter_context(patch.object(runtime, "readiness", side_effect=ready))
            stack.enter_context(patch.object(runtime.time, "sleep"))
            terminate = stack.enter_context(patch.object(runtime, "terminate_child"))
            stack.enter_context(contextlib.redirect_stdout(io.StringIO()))
            if fail_at:
                with self.assertRaisesRegex(runtime.ManualError, "injected"):
                    session.start()
            else:
                self.assertEqual(session.start(), 0)
            self.assertEqual([call.args[0] for call in terminate.call_args_list], [child for _, child in started])
        with runtime.RunLock(self.run):
            pass
        self.assertFalse((self.run / "vs-debug.json").exists())
        return [sid for sid, _ in started]

    def test_supervisor_starts_and_cleans_seven_services(self):
        self.assertEqual(self.run_simulated_session(), [s["Id"] for s in runtime.SERVICES[:-1]])

    def test_supervisor_leaves_debug_target_to_ide(self):
        self.assertEqual(self.run_simulated_session(debug="player"), [s["Id"] for s in runtime.SERVICES[:-1] if s["Id"] != "player"])

    def test_supervisor_cleans_partial_startup(self):
        self.assertEqual(self.run_simulated_session(fail_at="match"), ["gate", "player", "lobby"])

    def test_signal_sets_stop_without_interrupting_ownership_transfer(self):
        session = runtime.Session(self.args(), self.run)
        session.request_stop(signal.SIGTERM, None)
        self.assertTrue(session.stop)
        with self.assertRaises(runtime.ManualError):
            session.check_stop()


class LegacyParityTests(unittest.TestCase):
    def test_service_table_matches_retained_powershell(self):
        text = (Path(runtime.__file__).parent / "common.ps1").read_text()
        rows = re.findall(r"@\{Id='([^']+)'; Role='([^']+)'; ProcessId=(\d+); SceneId=(\d+); Host='([^']+)'; Health=(\d+)(?:; Address='([^']+)')?\}", text)
        actual = []
        for sid, role, pid, scene, host, health, address in rows:
            row = dict(Id=sid, Role=role, ProcessId=int(pid), SceneId=int(scene), Host=host, Health=int(health))
            if address:
                row["Address"] = address
            actual.append(row)
        self.assertEqual(runtime.SERVICES, actual)

    def test_environment_literals_match_retained_powershell(self):
        text = (Path(runtime.__file__).parent / "common.ps1").read_text()
        env = runtime.service_environment(runtime.SERVICES[0], Path("/run"), Path("/sdk/dotnet"), "db", 1200, 2, {})
        for key, value in re.findall(r"(AINATIVE_[A-Z_]+)='([^']*)'", text):
            if key in env:
                self.assertEqual(env[key], value, key)
        ordered = re.search(r"foreach\(\$setting in @\(([^)]+)\)\)", text).group(1)
        self.assertEqual(tuple(re.findall(r"'([^']+)'", ordered)), runtime.CONFIG_SETTINGS)

    def test_profiles_keep_seven_ide_targets(self):
        repo = Path(runtime.__file__).resolve().parents[2]
        for service in runtime.SERVICES[:-1]:
            path = repo / "server/src/Hosts" / ("AiNative." + service["Host"]) / "Properties/launchSettings.json"
            profile = json.loads(path.read_text())["profiles"]["Local Debug " + service["Id"]]
            self.assertEqual(profile["commandLineArgs"], f"--pid {service['ProcessId']} -m Release")
            self.assertEqual(profile["environmentVariables"]["AINATIVE_LOCAL_DEBUG_SERVICE"], service["Id"])


if __name__ == "__main__":
    unittest.main()
