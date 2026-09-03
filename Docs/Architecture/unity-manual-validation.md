# Unity macOS Validation

Status: Required temporary validation path under ADR-0014
Editor: Unity `6000.3.9f1` revision `7a9955a4f2fa`
Primary desktop gate: macOS Apple Silicon ARM64 + Mono

## Scope and pass criteria

Run the exact same source from `shared/gameplay`, `shared/realtime`, the client prediction and Fantasy transport packages, and the Battle Client application through Unity. A valid run uses a clean checkout of the exact commit under review, Unity `6000.3.9f1` revision `7a9955a4f2fa`, and the repository-pinned Fantasy and .NET image identities.

EditMode must report exactly 44 passed, zero failed, and zero skipped. The original contract, prediction, Fantasy transport, and application protocol/state tests remain mandatory. The WS-27 histogram tests and six WS-28 presentation-smoothing/composition tests freeze bounded correction measurement, render continuity, snap/reset behavior, authority separation, and zero allocation.

PlayMode must report exactly 2 passed, zero failed, and zero skipped against the real Fantasy KCP Battle Host: one covers login/join/first Snapshot/deterministic Input acknowledgement, and one forces reconnect and proves a newer epoch plus continued prediction.

The macOS ARM64 Mono Player smoke must then exit zero and write parseable JSON with `success: true`. It must prove a nonzero session, an increased reconnect epoch, acknowledgement growth before and after reconnect, a nonzero received Tick, and zero dropped input frames.

The same Player binary must then run for a ten-second warm-up and a 60-second measured window under the symmetric Regional profile: `50 +/- 10 ms` delay with 25% correlation in each direction, 1% random loss, 0.5% duplication, and 1% reordering with 50% correlation. The result must contain at least 1,000 reconciliation samples, correction P95 at or below 250 mm, correction P99 at or below 750 mm, no more than two corrections above 250 mm per player-minute, and zero history misses, dropped prediction inputs, or dropped input frames. WS-28 additionally requires at least one smoothed presentation correction, zero presentation snaps, and a final residual no greater than 250 mm. Both qdiscs must record their exact active configuration and at least one packet drop across the run.

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
4. executes the exact 44/2 EditMode/PlayMode totals;
5. builds an ARM64-only macOS Mono `.app`, verifies its notices, and runs the deterministic reconnect smoke;
6. applies the symmetric Regional qdiscs only to the Colima-to-container path, runs the 10+60-second real-client correction measurement, records qdisc statistics, and restores the original interface classes;
7. stops the Host normally and rejects any tracked worktree drift.

Evidence is written under `artifacts/unity-macos/<full-commit>/`. It includes metadata and identities, image build/Host logs, both NUnit XML/log pairs, Player build/run logs, the `.app`, smoke and Regional correction JSON, qdisc configuration/statistics, staged notices, key SHA-256 hashes, and a summary. Passing the script is required before recording a current WS-26 through WS-28 candidate as validated; code or scripts alone are not evidence.

## Recorded exact-main result

The reviewed bundle for exact `main` source `2987ce08475b2cf2342a98326ff86fa422a3a6a5` (tree `1f441d2cfbadd009533f707da3a78ddabbefbc0a`) passed on macOS Apple Silicon with Fantasy `f8bed0d464924f159d46498f1311206ea0694be8`, protocol SHA-256 `3cb86e21687e65af0e0d409d9186384d0f959fd6aa873eb9e1cd0cb39c77d37d`, and configuration SHA-256 `fc0714bcbe7c8c673cf638506a45f3a4440585f4024ff78048434346ab8a66e4`.

Unity `6000.3.9f1` revision `7a9955a4f2fa` reported 36/36 EditMode and 2/2 real-Fantasy-KCP PlayMode tests. The ARM64 Mono Player completed login, room join, input acknowledgement, forced reconnect, epoch `4 -> 5`, acknowledgement `30 -> 31`, and continued to Tick `2319` with `droppedInputFrames = 0`. The Host exited `0`, drained rooms and KCP, and was not force-terminated. The bundle used the fixed .NET SDK/runtime image digests and staged byte-matching Fantasy license and Third-Party Notices beside the `.app`.

The retained bundle is under `artifacts/unity-macos/2987ce08475b2cf2342a98326ff86fa422a3a6a5/`. Its hash manifest SHA-256 is `308b1fea377b1509cd8e9fa6a31dddbd2da832830ad451a5936f6858d1e5b538`; the EditMode XML, PlayMode XML, smoke JSON, and Player executable SHA-256 values are `07771b88b205206d36a24d0606169904acadda000eed16c4e59d4cb18deef71f`, `8991225d6f2dbf8b42c405ac957ea37bec3b91d1a88eef7c6961b16c510617df`, `10d0fb06b46fe116b9560f7c31810894e18c68e920c398c915daacb46c8108e7`, and `fefeba6994eb09fa3ed503666e510a115f3b1db3f7a2a9a3a2ab907d8dd8ced7`. The hashes were independently rechecked during evidence review.

The same source passed [.NET run 33486172442](https://github.com/rayss1/ai-native-unity-framework/actions/runs/33486172442) and the complete [Battle Host production-validation run 33500838422](https://github.com/rayss1/ai-native-unity-framework/actions/runs/33500838422). Attempt 1 of the latter hit only the unchanged telemetry comparison gate because its exporter-disabled baseline was anomalously low; one controlled failed-job rerun, with no source or threshold change, passed the full qualification including the 60-minute soak. This hosted Ubuntu evidence is release-equivalent validation, not a real Linux environment canary.

### WS-27 exact-main Regional result

The reviewed bundle for exact `main` source `6376265658a26fa07b08fc737c3932d52212314a` (tree `e5ea86c01e85e55475052e75f2fcd876db7069e3`) passed with the same pinned Fantasy, protocol, configuration, Unity, SDK, and runtime identities. Unity reported 38/38 EditMode and 2/2 real-Fantasy-KCP PlayMode tests. The ARM64 Mono reconnect smoke advanced epoch `4 -> 5` and acknowledgement `30 -> 32` with zero dropped input frames; the Host exited zero after draining rooms and KCP without forced termination.

Under the frozen symmetric Regional profile, the Player completed `10.0017 s` warm-up plus `60.0169 s` measurement. Its `1,219` reconciliation samples produced correction P95/P99 `9/10 mm`, maximum `12 mm`, zero corrections above `250 mm`, and zero history misses, stale Snapshots, dropped prediction inputs, or dropped application frames. The ingress/egress qdiscs recorded `34/138` dropped packets and were restored to their original classes.

The retained bundle is under `artifacts/unity-macos/6376265658a26fa07b08fc737c3932d52212314a/`. Its hash manifest SHA-256 is `1d9a8f403114c9cdcbbc45ebb42f2d0e95b7539ccffa161d5b5e7aeec4339591`; the EditMode XML, PlayMode XML, smoke JSON, Regional JSON, and Player executable SHA-256 values are `139f0e25d5778307bf08982575ab655a72a3936ce81938aa60b282dd8581504b`, `0bcfa4a4dc20fe4adc54bd432f37e332eb37ecc5ba60080ded09dfa0615e4081`, `c20e55c58be518b221a8b593b36a79953830644a2f8bec1da3b5c32f9d4745a5`, `6585ada48f97493e9b03805c95402f7906ec18aa77e1dd7fcddb92c86b0734c9`, and `99a8bb4062cb82bb6db4180250003a372789346455ec8e8d17142aa2fea4e784`. All manifest entries, binary architecture, and staged notices were independently rechecked. Exact-main [.NET run 33604282890](https://github.com/rayss1/ai-native-unity-framework/actions/runs/33604282890) passed 92/92 tests and all product checks.

## Windows supplemental command

The Windows x64 Mono path remains available for future cross-platform evidence and is no longer the current WS-26 blocking gate:

```powershell
tools/run-unity-windows-validation.ps1
```

It retains the current exact 44 EditMode, 2 real-KCP PlayMode, and Windows Player smoke contract. A macOS pass does not claim that Windows has passed, and a future Windows result must be recorded separately.

## Unity Editor UI alternative

The Test Runner can diagnose EditMode or PlayMode failures, but a UI-only run does not replace the automated macOS ARM64 Player build, license checks, smoke JSON, exact image identity, or normal Host shutdown evidence.

## Evidence handoff

For each reviewed commit, retain:

- the full Git commit/tree identities and recursive submodule status;
- `metadata.txt`, `summary.txt`, `hashes.sha256`, both NUnit XML/log pairs, Host/image build logs, Player build/run logs, `smoke.json`, `regional-correction.json`, qdisc configuration/statistics, the `.app`, and staged Fantasy notices;
- the operator and UTC timestamp plus any Docker, Unity, license, or platform limitation.

Evidence applies only to that commit. A later change to the Unity project, either client package, Shared Gameplay/Realtime, manifests/lockfile, Fantasy pin, validation script, or Unity version requires a new run. The current macOS ARM64 Mono bundle includes its local Regional real-client correction gate; it does not replace Windows, Universal/x86_64, Android/iOS IL2CPP, protocol generation, architecture, replay/load, production rollout, or real Linux environment-canary gates.
