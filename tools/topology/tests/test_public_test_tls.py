"""Public test deployment boundaries; OpenSSL generation uses the installed binary."""
import copy
import importlib.util
import json
import os
from pathlib import Path
import shutil
import tempfile
import unittest


def fixture():
    roles = ("gate", "player", "lobby", "match", "coordinator", "battle-1", "battle-2")
    services = {name: {"image": "private-image:fixed", "environment": {"SECRET_TEST_VALUE": "do-not-print-this-secret"}, "ports": [{"target": 8080, "published": str(24101 + i), "host_ip": "127.0.0.1", "protocol": "tcp"}], "volumes": ["owned-data:/data"]} for i, name in enumerate(roles)}
    services["gate"]["ports"].append({"target": 23001, "published": "32301", "host_ip": "127.0.0.1", "protocol": "tcp"})
    for n in (1, 2):
        services[f"battle-{n}"]["ports"].append({"target": 21999 + n, "published": str(31999 + n), "host_ip": "127.0.0.1", "protocol": "udp"})
        services[f"battle-{n}"]["environment"].update({"AINATIVE_BATTLE_ADDRESS": f"172.28.0.{15+n}:{21999+n}", "AINATIVE_MATCH_LENGTH_TICKS": "1200"})
    services["postgres"] = {"environment": {"POSTGRES_PASSWORD": "do-not-print-this-secret"}, "volumes": ["postgres-data:/var/lib/postgresql/data"]}
    return {"name": "ainative-cloud-test", "services": services, "volumes": {"postgres-data": {}}}


class PublicTLSBehaviorTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        path = Path(__file__).parents[1] / "public-test-tls.py"
        cls.tool = None
        if path.exists():
            spec = importlib.util.spec_from_file_location("public_test_tls", path)
            cls.tool = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(cls.tool)
        cls.openssl = os.environ.get("OPENSSL_BINARY") or shutil.which("openssl") or ("D:/Program Files/Git/usr/bin/openssl.exe" if Path("D:/Program Files/Git/usr/bin/openssl.exe").exists() else None)

    def module(self):
        self.assertIsNotNone(self.tool, "Missing guarded public TLS preparation tool")
        return self.tool

    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.directory = Path(self.temporary.name)
        self.compose = self.directory / "compose.json"
        self.compose.write_text(json.dumps(fixture()))
        self.tls = self.directory / "tls"

    def prepare(self, ticks=1200):
        module = self.module()
        if not self.openssl:
            self.skipTest("Installed OpenSSL is required for certificate integration behavior")
        return module.prepare(self.compose, self.tls, ticks, self.openssl)

    def test_prepare_does_not_change_running_compose_or_print_secrets(self):
        before = self.compose.read_bytes()
        plan = self.prepare()
        self.assertEqual(self.compose.read_bytes(), before)
        self.assertNotIn("do-not-print-this-secret", json.dumps(plan))
        self.assertEqual(plan["project"], "ainative-cloud-test")
        self.assertEqual(plan["gateTLS"], "111.230.230.247:443")
        self.assertEqual(len(plan["certificateSha256"]), 64)

    def test_apply_changes_only_two_battle_endpoints_and_tick_settings(self):
        self.prepare(36000)
        before = json.loads(self.compose.read_text())
        self.module().apply(self.compose, self.tls, self.openssl)
        after = json.loads(self.compose.read_text())
        expected = copy.deepcopy(before)
        for n in (1, 2):
            service = expected["services"][f"battle-{n}"]
            service["ports"][1]["host_ip"] = "0.0.0.0"
            service["environment"]["AINATIVE_BATTLE_ADDRESS"] = f"111.230.230.247:{31999+n}"
            service["environment"]["AINATIVE_MATCH_LENGTH_TICKS"] = "36000"
        self.assertEqual(after, expected)
        backups = list(self.directory.glob("compose.public-test-backup-*.json"))
        self.assertEqual(len(backups), 1)
        self.assertEqual(json.loads(backups[0].read_text()), before)
        if os.name == "posix":
            self.assertEqual(backups[0].stat().st_mode & 0o777, 0o600)

    def test_other_project_is_rejected_without_creating_tls_files(self):
        data = fixture()
        data["name"] = "production"
        self.compose.write_text(json.dumps(data))
        with self.assertRaisesRegex(ValueError, "project"):
            self.prepare()
        self.assertFalse(self.tls.exists())

    def test_public_gate_or_database_port_is_rejected(self):
        for target in ("gate", "postgres"):
            data = fixture()
            data["services"][target]["ports"] = [{"target": 23001 if target == "gate" else 5432, "published": "32301" if target == "gate" else "5432", "host_ip": "0.0.0.0", "protocol": "tcp"}]
            self.compose.write_text(json.dumps(data))
            with self.assertRaises(ValueError):
                self.prepare()

    def test_existing_certificate_is_reused_and_not_silently_rotated(self):
        first = self.prepare()
        key = (self.tls / "gate-key.pem").read_bytes()
        certificate = (self.tls / "gate-cert.pem").read_bytes()
        second = self.prepare()
        self.assertEqual(second["certificateSha256"], first["certificateSha256"])
        self.assertEqual((self.tls / "gate-key.pem").read_bytes(), key)
        self.assertEqual((self.tls / "gate-cert.pem").read_bytes(), certificate)

    def test_partial_existing_credentials_fail_without_replacement(self):
        self.tls.mkdir()
        key = self.tls / "gate-key.pem"
        key.write_text("existing-private-key-do-not-replace")
        with self.assertRaisesRegex(ValueError, "partial"):
            self.prepare()
        self.assertEqual(key.read_text(), "existing-private-key-do-not-replace")

    def test_compose_changes_after_review_are_rejected_before_apply(self):
        self.prepare()
        data = json.loads(self.compose.read_text())
        data["services"]["player"]["image"] = "changed-after-review"
        self.compose.write_text(json.dumps(data))
        changed = self.compose.read_bytes()
        with self.assertRaisesRegex(ValueError, "review"):
            self.module().apply(self.compose, self.tls, self.openssl)
        self.assertEqual(self.compose.read_bytes(), changed)
        self.assertEqual(list(self.directory.glob("compose.public-test-backup-*.json")), [])

    def test_generated_certificate_verifies_ip_san_and_configuration_keeps_gate_private(self):
        self.prepare()
        import ssl
        certificate = ssl._ssl._test_decode_cert(str(self.tls / "gate-cert.pem"))
        self.assertIn(("IP Address", "111.230.230.247"), certificate["subjectAltName"])
        config = (self.tls / "stunnel.conf").read_text()
        unit = (self.tls / "ainative-cloud-test-tls.service").read_text()
        self.assertIn("accept = 0.0.0.0:443", config)
        self.assertIn("connect = 127.0.0.1:32301", config)
        self.assertIn("User=ubuntu", unit)
        self.assertIn("AmbientCapabilities=CAP_NET_BIND_SERVICE", unit)
        self.assertIn("ProtectHome=read-only", unit)
        if os.name == "posix":
            self.assertEqual((self.tls / "gate-key.pem").stat().st_mode & 0o777, 0o600)

    def test_generated_certificate_supports_verified_tls_handshake_using_ip_identity(self):
        self.prepare()
        import socket
        import ssl
        import threading
        listener = socket.socket()
        listener.bind(("127.0.0.1", 0))
        listener.listen(1)
        listener.settimeout(3)
        server_context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        server_context.load_cert_chain(str(self.tls / "gate-cert.pem"), str(self.tls / "gate-key.pem"))
        errors = []
        def serve():
            try:
                raw, _ = listener.accept()
                with server_context.wrap_socket(raw, server_side=True) as peer:
                    peer.sendall(b"tls-ready")
            except Exception as error:
                errors.append(type(error).__name__)
        worker = threading.Thread(target=serve, daemon=True)
        worker.start()
        client_context = ssl.create_default_context(cafile=str(self.tls / "gate-cert.pem"))
        try:
            with socket.create_connection(listener.getsockname(), timeout=3) as connection:
                with client_context.wrap_socket(connection, server_hostname="111.230.230.247") as peer:
                    self.assertEqual(peer.recv(16), b"tls-ready")
        finally:
            listener.close()
            worker.join(timeout=4)
        self.assertFalse(worker.is_alive())
        self.assertEqual(errors, [])


if __name__ == "__main__":
    unittest.main()
