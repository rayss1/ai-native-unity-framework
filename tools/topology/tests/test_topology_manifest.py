"""Check the shared tool inventory against deployment and generated artifacts."""

import contextlib
import io
import json
from pathlib import Path
import re
import runpy
import shutil
import tempfile
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET


REPO = Path(__file__).resolve().parents[3]
TOOLS = REPO / "tools/topology"
MANIFEST = json.loads((TOOLS / "topology-manifest.json").read_text(encoding="utf-8"))
NS = {"f": "http://fantasy.net/config"}


class TopologyManifestTests(unittest.TestCase):
    def test_inventory_matches_fantasy_and_compose_deployment(self):
        services = MANIFEST["Services"]
        self.assertEqual(len(services), 8)
        self.assertEqual([s["Id"] for s in services if s["Role"] == "Client"], ["acceptance"])
        self.assertEqual(services[-1]["Id"], "acceptance")
        for key in ("Id", "ProcessId", "SceneId", "Health"):
            self.assertEqual(len({s[key] for s in services}), len(services), key)
        self.assertEqual(set(MANIFEST["ComposeAddresses"]), {s["Id"] for s in services})
        self.assertEqual(len(set(MANIFEST["ComposeAddresses"].values())), len(services))
        config = ET.parse(REPO / "infrastructure/topology/Fantasy.config")
        scenes = {
            int(s.attrib["id"]): s.attrib for s in config.findall("f:server/f:scenes/f:scene", NS)
        }
        self.assertEqual(set(scenes), {s["SceneId"] for s in services})
        compose = (REPO / "infrastructure/topology/compose.yaml").read_text()
        for service in services:
            sid = service["Id"]
            scene = scenes[service["SceneId"]]
            self.assertEqual(int(scene["processConfigId"]), service["ProcessId"])
            if "Address" in service:
                self.assertEqual(service["Address"], "127.0.0.1:" + scene["outerPort"])
            if service["Role"] == "Client":
                # Acceptance is a peer identity, not a long-running Compose service.
                self.assertNotIn("\n  acceptance:", compose)
                continue
            block = re.search(
                r"^  " + re.escape(sid) + r":\n(.*?)(?=^  \S|^\S|\Z)", compose, re.M | re.S
            ).group(1)
            self.assertIn("ipv4_address: " + MANIFEST["ComposeAddresses"][sid], block)
            self.assertIn('AINATIVE_PROCESS_ID: "' + str(service["ProcessId"]) + '"', block)
        self.assertTrue(all(isinstance(v, str) for v in MANIFEST["EnvironmentDefaults"].values()))
        self.assertTrue(
            set(MANIFEST["ReplayIdentitySettings"]) <= MANIFEST["EnvironmentDefaults"].keys()
        )

    def test_compose_generator_preserves_peers_and_machine_mapping(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            tools = root / "tools/topology"
            tools.mkdir(parents=True)
            for name in ("initialize-compose.py", "topology-manifest.json"):
                shutil.copy2(TOOLS / name, tools / name)
            config = root / "infrastructure/topology/Fantasy.config"
            config.parent.mkdir(parents=True)
            shutil.copy2(REPO / "infrastructure/topology/Fantasy.config", config)

            def fake_openssl(command, **kwargs):
                # Only key generation is stubbed; execute the real generator and XML writes.
                self.assertEqual(command[0], "openssl")
                Path(command[command.index("-out") + 1]).write_text("test key")

            with patch(
                "subprocess.run", side_effect=fake_openssl
            ) as openssl, contextlib.redirect_stdout(io.StringIO()):
                runpy.run_path(str(tools / "initialize-compose.py"))
                self.assertEqual(openssl.call_count, 18)
                openssl.reset_mock()
                runpy.run_path(str(tools / "initialize-compose.py"))
                openssl.assert_not_called()  # Existing keys must not be rotated.
            public = root / "artifacts/topology-compose/public"
            peers = json.loads((public / "peers.json").read_text())
            expected = [
                {
                    **{k: s[k] for k in ("Id", "Role", "ProcessId", "SceneId")},
                    "PublicKeyFile": "/run/topology/public/" + s["Id"] + ".public.pem",
                }
                for s in MANIFEST["Services"]
            ]
            self.assertEqual(peers, expected)
            generated = ET.parse(public / "Fantasy.config")
            machines = {
                int(m.attrib["id"]): m.attrib
                for m in generated.findall("f:server/f:machines/f:machine", NS)
            }
            processes = {
                int(p.attrib["id"]): p.attrib
                for p in generated.findall("f:server/f:processes/f:process", NS)
            }
            self.assertEqual(set(machines), {s["ProcessId"] for s in MANIFEST["Services"]})
            for service in MANIFEST["Services"]:
                pid = service["ProcessId"]
                self.assertEqual(processes[pid]["machineId"], str(pid))
                for key in ("outerIP", "outerBindIP", "innerBindIP"):
                    self.assertEqual(
                        machines[pid][key], MANIFEST["ComposeAddresses"][service["Id"]]
                    )


if __name__ == "__main__":
    unittest.main()
