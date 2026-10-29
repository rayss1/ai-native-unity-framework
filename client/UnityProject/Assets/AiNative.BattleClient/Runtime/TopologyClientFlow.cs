using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using AiNative.Client.Assets;
using AiNative.Client.Async;
using AiNative.Client.Character;
using AiNative.Client.Fantasy;
using AiNative.Client.Protocol;
using AiNative.Client.UI;
using AiNative.Gameplay;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.InputSystem;

namespace AiNative.Client.Application
{
    /// <summary>产品流程组合：连接 Gate 账号、队伍、匹配以及权威 Battle 对局。</summary>
    public sealed class TopologyClientFlow : MonoBehaviour
    {
#if UNITY_EDITOR
        // 由 Editor 程序集提供：MPPM 虚拟玩家即使窗口未激活，
        // 也可能报告 Application.isFocused；Runtime 不得引用 UnityEditor。
        public static Func<bool> EditorHasGameFocus { get; set; }
#endif
        private TopologyAccountLobbyService _accountLobby;
        private TopologyMatchController _matchController;
        private FrameworkClientRoot _root;
        private UniTaskCompletionSource _activeOperation;
        private GateBackendSession Backend
        {
            get => _accountLobby?.Backend;
            set => _accountLobby.Backend = value;
        }
        private GateParty LobbyParty
        {
            get => _accountLobby?.Party;
            set => _accountLobby.Party = value;
        }
        private GateParty[] LobbyInvitations
        {
            get => _accountLobby?.Invitations;
            set => _accountLobby.Invitations = value;
        }
        private GateMatch CurrentMatch
        {
            get => _matchController?.Match;
            set => _matchController.Match = value;
        }
        private string QueueRequestId
        {
            get => _matchController?.RequestId;
            set => _matchController.RequestId = value;
        }
        private GateConnectionOptions _options;
        private CancellationTokenSource _lifetime = new CancellationTokenSource();
        private string _username = "";
        private string _password = "";
        private string _host = "127.0.0.1";
        private string _port = "23001";
        private string _pin = "";
        private string _resultPath = "";
        private bool _tls;
        private bool _busy;
        private bool _automated;
        private bool _started;
        private bool _disposed;
        private bool _reconnectRequested;
        private bool _observedReconnecting;
        private bool _paused;
        private bool _resumePending;
        private bool _skipElapsedFrame;
        private bool _textInputFocused;
        private bool _pointerOverUi;
        private bool _uiBusy;
        private Func<UniTask> _pendingAction;
        private TopologyClientView _view;
        private IAssetService _uiAssets;
        private UiService _uiService;
        private UiService _registeredUiService;
        private ClientOperation _uiLoadTask;
        private UniTaskCompletionSource _stopTask;
        private UniTaskCompletionSource<bool> _uiCompletion;
        private TimeSpan _returnToLobbyTimeout = TimeSpan.FromSeconds(20);
        private const int ReturnToLobbyPollMilliseconds = 250;
        internal const string WaitingForRoomReleaseStatus = "正在等待房间释放…";
        private float _queueStarted;
        private int _pauseGeneration;
        private bool _battleReceiveComplete;
        private bool _successExitPending;
        private bool _evidenceSuccess;
        private float _successExitElapsed;
        private const float TerminalReceiveExitTimeoutSeconds = 5f;
        private string _runId = "";
        private ulong _priorSession;
        private ulong _inputTick;
        private uint _priorEntity;
        private uint _priorAcknowledgement;
        private uint _observedAcknowledgement;
        private float _acknowledgementStall;
        private long _baselinePlayed;

        // 当前自动化流程累计时间，单位为秒；按渲染帧经过时间推进。
        private float _elapsed;
        private float _nextPoll;
        private float _deadlineSeconds = 120;
        public BattleClientSession Battle
        {
            get => _matchController?.Battle;
            private set => _matchController.Battle = value;
        }
        public CharacterOrbitCamera CharacterCamera { get; set; }
        public bool IsCharacterAiming => _manualAim && CanSampleCharacterInput && !_pointerOverUi;
        public GateProfile Profile
        {
            get => _accountLobby?.Profile;
            private set => _accountLobby.Profile = value;
        }
        public string PlayerId => Backend?.PlayerId ?? "";
        public string MatchId => CurrentMatch?.MatchId ?? "";
        public string RoomId => CurrentMatch?.RoomId ?? "";
        public string State { get; private set; } = "Disconnected";
        public string Error { get; private set; } = "";
        public string BackendStatus { get; private set; } = "";
        public bool Completed { get; private set; }
        public bool Reconnected { get; private set; }
        public bool Automated => _automated;
        public bool ReconnectRequested => _reconnectRequested;
        public uint PreReconnectAcknowledgement => _priorAcknowledgement;
        public long PreparedInputs { get; private set; }
        public float MaxAcknowledgementStallSeconds { get; private set; }
        public int BackendReconnectCount { get; private set; }
        public bool SettlementConfirmed { get; private set; }
        public bool AuthenticationRequired => Backend == null || !Backend.IsAuthenticated;
        public int PauseCount { get; private set; }
        public int ResumeCount { get; private set; }
        public int ResumeCompletedCount { get; private set; }
        public bool UiBusy => _uiBusy;
        public GateParty Party => LobbyParty;
        public IReadOnlyList<GateParty> Invitations => LobbyInvitations;
        public string ConnectionHost => _host;
        public string ConnectionPort => _port;
        public bool ConnectionTls => _tls;
        public string ConnectionPin => _pin;
        public string DisplayError => ErrorMessage(Error);
        public float QueueElapsedSeconds =>
            State == "Queued" ? Math.Max(0, Time.unscaledTime - _queueStarted) : 0;

        public void SetUiInputBlocked(bool keyboard, bool pointer)
        {
            _textInputFocused = keyboard;
            _pointerOverUi = pointer;
            if (keyboard || pointer)
            {
                _characterInput.Clear();
            }
        }

        public void Authenticate(
            bool register,
            string username,
            string password,
            string host,
            string port,
            bool tls,
            string pin
        )
        {
            Submit(async () =>
            {
                if (!AuthenticationRequired)
                {
                    return;
                }

                string normalized = (username ?? "").Trim().ToLowerInvariant();
                if (
                    normalized.Length < 3
                    || normalized.Length > 32
                    || string.IsNullOrEmpty(password)
                    || password.Length < 6
                    || password.Length > 32
                )
                {
                    throw new GateCallException("invalid_credentials");
                }

                foreach (char c in normalized)
                {
                    if (!(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9') && c != '_')
                    {
                        throw new GateCallException("invalid_credentials");
                    }
                }

                if (
                    string.IsNullOrWhiteSpace(host)
                    || !int.TryParse(port, out int number)
                    || number < 1
                    || number > 65535
                )
                {
                    throw new GateCallException("invalid-address");
                }

                _username = normalized;
                _password = password;
                _host = host.Trim();
                _port = port;
                _tls = tls;
                _pin = pin ?? "";
                await ConnectAccountAsync(register);
            });
        }

        public void CreateParty() =>
            Submit(async () =>
            {
                RequireLobby();
                await _accountLobby.CreatePartyAsync(_lifetime.Token);
            });

        public void InvitePlayer(string playerId) =>
            Submit(async () =>
            {
                RequireLobby();
                await _accountLobby.InviteAsync(playerId, _lifetime.Token);
            });

        public void AcceptInvitation(string partyId) =>
            Submit(async () =>
            {
                RequireLobby();
                await _accountLobby.AcceptInvitationAsync(partyId, _lifetime.Token);
            });

        public void SetReady(bool ready) =>
            Submit(async () =>
            {
                RequireLobby();
                await _accountLobby.SetReadyAsync(ready, _lifetime.Token);
            });

        public void StartMatch() =>
            Submit(async () =>
            {
                RequireLobby();
                if (LobbyParty != null)
                {
                    await QueueAsync();
                }
            });

        public void CancelMatch() =>
            Submit(async () =>
            {
                if (State != "Queued")
                {
                    return;
                }

                await Backend.CancelQueueAsync(QueueRequestId, _lifetime.Token);
                QueueRequestId = "";
                State = "Lobby";
                if (LobbyParty != null)
                {
                    LobbyParty = await Backend.GetPartyAsync(LobbyParty.PartyId, _lifetime.Token);
                }
            });

        public void LeaveParty() =>
            Submit(async () =>
            {
                RequireLobby();
                await _accountLobby.LeavePartyAsync(_lifetime.Token);
            });

        public void ReconnectBattle() =>
            Submit(() =>
            {
                if (Battle != null && !Completed)
                {
                    _resumePending = true;
                    _nextPoll = 0;
                }

                return UniTask.CompletedTask;
            });

        public void ReturnToLobby() => Submit(ReturnToLobbyAsync);

        private void RequireLobby()
        {
            if (State != "Lobby" || AuthenticationRequired || Battle != null)
            {
                throw new GateCallException("state-changed");
            }
        }

        private void Submit(Func<UniTask> action)
        {
            if (_disposed || _uiBusy)
            {
                return;
            }

            _uiBusy = true;
            _uiCompletion = new UniTaskCompletionSource<bool>();
            Error = "";
            if (_busy)
            {
                _pendingAction = action;
                return;
            }

            Run(action, foreground: true);
        }

        /// <summary>
        /// ViewModel 的可等待入口，沿用既有串行请求与排队逻辑。
        /// 取消只结束界面等待；实际网络操作由 Flow 生命周期拥有。
        /// </summary>
        internal ValueTask ExecuteUiActionAsync(
            Action action,
            CancellationToken cancellationToken
        ) => ExecuteUiActionCoreAsync(action, cancellationToken).AsValueTask();

        private async UniTask ExecuteUiActionCoreAsync(
            Action action,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed || _uiBusy)
            {
                return;
            }
            action();
            UniTaskCompletionSource<bool> completion = _uiCompletion;
            if (completion == null)
            {
                return;
            }
            var cancelled = new UniTaskCompletionSource<bool>();
            using (cancellationToken.Register(() => cancelled.TrySetResult(true)))
            {
                await UniTask.WhenAny(completion.Task, cancelled.Task);
                cancellationToken.ThrowIfCancellationRequested();
                await completion.Task;
            }
        }

        /// <summary>启动前显式注入 UI 服务；服务所有权仍属于应用根或测试调用方。</summary>
        public void ConfigureUi(IAssetService assets, UiService uiService)
        {
            if (_started && !_disposed)
            {
                throw new InvalidOperationException("Topology flow already started.");
            }
            _uiAssets = assets ?? throw new ArgumentNullException(nameof(assets));
            _uiService = uiService ?? throw new ArgumentNullException(nameof(uiService));
        }

        public void Initialize(
            GateConnectionOptions options,
            bool automated = false,
            float deadlineSeconds = 120,
            string resultPath = "",
            string runId = ""
        )
        {
            if (_started && !_disposed)
            {
                throw new InvalidOperationException("Topology flow already started.");
            }

            if (deadlineSeconds < 30 || deadlineSeconds > 3600)
            {
                throw new ArgumentOutOfRangeException(nameof(deadlineSeconds));
            }

            _options = options ?? throw new ArgumentNullException(nameof(options));
            _automated = automated;
            _deadlineSeconds = deadlineSeconds;
            _resultPath = resultPath;
            _host = options.Host;
            _port = options.Port.ToString();
            _tls = options.UseTls;
            _pin = options.CertificateSha256;
            _runId = runId;
        }

        /// <summary>装配期借用应用拥有的业务服务；不取得释放权，不触发启动。</summary>
        internal void ConfigureServices(
            FrameworkClientRoot root,
            TopologyAccountLobbyService account,
            TopologyMatchController match
        )
        {
            if (_started && !_disposed)
            {
                if (!ReferenceEquals(_accountLobby, account))
                {
                    throw new InvalidOperationException("flow-already-started");
                }
                return;
            }
            _root = root;
            _accountLobby = account;
            _matchController = match;
        }

        internal async UniTask StartFlowAsync(CancellationToken token)
        {
            if (_started && !_disposed)
            {
                return;
            }
            if (_accountLobby == null || _matchController == null || _uiService == null)
            {
                throw new InvalidOperationException("flow-not-configured");
            }
            _lifetime.Dispose();
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            _disposed = false;
            _started = true;
            _stopTask = null;
            _busy = false;
            _uiBusy = false;
            _pendingAction = null;
            _uiCompletion = null;
            _activeOperation = null;
            _elapsed = 0;
            _nextPoll = 0;
            _queueStarted = 0;
            _paused = false;
            _resumePending = false;
            _skipElapsedFrame = false;
            _reconnectRequested = false;
            _observedReconnecting = false;
            _battleReceiveComplete = false;
            _successExitPending = false;
            _evidenceSuccess = false;
            _successExitElapsed = 0;
            _pauseGeneration = 0;
            _priorSession = 0;
            _inputTick = 0;
            _priorEntity = 0;
            _priorAcknowledgement = 0;
            _observedAcknowledgement = 0;
            _acknowledgementStall = 0;
            _baselinePlayed = 0;
            _textInputFocused = false;
            _pointerOverUi = false;
            State = "Disconnected";
            Error = "";
            BackendStatus = "";
            Completed = false;
            Reconnected = false;
            SettlementConfirmed = false;
            PreparedInputs = 0;
            MaxAcknowledgementStallSeconds = 0;
            BackendReconnectCount = 0;
            PauseCount = 0;
            ResumeCount = 0;
            ResumeCompletedCount = 0;
            ClearManualInput();
            if (_options == null)
            {
                ConfigureFromArguments(Environment.GetCommandLineArgs());
            }
            if (!_automated)
            {
                _uiLoadTask = new ClientOperation(LoadUiAsync);
                await _uiLoadTask.Task;
                if (_view == null)
                {
                    throw new InvalidOperationException("ui-load-failed");
                }
            }
            else
            {
                Run(async () =>
                {
                    _username = "unity_" + Guid.NewGuid().ToString("N").Substring(0, 16);
                    _password = Guid.NewGuid().ToString("N");
                    await ConnectAccountAsync(register: true);
                    LobbyParty = await Backend.CreatePartyAsync(_lifetime.Token);
                    LobbyParty = await Backend.ReadyAsync(LobbyParty, true, _lifetime.Token);
                    await QueueAsync();
                });
            }
        }

        /// <summary>主线程重试尚未创建视图的失败初始化；不重置账号或对局，停止后不再重试。</summary>
        public ValueTask RetryUiAsync() => RetryUiCoreAsync().AsValueTask();

        private async UniTask RetryUiCoreAsync()
        {
            if (_disposed || _automated || _view != null || Error != "ui-load-failed")
            {
                return;
            }
            if (_uiLoadTask != null && !_uiLoadTask.IsCompleted)
            {
                await _uiLoadTask.Task;
            }
            if (_disposed || _view != null)
            {
                return;
            }
            // 服务由根显式注入；局部界面重试不重新创建应用。
            Error = "";
            _uiLoadTask = new ClientOperation(LoadUiAsync);
            await _uiLoadTask.Task;
        }

        private async UniTask LoadUiAsync()
        {
            CancellationToken cancellationToken = _lifetime.Token;
            try
            {
                if (_uiAssets == null || _uiService == null)
                {
                    throw new InvalidOperationException("flow-ui-not-configured");
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (!ReferenceEquals(_registeredUiService, _uiService))
                {
                    _uiService.Register("topology", UiLayer.Page, CreateUiAsync);
                    _registeredUiService = _uiService;
                }
                await _uiService.OpenAsync("topology", cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 所属流程已经关闭，不把正常取消显示成加载失败。
            }
            catch (Exception exception)
            {
                if (!_disposed)
                {
                    Error = "ui-load-failed";
                    Debug.LogException(exception);
                }
            }
        }

        private ValueTask<UiView> CreateUiAsync(CancellationToken cancellationToken) =>
            CreateUiCoreAsync(cancellationToken).AsValueTask();

        private async UniTask<UiView> CreateUiCoreAsync(CancellationToken cancellationToken)
        {
            IAssetService assets = _uiAssets;
            UiService ownedUi = _uiService;
            AssetHandle<GameObject> handle = await assets.LoadAsync<GameObject>(
                new AssetId("ui.topology"),
                cancellationToken
            );
            GameObject instance = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                instance = Instantiate(handle.Value);
                TopologyClientView view = instance.GetComponent<TopologyClientView>();
                if (view == null)
                {
                    throw new InvalidOperationException(
                        "ui.topology must contain TopologyClientView."
                    );
                }
                view.Bind(this, ownedUi.EventSystem);
                cancellationToken.ThrowIfCancellationRequested();
                _view = view;
                GameObject ownedInstance = instance;
                return new UiView(instance, () => ReleaseUiAsync(ownedInstance, handle, assets));
            }
            catch
            {
                await ReleaseUiAsync(instance, handle, assets);
                throw;
            }
        }

        private ValueTask ReleaseUiAsync(
            GameObject instance,
            AssetHandle<GameObject> handle,
            IAssetService assets
        ) => ReleaseUiCoreAsync(instance, handle, assets).AsValueTask();

        private async UniTask ReleaseUiCoreAsync(
            GameObject instance,
            AssetHandle<GameObject> handle,
            IAssetService assets
        )
        {
            try
            {
                if (instance != null)
                {
                    TopologyClientView view = instance.GetComponent<TopologyClientView>();
                    if (_view == view)
                    {
                        _view = null;
                    }
                    try
                    {
                        view?.Close();
                    }
                    finally
                    {
                        if (global::UnityEngine.Application.isPlaying)
                        {
                            Destroy(instance);
                            // Destroy 在帧尾执行；实际实例销毁后才允许卸载其源 Bundle。
                            await UnityAsync.NextFrameAsync(CancellationToken.None);
                        }
                        else
                        {
                            DestroyImmediate(instance);
                        }
                    }
                }
            }
            finally
            {
                await assets.ReleaseAsync(handle);
            }
        }

        private void ConfigureFromArguments(string[] args)
        {
            _automated = Array.IndexOf(args, "--ainative-topology-smoke") >= 0;
            _host = Option(args, "--ainative-gate-host", "127.0.0.1");
            _port = Option(args, "--ainative-gate-port", "23001");
            _pin = Option(args, "--ainative-gate-pin", "");
            _tls = Array.IndexOf(args, "--ainative-gate-plaintext") < 0;
            _resultPath = Option(args, "--ainative-result", "");
            if (
                float.TryParse(
                    Option(args, "--ainative-topology-timeout", "120"),
                    out float timeout
                )
                && timeout >= 30
                && timeout <= 3600
            )
            {
                _deadlineSeconds = timeout;
            }

            if (_automated)
            {
                _options = new GateConnectionOptions(_host, int.Parse(_port), _tls, _pin);
            }
        }

        private static string Option(string[] args, string key, string fallback)
        {
            int index = Array.IndexOf(args, key);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
        }

        private async UniTask ConnectAccountAsync(bool register)
        {
            if (register && Battle != null)
            {
                throw new GateCallException("account-mismatch-active-battle");
            }

            string previousPlayer = PlayerId,
                previousState = State;
            if (Battle == null)
            {
                State = "Authenticating";
            }

            _options = new GateConnectionOptions(_host, int.Parse(_port), _tls, _pin);
            await _accountLobby.AuthenticateAsync(
                _options,
                _username,
                _password,
                register,
                Battle != null ? previousPlayer : null,
                _lifetime.Token
            );
            GateProfile profile = Profile;
            if (Battle != null)
            {
                BackendReconnectCount++;
            }

            if (Battle == null)
            {
                if (previousPlayer != Backend.PlayerId)
                {
                    _baselinePlayed = profile.Played;
                    LobbyParty = null;
                    QueueRequestId = "";
                    State = "Lobby";
                }
                else
                {
                    State =
                        previousState == "Queued" && QueueRequestId.Length > 0 ? "Queued" : "Lobby";
                }
            }
            else
            {
                if (Completed)
                {
                    State = "Finished";
                }
                else
                {
                    State = previousState == "Settling" ? "Settling" : "Battle";
                }
            }

            Error = "";
            BackendStatus = "";
        }

        private async UniTask QueueAsync()
        {
            QueueRequestId = Guid.NewGuid().ToString("N");
            State = "Queued";
            _queueStarted = Time.unscaledTime;
            try
            {
                await Backend.QueueAsync(LobbyParty, QueueRequestId, _lifetime.Token);
            }
            catch (GateCallException exception)
                when (exception.Code == "party_not_ready"
                    || exception.Code == "leader_required"
                    || exception.Code == "version_conflict"
                    || exception.Code == "party_not_found"
                    || exception.Code == "invalid_request"
                    || exception.Code == "invalid_queue"
                    || exception.Code == "player_busy"
                    || exception.Code == "queue_capacity"
                )
            {
                QueueRequestId = "";
                State = "Lobby";
                throw;
            }
            // 超时或结果未知时保留请求 ID，用于后续查询或取消对账。
        }

        private async UniTask ReturnToLobbyAsync()
        {
            if (_automated || !Completed || !SettlementConfirmed || Battle == null)
            {
                throw new GateCallException("state-changed");
            }

            GateParty refreshedParty = await WaitForLobbyReleaseAsync();

            GateProfile profile = await Backend.ProfileAsync(_lifetime.Token);
            _lifetime.Token.ThrowIfCancellationRequested();
            await _matchController.StopAsync();
            _lifetime.Token.ThrowIfCancellationRequested();
            ClearManualInput();
            Battle = null;
            CurrentMatch = null;
            QueueRequestId = "";
            LobbyParty = refreshedParty;
            Profile = profile;
            _baselinePlayed = profile.Played;
            Completed = SettlementConfirmed = Reconnected = false;
            _resumePending = _reconnectRequested = _observedReconnecting = false;
            _battleReceiveComplete = _successExitPending = _evidenceSuccess = false;
            _successExitElapsed = _elapsed = _acknowledgementStall = 0;
            _inputTick = _priorSession = 0;
            _priorEntity = _priorAcknowledgement = _observedAcknowledgement = 0;
            PreparedInputs = 0;
            MaxAcknowledgementStallSeconds = 0;
            BackendReconnectCount = 0;
            _nextPoll = 0;
            State = "Lobby";
            Error = "";
            BackendStatus = "请重新准备后开始下一局。";
        }

        private async UniTask<GateParty> WaitForLobbyReleaseAsync()
        {
            if (LobbyParty == null)
            {
                return null;
            }
            string partyId = LobbyParty.PartyId;
            string requestId = QueueRequestId;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token))
            {
                timeout.CancelAfter(_returnToLobbyTimeout);
                CancellationToken token = timeout.Token;
                try
                {
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        ValidateReturnQueue(partyId, requestId);
                        GateParty refreshedParty;
                        try
                        {
                            refreshedParty = await Backend.GetPartyAsync(partyId, token);
                        }
                        catch (GateCallException exception)
                            when (exception.Code == "party_not_found"
                                || exception.Code == "forbidden"
                            )
                        {
                            token.ThrowIfCancellationRequested();
                            ValidateReturnQueue(partyId, requestId);
                            return null;
                        }
                        token.ThrowIfCancellationRequested();
                        ValidateReturnQueue(partyId, requestId);
                        if (refreshedParty.PartyId != partyId)
                        {
                            throw new GateCallException("state-changed");
                        }
                        if (refreshedParty.QueueRequestId.Length == 0)
                        {
                            return refreshedParty;
                        }
                        if (refreshedParty.QueueRequestId != requestId)
                        {
                            throw new GateCallException("state-changed");
                        }
                        BackendStatus = WaitingForRoomReleaseStatus;
                        try
                        {
                            GateMatch terminal = await Backend.MatchAsync(requestId, token);
                            if (terminal.RequestId != requestId)
                            {
                                throw new GateCallException("state-changed");
                            }
                            bool released =
                                terminal.State == "failed" || terminal.State == "cancelled";
                            if (!released && terminal.State != "ready")
                            {
                                throw new GateCallException("state-changed");
                            }
                            ValidateOriginalMatch(terminal, allowMissing: released);
                        }
                        catch (GateCallException exception)
                            when (exception.Code == "queue_expired"
                                || exception.Code == "queue_not_found"
                            )
                        {
                            // 已过期队列沿用原成员隔离规则；绝不取消仍活跃的分配。
                            await Backend.CancelQueueAsync(requestId, token);
                        }
                        catch (GateCallException exception)
                            when (exception.Code == "invalid_allocation")
                        {
                            // CF002：Coordinator 已释放而 Match 尚未传播终态时，Gate 无法续签票据。
                            // 不据此认定释放，仅重查同一队伍与请求，直到权威 Lobby 清空队列。
                        }
                        await UniTask.Delay(
                            ReturnToLobbyPollMilliseconds,
                            ignoreTimeScale: true,
                            cancellationToken: token
                        );
                    }
                }
                catch (OperationCanceledException)
                    when (timeout.IsCancellationRequested && !_lifetime.IsCancellationRequested)
                {
                    throw new GateCallException("match-still-active");
                }
                finally
                {
                    if (BackendStatus == WaitingForRoomReleaseStatus)
                    {
                        BackendStatus = "";
                    }
                }
            }
        }

        private void ValidateReturnQueue(string partyId, string requestId)
        {
            if (LobbyParty == null || LobbyParty.PartyId != partyId || QueueRequestId != requestId)
            {
                throw new GateCallException("state-changed");
            }
        }

        // 在 Update（Input System 动态更新阶段）采样设备；固定 Tick 仅消费快照，
        // 避免在错误的输入更新阶段读取设备。
        private Vector2Int _manualMove;
        private bool _manualFire;
        private bool _manualAim;
        private readonly NetworkCharacterInput _characterInput = new NetworkCharacterInput();
        private bool HasGameFocus
        {
            get
            {
#if UNITY_EDITOR
                return EditorHasGameFocus?.Invoke() ?? false;
#else
                return global::UnityEngine.Application.isFocused;
#endif
            }
        }

        private bool CanSampleCharacterInput =>
            !_automated
            && HasGameFocus
            && !_textInputFocused
            && _uiService?.BlocksGameplay != true
            && !_paused
            && !_resumePending
            && !Completed
            && State == "Battle"
            && Battle != null
            && Battle.State == BattleClientState.Active
            && Battle.IsPredictionInitialized
            && Battle.ArenaPhase != ArenaMatchPhase.Finished;

        private void ClearManualInput()
        {
            _manualMove = Vector2Int.zero;
            _manualFire = _manualAim = false;
            _characterInput.Clear();
        }

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
            Mouse mouse = Mouse.current;
            _manualFire = mouse != null && mouse.leftButton.isPressed;
            _manualAim = mouse != null && mouse.rightButton.isPressed;
            if (!CanSampleCharacterInput || _pointerOverUi)
            {
                _characterInput.Clear();
                return;
            }

            // 按住鼠标右键环绕或瞄准；解锁鼠标时仍可操作大厅和战斗 UI。
            if (_manualAim && CharacterCamera != null)
            {
                CharacterCamera.ApplyLook(mouse.delta.ReadValue());
            }

            bool jumpPressed = keyboard != null && keyboard.spaceKey.wasPressedThisFrame;
            ArenaWeaponId selectedWeapon;
            if (keyboard != null && keyboard.digit1Key.wasPressedThisFrame)
            {
                selectedWeapon = ArenaWeaponId.Machinegun;
            }
            else if (keyboard != null && keyboard.digit2Key.wasPressedThisFrame)
            {
                selectedWeapon = ArenaWeaponId.Shotgun;
            }
            else
            {
                selectedWeapon =
                    keyboard != null && keyboard.digit3Key.wasPressedThisFrame
                        ? ArenaWeaponId.Rocket
                        : ArenaWeaponId.None;
            }
            _characterInput.Sample(
                jumpPressed,
                selectedWeapon,
                keyboard != null && keyboard.rKey.wasPressedThisFrame
            );
        }

        private void OnApplicationFocus(bool focused)
        {
            if (!focused)
            {
                ClearManualInput();
            }
        }

        private void Update()
        {
            if (!_started || _disposed || _paused)
            {
                return;
            }

            _view?.RefreshInputBlock();
            SampleManualInput();
            if (Completed || State == "Settling" || State == "AwaitingResult")
            {
                if (!_battleReceiveComplete && Battle != null)
                {
                    _battleReceiveComplete = Battle.DrainAcceptedPackets();
                }

                AdvanceTerminalCompletion(Time.unscaledDeltaTime);
                if (Completed)
                {
                    // 玩家查看结算界面期间，保持账号连接存活。
                    if (
                        !_automated
                        && !_busy
                        && Backend != null
                        && Backend.IsAuthenticated
                        && Time.unscaledTime >= _nextPoll
                    )
                    {
                        _nextPoll = Time.unscaledTime + 5;
                        Run(async () => Profile = await Backend.ProfileAsync(_lifetime.Token));
                    }

                    return;
                }
            }

            float elapsed = _skipElapsedFrame ? 0 : Time.unscaledDeltaTime;
            _skipElapsedFrame = false;
            _elapsed += elapsed;
            if (_automated && _elapsed >= _deadlineSeconds)
            {
                Fail("topology-timeout");
                return;
            }

            if (_resumePending)
            {
                // 准入续签是异步操作；不得用已过期的后台票据消耗 KCP 重试次数。
                if (!_busy && Backend != null && Time.unscaledTime >= _nextPoll)
                {
                    _nextPoll = Time.unscaledTime + 1;
                    Run(PollAsync);
                }

                return;
            }

            if (State != "Settling" && State != "AwaitingResult")
            {
                Battle?.Pump(elapsed);
            }

            if (Battle != null)
            {
                if (
                    _automated
                    && State == "Battle"
                    && Battle.State == BattleClientState.Active
                    && Battle.IsPredictionInitialized
                    && Battle.ArenaPhase == ArenaMatchPhase.Active
                )
                {
                    _acknowledgementStall =
                        Battle.LastAcknowledgedSequence > _observedAcknowledgement
                            ? 0
                            : _acknowledgementStall + Time.unscaledDeltaTime;
                    _observedAcknowledgement = Battle.LastAcknowledgedSequence;
                    MaxAcknowledgementStallSeconds = Math.Max(
                        MaxAcknowledgementStallSeconds,
                        _acknowledgementStall
                    );
                }
                else
                {
                    _acknowledgementStall = 0;
                    _observedAcknowledgement = 0;
                }

                if (State != "Settling" && Battle.State == BattleClientState.Faulted)
                {
                    State = "AwaitingResult";
                    BackendStatus = "战斗连接中断，正在查询原对局状态和结算。";
                }

                if (
                    _automated
                    && State == "Battle"
                    && !_reconnectRequested
                    && Battle.IsPredictionInitialized
                    && Battle.LastAcknowledgedSequence >= 30
                )
                {
                    _reconnectRequested = true;
                    _priorSession = Battle.SessionId;
                    _priorEntity = Battle.EntityId;
                    _priorAcknowledgement = Battle.LastAcknowledgedSequence;
                    Battle.RequestReconnect();
                    _observedReconnecting = Battle.State == BattleClientState.Reconnecting;
                }

                if (
                    _reconnectRequested
                    && _observedReconnecting
                    && Battle.State == BattleClientState.Active
                    && Battle.LastAcknowledgedSequence > _priorAcknowledgement
                )
                {
                    if (Battle.EntityId != _priorEntity || Battle.SessionId != _priorSession)
                    {
                        Fail("reconnect-identity-changed");
                        return;
                    }

                    Reconnected = true;
                }
            }

            if (!_busy && Backend != null && Time.unscaledTime >= _nextPoll)
            {
                _nextPoll = Time.unscaledTime + (Battle == null ? 1f : 5f);
                Run(PollAsync);
            }
        }

        private void FixedUpdate()
        {
            _view?.RefreshInputBlock();
            if (
                !_started
                || _disposed
                || _paused
                || _resumePending
                || Completed
                || State == "Settling"
                || State == "AwaitingResult"
                || Battle == null
                || Battle.ArenaPhase == ArenaMatchPhase.Finished
                || Battle.State != BattleClientState.Active
                || !Battle.IsPredictionInitialized
            )
            {
                _characterInput.Clear();
                return;
            }

            // 采用权威房间时间，防止网络阻塞后的积压输入被排到过远的未来。
            _inputTick = Math.Max(_inputTick + 1, Battle.LastReceivedTick + 1);
            bool acceptInput =
                HasGameFocus && !_textInputFocused && _uiService?.BlocksGameplay != true;
            if (!acceptInput || _pointerOverUi)
            {
                _characterInput.Clear();
            }

            int x;
            if (_automated)
            {
                x = (_inputTick / 60) % 2 == 0 ? 1000 : -1000;
            }
            else
            {
                x = acceptInput ? _manualMove.x : 0;
            }
            int z = !_automated && acceptInput ? _manualMove.y : 0;
            AiNative.Client.Prediction.PredictionPrepareStatus prepared;
            if (Battle.TryGetArenaState(out var state))
            {
                int yaw = 0,
                    pitch = 0;
                if (!_automated && CharacterCamera != null && acceptInput)
                {
                    Vector2Int move = NetworkCharacterInput.RotateMove(
                        new Vector2Int(x, z),
                        CharacterCamera.Yaw
                    );
                    x = move.x;
                    z = move.y;
                    if (!_pointerOverUi)
                    {
                        var presentation = GetComponent<BattleClientCompositionRoot>();
                        if (presentation != null)
                        {
                            NetworkCharacterInput.AimDelta(
                                state,
                                presentation.GetAimPoint(),
                                out yaw,
                                out pitch
                            );
                        }
                        else
                        {
                            NetworkCharacterInput.LookDelta(
                                state,
                                CharacterCamera.Yaw,
                                CharacterCamera.Pitch,
                                out yaw,
                                out pitch
                            );
                        }
                    }
                }

                ArenaButtons buttons =
                    !_automated && acceptInput && !_pointerOverUi
                        ? _characterInput.Buttons(_manualFire)
                        : ArenaButtons.None;
                prepared = Battle.PredictAndQueueArenaInput(
                    _inputTick,
                    x,
                    z,
                    yaw,
                    pitch,
                    buttons,
                    _automated ? ArenaWeaponId.Machinegun : _characterInput.Weapon
                );
            }
            else
            {
                prepared = Battle.PredictAndQueueInput(_inputTick, x, z);
            }

            if (prepared == AiNative.Client.Prediction.PredictionPrepareStatus.Prepared)
            {
                PreparedInputs++;
                _characterInput.Commit();
            }
        }

        private async UniTask PollAsync()
        {
            try
            {
                await PollCoreAsync();
            }
            finally
            {
                if (!_disposed)
                {
                    WriteCheckpoint();
                }
            }
        }

        // ADR-0017：Gate 恢复不能替换原对局身份；分配、结算与大厅队列分别向权威服务对账。
        // 见 Docs/ADR/0017-single-region-service-topology.md。
        private async UniTask PollCoreAsync()
        {
            if (_paused)
            {
                return;
            }

            if (!Backend.IsAuthenticated)
            {
                if (
                    Battle != null
                    && (
                        _automated
                        || (_resumePending && _username.Length > 0 && _password.Length > 0)
                    )
                )
                {
                    await ConnectAccountAsync(register: false);
                }
                else
                {
                    BackendStatus = "后台连接或登录凭证已失效，请重新登录；当前对局继续。";
                    return;
                }
            }

            if (_resumePending && Battle != null)
            {
                int generation = _pauseGeneration;
                GateMatch renewed = await Backend.MatchAsync(QueueRequestId, _lifetime.Token);
                if (renewed.Failure == "queue_lost" && renewed.EntryTicket.Length == 0)
                {
                    // Lobby 重启可能丢失队列，但 Player 仍持有原始回执。
                    // 缺失分配信息不得授权 KCP；任何冲突身份都必须拒绝。
                    ValidateOriginalMatch(renewed, allowMissing: true);
                    if (_paused || generation != _pauseGeneration)
                    {
                        return;
                    }

                    await PollOriginalSettlementAsync();
                    if (!Completed)
                    {
                        BackendStatus = "原排队记录已失效，正在等待原对局结算；不会创建新对局。";
                    }

                    return;
                }

                ValidateOriginalMatch(renewed);
                if (_paused || generation != _pauseGeneration)
                {
                    return;
                }

                if (renewed.EntryTicket.Length > 0)
                {
                    Battle.ResumeOriginalAllocation(
                        new BattleAdmissionInfo(
                            renewed.RoomId,
                            renewed.BootEpoch,
                            renewed.EntryTicket,
                            renewed.NodeId,
                            PlayerId
                        )
                    );
                    if (State != "Settling")
                    {
                        State = "Battle";
                    }

                    ResumeCompletedCount++;
                    _resumePending = false;
                    BackendStatus = "";
                    return;
                }

                if (renewed.Failure == "room_released")
                {
                    _resumePending = false;
                    State = "Settling";
                }
                else
                {
                    BackendStatus = "正在等待原对局的新接入凭证。";
                    return;
                }
            }

            if (Battle != null)
            {
                if (await PollOriginalSettlementAsync())
                {
                    return;
                }

                GateMatch refreshed = await Backend.MatchAsync(QueueRequestId, _lifetime.Token);
                if (refreshed.EntryTicket.Length > 0)
                {
                    ValidateOriginalMatch(refreshed);
                    Battle.UpdateAdmission(
                        new BattleAdmissionInfo(
                            refreshed.RoomId,
                            refreshed.BootEpoch,
                            refreshed.EntryTicket,
                            refreshed.NodeId,
                            PlayerId
                        )
                    );
                }
                else if (
                    refreshed.Failure == "room_released"
                    && refreshed.MatchId == MatchId
                    && refreshed.RoomId == RoomId
                    && refreshed.BootEpoch == CurrentMatch.BootEpoch
                    && refreshed.NodeId == CurrentMatch.NodeId
                )
                {
                    // 房间关闭时，最后一份非可靠快照可能丢失；只有确认本次分配已释放
                    // 才能查询结算，仍须验证玩家档案中的对局计数递增。
                    State = "Settling";
                }
                else if (refreshed.Failure == "queue_lost")
                {
                    BackendStatus = "匹配后台已重启；当前对局继续，使用原对局查询结算。";
                }
                else if (refreshed.Failure.Length > 0)
                {
                    Fail("match-ended:" + refreshed.Failure);
                }

                return;
            }

            if (State == "Queued")
            {
                CurrentMatch = await Backend.MatchAsync(QueueRequestId, _lifetime.Token);
                // 响应完成后的继续执行可能晚于 OnDestroy；此时流程可能已经释放。
                if (_disposed)
                {
                    return;
                }

                if (CurrentMatch.Failure.Length > 0)
                {
                    State = "Lobby";
                    QueueRequestId = "";
                    if (LobbyParty != null)
                    {
                        LobbyParty = await Backend.GetPartyAsync(
                            LobbyParty.PartyId,
                            _lifetime.Token
                        );
                    }

                    BackendStatus = "排队状态已失效，请重新确认队伍和准备状态。";
                    return;
                }

                if (CurrentMatch.EntryTicket.Length > 0)
                {
                    await _matchController.StartAllocatedBattleAsync(PlayerId, _lifetime.Token);
                    State = "Battle";
                }

                return;
            }

            if (State == "Lobby")
            {
                await _accountLobby.RefreshLobbyAsync(_lifetime.Token);
                if (LobbyParty != null)
                {
                    if (LobbyParty.QueueRequestId.Length > 0)
                    {
                        QueueRequestId = LobbyParty.QueueRequestId;
                        State = "Queued";
                        _queueStarted = Time.unscaledTime;
                    }
                }
            }
        }

        private async UniTask<bool> PollOriginalSettlementAsync()
        {
            Profile = await Backend.ProfileAsync(_lifetime.Token); // 同时维持 TCP 会话活跃。
            BackendStatus = "";
            GateSettlement settlement;
            try
            {
                settlement = await Backend.SettlementAsync(MatchId, _lifetime.Token);
            }
            catch (InvalidDataException exception)
                when (exception.Message == "gate-settlement-identity")
            {
                throw new GateCallException("settlement-mismatch-active-battle");
            }

            if (settlement.MatchId != MatchId)
            {
                throw new GateCallException("settlement-mismatch-active-battle");
            }

            if (settlement.Confirmed)
            {
                SettlementConfirmed = true;
                State = "Settling";
            }

            if (Battle.ArenaPhase != ArenaMatchPhase.Finished && State != "Settling")
            {
                return false;
            }

            State = "Settling";
            if (settlement.Confirmed && Profile.Played == _baselinePlayed + 1)
            {
                if (_automated && !Reconnected)
                {
                    Fail("reconnect-not-observed");
                    return true;
                }

                _resumePending = false;
                Error = "";
                Completed = true;
                State = "Finished";
                WriteEvidence(true);
            }
            else if (Profile.Played > _baselinePlayed + 1)
            {
                Fail("unexpected-profile-increment");
            }

            return true;
        }

        private void ValidateOriginalMatch(GateMatch candidate, bool allowMissing = false)
        {
            _matchController.ValidateOriginalMatch(candidate, allowMissing);
        }

        private void OnApplicationPause(bool paused)
        {
            if (_disposed || Completed || paused == _paused)
            {
                return; // Initial false and duplicate callbacks have no effect.
            }

            _paused = paused;
            if (paused)
            {
                PauseCount++;
                _pauseGeneration++;
                ClearManualInput();
            }
            else
            {
                ResumeCount++;
                _skipElapsedFrame = true;
                _resumePending = Battle != null;
                _nextPoll = 0;
                if (_resumePending)
                {
                    BackendStatus = "正在验证原对局并恢复连接。";
                }
            }

            WriteCheckpoint();
        }

        private void Run(Func<UniTask> action, bool foreground = false)
        {
            if (_busy || _disposed)
            {
                return;
            }
            _busy = true;
            var completion = new UniTaskCompletionSource();
            _activeOperation = completion;
            RunCoreAsync(action, foreground, completion).Forget(Debug.LogException);
        }

        private async UniTask RunCoreAsync(
            Func<UniTask> action,
            bool foreground,
            UniTaskCompletionSource operation
        )
        {
            try
            {
                await action();
            }
            catch (OperationCanceledException)
            {
                if (!_disposed && Battle != null)
                {
                    BackendStatus = "后台请求超时；当前对局继续。";
                    _nextPoll = Time.unscaledTime + 1;
                }
                else if (!_disposed)
                {
                    if (foreground)
                    {
                        Error = "操作超时，请重试。";
                    }
                    else
                    {
                        BackendStatus = "后台请求超时，正在重试。";
                    }

                    if (State == "Authenticating")
                    {
                        State = "Disconnected";
                    }
                }
            }
            catch (Exception exception)
            {
                if (_disposed)
                {
                    return;
                }

                string code = exception is GateCallException gate
                    ? gate.Code
                    : exception.GetType().Name;
                if (
                    Battle != null
                    && (
                        code == "allocation-mismatch-active-battle"
                        || code == "account-mismatch-active-battle"
                        || code == "settlement-mismatch-active-battle"
                    )
                )
                {
                    Error = code;
                    BackendStatus = "恢复身份与原对局不一致，已拒绝更换玩家或对局。";
                    _nextPoll = Time.unscaledTime + 1;
                    return;
                }

                if (
                    Backend != null
                    && (
                        code == "session-expired"
                        || code == "invalid_session"
                        || code == "invalid-session"
                        || code == "unauthorized"
                    )
                )
                {
                    BackendStatus = "登录凭证已失效，请重新登录；当前对局继续，结算会重试。";
                    _nextPoll = Time.unscaledTime + 1;
                    return;
                }

                if (Battle == null && code == "party_not_found")
                {
                    LobbyParty = null;
                    QueueRequestId = "";
                    State = "Lobby";
                    BackendStatus = "大厅状态已重置，请重新建队和准备。";
                    return;
                }

                if (Battle == null && (code == "queue_not_found" || code == "queue_expired"))
                {
                    QueueRequestId = "";
                    State = "Lobby";
                    BackendStatus = "匹配状态已重置，请重新确认队伍和准备状态。";
                    return;
                }

                if (
                    !foreground
                    && Battle != null
                    && (
                        exception is IOException
                        || exception is SocketException
                        || exception is TimeoutException
                        || code == "timeout"
                        || code == "unavailable"
                        || code == "authority_unavailable"
                        || code == "allocation_uncertain"
                        || code == "queue_expired"
                        || code == "queue_not_found"
                    )
                )
                {
                    BackendStatus = "后台暂时不可用；当前对局继续，结算会重试。";
                    _nextPoll = Time.unscaledTime + 1;
                    return;
                }

                if (_automated)
                {
                    Fail(code);
                }
                else
                {
                    if (foreground)
                    {
                        Error = code;
                    }
                    else
                    {
                        BackendStatus = "后台刷新失败：" + ErrorMessage(code);
                    }

                    // 取消或排队请求失败时，不得假定服务器已经释放队列。
                    if (State == "Authenticating")
                    {
                        State = Backend?.IsAuthenticated == true ? "Lobby" : "Disconnected";
                    }
                }
            }
            finally
            {
                _busy = false;
                if (foreground)
                {
                    _uiBusy = false;
                    UniTaskCompletionSource<bool> completion = _uiCompletion;
                    _uiCompletion = null;
                    completion?.TrySetResult(true);
                }

                operation.TrySetResult();
                if (!_disposed && _pendingAction != null)
                {
                    var pending = _pendingAction;
                    _pendingAction = null;
                    Run(pending, foreground: true);
                }
            }
        }

        private void Fail(string error)
        {
            Error = error;
            State = "Failed";
            if (_automated)
            {
                Completed = true;
                WriteEvidence(false);
            }
        }

        private static string ErrorMessage(string code)
        {
            switch (code)
            {
                case "invalid_credentials":
                    return "账号或密码不符合要求，或登录信息不正确。账号须为 3–32 位英文字母、数字或下划线；密码须为 6–32 位。";
                case "username_exists":
                    return "账号已存在，请使用“登录 / 恢复后台连接”，或换一个账号注册。";
                case "unavailable":
                case "authority_unavailable":
                    return "服务暂时不可用，请确认服务器全部就绪后重试。";
                case "SocketException":
                case "IOException":
                    return "连接失败，请检查服务器是否启动，以及地址、端口和 TLS 设置。";
                case "timeout":
                case "TimeoutException":
                    return "请求超时，请检查服务器是否就绪或停在 VS 断点，然后重试。";
                case "invalid-address":
                    return "请输入有效的服务器地址和端口（1–65535）。";
                case "version_conflict":
                    return "队伍状态刚刚发生变化，请稍后重试。";
                case "leader_required":
                    return "这项操作需要队长执行。";
                case "party_not_ready":
                    return "请等待所有队员准备。";
                case "allocation_claimed":
                    return "服务器正在分配房间，暂时无法取消，请等待结果。";
                case "invitation_expired":
                    return "邀请已失效，请对方重新邀请。";
                case "state-changed":
                    return "当前状态已变化，请按界面提示操作。";
                case "match-still-active":
                    return "服务器尚未结束原对局，请稍后再返回大厅。";
                default:
                    return code;
            }
        }

        private void AdvanceTerminalCompletion(float elapsedSeconds)
        {
            if (!_successExitPending)
            {
                return;
            }

            if (Battle?.ArenaPhase == ArenaMatchPhase.Finished && _battleReceiveComplete)
            {
                _successExitPending = false;
                WriteEvidence(true);
                return;
            }

            _successExitElapsed += elapsedSeconds;
            if (_successExitElapsed < TerminalReceiveExitTimeoutSeconds)
            {
                return;
            }

            _successExitPending = false;
            Error = "terminal-receive-timeout";
            WriteEvidence(false);
        }

        private void WriteEvidence(bool success)
        {
            if (_resultPath.Length == 0)
            {
                return;
            }

            // 结算状态仍立即完成；仅自动化证据记录和进程退出需要等待。
            if (
                success
                && Battle != null
                && (Battle.ArenaPhase != ArenaMatchPhase.Finished || !_battleReceiveComplete)
            )
            {
                if (!_successExitPending)
                {
                    _successExitPending = true;
                    _successExitElapsed = 0;
                }

                WriteCheckpoint();
                return;
            }

            _evidenceSuccess = success;
            if (!WriteCheckpoint(success))
            {
                success = false;
                _evidenceSuccess = false;
            }

#if !UNITY_EDITOR
            _root.RequestQuit(success ? 0 : 1);
#endif
        }

        private bool WriteCheckpoint(bool success = false)
        {
            if (_resultPath.Length == 0)
            {
                return true;
            }

            try
            {
                string fullPath = Path.GetFullPath(_resultPath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
                File.WriteAllText(
                    fullPath,
                    JsonUtility.ToJson(
                        new Evidence
                        {
                            success = success || _evidenceSuccess,
                            completed = Completed,
                            finalReceived = Battle?.ArenaPhase == ArenaMatchPhase.Finished,
                            receiveDrainCompleted = _battleReceiveComplete,
                            state = State,
                            runId = _runId,
                            paused = _paused,
                            pauseCount = PauseCount,
                            resumeCount = ResumeCount,
                            resumeCompletedCount = ResumeCompletedCount,
                            resumePending = _resumePending,
                            error = Error,
                            playerId = PlayerId,
                            matchId = MatchId,
                            roomId = RoomId,
                            nodeId = CurrentMatch?.NodeId ?? "",
                            bootEpoch = CurrentMatch?.BootEpoch ?? "",
                            reconnected = Reconnected,
                            settlementConfirmed = SettlementConfirmed,
                            backendReconnectCount = BackendReconnectCount,
                            preparedInputs = PreparedInputs,
                            maxAcknowledgementStallSeconds = MaxAcknowledgementStallSeconds,
                            played = Profile?.Played ?? 0,
                            lastTick = Battle?.LastReceivedTick.ToString() ?? "0",
                            lastAcknowledgedSequence = Battle?.LastAcknowledgedSequence ?? 0,
                        },
                        true
                    )
                );
                return true;
            }
            catch (Exception exception)
            {
                Error = "evidence-write:" + exception.GetType().Name;
                return false;
            }
        }

        [Serializable]
        private sealed class Evidence
        {
            public bool success;
            public bool reconnected;
            public bool settlementConfirmed;
            public bool completed;
            public bool paused;
            public bool resumePending;
            public bool finalReceived;
            public bool receiveDrainCompleted;
            public string error;
            public string playerId;
            public string matchId;
            public string roomId;
            public string nodeId;
            public string bootEpoch;
            public string lastTick;
            public string state;
            public string runId;
            public long played;
            public long preparedInputs;
            public uint lastAcknowledgedSequence;
            public int backendReconnectCount;
            public int pauseCount;
            public int resumeCount;
            public int resumeCompletedCount;
            public float maxAcknowledgementStallSeconds;
        }

        /// <summary>
        /// 正常流程退出时显式等待；先取消请求与界面加载，再释放 UI、资源句柄和对局。
        /// 重复调用等待同一次清理，不依赖 Unity 的 OnDestroy 调用顺序。
        /// </summary>
        public ValueTask StopAsync()
        {
            if (_stopTask == null)
            {
                var completion = new UniTaskCompletionSource();
                _stopTask = completion;
                _disposed = true;
                _pendingAction = null;
                _password = "";
                _uiBusy = false;
                _uiCompletion?.TrySetCanceled();
                _uiCompletion = null;
                StopCoreAsync(completion).Forget(Debug.LogException);
            }
            return _stopTask.Task.AsValueTask();
        }

        private async UniTask StopCoreAsync(UniTaskCompletionSource completion)
        {
            var errors = new List<Exception>();
            // 先发布停止任务再触发取消；回调重入必须观察同一次清理。
            try
            {
                _lifetime.Cancel();
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }
            if (_activeOperation != null)
            {
                try
                {
                    await _activeOperation.Task;
                }
                catch (Exception exception)
                {
                    errors.Add(exception);
                }
            }
            if (_uiLoadTask != null)
            {
                try
                {
                    await _uiLoadTask.Task;
                }
                catch (Exception exception)
                {
                    errors.Add(exception);
                }
            }
            if (_uiService != null)
            {
                try
                {
                    await _uiService.CloseAsync("topology");
                }
                catch (Exception exception)
                {
                    errors.Add(exception);
                }
            }
            // 对局与账号由后续 Core 模块释放；Flow 只停止所属工作与页面。
            _registeredUiService = null;
            _uiLoadTask = null;
            _uiAssets = null;
            _uiService = null;
            try
            {
                _lifetime.Dispose();
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }
            if (errors.Count > 0)
            {
                completion.TrySetException(new AggregateException(errors));
            }
            else
            {
                completion.TrySetResult();
            }
        }

        private void OnDestroy()
        {
            if (_disposed)
            {
                return;
            }
            _lifetime.Cancel();
            if (_root != null)
            {
                _root.StopAsync().AsUniTask().Forget(Debug.LogException);
            }
            else
            {
                StopAsync().AsUniTask().Forget(Debug.LogException);
            }
        }
    }
}
