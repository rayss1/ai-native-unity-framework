# Public IP test endpoints with native TLS

`public-test-tls.py` prepares the isolated `ainative-cloud-test` project for a temporary no-domain test using IP `111.230.230.247`. It reads the local credential-bearing Compose JSON and emits only a sanitized plan and the SHA-256 fingerprint of the certificate's DER bytes. Never display or attach the Compose JSON, backup, private key, or deployment environment.

Public endpoints are TCP `111.230.230.247:443` through native stunnel to Gate's unchanged loopback `127.0.0.1:32301`, plus Battle UDP `32000` and `32001`. Gate TCP `32301`, HTTP health, PostgreSQL, inner routing and other services remain private. Internal Battle target ports remain `22000/22001`. The tool does not touch any other project or the existing legacy host endpoint on `22000`.

Run preparation **as Ubuntu's `ubuntu` user** so its private TLS directory and key are readable by the generated unit's `User=ubuntu`. OpenSSL must already be installed; `stunnel4` must be supplied through the separately reviewed host installation workflow. This script does not install packages, install units, start services, invoke Docker or delete volumes.

```sh
cd /home/ubuntu/ainative-cloud-test/repository
python3 tools/topology/public-test-tls.py --match-ticks 1200
```

Preparation leaves `artifacts/cloud-deploy/compose.json` byte-for-byte unchanged. It creates `artifacts/cloud-deploy/tls` with mode `0700`, and these mode `0600` artifacts:

- `gate-key.pem` and a seven-day self-signed `gate-cert.pem` with IP SAN `111.230.230.247`.
- `stunnel.conf`, forwarding only native TCP TLS `443` to Gate loopback `32301`.
- `ainative-cloud-test-tls.service`, a foreground service running as `ubuntu` with only `CAP_NET_BIND_SERVICE`, `NoNewPrivileges`, read-only home/system protection and private temporary/device namespaces. The unit denies access to Compose credentials, topology private keys and the Docker socket.
- `plan.json`, containing the reviewed Compose hash, public endpoints, tick budget and certificate fingerprint.

The certificate is deliberately self-signed for this isolated test. Clients must use the reviewed SHA-256 certificate pin or explicitly trust this exact test certificate while verifying its IP identity. Do not disable certificate validation globally. Existing certificates are verified and reused; missing halves, expired certificates, SAN mismatch and key mismatch fail without replacement. Renew or rotate only through an explicit operator action, then review and update the client pin before use.

After reviewing the sanitized plan and generated configuration, apply the file change explicitly:

```sh
python3 tools/topology/public-test-tls.py --apply
```

Apply refuses a Compose or certificate change since preparation. It first saves a mode `0600` backup named `compose.public-test-backup-*.json`, then changes only the two Battle UDP mappings from loopback to `0.0.0.0`, their advertised addresses to the public IP and their `AINATIVE_MATCH_LENGTH_TICKS` settings. Images, other ports, credentials, volumes, Gate and all other service definitions are preserved. Apply does not restart any process.

The operator then separately validates and installs the reviewed native unit, supplies `stunnel4`, and recreates only this project's two Battle services through the existing deployment workflow. Keep all outbox/database volumes. Confirm the Gate TCP backend is still loopback-only and the only public test listeners are TCP `443` and UDP `32000/32001`. Host/cloud ingress rules and actual public TLS/Fantasy/KCP behavior require real network verification.

For a ten-minute run, prepare a new review with `--match-ticks 36000`, then explicitly apply it; the existing certificate stays unchanged. Re-preparation is also required after a separately approved image/configuration update changes Compose. Image upgrades are handled by their own workflow.

Rollback uses the exact private backup and the same scoped Battle deployment workflow; stop the dedicated TLS unit if withdrawing the public TCP endpoint. Do not remove persistent volumes or erase pending results. The script provides no automatic rollback or destructive cleanup command.

Local verification:

```sh
python3 -m unittest discover -s tools/topology/tests -p test_public_test_tls.py
```

These tests use real installed OpenSSL and a verified loopback TLS handshake, and assert preparation immutability, exact apply scope, secret-free plans, backups, drift rejection and certificate reuse. They do not establish Linux systemd/stunnel startup, public firewall reachability or actual cloud deployment results.
