using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AiNative.Client.Assets;
using AiNative.Client.Async;
using AiNative.Client.Character;
using AiNative.Client.Prediction;
using AiNative.Client.Protocol;
using AiNative.Client.Protocol.Protobuf;
using AiNative.Gameplay;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.InputSystem;

namespace AiNative.Client.Application
{
    [DisallowMultipleComponent]
    public sealed class BattleClientCompositionRoot : MonoBehaviour
    {
#if UNITY_EDITOR
        [SerializeField, Tooltip("Open the local Gate login flow in the Editor only.")]
        private bool editorLocalTopology;
#endif
        private const int MoveScaleMilli = 1000;
        private const float SmokeTimeoutSeconds = 30f;
        private const float RegionalWarmupSeconds = 10f;
        private const float RegionalMeasurementSeconds = 60f;
        private const long RegionalMinimumReconciliationSamples = 1000;
        private BattleClientSession _session;
        private TopologyClientFlow _topology;
        private BattleClientLaunchOptions _launch;
        private ulong _roomTick;
        private float _smokeElapsed;
        private bool _smokeReconnectRequested;
        private uint _preReconnectAcknowledgement;
        private float _regionalWarmupElapsed;
        private float _regionalMeasurementElapsed;
        private bool _regionalMeasurementStarted;
        private bool _finished;
        private GameObject _playerVisual;
        private NetworkCharacterPresenter _character;
        private CharacterOrbitCamera _orbitCamera;
        private uint _visualEpoch;
        private readonly ArenaPresentationEvent[] _combatEvents = new ArenaPresentationEvent[256];
        private string _combatFeedback = "";
        private float _feedbackUntil;
        private GameObject _floorVisual;
        private Camera _playerCamera;
        private Light _arenaLight;
        private GUIStyle _hudStyle;
        private ArenaRemoteVisualPool _remoteVisuals;
        private readonly ArenaRemotePose[] _remotePoses = new ArenaRemotePose[
            BattlePacketBuffer.MaxArenaPlayers
        ];
        public BattleClientSession Session => _topology != null ? _topology.Battle : _session;

        private FrameworkClientRoot _framework;
        private bool _launchConfigured;
        private bool _forceTopology;
        private bool _switching;

        // 场景激活只采集组件，配置校验和装配由显式应用启动执行。
        private void Awake()
        {
            _topology = GetComponent<TopologyClientFlow>();
        }

        internal void ConfigureLaunch() => ConfigureLaunch(Environment.GetCommandLineArgs());

        internal void ConfigureLaunch(string[] arguments)
        {
            if (arguments == null)
            {
                throw new ArgumentNullException(nameof(arguments));
            }
            _topology = GetComponent<TopologyClientFlow>();
            if (_launchConfigured)
            {
                if (_forceTopology && _topology == null)
                {
                    _topology = gameObject.AddComponent<TopologyClientFlow>();
                }
                return;
            }
            if (_topology != null)
            {
                _launchConfigured = true;
                return;
            }
            global::UnityEngine.Application.runInBackground = true;
            QualitySettings.vSyncCount = 0;
            global::UnityEngine.Application.targetFrameRate = 60;
#if UNITY_ANDROID && !UNITY_EDITOR
            AndroidClientLaunchConfiguration configuration;
            bool rejectedIntent = false;
            try
            {
                configuration = AndroidClientLaunchConfiguration.ReadCurrentIntent();
            }
            catch (ArgumentException)
            {
                // 无效的公开 Intent 不得启用账号自动化；先完成降级配置再创建流程组件。
                configuration = AndroidClientLaunchConfiguration.Parse(
                    new System.Collections.Generic.Dictionary<string, string>(),
                    global::UnityEngine.Application.persistentDataPath
                );
                rejectedIntent = true;
            }
            _topology = gameObject.AddComponent<TopologyClientFlow>();
            _topology.Initialize(
                configuration.GateOptions,
                !rejectedIntent && configuration.Automated,
                configuration.TimeoutSeconds,
                configuration.ResultPath,
                configuration.RunId
            );
            if (rejectedIntent)
            {
                Debug.LogWarning(
                    "Android launch configuration was rejected; interactive login is available."
                );
            }
            _launchConfigured = true;
            return;
#else
            if (
                Array.IndexOf(arguments, "--ainative-topology") >= 0
                || Array.IndexOf(arguments, "--ainative-topology-smoke") >= 0
            )
            {
                _topology = gameObject.AddComponent<TopologyClientFlow>();
                _launchConfigured = true;
                return;
            }

#if UNITY_EDITOR
            if (editorLocalTopology)
            {
                _topology = gameObject.AddComponent<TopologyClientFlow>();
                _topology.Initialize(
                    new AiNative.Client.Fantasy.GateConnectionOptions("127.0.0.1", 23001, false, "")
                );
                _launchConfigured = true;
                return;
            }

#endif
            // 校验成功后才发布配置及完成标记，失败后的重试必须重新验证原参数。
            BattleClientLaunchOptions launch = BattleClientLaunchOptions.Parse(arguments);
            _launch = launch;
            _launchConfigured = true;
#endif
        }

        private UniTaskCompletionSource _sessionStop;

        internal void StartSession()
        {
            if (_topology != null || _session != null)
            {
                return;
            }
            _sessionStop = null;
            _roomTick = 0;
            _smokeElapsed = 0;
            _smokeReconnectRequested = false;
            _preReconnectAcknowledgement = 0;
            _regionalWarmupElapsed = 0;
            _regionalMeasurementElapsed = 0;
            _regionalMeasurementStarted = false;
            _finished = false;
            _session = new BattleClientSession(
                new ProtobufBattleProtocolCodec(),
                _launch.Host,
                _launch.Port,
                global::UnityEngine.Application.version
            );
            _session.Start();
        }

        internal ValueTask StopSessionAsync()
        {
            if (_sessionStop == null)
            {
                _sessionStop = new UniTaskCompletionSource();
                StopSessionObservedAsync(_sessionStop).Forget();
            }
            return _sessionStop.Task.AsValueTask();
        }

        private async UniTask StopSessionObservedAsync(UniTaskCompletionSource completion)
        {
            BattleClientSession session = _session;
            _session = null;
            try
            {
                if (session != null)
                {
                    await session.DisposeAsync();
                }
                completion.TrySetResult();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }

        private CancellationTokenSource _visualLifetime;
        private IAssetService _visualAssets;
        private AssetHandle<GameObject> _characterAsset;
        private AssetHandle<Material> _playerMaterial;
        private UniTaskCompletionSource _visualLoad;
        private UniTaskCompletionSource _visualRelease;

        internal async UniTask StartPresentationAsync(
            FrameworkClientRoot root,
            IAssetService assets,
            CancellationToken cancellationToken
        )
        {
            _framework = root ?? throw new ArgumentNullException(nameof(root));
            if (_visualRelease != null)
            {
                await _visualRelease.Task;
            }
            if (_visualLoad != null && _visualRelease == null)
            {
                await _visualLoad.Task;
                return;
            }
            _stopTask = null;
            _visualRelease = null;
            _visualAssets = assets ?? throw new ArgumentNullException(nameof(assets));
            _visualLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _visualLoad = new UniTaskCompletionSource();
            InitializeVisualsObservedAsync(_visualLoad, _visualLifetime.Token).Forget();
            await _visualLoad.Task;
        }

        private async UniTask InitializeVisualsObservedAsync(
            UniTaskCompletionSource completion,
            CancellationToken token
        )
        {
            try
            {
                await InitializeVisualsAsync(token);
                completion.TrySetResult();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }

        private async UniTask InitializeVisualsAsync(CancellationToken cancellationToken)
        {
            _characterAsset = await _visualAssets.LoadAsync<GameObject>(
                new AssetId("character.network"),
                cancellationToken
            );
            _playerMaterial = await _visualAssets.LoadAsync<Material>(
                new AssetId("material.player"),
                cancellationToken
            );
            cancellationToken.ThrowIfCancellationRequested();
            GameObject prefab = _characterAsset.Value;
            if (prefab == null)
            {
                throw new InvalidOperationException(
                    "Missing NetworkCharacter prefab; run AiNative/Battle Client/Build Network Character."
                );
            }

            _playerVisual = Instantiate(prefab, transform);
            _playerVisual.name = "LocalNetworkCharacter";
            _playerVisual.transform.localPosition = Vector3.zero;
            _character = _playerVisual.GetComponent<NetworkCharacterPresenter>();
            _remoteVisuals = new ArenaRemoteVisualPool(_playerMaterial.Value, prefab);
            GameObject cameraObject = new GameObject("LocalPlayerCamera");
            _playerCamera = cameraObject.AddComponent<Camera>();
            _playerCamera.nearClipPlane = .1f;
            _playerCamera.farClipPlane = 250f;
            _playerCamera.tag = "MainCamera";
            _orbitCamera = cameraObject.AddComponent<CharacterOrbitCamera>();
            _orbitCamera.ConfigurePresentation(transform);
            if (_topology != null)
            {
                _topology.CharacterCamera = _orbitCamera;
            }

            _floorVisual = new GameObject("ArenaTrainingGround");
            _floorVisual.AddComponent<ArenaTrainingGroundPresentation>();
            GameObject lightObject = new GameObject("ArenaGreyboxLight");
            _arenaLight = lightObject.AddComponent<Light>();
            _arenaLight.type = LightType.Directional;
            _arenaLight.intensity = 1.2f;
            _arenaLight.color = new Color(0.8f, 0.9f, 1f);
            lightObject.transform.rotation = Quaternion.Euler(45f, -30f, 0f);
        }

        private void OnGUI()
        {
            DrawCharacterHud();
            if (_topology != null)
            {
                return; // 交互流程统一拥有 uGUI 与战斗 HUD，此入口不再重复绘制。
            }

            if (
                _topology == null
                && !_launch.Smoke
                && !_launch.RegionalCorrection
                && GUI.Button(new Rect(740, 16, 180, 32), "登录大厅与匹配")
            )
            {
                SwitchToTopology();
            }

            BattleClientSession session = Session;
            if (session is null)
            {
                return;
            }

            _hudStyle ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 16,
                normal = { textColor = Color.white },
            };
            GUI.Label(
                new Rect(16, 16, 700, 28),
                $"BattleClient  {session.State}  epoch={session.ConnectionEpoch} tick={session.LastReceivedTick} ack={session.LastAcknowledgedSequence} dropped={session.DroppedInputFrames}",
                _hudStyle
            );
            if (session.TryGetArenaState(out ArenaPlayerState arenaState))
            {
                GUI.Label(
                    new Rect(16, 44, 700, 28),
                    $"Arena  {session.ArenaPhase}  HP={arenaState.Health}  AR={arenaState.Armor}  "
                        + $"Weapon={arenaState.Weapon}  Kills={arenaState.Kills}  Leader={session.ArenaLeaderEntityId}",
                    _hudStyle
                );
                GUI.Label(
                    new Rect(16, 72, 900, 28),
                    "WASD move  |  hold left mouse to fire  |  movement is predicted and server-corrected",
                    _hudStyle
                );
            }
        }

        private void SwitchToTopology()
        {
            if (_switching || _framework == null)
            {
                return;
            }
            _switching = true;
            SwitchToTopologyAsync().Forget(Debug.LogException);
        }

        private async UniTask SwitchToTopologyAsync()
        {
            try
            {
                await _framework.StopAsync();
                _forceTopology = true;
                await _framework.StartAsync();
            }
            finally
            {
                _switching = false;
            }
        }

        /// <summary>主线程幂等释放表现；等待迟到加载及实例销毁后释放借用资源服务的句柄。</summary>
        public ValueTask ReleaseVisualsAsync()
        {
            if (_visualRelease == null)
            {
                _visualRelease = new UniTaskCompletionSource();
                ReleaseVisualsObservedAsync(_visualRelease).Forget();
            }
            return _visualRelease.Task.AsValueTask();
        }

        private async UniTask ReleaseVisualsObservedAsync(UniTaskCompletionSource completion)
        {
            try
            {
                await ReleaseVisualsCoreAsync();
                completion.TrySetResult();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }

        private async UniTask ReleaseVisualsCoreAsync()
        {
            var errors = new List<Exception>();
            try
            {
                _visualLifetime?.Cancel();
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }
            if (_visualLoad != null)
            {
                try
                {
                    await _visualLoad.Task;
                }
                catch (Exception)
                { /* 加载错误由入口记录。 */
                }
            }
            if (_topology != null)
            {
                _topology.CharacterCamera = null;
            }
            if (_playerCamera != null)
            {
                Destroy(_playerCamera.gameObject);
            }
            try
            {
                _remoteVisuals?.Dispose();
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }
            _remoteVisuals = null;
            if (_playerVisual != null)
            {
                Destroy(_playerVisual);
            }
            if (_floorVisual != null)
            {
                Destroy(_floorVisual);
            }
            if (_arenaLight != null)
            {
                Destroy(_arenaLight.gameObject);
            }
            _playerVisual = null;
            _floorVisual = null;
            _playerCamera = null;
            _arenaLight = null;
            // Destroy 在帧尾生效；句柄必须在场景实例销毁之后释放。
            if (_visualAssets != null && (_characterAsset != null || _playerMaterial != null))
            {
                await UnityAsync.NextFrameAsync(CancellationToken.None);
                if (_characterAsset != null)
                {
                    try
                    {
                        await _visualAssets.ReleaseAsync(_characterAsset);
                    }
                    catch (Exception exception)
                    {
                        errors.Add(exception);
                    }
                }
                if (_playerMaterial != null)
                {
                    try
                    {
                        await _visualAssets.ReleaseAsync(_playerMaterial);
                    }
                    catch (Exception exception)
                    {
                        errors.Add(exception);
                    }
                }
                _characterAsset = null;
                _playerMaterial = null;
            }
            _visualLifetime?.Dispose();
            _visualLifetime = null;
            if (errors.Count > 0)
            {
                throw new AggregateException(errors);
            }
        }

        private Vector2Int _manualMove;
        private bool _manualFire;

        private void SampleManualInput()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null)
            {
                _manualMove = Vector2Int.zero;
            }
            else
            {
                _manualMove = new Vector2Int(
                    (keyboard.dKey.isPressed ? 1000 : 0) - (keyboard.aKey.isPressed ? 1000 : 0),
                    (keyboard.wKey.isPressed ? 1000 : 0) - (keyboard.sKey.isPressed ? 1000 : 0)
                );
            }
            _manualFire = Mouse.current != null && Mouse.current.leftButton.isPressed;
        }

        private void OnApplicationFocus(bool focused)
        {
            if (!focused)
            {
                _manualMove = Vector2Int.zero;
                _manualFire = false;
            }
        }

        private void Update()
        {
            if (_topology != null || _session == null)
            {
                return;
            }

            SampleManualInput();
            _session.Pump(Time.unscaledDeltaTime);
            if (_finished)
            {
                return;
            }

            if (_launch.RegionalCorrection)
            {
                UpdateRegionalCorrection();
                return;
            }

            if (!_launch.Smoke)
            {
                return;
            }

            _smokeElapsed += Time.unscaledDeltaTime;
            if (_session.State == BattleClientState.Faulted)
            {
                FinishSmoke(false, _session.FaultReason);
                return;
            }

            if (
                !_smokeReconnectRequested
                && _session.IsPredictionInitialized
                && _session.LastAcknowledgedSequence >= 30
            )
            {
                _smokeReconnectRequested = true;
                _preReconnectAcknowledgement = _session.LastAcknowledgedSequence;
                _session.RequestReconnect();
            }

            if (
                _smokeReconnectRequested
                && _session.State == BattleClientState.Active
                && _session.ConnectionEpoch > _session.InitialConnectionEpoch
                && _session.LastAcknowledgedSequence > _preReconnectAcknowledgement
            )
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
            if (_topology != null || _session == null)
            {
                return;
            }

            if (!_session.IsPredictionInitialized || _session.State != BattleClientState.Active)
            {
                return;
            }

            _roomTick = Math.Max(_roomTick + 1, _session.LastReceivedTick + 1);
            int moveX;
            int moveZ;
            if (_launch.Smoke || _launch.RegionalCorrection)
            {
                // 使用可重复的确定性输入模式，使冒烟验收证据可复现。
                ulong phase = (_roomTick / 30) & 3;
                moveX = 0;
                moveZ = 0;
                if (phase == 0)
                {
                    moveX = MoveScaleMilli;
                }
                else if (phase == 2)
                {
                    moveX = -MoveScaleMilli;
                }
                if (phase == 1)
                {
                    moveZ = MoveScaleMilli;
                }
                else if (phase == 3)
                {
                    moveZ = -MoveScaleMilli;
                }
            }
            else
            {
                moveX = global::UnityEngine.Application.isFocused ? _manualMove.x : 0;
                moveZ = global::UnityEngine.Application.isFocused ? _manualMove.y : 0;
            }

            if (!_session.TryGetArenaState(out _))
            {
                _session.PredictAndQueueInput(_roomTick, moveX, moveZ);
                return;
            }

            ArenaButtons buttons =
                global::UnityEngine.Application.isFocused && _manualFire
                    ? ArenaButtons.Fire
                    : ArenaButtons.None;
            _session.PredictAndQueueArenaInput(
                _roomTick,
                moveX,
                moveZ,
                0,
                0,
                buttons,
                ArenaWeaponId.Machinegun
            );
        }

        private void LateUpdate()
        {
            if (_playerVisual == null)
            {
                return;
            }
            BattleClientSession session = Session;
            int remoteCount =
                session?.AdvanceRemotePresentation(Time.unscaledDeltaTime, _remotePoses) ?? 0;
            _remoteVisuals?.Apply(_remotePoses, remoteCount);
            if (session is null)
            {
                if (_playerVisual != null)
                {
                    _playerVisual.SetActive(false);
                }

                _visualEpoch = 0;
                return;
            }

            if (_visualEpoch != session.ConnectionEpoch)
            {
                _visualEpoch = session.ConnectionEpoch;
                _orbitCamera?.Snap();
                _combatFeedback = "";
            }

            int eventCount = session.DrainCombatEvents(_combatEvents);
            for (int i = 0; i < eventCount; i++)
            {
                PresentCombatEvent(_combatEvents[i], session.EntityId);
            }

            if (
                session.TryAdvancePresentation(
                    Time.unscaledDeltaTime,
                    out PresentationPosition position
                )
            )
            {
                Vector3 current = transform.position;
                transform.position = new Vector3(
                    (float)(position.XMillimetres / 1000d),
                    current.y,
                    (float)(position.ZMillimetres / 1000d)
                );
            }

            if (
                _playerVisual is not null
                && session.TryGetArenaState(out ArenaPlayerState arenaState)
            )
            {
                Vector3 current = transform.position;
                transform.position = new Vector3(
                    current.x,
                    arenaState.PositionYMillimetres / 1000f,
                    current.z
                );
                _playerVisual.transform.localRotation = Quaternion.Euler(
                    0f,
                    arenaState.YawMillidegrees / 1000f,
                    0f
                );
                _character.Apply(arenaState, _topology == null || _topology.IsCharacterAiming);
                _orbitCamera.SetPresentationState(_character.MotionState);
            }
        }

        private void PresentCombatEvent(ArenaPresentationEvent item, uint localId)
        {
            if (item.Kind == ArenaPresentationEventKind.Fire)
            {
                NetworkCharacterPresenter shooter =
                    item.SourceEntityId == localId
                        ? _character
                        : _remoteVisuals
                            ?.GetVisual(item.SourceEntityId)
                            ?.GetComponent<NetworkCharacterPresenter>();
                shooter?.NotifyShot();
            }

            if (
                item.Kind == ArenaPresentationEventKind.Hit
                && (item.SourceEntityId == localId || item.TargetEntityId == localId)
            )
            {
                _combatFeedback =
                    item.TargetEntityId == localId
                        ? $"受到伤害 {item.Damage}"
                        : $"命中 {item.Damage}";
            }
            else if (item.Kind == ArenaPresentationEventKind.Kill)
            {
                _combatFeedback =
                    item.TargetEntityId == localId
                        ? "已阵亡"
                        : $"玩家 {item.SourceEntityId} 击败 {item.TargetEntityId}";
            }
            else if (
                item.Kind == ArenaPresentationEventKind.Respawn
                && item.SourceEntityId == localId
            )
            {
                _combatFeedback = "已重生";
                _orbitCamera?.Snap();
            }
            else
            {
                return;
            }

            _feedbackUntil = Time.unscaledTime + 2;
        }

        internal Vector3 GetAimPoint()
        {
            Vector3 direction =
                Quaternion.Euler(_orbitCamera.Pitch, _orbitCamera.Yaw, 0) * Vector3.forward;
            var ray = new Ray(_playerCamera.transform.position, direction);
            float range = 100;
            Vector3 origin = ray.origin * 1000f;
            if (
                ArenaTrainingMap.Raycast(
                    origin.x,
                    origin.y,
                    origin.z,
                    direction.x,
                    direction.y,
                    direction.z,
                    range * 1000d,
                    out double distance
                )
            )
            {
                range = (float)(distance / 1000d);
            }

            Vector3 point =
                _remoteVisuals != null ? _remoteVisuals.AimPoint(ray, range) : ray.GetPoint(range);
            return ray.GetPoint(Mathf.Min(range, Vector3.Dot(point - ray.origin, ray.direction)));
        }

        private void DrawCharacterHud()
        {
            var session = Session;
            if (
                session == null
                || !session.TryGetArenaState(out var state)
                || session.ArenaPhase == ArenaMatchPhase.Finished
            )
            {
                return;
            }

            if (_topology != null)
            {
                GUI.Label(
                    new Rect(16, Screen.height - 50, 1000, 24),
                    "WASD 移动  |  按住右键转向/瞄准  |  左键开火  |  Space 跳跃  |  1/2/3 武器"
                );
            }

            if (Time.unscaledTime < _feedbackUntil)
            {
                GUI.Label(
                    new Rect(Screen.width / 2 - 100, Screen.height / 2 + 35, 260, 30),
                    _combatFeedback
                );
            }

            GUI.Label(new Rect(Screen.width / 2 - 6, Screen.height / 2 - 10, 24, 24), "+");
            GUI.Label(
                new Rect(16, Screen.height - 75, 800, 24),
                "R 换弹 · 空仓自动换弹（2秒）· 备用弹不限"
            );
        }

        private UniTaskCompletionSource _stopTask;

        /// <summary>主线程停止表现与旧入口会话；重复调用共享结果，正常退出应在销毁对象前等待。</summary>
        public ValueTask StopAsync()
        {
            if (_stopTask == null)
            {
                var completion = new UniTaskCompletionSource();
                _stopTask = completion;
                StopObservedAsync(completion).Forget();
            }
            return _stopTask.Task.AsValueTask();
        }

        private async UniTask StopObservedAsync(UniTaskCompletionSource completion)
        {
            var errors = new List<Exception>();
            try
            {
                await ReleaseVisualsAsync();
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }
            try
            {
                await StopSessionAsync();
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }
            if (errors.Count == 0)
            {
                completion.TrySetResult();
            }
            else
            {
                completion.TrySetException(new AggregateException(errors));
            }
        }

        private void OnDestroy() => ObserveStopAsync().Forget();

        private async UniTask ObserveStopAsync()
        {
            try
            {
                await StopAsync();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
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
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(_launch.ResultPath, JsonUtility.ToJson(result, true));
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                success = false;
            }

            Debug.Log(success ? "WS-26 smoke passed." : "WS-26 smoke failed: " + error);
#if !UNITY_EDITOR
            _framework.RequestQuit(success ? 0 : 1);
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
                if (_regionalWarmupElapsed < RegionalWarmupSeconds)
                {
                    return;
                }

                if (
                    !_session.ResetPredictionDiagnostics()
                    || !_session.ResetPresentationDiagnostics()
                )
                {
                    FinishRegionalCorrection(
                        false,
                        "Prediction or presentation diagnostics could not be reset after warm-up."
                    );
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
            PresentationCorrectionDiagnostics presentation = _session.PresentationDiagnostics;
            double measuredMinutes = _regionalMeasurementElapsed / 60d;
            double correctionsOver250PerPlayerMinute =
                measuredMinutes > 0
                    ? diagnostics.CorrectionsOver250Millimetres / measuredMinutes
                    : 0d;
            bool gatesPassed =
                completed
                && _regionalMeasurementStarted
                && diagnostics.ReconciliationSamples >= RegionalMinimumReconciliationSamples
                && diagnostics.CorrectionP95Millimetres <= 250
                && diagnostics.CorrectionP99Millimetres <= 750
                && correctionsOver250PerPlayerMinute <= 2d
                && diagnostics.HistoryMisses == 0
                && diagnostics.DroppedInputs == 0
                && presentation.SmoothedCorrections > 0
                && presentation.SnappedCorrections == 0
                && presentation.ResidualMillimetres <= 250
                && _session.DroppedInputFrames == 0
                && _session.State == BattleClientState.Active;
            _finished = true;
            string failureReason = string.Empty;
            if (!gatesPassed)
            {
                failureReason = string.IsNullOrEmpty(error)
                    ? "Regional correction gates failed."
                    : error;
            }
            RegionalCorrectionResult result = new RegionalCorrectionResult
            {
                success = gatesPassed,
                error = failureReason,
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
                presentationSmoothedCorrections = presentation.SmoothedCorrections,
                presentationSnappedCorrections = presentation.SnappedCorrections,
                presentationResidualMillimetres = presentation.ResidualMillimetres,
            };
            WriteResult(result, ref gatesPassed);
            Debug.Log(
                gatesPassed
                    ? "WS-27 Regional correction validation passed."
                    : "WS-27 Regional correction validation failed: " + result.error
            );
#if !UNITY_EDITOR
            _framework.RequestQuit(gatesPassed ? 0 : 1);
#endif
        }

        private void WriteResult(object result, ref bool success)
        {
            try
            {
                string directory = Path.GetDirectoryName(_launch.ResultPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

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
            public long presentationSmoothedCorrections;
            public long presentationSnappedCorrections;
            public int presentationResidualMillimetres;
        }
    }

    internal readonly struct BattleClientLaunchOptions
    {
        internal BattleClientLaunchOptions(
            bool smoke,
            bool regionalCorrection,
            string host,
            int port,
            string resultPath
        )
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
            string result = Path.GetFullPath(
                Path.Combine(
                    global::UnityEngine.Application.persistentDataPath,
                    "ainative-ws26-smoke.json"
                )
            );
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
                else if (
                    argument == "--ainative-port"
                    && index + 1 < arguments.Length
                    && int.TryParse(arguments[++index], out int parsedPort)
                )
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
                    "Smoke and Regional correction modes are mutually exclusive."
                );
            }

            return new BattleClientLaunchOptions(smoke, regionalCorrection, host, port, result);
        }
    }
}
