using System;
using System.IO;
using AiNative.Client.Prediction;
using UnityEngine;

namespace AiNative.Client.Application
{
    [DisallowMultipleComponent]
    public sealed class BattleClientCompositionRoot : MonoBehaviour
    {
        private const int MoveScaleMilli = 1000;
        private const float SmokeTimeoutSeconds = 30f;
        private const float RegionalWarmupSeconds = 10f;
        private const float RegionalMeasurementSeconds = 60f;
        private const long RegionalMinimumReconciliationSamples = 1000;
        private BattleClientSession _session;
        private BattleClientLaunchOptions _launch;
        private ulong _roomTick;
        private float _smokeElapsed;
        private bool _smokeReconnectRequested;
        private uint _preReconnectAcknowledgement;
        private float _regionalWarmupElapsed;
        private float _regionalMeasurementElapsed;
        private bool _regionalMeasurementStarted;
        private bool _finished;

        public BattleClientSession Session => _session;

        private void Awake()
        {
            global::UnityEngine.Application.runInBackground = true;
            QualitySettings.vSyncCount = 0;
            global::UnityEngine.Application.targetFrameRate = 60;
            _launch = BattleClientLaunchOptions.Parse(Environment.GetCommandLineArgs());
            _session = new BattleClientSession(
                _launch.Host,
                _launch.Port,
                global::UnityEngine.Application.version);
        }

        private void Start() => _session.Start();

        private void Update()
        {
            _session.Pump(Time.unscaledDeltaTime);
            if (_finished) return;

            if (_launch.RegionalCorrection)
            {
                UpdateRegionalCorrection();
                return;
            }

            if (!_launch.Smoke) return;

            _smokeElapsed += Time.unscaledDeltaTime;
            if (_session.State == BattleClientState.Faulted)
            {
                FinishSmoke(false, _session.FaultReason);
                return;
            }

            if (!_smokeReconnectRequested &&
                _session.IsPredictionInitialized &&
                _session.LastAcknowledgedSequence >= 30)
            {
                _smokeReconnectRequested = true;
                _preReconnectAcknowledgement = _session.LastAcknowledgedSequence;
                _session.RequestReconnect();
            }

            if (_smokeReconnectRequested &&
                _session.State == BattleClientState.Active &&
                _session.ConnectionEpoch > _session.InitialConnectionEpoch &&
                _session.LastAcknowledgedSequence > _preReconnectAcknowledgement)
            {
                FinishSmoke(true, string.Empty);
                return;
            }

            if (_smokeElapsed >= SmokeTimeoutSeconds)
            {
                FinishSmoke(false, "Smoke validation timed out.");
            }
        }

        private void FixedUpdate()
        {
            _roomTick++;
            int moveX;
            int moveZ;
            if (_launch.Smoke || _launch.RegionalCorrection)
            {
                // A repeating, deterministic pattern keeps smoke evidence reproducible.
                ulong phase = (_roomTick / 30) & 3;
                moveX = phase == 0 ? MoveScaleMilli : phase == 2 ? -MoveScaleMilli : 0;
                moveZ = phase == 1 ? MoveScaleMilli : phase == 3 ? -MoveScaleMilli : 0;
            }
            else
            {
                moveX = (Input.GetKey(KeyCode.D) ? MoveScaleMilli : 0) -
                        (Input.GetKey(KeyCode.A) ? MoveScaleMilli : 0);
                moveZ = (Input.GetKey(KeyCode.W) ? MoveScaleMilli : 0) -
                        (Input.GetKey(KeyCode.S) ? MoveScaleMilli : 0);
            }

            _session.PredictAndQueueInput(_roomTick, moveX, moveZ);
        }

        private async void OnDestroy()
        {
            if (_session is not null)
            {
                await _session.DisposeAsync();
            }
        }

        private void FinishSmoke(bool success, string error)
        {
            _finished = true;
            SmokeResult result = new SmokeResult
            {
                success = success,
                error = error ?? string.Empty,
                sessionId = _session.SessionId.ToString(),
                initialEpoch = _session.InitialConnectionEpoch,
                reconnectedEpoch = _session.ConnectionEpoch,
                lastAcknowledgedSequence = _session.LastAcknowledgedSequence,
                preReconnectAcknowledgedSequence = _preReconnectAcknowledgement,
                lastReceivedTick = _session.LastReceivedTick.ToString(),
                droppedInputFrames = _session.DroppedInputFrames,
            };

            try
            {
                string directory = Path.GetDirectoryName(_launch.ResultPath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.WriteAllText(_launch.ResultPath, JsonUtility.ToJson(result, true));
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                success = false;
            }

            Debug.Log(success ? "WS-26 smoke passed." : "WS-26 smoke failed: " + error);
#if !UNITY_EDITOR
            global::UnityEngine.Application.Quit(success ? 0 : 1);
#endif
        }

        private void UpdateRegionalCorrection()
        {
            if (_session.State == BattleClientState.Faulted)
            {
                FinishRegionalCorrection(false, _session.FaultReason);
                return;
            }

            if (_session.State != BattleClientState.Active || !_session.IsPredictionInitialized)
            {
                return;
            }

            if (!_regionalMeasurementStarted)
            {
                _regionalWarmupElapsed += Time.unscaledDeltaTime;
                if (_regionalWarmupElapsed < RegionalWarmupSeconds) return;
                if (!_session.ResetPredictionDiagnostics())
                {
                    FinishRegionalCorrection(false, "Prediction diagnostics could not be reset after warm-up.");
                    return;
                }

                _regionalMeasurementStarted = true;
                return;
            }

            _regionalMeasurementElapsed += Time.unscaledDeltaTime;
            if (_regionalMeasurementElapsed >= RegionalMeasurementSeconds)
            {
                FinishRegionalCorrection(true, string.Empty);
            }
        }

        private void FinishRegionalCorrection(bool completed, string error)
        {
            PredictionDiagnostics diagnostics = _session.PredictionDiagnostics;
            double measuredMinutes = _regionalMeasurementElapsed / 60d;
            double correctionsOver250PerPlayerMinute = measuredMinutes > 0
                ? diagnostics.CorrectionsOver250Millimetres / measuredMinutes
                : 0d;
            bool gatesPassed = completed &&
                               _regionalMeasurementStarted &&
                               diagnostics.ReconciliationSamples >= RegionalMinimumReconciliationSamples &&
                               diagnostics.CorrectionP95Millimetres <= 250 &&
                               diagnostics.CorrectionP99Millimetres <= 750 &&
                               correctionsOver250PerPlayerMinute <= 2d &&
                               diagnostics.HistoryMisses == 0 &&
                               diagnostics.DroppedInputs == 0 &&
                               _session.DroppedInputFrames == 0 &&
                               _session.State == BattleClientState.Active;
            _finished = true;
            RegionalCorrectionResult result = new RegionalCorrectionResult
            {
                success = gatesPassed,
                error = gatesPassed
                    ? string.Empty
                    : string.IsNullOrEmpty(error)
                        ? "Regional correction gates failed."
                        : error,
                evidenceClass = "macos-arm64-mono-regional-real-client",
                profile = "Regional",
                configuredRttMilliseconds = 100,
                configuredOneWayDelayMilliseconds = 50,
                configuredJitterMilliseconds = 10,
                configuredJitterCorrelationPercent = 25,
                configuredLossPercent = 1,
                configuredDuplicatePercent = 0.5,
                configuredReorderPercent = 1,
                configuredReorderCorrelationPercent = 50,
                warmupSeconds = _regionalWarmupElapsed,
                measuredSeconds = _regionalMeasurementElapsed,
                sessionId = _session.SessionId.ToString(),
                connectionEpoch = _session.ConnectionEpoch,
                lastAcknowledgedSequence = _session.LastAcknowledgedSequence,
                lastReceivedTick = _session.LastReceivedTick.ToString(),
                acceptedSnapshots = diagnostics.AcceptedSnapshots,
                reconciliationSamples = diagnostics.ReconciliationSamples,
                corrections = diagnostics.Corrections,
                correctionP95Millimetres = diagnostics.CorrectionP95Millimetres,
                correctionP99Millimetres = diagnostics.CorrectionP99Millimetres,
                maximumCorrectionMillimetres = diagnostics.MaximumCorrectionMillimetres,
                correctionsOver250Millimetres = diagnostics.CorrectionsOver250Millimetres,
                correctionsOver250PerPlayerMinute = correctionsOver250PerPlayerMinute,
                historyMisses = diagnostics.HistoryMisses,
                staleSnapshots = diagnostics.StaleSnapshots,
                droppedPredictionInputs = diagnostics.DroppedInputs,
                droppedInputFrames = _session.DroppedInputFrames,
            };

            WriteResult(result, ref gatesPassed);
            Debug.Log(gatesPassed
                ? "WS-27 Regional correction validation passed."
                : "WS-27 Regional correction validation failed: " + result.error);
#if !UNITY_EDITOR
            global::UnityEngine.Application.Quit(gatesPassed ? 0 : 1);
#endif
        }

        private void WriteResult(object result, ref bool success)
        {
            try
            {
                string directory = Path.GetDirectoryName(_launch.ResultPath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.WriteAllText(_launch.ResultPath, JsonUtility.ToJson(result, true));
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                success = false;
            }
        }

        [Serializable]
        private sealed class SmokeResult
        {
            public bool success;
            public string error;
            public string sessionId;
            public uint initialEpoch;
            public uint reconnectedEpoch;
            public uint lastAcknowledgedSequence;
            public uint preReconnectAcknowledgedSequence;
            public string lastReceivedTick;
            public long droppedInputFrames;
        }

        [Serializable]
        private sealed class RegionalCorrectionResult
        {
            public bool success;
            public string error;
            public string evidenceClass;
            public string profile;
            public int configuredRttMilliseconds;
            public int configuredOneWayDelayMilliseconds;
            public int configuredJitterMilliseconds;
            public int configuredJitterCorrelationPercent;
            public double configuredLossPercent;
            public double configuredDuplicatePercent;
            public double configuredReorderPercent;
            public int configuredReorderCorrelationPercent;
            public float warmupSeconds;
            public float measuredSeconds;
            public string sessionId;
            public uint connectionEpoch;
            public uint lastAcknowledgedSequence;
            public string lastReceivedTick;
            public long acceptedSnapshots;
            public long reconciliationSamples;
            public long corrections;
            public int correctionP95Millimetres;
            public int correctionP99Millimetres;
            public int maximumCorrectionMillimetres;
            public long correctionsOver250Millimetres;
            public double correctionsOver250PerPlayerMinute;
            public long historyMisses;
            public long staleSnapshots;
            public long droppedPredictionInputs;
            public long droppedInputFrames;
        }
    }

    internal readonly struct BattleClientLaunchOptions
    {
        internal BattleClientLaunchOptions(
            bool smoke,
            bool regionalCorrection,
            string host,
            int port,
            string resultPath)
        {
            Smoke = smoke;
            RegionalCorrection = regionalCorrection;
            Host = host;
            Port = port;
            ResultPath = resultPath;
        }

        internal bool Smoke { get; }

        internal bool RegionalCorrection { get; }

        internal string Host { get; }

        internal int Port { get; }

        internal string ResultPath { get; }

        internal static BattleClientLaunchOptions Parse(string[] arguments)
        {
            bool smoke = false;
            bool regionalCorrection = false;
            string host = "127.0.0.1";
            int port = 22000;
            string result = Path.GetFullPath(Path.Combine(
                global::UnityEngine.Application.persistentDataPath,
                "ainative-ws26-smoke.json"));

            for (int index = 0; index < arguments.Length; index++)
            {
                string argument = arguments[index];
                if (argument == "--ainative-smoke")
                {
                    smoke = true;
                }
                else if (argument == "--ainative-regional-correction")
                {
                    regionalCorrection = true;
                }
                else if (argument == "--ainative-host" && index + 1 < arguments.Length)
                {
                    host = arguments[++index];
                }
                else if (argument == "--ainative-port" && index + 1 < arguments.Length &&
                         int.TryParse(arguments[++index], out int parsedPort))
                {
                    port = parsedPort;
                }
                else if (argument == "--ainative-result" && index + 1 < arguments.Length)
                {
                    result = Path.GetFullPath(arguments[++index]);
                }
            }

            if (smoke && regionalCorrection)
            {
                throw new ArgumentException(
                    "Smoke and Regional correction modes are mutually exclusive.");
            }

            return new BattleClientLaunchOptions(
                smoke,
                regionalCorrection,
                host,
                port,
                result);
        }
    }
}
