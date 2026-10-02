#!/usr/bin/env python3
"""Prepare reviewable native TLS and narrowly apply isolated cloud-test endpoints.

Reads credential-bearing Compose JSON locally; never prints its contents. Does
not install packages, install/start units, invoke Docker, or remove volumes.
"""
import argparse
import copy
import hashlib
import json
import os
from pathlib import Path
import subprocess
import time
import uuid

PROJECT = "ainative-cloud-test"
PUBLIC_IP = "111.230.230.247"
DEFAULT_COMPOSE = "/home/ubuntu/ainative-cloud-test/repository/artifacts/cloud-deploy/compose.json"


def private_write(path, data):
    path = Path(path)
    if path.is_symlink():
        raise ValueError("symlink-artifact-not-allowed")
    temporary = path.with_name(path.name + ".tmp-" + uuid.uuid4().hex)
    descriptor = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    try:
        with os.fdopen(descriptor, "wb") as output:
            output.write(data if isinstance(data, bytes) else data.encode("utf-8"))
            output.flush()
            os.fsync(output.fileno())
        os.replace(temporary, path)
        path.chmod(0o600)
    finally:
        temporary.unlink(missing_ok=True)


def paths(compose_file, tls_directory):
    compose_file, tls_directory = Path(compose_file), Path(tls_directory)
    if compose_file.is_symlink() or tls_directory.is_symlink():
        raise ValueError("symlink-deployment-path-not-allowed")
    compose_file, tls_directory = compose_file.resolve(), tls_directory.resolve()
    if tls_directory == compose_file.parent or not tls_directory.is_relative_to(compose_file.parent):
        raise ValueError("tls-directory-must-stay-inside-deployment-directory")
    if any(char.isspace() for char in str(tls_directory)) or any(char in str(tls_directory) for char in ('"', ';', '#')):
        raise ValueError("unsupported-tls-directory-characters")
    return compose_file, tls_directory


def load_compose(compose_file):
    raw = Path(compose_file).read_bytes()
    data = json.loads(raw)
    if data.get("name") != PROJECT:
        raise ValueError("compose-project-must-be-ainative-cloud-test")
    services = data.get("services", {})
    expected = {"gate": (23001, "32301", "tcp"), "battle-1": (22000, "32000", "udp"), "battle-2": (22001, "32001", "udp")}
    for name, mapping in expected.items():
        if name not in services:
            raise ValueError("required-test-service-missing")
        matches = [p for p in services[name].get("ports", []) if isinstance(p, dict) and (p.get("target"), str(p.get("published")), p.get("protocol", "tcp")) == mapping]
        if len(matches) != 1 or matches[0].get("host_ip") not in (("127.0.0.1", "0.0.0.0") if name.startswith("battle-") else ("127.0.0.1",)):
            raise ValueError("expected-test-endpoint-mapping-missing-or-public-gate")
        if name.startswith("battle-") and not isinstance(services[name].get("environment"), dict):
            raise ValueError("battle-environment-must-be-resolved-object")
    for name, service in services.items():
        for port in service.get("ports", []):
            if not isinstance(port, dict):
                raise ValueError("resolved-compose-port-object-required")
            if port.get("host_ip") == "127.0.0.1":
                continue
            if name in ("battle-1", "battle-2") and (port.get("target"), str(port.get("published")), port.get("protocol", "tcp")) == expected[name] and port.get("host_ip") == "0.0.0.0":
                continue
            raise ValueError("non-battle-endpoint-must-remain-loopback")
    return raw, data


def openssl_call(binary, arguments, input_data=None):
    result = subprocess.run([str(binary)] + list(arguments), input=input_data, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, timeout=45, check=False)
    if result.returncode:
        raise ValueError("openssl-operation-failed-existing-certificates-never-auto-rotate")
    return result.stdout


def verify_certificate(binary, certificate, key):
    openssl_call(binary, ["x509", "-in", str(certificate), "-noout", "-checkip", PUBLIC_IP])
    openssl_call(binary, ["x509", "-in", str(certificate), "-noout", "-checkend", "0"])
    public = openssl_call(binary, ["x509", "-in", str(certificate), "-pubkey", "-noout"])
    certificate_public = openssl_call(binary, ["pkey", "-pubin", "-outform", "DER"], public)
    key_public = openssl_call(binary, ["pkey", "-in", str(key), "-pubout", "-outform", "DER"])
    if certificate_public != key_public:
        raise ValueError("existing-certificate-key-mismatch-no-auto-rotation")
    der = openssl_call(binary, ["x509", "-in", str(certificate), "-outform", "DER"])
    return hashlib.sha256(der).hexdigest().upper()


def certificate(binary, tls_directory):
    cert, key = tls_directory / "gate-cert.pem", tls_directory / "gate-key.pem"
    if cert.is_symlink() or key.is_symlink():
        raise ValueError("symlink-credentials-not-allowed")
    if cert.exists() != key.exists():
        raise ValueError("partial-existing-credentials-require-explicit-operator-repair")
    if not cert.exists():
        suffix = uuid.uuid4().hex
        temporary_cert, temporary_key = tls_directory / ("cert.tmp-" + suffix), tls_directory / ("key.tmp-" + suffix)
        private_write(temporary_cert, b"")
        private_write(temporary_key, b"")
        try:
            openssl_call(binary, ["req", "-x509", "-newkey", "rsa:3072", "-nodes", "-days", "7", "-subj", "/CN=" + PUBLIC_IP, "-addext", "subjectAltName=IP:" + PUBLIC_IP, "-keyout", str(temporary_key), "-out", str(temporary_cert)])
            verify_certificate(binary, temporary_cert, temporary_key)
            # Link without replacement: concurrent preparation cannot rotate a pair.
            os.link(temporary_key, key)
            os.link(temporary_cert, cert)
        finally:
            temporary_cert.unlink(missing_ok=True)
            temporary_key.unlink(missing_ok=True)
    key.chmod(0o600)
    cert.chmod(0o600)
    return verify_certificate(binary, cert, key)


def prepare(compose_file, tls_directory, match_ticks=1200, openssl="openssl"):
    if not isinstance(match_ticks, int) or not 120 <= match_ticks <= 360000:
        raise ValueError("match-ticks-must-be-120-to-360000")
    compose_file, tls_directory = paths(compose_file, tls_directory)
    raw, _ = load_compose(compose_file)
    tls_directory.mkdir(mode=0o700, parents=True, exist_ok=True)
    tls_directory.chmod(0o700)
    fingerprint = certificate(openssl, tls_directory)
    # stunnel paths use quoted absolute paths; no configuration interpolation.
    base = tls_directory.as_posix()
    private_write(tls_directory / "stunnel.conf", f'''foreground = yes
pid =
syslog = no
sslVersionMin = TLSv1.2
[gate]
client = no
accept = 0.0.0.0:443
connect = 127.0.0.1:32301
cert = {base}/gate-cert.pem
key = {base}/gate-key.pem
''')
    private_write(tls_directory / "ainative-cloud-test-tls.service", f'''[Unit]
Description=Isolated AI Native cloud test Gate TLS
After=network-online.target docker.service
Wants=network-online.target

[Service]
Type=simple
User=ubuntu
Group=ubuntu
ExecStart=/usr/bin/stunnel4 "{base}/stunnel.conf"
Restart=on-failure
RestartSec=3
AmbientCapabilities=CAP_NET_BIND_SERVICE
CapabilityBoundingSet=CAP_NET_BIND_SERVICE
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=read-only
ReadOnlyPaths="{base}"
InaccessiblePaths=-"{compose_file.as_posix()}" -"{compose_file.parent.as_posix()}/deployment.env"
InaccessiblePaths=-"{compose_file.parent.parent.as_posix()}/topology-compose/private" -"{compose_file.parent.parent.as_posix()}/topology-compose/postgres-password" -/var/run/docker.sock
PrivateTmp=true
PrivateDevices=true
ProtectKernelTunables=true
ProtectKernelModules=true
ProtectControlGroups=true
RestrictSUIDSGID=true
RestrictAddressFamilies=AF_INET AF_INET6 AF_UNIX
StandardOutput=journal
StandardError=journal

[Install]
WantedBy=multi-user.target
''')
    plan = {"project": PROJECT, "operation": "prepare", "composeSha256": hashlib.sha256(raw).hexdigest(), "certificateSha256": fingerprint, "gateTLS": PUBLIC_IP + ":443", "gateUpstream": "127.0.0.1:32301", "battleUDP": [PUBLIC_IP + ":32000", PUBLIC_IP + ":32001"], "matchTicks": match_ticks, "tlsDirectory": str(tls_directory), "requiresExplicitApply": True, "installsPackagesOrUnits": False, "runsDocker": False}
    private_write(tls_directory / "plan.json", json.dumps(plan, indent=2) + "\n")
    return plan


def apply(compose_file, tls_directory, openssl="openssl"):
    compose_file, tls_directory = paths(compose_file, tls_directory)
    plan_path = tls_directory / "plan.json"
    if not plan_path.is_file() or plan_path.is_symlink():
        raise ValueError("prepare-and-review-plan-before-apply")
    plan = json.loads(plan_path.read_text())
    raw, data = load_compose(compose_file)
    if plan.get("project") != PROJECT or plan.get("composeSha256") != hashlib.sha256(raw).hexdigest():
        raise ValueError("compose-changed-after-review-prepare-again")
    match_ticks = plan.get("matchTicks")
    if not isinstance(match_ticks, int) or not 120 <= match_ticks <= 360000:
        raise ValueError("reviewed-match-tick-budget-invalid")
    certificate_path, key_path = tls_directory / "gate-cert.pem", tls_directory / "gate-key.pem"
    if certificate_path.is_symlink() or key_path.is_symlink() or not certificate_path.is_file() or not key_path.is_file():
        raise ValueError("reviewed-credentials-must-exist-without-symlinks")
    if verify_certificate(openssl, certificate_path, key_path) != plan.get("certificateSha256"):
        raise ValueError("certificate-changed-after-review-prepare-again")
    updated = copy.deepcopy(data)
    for n in (1, 2):
        service = updated["services"][f"battle-{n}"]
        for port in service["ports"]:
            if port["target"] == 21999 + n and str(port.get("published")) == str(31999 + n) and port.get("protocol") == "udp":
                port["host_ip"] = "0.0.0.0"
        service["environment"]["AINATIVE_BATTLE_ADDRESS"] = PUBLIC_IP + ":" + str(31999 + n)
        service["environment"]["AINATIVE_MATCH_LENGTH_TICKS"] = str(match_ticks)
    # A final ownership/exposure check precedes the only credential-bearing write.
    backup = compose_file.parent / ("compose.public-test-backup-" + str(time.time_ns()) + ".json")
    descriptor = os.open(backup, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(descriptor, "wb") as output:
        output.write(raw)
        output.flush()
        os.fsync(output.fileno())
    backup.chmod(0o600)
    if compose_file.read_bytes() != raw:
        raise ValueError("compose-changed-during-apply-review-again")
    private_write(compose_file, json.dumps(updated, indent=2) + "\n")
    return {"project": PROJECT, "operation": "apply-config-only", "certificateSha256": plan["certificateSha256"], "battleUDP": [PUBLIC_IP + ":32000", PUBLIC_IP + ":32001"], "matchTicks": match_ticks, "backup": str(backup), "runsDocker": False}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--compose-file", default=DEFAULT_COMPOSE)
    parser.add_argument("--tls-directory", help="Private directory inside the Compose file's deployment directory; defaults to tls")
    parser.add_argument("--match-ticks", type=int, default=1200, help="Preparation only; 36000 for ten-minute qualification")
    parser.add_argument("--openssl", default="openssl")
    parser.add_argument("--apply", action="store_true", help="Explicitly apply the previously reviewed plan to Compose JSON; does not restart services")
    args = parser.parse_args()
    directory = args.tls_directory or str(Path(args.compose_file).parent / "tls")
    try:
        result = apply(args.compose_file, directory, args.openssl) if args.apply else prepare(args.compose_file, directory, args.match_ticks, args.openssl)
        print(json.dumps(result, indent=2))
        return 0
    except Exception as error:
        # All external stderr and credential-bearing JSON stay local and undisclosed.
        reason = str(error) if isinstance(error, ValueError) and str(error).replace("-", "").isalnum() else "preparation-or-apply-failed"
        print(json.dumps({"project": PROJECT, "passed": False, "reason": reason}))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
