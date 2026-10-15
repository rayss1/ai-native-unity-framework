"""Shared local topology lifecycle. No shell command strings or global env mutation."""
from __future__ import annotations

import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import shutil
import signal
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.request
import uuid

REPO = Path(__file__).resolve().parents[2]
DEFAULT_RUN = "artifacts/manual-test"
WINDOWS = os.name == "nt"
SERVICES = [
    dict(Id="gate", Role="Gate", ProcessId=1, SceneId=1101, Host="GateHost", Health=24101),
    dict(Id="player", Role="Player", ProcessId=2, SceneId=1102, Host="PlayerHost", Health=24102),
    dict(Id="lobby", Role="Lobby", ProcessId=3, SceneId=1103, Host="LobbyHost", Health=24103),
    dict(Id="match", Role="Match", ProcessId=4, SceneId=1104, Host="MatchHost", Health=24104),
    dict(Id="coordinator", Role="Coordinator", ProcessId=5, SceneId=1105, Host="RoomCoordinatorHost", Health=24105),
    dict(Id="battle-1", Role="Battle", ProcessId=100, SceneId=1106, Host="BattleHost", Health=24106, Address="127.0.0.1:22000"),
    dict(Id="battle-2", Role="Battle", ProcessId=101, SceneId=1107, Host="BattleHost", Health=24107, Address="127.0.0.1:22001"),
    dict(Id="acceptance", Role="Client", ProcessId=99, SceneId=1099, Host="TopologyAcceptance", Health=24199),
]
CONFIG_SETTINGS = (
    "AINATIVE_BATTLE_WORKERS", "AINATIVE_ROOMS_PER_WORKER", "AINATIVE_BATTLE_MAILBOX_CAPACITY",
    "AINATIVE_MATCH_LENGTH_TICKS", "AINATIVE_PLAYERS_PER_MATCH", "AINATIVE_OUTBOX_MAX_RESULTS",
    "AINATIVE_OUTBOX_MAX_BYTES", "AINATIVE_ARENA_REPLAY_CAPACITY", "AINATIVE_ARENA_REPLAY_MAX_FILES",
    "AINATIVE_ARENA_REPLAY_MAX_BYTES",
)


class ManualError(RuntimeError):
    pass


def executable(name: str) -> str:
    return name + (".exe" if WINDOWS else "")


def resolve_run(value: str) -> Path:
    path = (REPO / value).resolve()
    artifacts = (REPO / "artifacts").resolve()
    if not artifacts.is_relative_to(REPO.resolve()) or path == artifacts or not path.is_relative_to(artifacts):
        raise ManualError("Run directory must be inside repository artifacts (including symlink targets).")
    return path


def read_json(path: Path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def write_json(path: Path, data):
    temporary = path.with_name(path.name + "." + uuid.uuid4().hex + ".tmp")
    try:
        with temporary.open("x", encoding="utf-8") as stream:
            if not WINDOWS:
                os.chmod(temporary, 0o600)
            json.dump(data, stream, ensure_ascii=False, indent=2)
            stream.write("\n")
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def sha(text: str) -> str:
    return hashlib.sha256(text.encode("utf-8")).hexdigest().upper()


def invoke(arguments, *, cwd=REPO, log=None, allowed=(0,), timeout=60):
    # Never include arguments in errors: database/config commands may carry sensitive values.
    result = subprocess.run([str(a) for a in arguments], cwd=cwd, stdin=subprocess.DEVNULL,
                            stdout=log or subprocess.PIPE, stderr=log or subprocess.PIPE,
                            timeout=timeout, text=True, encoding="utf-8", errors="replace")
    if result.returncode not in allowed:
        raise ManualError(f"{Path(arguments[0]).name} failed (exit {result.returncode}); inspect the relevant log.")
    return result


def find_sdk(value=None, *, check=True) -> Path:
    local = REPO / "artifacts/dotnet" / executable("dotnet")
    selected = value or (str(local) if local.is_file() else shutil.which("dotnet"))
    if not selected:
        raise ManualError("dotnet not found. Install an SDK compatible with repository global.json.")
    sdk = Path(selected).expanduser().resolve()
    if not sdk.is_file():
        raise ManualError("dotnet executable not found.")
    if check:
        result = invoke([sdk, "--version"], allowed=range(256), timeout=30)
        if result.returncode:
            raise ManualError("No SDK compatible with root global.json. Run dotnet --version at repository root; server/global.json does not override this entry.")
        print(f"SDK (repository root): {result.stdout.strip()}", flush=True)
    return sdk


def find_postgres(value=None) -> Path:
    candidates = []
    if value:
        candidates.append(Path(value).expanduser())
    else:
        candidates.append(REPO / "artifacts/postgresql/17.11/bin")
        found = shutil.which(executable("pg_ctl"))
        if found:
            candidates.append(Path(found).parent)
        if WINDOWS:
            candidates.append(REPO / "artifacts/status-sync-20261008/postgresql/runtime/pgsql/bin")
            if os.environ.get("ProgramFiles"):
                candidates.append(Path(os.environ["ProgramFiles"]) / "PostgreSQL/17/bin")
        else:
            candidates += [Path(p) for p in ("/opt/homebrew/opt/postgresql@17/bin", "/usr/local/opt/postgresql@17/bin", "/Library/PostgreSQL/17/bin")]
    for candidate in candidates:
        directory = candidate.resolve()
        if all((directory / executable(tool)).is_file() for tool in ("pg_ctl", "postgres", "initdb", "psql", "createdb")):
            version = invoke([directory / executable("pg_ctl"), "--version"]).stdout.strip()
            if version == "pg_ctl (PostgreSQL) 17.11":
                return directory
    raise ManualError("PostgreSQL 17.11 tools not found. Supply --postgres-bin-directory; no automatic install or version substitution is performed.")


class RunLock:
    """Kernel-released lock; persistent file is not itself evidence of a live owner."""
    def __init__(self, run):
        self.path = run / "manual-start.lock"
        self.stream = None

    def __enter__(self):
        self.stream = self.path.open("a+b")
        try:
            if WINDOWS:
                import msvcrt
                self.stream.seek(0)
                if not self.stream.read(1):
                    self.stream.write(b"\0")
                    self.stream.flush()
                self.stream.seek(0)
                msvcrt.locking(self.stream.fileno(), msvcrt.LK_NBLCK, 1)
            else:
                import fcntl
                fcntl.flock(self.stream.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        except OSError as error:
            self.stream.close()
            raise ManualError("A local supervisor already owns this run directory.") from error
        return self

    def __exit__(self, *args):
        if WINDOWS:
            import msvcrt
            self.stream.seek(0)
            msvcrt.locking(self.stream.fileno(), msvcrt.LK_UNLCK, 1)
        self.stream.close()


class Helper:
    def __init__(self, sdk, run):
        self.sdk = sdk
        self.directory = run / "bin/local-development"
        self.dll = self.directory / "AiNative.Tools.LocalDevelopment.dll"

    def build(self, log):
        invoke([self.sdk, "publish", REPO / "tools/LocalDevelopment/LocalDevelopment.csproj",
                "-c", "Release", "-o", self.directory, "--nologo", "-v", "quiet"], log=log, timeout=600)

    def identity(self, pid):
        result = invoke([self.sdk, self.dll, "identity", str(pid)], allowed=(0, 3))
        return json.loads(result.stdout) if result.returncode == 0 else None

    def keys(self, private, public):
        invoke([self.sdk, self.dll, "keys", private, public])


def same_identity(actual, expected) -> bool:
    if not isinstance(actual, dict) or not isinstance(expected, dict):
        return False
    if not all(isinstance(record.get("Executable"), str) and isinstance(record.get("StartUtc"), str)
               and isinstance(record.get("Pid"), int) for record in (actual, expected)):
        return False
    return bool(actual and expected and actual.get("Pid") == expected.get("Pid")
                and actual.get("StartUtc") == expected.get("StartUtc")
                and Path(actual["Executable"]).resolve() == Path(expected["Executable"]).resolve())


def stop_requested(run, supervisor) -> bool:
    try:
        request = read_json(run / "stop-request.json")
        return isinstance(request, dict) and request.get("Session") == supervisor["Session"] and same_identity(request, supervisor)
    except (OSError, ValueError, KeyError):
        return False


def assert_free_ports(postgres_port):
    tcp = [23001, *range(24101, 24108), *range(23101, 23108), postgres_port]
    if len(set(tcp)) != len(tcp):
        raise ManualError("PostgreSQL port conflicts with a topology port.")
    assert_ports_available(tcp, [22000, 22001])


def assert_ports_available(tcp, udp):
    """Probe explicit port sets so socket tests need not touch running topology services."""
    for kind, ports in ((socket.SOCK_STREAM, tcp), (socket.SOCK_DGRAM, udp)):
        for port in ports:
            with socket.socket(socket.AF_INET, kind) as probe:
                if WINDOWS:
                    probe.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
                elif kind == socket.SOCK_STREAM:
                    # Like the native servers, allow a new listener over old TIME_WAIT
                    # connections. bind + listen still rejects a live listener; never
                    # enable SO_REUSEPORT, which could share a live service's port.
                    probe.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
                try:
                    probe.bind(("127.0.0.1", port))
                    if kind == socket.SOCK_STREAM:
                        probe.listen(1)
                except OSError as error:
                    raise ManualError(f"Local port {port} is occupied; no existing process will be stopped.") from error


def readiness():
    pending = []
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    for service in SERVICES[:-1]:
        try:
            with opener.open(f"http://127.0.0.1:{service['Health']}/health/ready", timeout=1) as response:
                if response.status != 200:
                    pending.append(service["Id"])
        except (OSError, urllib.error.URLError):
            pending.append(service["Id"])
    return pending


def source_identity():
    head = invoke(["git", "rev-parse", "HEAD"]).stdout.strip()
    fantasy = invoke(["git", "-C", REPO / "server/vendor/Fantasy", "rev-parse", "HEAD"]).stdout.strip()
    files = invoke(["git", "ls-files", "-z", "--cached", "--others", "--exclude-standard", "--",
                    "server/src", "server/acceptance", "shared", "packages"]).stdout.split("\0")
    extensions = {".cs", ".csproj", ".proto", ".props", ".targets", ".asmdef"}
    paths = sorted({p for p in files if Path(p).suffix in extensions and (REPO / p).is_file()}, key=str.casefold)
    manifest = "\n".join(p + ":" + digest(REPO / p) for p in paths)
    protocol = "|".join(digest(p) for p in sorted((REPO / "shared/schemas/ainative/v1").glob("*.proto"), key=lambda p: p.name.casefold()))
    return dict(Source=head + "+source-worktree-sha256:" + sha(manifest), Fantasy=fantasy,
                Protocol="sha256:" + sha(protocol), SourceManifestScope="server/src,server/acceptance,shared,packages source/project/schema files")


def service_environment(service, run, sdk, database, match_ticks, players, identity):
    sid = service["Id"]
    env = {
        "AINATIVE_SERVICE_ID": sid, "AINATIVE_PEERS_FILE": str(run / "peers.json"),
        "AINATIVE_SERVICE_PRIVATE_KEY_FILE": str(run / (sid + ".private.pem")),
        "AINATIVE_PLAYER_SIGNING_KEY_FILE": str(run / "player-ticket.private.pem"),
        "AINATIVE_PLAYER_PUBLIC_KEY_FILE": str(run / "player-ticket.public.pem"),
        "AINATIVE_FANTASY_CONFIG_FILE": str(REPO / "infrastructure/topology/Fantasy.config"),
        "ASPNETCORE_URLS": f"http://127.0.0.1:{service['Health']}",
        "AINATIVE_POSTGRES_CONNECTION_STRING": database, "AINATIVE_SERVER_TOPOLOGY": "true",
        "AINATIVE_BATTLE_WORKERS": "2", "AINATIVE_ROOMS_PER_WORKER": "2", "AINATIVE_BATTLE_MAILBOX_CAPACITY": "256",
        "AINATIVE_MATCH_LENGTH_TICKS": str(match_ticks), "AINATIVE_PLAYERS_PER_MATCH": str(players),
        "AINATIVE_ARENA_REPLAY_CAPACITY": "4096", "AINATIVE_ARENA_REPLAY_MAX_FILES": "1024",
        "AINATIVE_ARENA_REPLAY_MAX_BYTES": "2147483648", "AINATIVE_OUTBOX_MAX_RESULTS": "128",
        "AINATIVE_OUTBOX_MAX_BYTES": "16777216", "AINATIVE_OUTBOX_PATH": str(run / "outbox" / sid),
        "AINATIVE_ACCEPTANCE_REPORT": str(run / "acceptance.json"), "DOTNET_ROOT": str(sdk.parent),
    }
    if "Address" in service:
        env["AINATIVE_BATTLE_ADDRESS"] = service["Address"]
    if service["Role"] == "Battle":
        configuration = digest(REPO / "infrastructure/topology/Fantasy.config")
        configuration += "".join("|" + setting + "=" + env[setting] for setting in CONFIG_SETTINGS)
        env.update(AINATIVE_ARENA_REPLAY_PATH=str(run / "replay" / sid), AINATIVE_SOURCE_COMMIT=identity["Source"],
                   AINATIVE_FANTASY_COMMIT=identity["Fantasy"], AINATIVE_PROTOCOL_IDENTITY=identity["Protocol"],
                   AINATIVE_CONFIGURATION_IDENTITY="sha256:" + sha(configuration))
    return env


def debug_profile(service, environment, supervisor):
    paths = [environment[key] for key in ("AINATIVE_PEERS_FILE", "AINATIVE_FANTASY_CONFIG_FILE",
             "AINATIVE_SERVICE_PRIVATE_KEY_FILE", "AINATIVE_PLAYER_SIGNING_KEY_FILE", "AINATIVE_PLAYER_PUBLIC_KEY_FILE")]
    paths += [peer["PublicKeyFile"] for peer in read_json(Path(environment["AINATIVE_PEERS_FILE"]))]
    return dict(Version=1, ServiceId=service["Id"], ProcessId=service["ProcessId"], Host=service["Host"],
                Environment=environment, RequiredFiles={str(Path(p).resolve()): digest(Path(p)) for p in paths},
                SupervisorPid=supervisor["Pid"], SupervisorStartUtc=supervisor["StartUtc"])


def child_environment(settings):
    env = os.environ.copy()
    # Explicit local configuration must not accidentally inherit an IDE handoff.
    for name in ("AINATIVE_LOCAL_DEBUG_PROFILE", "AINATIVE_LOCAL_DEBUG_SERVICE"):
        env.pop(name, None)
    env.update(settings)
    return env


def terminate_child(process, timeout=10):
    if process.poll() is not None:
        return
    try:
        process.send_signal(signal.CTRL_BREAK_EVENT if WINDOWS else signal.SIGTERM)
        process.wait(timeout=timeout)
    except (subprocess.TimeoutExpired, OSError):
        if process.poll() is None:
            process.kill()
            process.wait(timeout=10)


def start_child(service, run, environment, helper):
    directory = run / "bin" / service["Id"]
    app = directory / executable("AiNative." + service["Host"])
    if not app.is_file():
        raise ManualError(f"Build missing: {app}")
    process = None
    try:
        with (run / (service["Id"] + ".stdout.log")).open("wb") as stdout, (run / (service["Id"] + ".stderr.log")).open("wb") as stderr:
            process = subprocess.Popen([str(app), "--pid", str(service["ProcessId"]), "-m", "Release"],
                                       cwd=directory, env=child_environment(environment), stdin=subprocess.DEVNULL,
                                       stdout=stdout, stderr=stderr,
                                       creationflags=subprocess.CREATE_NEW_PROCESS_GROUP if WINDOWS else 0,
                                       start_new_session=not WINDOWS)
        identity = helper.identity(process.pid)
        if not identity:
            raise ManualError(f"{service['Id']} exited during startup; inspect its logs.")
        write_json(run / (service["Id"] + ".pid.json"), dict(identity, Id=service["Id"]))
        return process
    except BaseException:
        if process is not None:
            terminate_child(process)
        raise


class Session:
    def __init__(self, args, run):
        self.args, self.run = args, run
        self.children = []
        self.stop = False
        self.database_identity = None
        self.database_attempt = None
        self.supervisor = None
        self.pg = None
        self.helper = None
        self.data = run / "postgres/data"

    def request_stop(self, *_):
        self.stop = True

    def check_stop(self):
        if self.stop or (self.supervisor and stop_requested(self.run, self.supervisor)):
            raise ManualError("Startup stopped by request.")

    def start(self):
        if sys.platform not in ("win32", "darwin"):
            raise ManualError("Native manual topology currently supports Windows and macOS.")
        if self.args.debug_service and self.run != resolve_run(DEFAULT_RUN):
            raise ManualError("IDE profiles require artifacts/manual-test; omit --run-directory with --debug-service.")
        sdk = find_sdk(self.args.sdk_path)
        self.pg = find_postgres(self.args.postgres_bin_directory)
        self.run.mkdir(parents=True, exist_ok=True)
        if not WINDOWS:
            os.chmod(self.run, 0o700)
        with RunLock(self.run):
            signals = (signal.SIGINT, signal.SIGTERM, signal.SIGBREAK) if WINDOWS else (signal.SIGINT, signal.SIGTERM)
            old = {sig: signal.signal(sig, self.request_stop) for sig in signals}
            try:
                return self.run_locked(sdk)
            finally:
                try:
                    self.cleanup()
                finally:
                    for sig, handler in old.items():
                        signal.signal(sig, handler)

    def run_locked(self, sdk):
        assert_free_ports(self.args.postgres_port)
        if (self.data / "PG_VERSION").exists():
            status = invoke([self.pg / executable("pg_ctl"), "status", "-D", self.data], allowed=(0, 3))
            if status.returncode == 0:
                raise ManualError("The manual database is already running; it will not be adopted.")
        self.helper = Helper(sdk, self.run)
        print("[1/4] Publishing local helper and server hosts...", flush=True)
        with (self.run / "build.log").open("w", encoding="utf-8") as log:
            self.helper.build(log)
            self.supervisor = dict(self.helper.identity(os.getpid()), Session=uuid.uuid4().hex, SdkPath=str(sdk))
            write_json(self.run / "supervisor.json", self.supervisor)
            for service in SERVICES[:-1]:
                record = self.run / (service["Id"] + ".pid.json")
                if record.exists():
                    previous = read_json(record)
                    if same_identity(self.helper.identity(previous["Pid"]), previous):
                        raise ManualError(f"Existing service {service['Id']} is still running; it will not be adopted.")
                self.check_stop()
                project = REPO / "server/src/Hosts" / ("AiNative." + service["Host"]) / ("AiNative." + service["Host"] + ".csproj")
                invoke([sdk, "publish", project, "-c", "Release", "-o", self.run / "bin" / service["Id"], "--nologo", "-v", "quiet"], log=log, timeout=600)
        self.check_stop()
        print("[2/4] Preparing isolated PostgreSQL database...", flush=True)
        self.start_database()
        self.check_stop()
        print("[3/4] Preparing configuration and starting owned services...", flush=True)
        identity = source_identity()
        write_json(self.run / "replay-identities.json", identity)
        peers = []
        for service in SERVICES:
            private = self.run / (service["Id"] + ".private.pem")
            public = self.run / (service["Id"] + ".public.pem")
            self.helper.keys(private, public)
            if not WINDOWS:
                os.chmod(private, 0o600)
            peers.append({k: service[k] for k in ("Id", "Role", "ProcessId", "SceneId")} | {"PublicKeyFile": str(public)})
        self.helper.keys(self.run / "player-ticket.private.pem", self.run / "player-ticket.public.pem")
        if not WINDOWS:
            os.chmod(self.run / "player-ticket.private.pem", 0o600)
        write_json(self.run / "peers.json", peers)
        database = f"Host=127.0.0.1;Port={self.args.postgres_port};Database=ainative_manual;Username=ainative_manual;Pooling=true"
        for service in SERVICES[:-1]:
            self.check_stop()
            env = service_environment(service, self.run, sdk, database, self.args.match_ticks, self.args.players_per_room, identity)
            if service["Role"] == "Battle":
                write_json(self.run / (service["Id"] + ".replay-identity.json"), {k: identity[k] for k in ("Source", "Fantasy", "Protocol")} | {"Configuration": env["AINATIVE_CONFIGURATION_IDENTITY"]})
            if service["Id"] == self.args.debug_service:
                write_json(self.run / "vs-debug.json", debug_profile(service, env, self.supervisor))
                print(f"IDE: Debug build, AiNative.{service['Host']}, profile 'Local Debug {service['Id']}'. Waiting for Rider/VS.", flush=True)
            else:
                self.children.append(start_child(service, self.run, env, self.helper))
        print(f"[4/4] Waiting for readiness. Logs: {self.run}\nKeep this terminal open; Ctrl+C requests cleanup. Accounts are retained.", flush=True)
        deadline = time.monotonic() + 60
        ever_ready = False
        previous = None
        while not self.stop and not stop_requested(self.run, self.supervisor):
            if any(child.poll() is not None for child in self.children):
                raise ManualError("An owned service exited; inspect service logs.")
            pending = readiness()
            if pending != previous:
                print("Waiting: " + ", ".join(pending) if pending else "服务器已就绪：Gate 127.0.0.1:23001；Unity 本机连接关闭 TLS。", flush=True)
            if not pending:
                ever_ready = True
            elif not self.args.debug_service and not ever_ready and time.monotonic() >= deadline:
                raise ManualError("Readiness timeout: " + ", ".join(pending))
            previous = pending
            time.sleep(0.5)
        return 0

    def pg_control(self, *arguments):
        # The daemon must not inherit PIPE handles: pg_ctl exits but postgres stays alive.
        with (self.run / "postgres/control.log").open("a", encoding="utf-8") as log:
            return invoke([self.pg / executable("pg_ctl"), *arguments], log=log, allowed=(0, 1), timeout=45).returncode

    def capture_database(self):
        pidfile = self.data / "postmaster.pid"
        if not self.database_attempt or not pidfile.exists():
            return None
        identity = self.helper.identity(int(pidfile.read_text().splitlines()[0]))
        if (identity and Path(identity["Executable"]).resolve() == (self.pg / executable("postgres")).resolve()
                and dt.datetime.fromisoformat(identity["StartUtc"]) >= self.database_attempt):
            return identity
        return None

    def start_database(self):
        self.data.parent.mkdir(parents=True, exist_ok=True)
        if not (self.data / "PG_VERSION").exists():
            with (self.data.parent / "initdb.log").open("w", encoding="utf-8") as log:
                invoke([self.pg / executable("initdb"), "-D", self.data, "-U", "ainative_manual", "--auth=trust", "--encoding=UTF8", "--locale=C"], log=log)
        self.database_attempt = dt.datetime.now(dt.timezone.utc)
        code = self.pg_control("start", "-D", self.data, "-o", f"-h 127.0.0.1 -p {self.args.postgres_port}", "-l", str(self.data.parent / "server.log"), "-w", "-t", "30")
        self.database_identity = self.capture_database()
        if code or not self.database_identity:
            raise ManualError("Database startup failed; inspect postgres/server.log and control.log.")
        pgargs = ["-h", "127.0.0.1", "-p", str(self.args.postgres_port), "-U", "ainative_manual", "-w"]
        exists = invoke([self.pg / executable("psql"), *pgargs, "-d", "postgres", "-tAc", "SELECT 1 FROM pg_database WHERE datname = 'ainative_manual'"]).stdout.strip()
        if exists != "1":
            invoke([self.pg / executable("createdb"), *pgargs, "ainative_manual"])

    def cleanup(self):
        errors = []
        for child in self.children:
            try:
                terminate_child(child)
            except (OSError, subprocess.SubprocessError) as error:
                errors.append(type(error).__name__ + " while stopping owned service")
        try:
            if self.database_attempt:
                identity = self.database_identity or self.capture_database()
                if identity and same_identity(self.helper.identity(identity["Pid"]), identity):
                    if self.pg_control("stop", "-D", self.data, "-m", "fast", "-w", "-t", "30"):
                        errors.append("Database shutdown failed; inspect postgres/control.log")
        except (OSError, ValueError, ManualError, subprocess.SubprocessError) as error:
            errors.append(type(error).__name__ + " while stopping database; inspect postgres logs")
        if self.supervisor:
            (self.run / "vs-debug.json").unlink(missing_ok=True)
            # Keep supervisor identity for diagnostics; commands validate liveness + lock.
        if self.args.debug_service:
            print("IDE-owned service was left untouched; stop debugging in Rider/VS.", flush=True)
        if errors:
            raise ManualError("; ".join(errors))


def control(command, run, sdk_path=None):
    record_path = run / "supervisor.json"
    if not record_path.exists():
        if command == "status":
            print("Stopped (no supervisor record).")
            return 0
        raise ManualError("No supervisor record exists.")
    # A released kernel lock takes precedence over a record from a reused Python PID.
    try:
        with RunLock(run):
            unlocked = True
    except ManualError:
        unlocked = False
    if unlocked:
        if command == "status":
            print("Stopped (supervisor lock is free; records retained for diagnostics).")
            return 0
        raise ManualError("No active supervisor owns this directory.")
    record = read_json(record_path)
    if not same_identity(record, record) or not record.get("Session"):
        raise ManualError("Invalid or legacy supervisor record; no process was signalled.")
    sdk = find_sdk(sdk_path or record.get("SdkPath"), check=False)
    helper = Helper(sdk, run)
    if not same_identity(helper.identity(record["Pid"]), record):
        raise ManualError("Supervisor identity is stale; no process was signalled.")
    if command == "stop":
        write_json(run / "stop-request.json", record)
        print("Stop requested. Wait for the startup terminal to finish cleanup; stop IDE debugging separately.")
    else:
        pending = readiness()
        print("Running; pending: " + ", ".join(pending) if pending else "Running; all seven services ready.")
    return 0
