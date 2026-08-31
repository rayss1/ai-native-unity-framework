# Unity WS-26 Validation

Status: Required temporary validation path under ADR-0014
Editor: Unity `6000.3.9f1` revision `7a9955a4f2fa`

## Scope and pass criteria

Run the exact same source from `shared/gameplay`, `shared/realtime`, the client prediction and Fantasy transport packages, and the Battle Client application through Unity. A valid run must use a clean checkout of the exact commit under review and Unity `6000.3.9f1` revision `7a9955a4f2fa`.

EditMode must report exactly 36 passed, zero failed, and zero skipped. The original 22 contract/prediction tests remain mandatory:

1. `GameplayClockContractTests.ClockExposesCommittedTickAndFixedDelta`
2. `DeterminismContractTests.Pcg32MatchesPublishedReferenceVector`
3. `DeterminismContractTests.CapturedRandomStateReplaysTheSameSequence`
4. `DeterminismContractTests.XxHash64MatchesCanonicalEmptyVector`
5. `AcceptanceSimulationVectorTests.SixtyFourBotMovementAndFireVectorIsReplayable`
6. `TransportContractTests.ReceivedPacketReportsTruncationWithoutHidingRequiredSize`
7. `TransportContractTests.AcceptedSendRecordsCopiedByteCount`
8. `ClientPredictionTests.IntegerMovementClampsInputAndAdvancesOneFixedTick`
9. `ClientPredictionTests.ReconciliationReplaysOnlyUnacknowledgedInputs`
10. `ClientPredictionTests.MatchingSnapshotRetainsPredictedStateWithoutCorrection`
11. `ClientPredictionTests.FullHistoryDropsOldestAndIgnoresOlderSnapshot`
12. `ClientPredictionTests.AuthoritativeSequenceAheadResetsPredictionEpoch`
13. `ClientPredictionTests.MissingAcknowledgementFailsClosedToAuthoritativeState`
14. `ClientPredictionTests.PredictionAndMatchingReconciliationAllocateNothingAfterWarmup`
15. `ClientPredictionAdapterTests.InputSendUsesProtocolV1BytesAndInputChannel`
16. `ClientPredictionAdapterTests.SnapshotAcknowledgementRewindsAndReplaysNewerInput`
17. `ClientPredictionAdapterTests.MatchingSnapshotDoesNotRecordCorrection`
18. `ClientPredictionAdapterTests.MissingPlayerAndProtocolMismatchFailClosed`
19. `ClientPredictionAdapterTests.TruncatedAndWrongChannelPacketsDoNotChangePrediction`
20. `ClientPredictionAdapterTests.ReconnectResponseAdvancesEpochAndReconciles`
21. `ClientPredictionAdapterTests.TransportBackpressureRemainsObservableAfterPrediction`
22. `ClientPredictionAdapterTests.SteadyStatePredictionAndInputEncodingAllocateNothing`

The additional 14 EditMode tests comprise eight Fantasy transport tests (envelope/channel mapping, sequence handling, epoch monotonicity, truncation, backpressure/bounds, disposal/late callback handling, and the warmed-up allocation path) plus six application protocol/state tests (login, join, routing, timeout, reconnect, and prediction continuity).

PlayMode must report exactly 2 passed, zero failed, and zero skipped against the local real Fantasy KCP Battle Host: one covers login/join/first Snapshot/deterministic Input acknowledgement, and one forces reconnect and proves a newer epoch plus continued prediction. The Windows x64 Mono Player smoke must then exit zero and write parseable JSON with `success: true` after completing the same local vertical path.

The run also fails if Unity reports compilation/package-resolution errors, selects another Editor revision, cannot resolve the pinned local/Git UPM packages, omits the license/Third-Party Notices from the Player, or does not produce the required NUnit XML and smoke JSON.

## Preferred macOS command

From the repository root, with no tracked changes:

```bash
tools/run-unity-manual-validation.sh
```

If Unity is installed elsewhere:

```bash
UNITY_EDITOR_PATH="/absolute/path/to/Unity" tools/run-unity-manual-validation.sh
```

The script writes ignored evidence under `artifacts/unity-manual/<full-commit>/` and verifies the exact 36-test EditMode total. It is the cross-platform package/vector check; it does not claim the two real-KCP PlayMode or Windows Player gates.

## Windows full command

From a clean repository checkout in PowerShell:

```powershell
tools/run-unity-windows-validation.ps1
```

Set `UNITY_EDITOR_PATH` or pass `-UnityEditorPath` when Unity is installed elsewhere. The script verifies the exact SDK, Editor project revision, Fantasy gitlink, and clean source commit; publishes and starts the local Battle Host on KCP `127.0.0.1:22000`; waits for readiness on `127.0.0.1:22080`; executes exact 36/2 EditMode/PlayMode totals; builds a Windows x64 Mono Player; and runs smoke arguments `--ainative-smoke --ainative-host 127.0.0.1 --ainative-port 22000 --ainative-result <absolute-json>`. It stops only the exact Player and Host processes it created.

Evidence is written under `artifacts/unity-windows/<full-commit>/` by default. It includes metadata and identities, package-lock hashes, both NUnit XML/log pairs, Host logs, Player build/run logs, the built Player, smoke JSON, and a summary. Passing the script is required before recording WS-26 as validated; adding the script or tests alone is not evidence.

## Unity Editor UI alternative

1. Open `client/UnityProject` with exactly Unity `6000.3.9f1`.
2. Confirm both local packages resolve without Console errors.
3. Open **Window > General > Test Runner**, select **EditMode**, then **Run All**.
4. Confirm exactly 36 EditMode tests pass with no skipped test or compiler error.
5. With the local Battle Host running, select **PlayMode**, run all, and confirm exactly two tests pass.
6. Export/save both results and capture the Test Runner result plus Editor version. A UI-only run does not replace the Windows Player smoke.

## Evidence handoff

For each reviewed commit, provide:

- the full Git commit SHA and `git submodule status --recursive` output;
- `metadata.txt`, `summary.txt`, both NUnit XML/log pairs, Host/Player logs, Player build log, and `smoke.json` from the Windows script; for the macOS partial gate, provide `editmode.xml` and `editmode.log` and label PlayMode/Player unverified;
- the operator name and validation UTC timestamp;
- any platform/license limitation encountered.

Evidence applies only to that commit. Any subsequent change to `client/UnityProject`, either client package, `shared/gameplay`, `shared/realtime`, their package manifests/lockfile, the Fantasy pin, or the Unity version requires a new run. Windows Mono evidence does not replace Regional real-client correction measurement, Android/iOS IL2CPP, .NET, protocol-generation, architecture, replay, load, legal, or production rollout gates.
