# Local Unity CLI validation

Project: `client/UnityProject`; Unity **6000.3.23f1**, revision **09d2ecc7fb28**, URP **17.3.0**. This is the local development path alongside the exact-commit [qualification procedure](unity-manual-validation.md), not credentialed CI or production qualification.

## Configuration

Project version selection comes from `ProjectSettings/ProjectVersion.txt`, independently of machine defaults. Check `unity --version` and select the installed editor explicitly; workstation-specific paths and CLI versions in the dated evidence below are historical. On 2026-10-08 this workstation has CLI 1.0.0-beta.11 and Unity 6000.3.23f1 under `C:\Program Files\Unity\Hub\Editor\6000.3.23f1\Editor\Unity.exe`. No CLI upgrade is implied by running the project tests.

[`ProjectSettings/UnityCliConfig.json`](../../client/UnityProject/ProjectSettings/UnityCliConfig.json) supplies EditMode by default, both NUnit/JUnit reports, and 600-second test/build timeouts. Explicit CLI flags override the project defaults. `Packages/manifest.json` pins `com.unity.pipeline` to `0.8.0-exp.1`.

Earlier runs encountered a missing Windows `ALLUSERSPROFILE` variable, causing Unity Package Manager to fail before loading tests. The entries now supply the process-only ProgramData fallback to their owned Unity processes; they do not require changing persistent user environment settings.

## Commands

From the repository root, run EditMode without a Battle Host:

```powershell
if (-not $env:ALLUSERSPROFILE) { $env:ALLUSERSPROFILE = $env:ProgramData }
New-Item -ItemType Directory -Force artifacts/unity-cli | Out-Null
unity test client/UnityProject --output artifacts/unity-cli/editmode.xml
```

For real-KCP PlayMode tests, start the existing v1 Battle Host in another terminal:

```powershell
$env:AINATIVE_SERVER_TOPOLOGY = 'false'
$env:AINATIVE_FANTASY_ENABLED = 'true'
$env:ASPNETCORE_URLS = 'http://127.0.0.1:22080'
dotnet run --project server/src/Hosts/AiNative.BattleHost -c Release --no-launch-profile -- --pid 1 -m Release
```

With that Host ready on UDP 22000:

```powershell
unity test client/UnityProject --mode PlayMode --output artifacts/unity-cli/playmode.xml
```

The tests use batch mode, so their real-host path runs without the interactive-only enable flag. Stop the Host after testing. Use project-scoped paths when multiple projects are open; close the target project's Editor before a batch test tries to open it again.

Build the existing Windows Mono smoke Player through the project's build method:

```powershell
$output = [IO.Path]::GetFullPath('artifacts/unity-cli/player/AiNative.BattleClient.exe')
unity run client/UnityProject --timeout 600 --log-file artifacts/unity-cli/player-build.log -- -nographics -executeMethod AiNative.Client.Editor.BattleClientBuild.BuildWindowsSmoke --ainative-build-output $output
```

The method validates URP, selects Windows x64 Mono and copies the approved Fantasy notices. The Windows runner manages the complete Host/test/build/reconnect-smoke lifecycle with SDK 10.0.202. Current source selects `-Profile Current -ExpectedEditModePassed 166`; historical `Baseline`/`Candidate`/`TerminalDelivery` inventories remain 95/147/164. Default `CleanCommit` requires a clean checkout; explicit `Worktree` mode requires a frozen source hash and reports development evidence. See [the unified entry](../../tools/validation/README.md) and [current progress](current-status.md). Worktree results are not exact-commit release qualification.

## Executed development evidence — 2026-10-02

Evidence directory: `artifacts/unity-cli-validation/20261002-024150` (ignored local outputs). Unity CLI launched the real editor with the pinned version, .NET SDK **10.0.401**, a managed v1 Battle Host and an independent Player. The development-only runner copy under that directory bypassed the clean-worktree/SDK qualification preconditions; the checked-in runner retains them. Already-completed EditMode results were checked rather than rerun.

| Verification | Result |
| --- | --- |
| Unity EditMode | **56 passed, zero failed/skipped**: Application 10, Fantasy 8, Prediction 16, Gameplay 20, Realtime 2 |
| Unity real-KCP PlayMode | **2 passed, zero failed/skipped** |
| Shared Gameplay .NET counterpart | **20 passed, zero failed/skipped**; all twenty test identities match the Unity Gameplay suite |
| Windows x64 Mono Player | Build exited zero; approved third-party notices included |
| Independent Player reconnect smoke | Success; epoch **4 → 5**, acknowledged sequence **30 → 31**, zero dropped input frames |
| Architecture validator | Passed |

NUnit/JUnit, editor/Player/Host logs, `smoke.json`, `summary.txt`, the .NET TRX, `shared-vector-parity.json` and `development-identity.json` are retained. A 270-file source manifest records the tested worktree. Four build-time line-ending-only serializations were restored only when their byte hashes exactly matched the pre-build identity; no source or asset-content difference remained. Owned test Host, Player and batch Editors exited.

This validates the current local v1 Unity client and Shared source. It does not cover a Unity frontend for the new backend topology, credentialed CI, macOS, IL2CPP/mobile, sustained capacity, real WAN impairment or a graphics-enabled visual review. Commit the reviewed source and rerun the relevant exact-commit/platform gates before claiming release qualification.
