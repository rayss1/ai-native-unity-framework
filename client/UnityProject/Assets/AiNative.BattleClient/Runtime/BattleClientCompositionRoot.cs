using System;
using System.IO;
using UnityEngine;

namespace AiNative.Client.Application
{
    [DisallowMultipleComponent]
    public sealed class BattleClientCompositionRoot : MonoBehaviour
    {
        private const int MoveScaleMilli = 1000;
        private const float SmokeTimeoutSeconds = 30f;
        private BattleClientSession _session;
        private BattleClientLaunchOptions _launch;
        private ulong _roomTick;
        private float _smokeElapsed;
        private bool _smokeReconnectRequested;
        private uint _preReconnectAcknowledgement;
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
            if (!_launch.Smoke || _finished) return;

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
            if (_launch.Smoke)
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
    }

    internal readonly struct BattleClientLaunchOptions
    {
        internal BattleClientLaunchOptions(
            bool smoke,
            string host,
            int port,
            string resultPath)
        {
            Smoke = smoke;
            Host = host;
            Port = port;
            ResultPath = resultPath;
        }

        internal bool Smoke { get; }

        internal string Host { get; }

        internal int Port { get; }

        internal string ResultPath { get; }

        internal static BattleClientLaunchOptions Parse(string[] arguments)
        {
            bool smoke = false;
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

            return new BattleClientLaunchOptions(smoke, host, port, result);
        }
    }
}
