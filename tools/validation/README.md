# Current-source validation

October 20 documentation sync is based on PR #44 merged at `aa06c05`. The earlier 132-test local review used the pre-#44 worktree at `2d92bcb`; it does not validate the training map or combat repairs. See the [review scope and commands](../../Docs/Architecture/current-status.md).

Current project progress is maintained in [one status page](../../Docs/Architecture/current-status.md). For the October 16 P1 source, select `-Profile Development -ExpectedDotnetPassed 607 -ExpectedEditModePassed 241` on Windows. The local Windows solution run discovered 607 .NET cases: 602 passed and five PostgreSQL cases were skipped without AINATIVE_TEST_POSTGRES. The full gate expects all 607 to pass with the database configured; this local run is not a full release qualification claim. The P1 targeted prediction suite passed 56 cases; the historical P0 macOS full run passed 541 and skipped five Windows-only tests. Strict release gates still reject skips. The two executable entries obtain Unity inventories and Fantasy package pins from the same profile contract. Defaults remain historical for compatibility; select the profile explicitly.

| Profile | Unity EditMode | Source/dependency scope |
| --- | ---: | --- |
| Baseline | 95 | Clean `c9098be`, Fantasy `f8bed0d`; 333 .NET tests |
| Candidate | 147 | Earlier socket/lifecycle source generation, Fantasy `df4ad5f`; explicit .NET count |
| TerminalDelivery | 164 | October 6 terminal-receive tests, Fantasy `df4ad5f`; explicit .NET count (historically 530) |
| Current | 166 | Legacy-health/additive-field regressions, Fantasy `df4ad5f`; 536 .NET tests (including four snapshot-fanout regressions) |
| Development | 241 | October 16 training map, combat/reload, password and P1 regressions; Fantasy `df4ad5f`; expected .NET inventory 607 |

Wrong totals, missing fixtures, failed/skipped cases and source changes fail the gate. Historical 128-test observations below remain evidence records, not a selectable current inventory.

## Current commands

For a clean current commit, start an isolated PostgreSQL 17.11, set `AINATIVE_TEST_POSTGRES` in the invoking process without saving/logging credentials, then run:

```powershell
./tools/validation/run-current-source.ps1 `
  -SourceRoot . -ExpectedCommit (git rev-parse HEAD) `
  -SdkPath (Get-Command dotnet).Source `
  -UnityEditorPath 'C:/Program Files/Unity/Hub/Editor/6000.3.23f1/Editor/Unity.exe' `
  -EvidenceDirectory ./artifacts/validation/new-current-run `
  -Profile Development -ExpectedDotnetPassed 607 -ExpectedEditModePassed 241 `
  -Phases Dotnet,Architecture,WindowsLegacy
```

`WindowsLegacy` includes the selected profile's EditMode tests, three real-host legacy KCP PlayMode tests, Windows Player build and reconnect smoke; a separate `EditMode` phase is optional, not necessary to duplicate this run. SDK 10.0.202 is required. The caller owns the database; the runner owns its test Host/Player. Use an isolated checkout when another Editor is open. Character and topology PlayMode suites are separate from the legacy KCP gate.

## Explicit worktree development validation

Default `-SourceMode CleanCommit` still rejects tracked changes and nonignored untracked files. To validate an uncommitted change, copy the intended source into an isolated Git checkout, retain the exact vendor gitlink, and freeze its full tracked/nonignored source inventory before execution:

```powershell
. ./tools/validation/source-identity.ps1
$validationSource = [IO.Path]::GetFullPath('./artifacts/validation/source')
$validationManifest = Get-AiNativeSourceManifest -Root $validationSource
./tools/validation/run-current-source.ps1 `
  -SourceRoot $validationSource -ExpectedCommit (git -C $validationSource rev-parse HEAD) `
  -SdkPath (Get-Command dotnet).Source `
  -UnityEditorPath 'C:/Program Files/Unity/Hub/Editor/6000.3.23f1/Editor/Unity.exe' `
  -EvidenceDirectory ./artifacts/validation/new-worktree-run `
  -Profile Development -ExpectedDotnetPassed 607 -ExpectedEditModePassed 241 `
  -SourceMode Worktree -ExpectedSourceManifestSha256 $validationManifest.sha256 `
  -Phases Dotnet,Architecture,WindowsLegacy
```

The manifest hashes exact file bytes and inventories tracked and nonignored untracked files (including new sources). Missing files, additions and modifications after freezing fail verification. Ignored build caches/evidence are outside the source identity; the vendor must remain clean and pinned. `validation.json` retains the manifest, mode and identities; Windows receives and rechecks the same hash. Worktree mode is development evidence, not release qualification, and cannot run topology publication/qualification phases. Historical Baseline is clean-commit-only. Do not hide unknown changes or weaken the default clean-source gate to run a worktree.

The Windows entry restores the original generated solution/project bytes and newline-only changes to the four named shader/render settings assets after its owned Editor exits. It removes only newly generated Fantasy project files with the Unity generation marker. Substantive settings changes and unknown files remain visible and fail source verification.

Run `tests/test-source-profiles.ps1`, `tests/test-worktree-entry.ps1` and `tests/test-unity-generated-state.ps1` alongside the existing report/boundary/cleanup regressions. It covers all five inventories, stale Windows counts and real byte/inventory hash drift.

## Local macOS P0 checks

The [October 15 P0 record](../../Docs/Architecture/p0-validation-2026-10-15.md) covers the local worktree. Use an explicitly compatible SDK, for example `./artifacts/dotnet/dotnet`, when system PATH resolves another installation. Do not relax `global.json`. Python topology tests use ephemeral ports for socket fixtures and can run while the local topology is active. PowerShell-dependent fixtures report skips when `pwsh` is absent; install/provide PowerShell 7 to run the complete suite. CI requires `pwsh` before running it.

Run the five real PostgreSQL cases into a **separate** report directory using the filters in `.github/workflows/dotnet.yml`, then pass that directory to `verify-postgres-tests.py`. Passing the full cross-platform solution directory to this strict gate will also reject unrelated Windows-only skips; do not weaken the gate to accommodate that invocation.

## Historical validation observations

`run-current-source.ps1` validates an explicit clean checkout and the pinned Fantasy submodule. Every invocation needs a new evidence directory. Reports identify the source, runner, SDK, selected phases, results and retained artifacts; a failure stops the run. Import-only line-ending/stat changes are accepted only when Git's content comparison remains clean.

`-Profile Baseline` binds the merged baseline `c9098be7e2a44efc42182a87aca2551648993705` to 333 .NET tests across 13 assemblies and the named 95-test Unity EditMode inventory. `-Profile Candidate` requires a different explicit clean commit, explicit `-ExpectedDotnetPassed` and `-ExpectedEditModePassed 147`. Its named inventory contains the reviewed Android/lifecycle, remote-player and prediction-send additions. A future Unity source that changes that inventory requires a reviewed profile change; overriding the total alone cannot bypass discovery. Skipped tests, incomplete inventory, missing fixtures and stale reports fail validation.

An initial baseline run passed, but a later full rerun reproduced an existing Worker termination fixture's scheduling-dependent failure. Both observations are retained. The following candidate observations have separate identities and do not qualify the baseline or future commits:

| Observation | Source identity and report |
| --- | --- |
| Baseline 333 .NET / 95 Unity | c9098be; [initial .NET summary](../../artifacts/release-validation-20261003/baseline/dotnet-summary.json), [Unity XML](../../artifacts/release-validation-20261003/baseline/editmode.xml); [later failed entry](../../artifacts/release-validation-20261003/entry-main-final/validation.json) |
| Candidate 359 .NET | Worktree manifest `7ADEAFC9A986A67D72BEBEEC667E9B4B872758C07301B317553F20892F7A93FC`; [summary](../../artifacts/release-validation-20261003/candidate-dotnet-connect-final/summary.json) |
| Candidate 360 .NET, MTU boundary revision | Worktree manifest `88EDC9FECC65B454CEFCF80711A9877139180B8414E2ECD5E2E24DE1B3E1B99A`; [summary](../../artifacts/release-validation-20261003/candidate-dotnet-mtu-boundary-final/summary.json) |
| Candidate 364 .NET, capacity revision | Worktree manifest `34C21E37C80F293BCDAF49A53EFD0B765473F6C3CE827B13FE6D77CEB62C3BAC`; [summary](../../artifacts/release-validation-20261003/candidate-dotnet-capacity-final/summary.json). This observed count is not the default for the next candidate. |
| Candidate 371 .NET, reviewed capacity revision | Worktree manifest `0A927B1CC3810FEC10B6C84669277EEA5C02A79C8FE05267190D106F593D8322`; [summary](../../artifacts/release-validation-20261003/candidate-dotnet-reviewed-capacity-final/summary.json): 13 TRX, zero failed/skipped. The next clean-commit run still requires its explicit expected count. |
| Candidate 391 .NET, rejection diagnosis and roster lifecycle revision | Worktree manifest `EA4806D88970328B6A62F69128319D6E77FEF8E0579C6740F595CE64FDF27F94`; [summary](../../artifacts/release-validation-20261003/candidate-dotnet-rejection-roster-final/summary.json): 13 TRX, zero failed/skipped; build and architecture passed. The unavailable online package vulnerability query was explicitly skipped. This does not qualify capacity or the still-proposed isolated probe groups. |
| Candidate 401 .NET, RPC completion and isolated probe groups | Worktree manifest `60764F9446D83368C126388BE20AB48277BAF62AAF0889209C7F1308B9AFCA9E`; [summary](../../artifacts/release-validation-20261003/candidate-dotnet-probe-groups-final-owned/summary.json): 13 TRX, zero failed/skipped; build and architecture passed. Online package vulnerability queries were unavailable and explicitly skipped. [Actual group lifecycle diagnostics](../../artifacts/release-validation-20261003/probe-group-harness/lifecycle-01.json) passed separately; capacity/soak remain unqualified. |
| Earlier candidate 128 Unity | [XML](../../artifacts/release-validation-20261003/android/full-final-green.xml), SHA256 `C7956EBE8A2FFAD74266E112CE73EA155E7C758E5F0E775D95D5181CBAFBA5C1`, 14 named fixtures. This separate worktree observation does not borrow any .NET manifest above or establish exact-commit qualification. |
| Earlier Android APK candidate | Worktree manifest `AEBBDA5031F05E46BC830E1AC22390C764FA9EF69C22DF7E15230AF8314A17CE`; [build report](../../artifacts/release-validation-20261003/android/apk-final/BUILD-VALIDATION.json), APK SHA256 `2a41fe70eab034058b22d18e41023f8f0499fb8f26f108bdab3220163cb9ceee`. Device acceptance remains false; this source identity differs from the 371-test candidate. |

See the [dated ledger](../../Docs/Architecture/release-validation-2026-10-03.md) for the retained observations. Ignored local artifact links require the original evidence directory; they are not published evidence.

```powershell
./tools/validation/run-current-source.ps1 `
  -SourceRoot ./artifacts/release-validation-20261003/source `
  -ExpectedCommit c9098be7e2a44efc42182a87aca2551648993705 `
  -SdkPath ./artifacts/dotnet/dotnet.exe `
  -UnityEditorPath 'D:/Program Files/Unity/Hub/Editor/6000.3.23f1/Editor/Unity.exe' `
  -EvidenceDirectory ./artifacts/validation/new-run `
  -Profile Baseline `
  -Phases Dotnet,Architecture,EditMode
```

Before `Dotnet`, start an isolated real PostgreSQL and set `AINATIVE_TEST_POSTGRES` without printing or saving its credentials. The caller owns that database's lifecycle.

## Mandatory PostgreSQL CI gate

The [.NET workflow](../../.github/workflows/dotnet.yml) and [runtime acceptance workflow](../../.github/workflows/runtime-acceptance.yml) each start a disposable PostgreSQL 17.11 service, set `AINATIVE_TEST_POSTGRES`, retain the full solution tests and run the two persistence test classes into separate TRX files. Their literal CI credentials belong only to the job-owned service; never reuse them for a shared or deployed database. Image licensing, upgrade and rollback guidance remains in [topology operations](../../Docs/Architecture/server-topology-operations.md).

`verify-postgres-tests.py` requires passed results for these five fully qualified cases:

- `AiNative.Server.Backend.Tests.PostgresTests.Migrated_schema_initializes_with_only_runtime_DML_privileges`
- `AiNative.Server.Backend.Tests.PostgresTests.Settlement_transaction_survives_store_restart_and_updates_once`
- `AiNative.Server.Rooms.Tests.PostgresAllocationStoreTests.MigratedSchemaInitializesWithOnlyRuntimeDmlPrivileges`
- `AiNative.Server.Rooms.Tests.PostgresAllocationStoreTests.TerminatedOwnershipConnectionCannotContinueAllocating`
- `AiNative.Server.Rooms.Tests.PostgresAllocationStoreTests.ExclusiveOwnerDurableReloadAndConflictingIdentityAreEnforcedByPostgres`

Every reported test must pass. Missing discovery/results, `NotExecuted` or any other non-passing outcome, duplicate identities, malformed reports and unsuccessful run summaries fail the gate even when aggregate counters appear green. The selected Player class also runs its unavailable-database case. Both workflows always attempt verification and artifact upload, including on failure. Use a fresh directory containing only this invocation's persistence reports:

```sh
python tools/validation/verify-postgres-tests.py artifacts/postgres-tests
python -m unittest discover -s tools/validation/tests -p test_postgres_gate.py -v
```

This gate checks named persistence execution; it does not replace current-source qualification or alter the historical observations above.

## Current-source phases and evidence

Candidate `Dotnet` builds once, then starts a separate testhost invocation filtered to `FantasyProbeInitializationTests.IndependentProbeRuntimeConfiguresOuterKcpBeforeItsFirstNetwork`. Exactly one matching passed test and one fresh-process TRX are required before the full 13-assembly matrix runs. Missing discovery is a failure. The independent report lives in `Dotnet/fresh-probe` and is indexed separately rather than added to the matrix total. This prevents another Gateway test's process-global MTU setup from masking initialization failure.

`validation.json` binds the chosen profile and expected totals to that invocation's clean commit. It indexes each TRX and its executed test identities, exact Unity fixture discovery, logs, publication and copied acceptance reports by absolute path and SHA256. `WindowsLegacy/windows/reports.json` similarly indexes its source/profile, NUnit reports, metadata, smoke result, Player binary, notices and build logs. Historical reports are not rewritten to claim newer runner checks.

Additional phases:

- `WindowsLegacy`: filtered legacy KCP PlayMode tests (3), a Windows Player build, notice/license verification and reconnect smoke. It owns and cleans up its Host and Player processes. Existing topology services must be stopped first because the KCP port is shared.
- `TopologyPublish`: publishes all services and acceptance/replay clients from the explicitly validated source into a new directory under that source's `artifacts`. The publication manifest binds immutable binary hashes and the canonical topology configuration hash. Set `-TopologyRunDirectory` consistently for subsequent phases.
- `TopologyAcceptance`: all eight short network scenarios against services started from that `TopologyPublish` output. The caller owns the service lifecycle. Each service's executable, start time, binary inventory and effective canonical configuration must match the publication; the copied acceptance report must be newly written during this phase.
- `TopologyPlayMode`: the named real two-client topology test (1), including original allocation and persisted settlement. It requires the seven local topology services, a real database and the 1200-tick test profile.

The short acceptance client uses 10 Hz input; it establishes functional behavior, not 60 Hz capacity. Windows ten-minute matches, public TLS acceptance, capacity/soak and Android device checks remain separate qualification gates in [the release plan](../../Docs/Architecture/release-validation-2026-10-03.md). Verify every completed replay with the independently published replay verifier and its recorded per-node identities.

Run the entry and cleanup failure-path checks with:

```powershell
./tools/validation/tests/test-nunit-report.ps1
./tools/validation/tests/test-validation-contracts.ps1
./tools/validation/tests/test-entry-boundaries.ps1
./tools/validation/tests/test-current-source.ps1 `
  -SourceRoot ./artifacts/release-validation-20261003/source `
  -ExpectedCommit c9098be7e2a44efc42182a87aca2551648993705 `
  -SdkPath ./artifacts/dotnet/dotnet.exe `
  -EvidenceDirectory ./artifacts/validation/new-failure-fixture
./tools/validation/tests/test-windows-process-cleanup.ps1
```

The contract/boundary scripts use controlled report, Git, SDK and process stubs; they start no Unity, .NET build, database or service. They test source/gitlink/dirty-state rejection, publication source/dependency/configuration/binary mismatches, missing/stale/failed reports, unique independent-probe discovery, independent-before-matrix ordering and persisted failed phase status. The cleanup script starts only a short-lived PowerShell child to exercise the actual owned-process timeout function.

`build-android.ps1` operates on an isolated Unity project under workspace `artifacts`. It prepares Android definitions before starting a fresh Unity process with the Android target, builds a non-Development ARM64 IL2CPP APK and retains source, toolchain, package, binary and notice identities. The outer wrapper restores ProjectSettings bytes. Build success explicitly leaves device acceptance false; login, battle, reconnect and background/foreground behavior must still be observed on hardware. See the release ledger for preference-restoration limitations.
