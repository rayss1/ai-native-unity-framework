# Unity WS-26 macOS Validation

Status: Required temporary validation path under ADR-0014
Editor: Unity `6000.3.9f1` revision `7a9955a4f2fa`
Primary desktop gate: macOS Apple Silicon ARM64 + Mono

## Scope and pass criteria

Run the exact same source from `shared/gameplay`, `shared/realtime`, the client prediction and Fantasy transport packages, and the Battle Client application through Unity. A valid run uses a clean checkout of the exact commit under review, Unity `6000.3.9f1` revision `7a9955a4f2fa`, and the repository-pinned Fantasy and .NET image identities.

EditMode must report exactly 36 passed, zero failed, and zero skipped. The original 22 contract/prediction tests remain mandatory, together with eight Fantasy transport tests and six application protocol/state tests covering envelope/channel mapping, sequence and epoch handling, truncation, bounded backpressure, disposal, allocation, login, join, routing, timeout, reconnect, and prediction continuity.

PlayMode must report exactly 2 passed, zero failed, and zero skipped against the real Fantasy KCP Battle Host: one covers login/join/first Snapshot/deterministic Input acknowledgement, and one forces reconnect and proves a newer epoch plus continued prediction.

The macOS ARM64 Mono Player smoke must then exit zero and write parseable JSON with `success: true`. It must prove a nonzero session, an increased reconnect epoch, acknowledgement growth before and after reconnect, a nonzero received Tick, and zero dropped input frames.

The run also fails if Unity reports compilation/package-resolution errors, selects another Editor revision, cannot resolve the pinned local/Git UPM packages, produces a non-ARM64 Player, omits the Fantasy license or Third-Party Notices, uses an unpinned Server SDK/runtime image, force-terminates the Host, or changes the clean worktree.

## macOS full command

Start Colima with Linux x64 emulation and a macOS-reachable VM address, then run from a clean Apple Silicon checkout. The reachable address is required because Colima's default SSH port forward handles TCP readiness but does not expose the Fantasy KCP/UDP port:

```bash
colima start --vm-type vz --vz-rosetta --arch aarch64 --cpus 4 --memory 8 --network-address
tools/run-unity-manual-validation.sh
```

If Unity is installed elsewhere:

```bash
UNITY_EDITOR_PATH="/absolute/path/to/Unity" tools/run-unity-manual-validation.sh
```

The script:

1. verifies macOS ARM64, Unity, source, submodule, UPM, license, and tool identities;
2. builds the exact-source Battle Host with the fixed .NET `10.0.202` SDK and `10.0.4` runtime image digests under Linux x64 emulation;
3. starts only that container, keeps readiness available at `127.0.0.1:22080`, discovers and proves a macOS-reachable Colima address, and uses that address for KCP `22000/udp`;
4. executes the exact 36/2 EditMode/PlayMode totals;
5. builds an ARM64-only macOS Mono `.app`, verifies its notices, and runs the deterministic reconnect smoke;
6. stops the Host normally and rejects any tracked worktree drift.

Evidence is written under `artifacts/unity-macos/<full-commit>/`. It includes metadata and identities, image build/Host logs, both NUnit XML/log pairs, Player build/run logs, the `.app`, smoke JSON, staged notices, key SHA-256 hashes, and a summary. Passing the script is required before recording WS-26 as validated; code or scripts alone are not evidence.

## Windows supplemental command

The Windows x64 Mono path remains available for future cross-platform evidence and is no longer the current WS-26 blocking gate:

```powershell
tools/run-unity-windows-validation.ps1
```

It retains its exact 36 EditMode, 2 real-KCP PlayMode, and Windows Player smoke contract. A macOS pass does not claim that Windows has passed, and a future Windows result must be recorded separately.

## Unity Editor UI alternative

The Test Runner can diagnose EditMode or PlayMode failures, but a UI-only run does not replace the automated macOS ARM64 Player build, license checks, smoke JSON, exact image identity, or normal Host shutdown evidence.

## Evidence handoff

For each reviewed commit, retain:

- the full Git commit/tree identities and recursive submodule status;
- `metadata.txt`, `summary.txt`, `hashes.sha256`, both NUnit XML/log pairs, Host/image build logs, Player build/run logs, `smoke.json`, the `.app`, and staged Fantasy notices;
- the operator and UTC timestamp plus any Docker, Unity, license, or platform limitation.

Evidence applies only to that commit. A later change to the Unity project, either client package, Shared Gameplay/Realtime, manifests/lockfile, Fantasy pin, validation script, or Unity version requires a new run. macOS ARM64 Mono evidence does not replace Windows, Universal/x86_64, Regional real-client correction, Android/iOS IL2CPP, protocol generation, architecture, replay/load, or production rollout gates.
