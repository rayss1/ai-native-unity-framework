# Manual Unity Shared-Vector Validation

Status: Required temporary validation path under ADR-0014
Editor: Unity `6000.3.9f1` revision `7a9955a4f2fa`

## Scope and pass criteria

Run the exact same source from `shared/gameplay`, `shared/realtime`, and the Unity-ready client prediction package through Unity EditMode. A valid run must be made from a clean checkout of the exact commit being reviewed and must report exactly 22 passed tests, zero failed, and zero skipped:

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

The run also fails if Unity reports compilation/package-resolution errors, selects another Editor revision, cannot resolve the three local UPM packages, or does not produce an NUnit XML result.

## Preferred macOS command

From the repository root, with no tracked changes:

```bash
tools/run-unity-manual-validation.sh
```

If Unity is installed elsewhere:

```bash
UNITY_EDITOR_PATH="/absolute/path/to/Unity" tools/run-unity-manual-validation.sh
```

The script writes ignored evidence under `artifacts/unity-manual/<full-commit>/` and verifies the NUnit totals. It does not read or require GitHub Secrets.

## Unity Editor UI alternative

1. Open `client/UnityProject` with exactly Unity `6000.3.9f1`.
2. Confirm both local packages resolve without Console errors.
3. Open **Window > General > Test Runner**, select **EditMode**, then **Run All**.
4. Confirm the 22 named tests pass with no skipped test or compiler error.
5. Export/save the test result when available and capture the Test Runner result plus Editor version.

## Evidence handoff

For each reviewed commit, provide:

- the full Git commit SHA and `git submodule status --recursive` output;
- `metadata.txt`, `summary.txt`, `editmode.xml`, and `editmode.log` from the script; or equivalent Editor screenshots/exported results;
- the operator name and validation UTC timestamp;
- any platform/license limitation encountered.

Evidence applies only to that commit. Any subsequent change to `client/UnityProject`, `packages/com.ainative.client.prediction`, `shared/gameplay`, `shared/realtime`, their package manifests/lockfile, or the Unity version requires a new run. Manual evidence does not replace the .NET, protocol-generation, architecture, replay, load, legal, or release gates.
