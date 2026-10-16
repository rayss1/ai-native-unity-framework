using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using AiNative.Client.Fantasy;
using AiNative.Client.Character;
using AiNative.Gameplay;
using UnityEngine;
using UnityEngine.InputSystem;

namespace AiNative.Client.Application
{
    /// <summary>Product composition for Gate account/party/match and direct authoritative Battle gameplay.</summary>
    public sealed class TopologyClientFlow : MonoBehaviour
    {
#if UNITY_EDITOR
        // Supplied by the Editor assembly: MPPM virtual players report Application.isFocused
        // even when their OS window is inactive. Runtime never references UnityEditor.
        public static Func<bool> EditorHasGameFocus { get; set; }
#endif
        private GateBackendSession _backend;
        private GateConnectionOptions _options;
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private GateParty _party;
        private GateParty[] _invitations = Array.Empty<GateParty>();
        private GateMatch _match;
        private string _requestId = "", _username = "", _password = "";
        private string _host = "127.0.0.1", _port = "23001", _pin = "", _resultPath = "";
        private bool _tls, _busy, _automated, _started, _disposed, _reconnectRequested, _observedReconnecting;
        private bool _paused, _resumePending, _skipElapsedFrame;
        private bool _textInputFocused;
        private bool _pointerOverUi, _uiBusy;
        private Func<Task> _pendingAction;
        private TopologyClientView _view;
        private float _queueStarted;
        private int _pauseGeneration;
        private bool _battleReceiveComplete, _successExitPending, _evidenceSuccess;
        private float _successExitElapsed;
        private const float TerminalReceiveExitTimeoutSeconds = 5f;
        private string _runId = "";
        private ulong _priorSession, _inputTick;
        private uint _priorEntity;
        private uint _priorAcknowledgement;
        private uint _observedAcknowledgement;
        private float _acknowledgementStall;
        private long _baselinePlayed;
        private float _elapsed, _nextPoll, _deadlineSeconds = 120;
        public BattleClientSession Battle { get; private set; }
        public CharacterOrbitCamera CharacterCamera { get; set; }
        public bool IsCharacterAiming => _manualAim && CanSampleCharacterInput && !_pointerOverUi;
        public GateProfile Profile { get; private set; }
        public string PlayerId => _backend?.PlayerId ?? "";
        public string MatchId => _match?.MatchId ?? "";
        public string RoomId => _match?.RoomId ?? "";
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
        public bool AuthenticationRequired => _backend == null || !_backend.IsAuthenticated;
        public int PauseCount { get; private set; }
        public int ResumeCount { get; private set; }
        public int ResumeCompletedCount { get; private set; }
        public bool UiBusy => _uiBusy;
        public GateParty Party => _party;
        public IReadOnlyList<GateParty> Invitations => _invitations;
        public string ConnectionHost => _host;
        public string ConnectionPort => _port;
        public bool ConnectionTls => _tls;
        public string ConnectionPin => _pin;
        public string DisplayError => ErrorMessage(Error);
        public float QueueElapsedSeconds => State == "Queued" ? Math.Max(0, Time.unscaledTime - _queueStarted) : 0;

        public void SetUiInputBlocked(bool keyboard, bool pointer)
        {
            _textInputFocused = keyboard; _pointerOverUi = pointer;
            if (keyboard || pointer) _characterInput.Clear();
        }

        public void Authenticate(bool register, string username, string password, string host, string port, bool tls, string pin)
        {
            Submit(async () =>
            {
                if (!AuthenticationRequired) return;
                string normalized = (username ?? "").Trim().ToLowerInvariant();
                if (normalized.Length < 3 || normalized.Length > 32 || string.IsNullOrEmpty(password) || password.Length < 6 || password.Length > 32)
                    throw new GateCallException("invalid_credentials");
                foreach (char c in normalized)
                    if (!(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9') && c != '_') throw new GateCallException("invalid_credentials");
                if (string.IsNullOrWhiteSpace(host) || !int.TryParse(port, out int number) || number < 1 || number > 65535)
                    throw new GateCallException("invalid-address");
                _username = normalized; _password = password; _host = host.Trim(); _port = port; _tls = tls; _pin = pin ?? "";
                await ConnectAccountAsync(register);
            });
        }
        public void CreateParty() => Submit(async () => { RequireLobby(); if (_party == null) _party = await _backend.CreatePartyAsync(_lifetime.Token); });
        public void InvitePlayer(string playerId) => Submit(async () => { RequireLobby(); if (_party != null) _party = await _backend.InviteAsync(_party, (playerId ?? "").Trim(), _lifetime.Token); });
        public void AcceptInvitation(string partyId) => Submit(async () =>
        {
            RequireLobby();
            if (_party != null) return;
            foreach (GateParty invitation in _invitations)
                if (invitation.PartyId == partyId) { _party = await _backend.AcceptAsync(invitation, _lifetime.Token); return; }
            throw new GateCallException("invitation_expired");
        });
        public void SetReady(bool ready) => Submit(async () => { RequireLobby(); if (_party != null) _party = await _backend.ReadyAsync(_party, ready, _lifetime.Token); });
        public void StartMatch() => Submit(async () => { RequireLobby(); if (_party != null) await QueueAsync(); });
        public void CancelMatch() => Submit(async () =>
        {
            if (State != "Queued") return;
            await _backend.CancelQueueAsync(_requestId, _lifetime.Token);
            _requestId = ""; State = "Lobby";
            if (_party != null) _party = await _backend.GetPartyAsync(_party.PartyId, _lifetime.Token);
        });
        public void LeaveParty() => Submit(async () => { RequireLobby(); if (_party != null) { await _backend.LeaveAsync(_party, _lifetime.Token); _party = null; } });
        public void ReconnectBattle() => Submit(() => { if (Battle != null && !Completed) { _resumePending = true; _nextPoll = 0; } return Task.CompletedTask; });
        public void ReturnToLobby() => Submit(ReturnToLobbyAsync);

        private void RequireLobby()
        {
            if (State != "Lobby" || AuthenticationRequired || Battle != null) throw new GateCallException("state-changed");
        }
        private void Submit(Func<Task> action)
        {
            if (_disposed || _uiBusy) return;
            _uiBusy = true; Error = "";
            if (_busy) { _pendingAction = action; return; }
            Run(action, foreground: true);
        }

        public void Initialize(GateConnectionOptions options, bool automated = false, float deadlineSeconds = 120, string resultPath = "", string runId = "")
        {
            if (_started) throw new InvalidOperationException("Topology flow already started.");
            if (deadlineSeconds < 30 || deadlineSeconds > 3600) throw new ArgumentOutOfRangeException(nameof(deadlineSeconds));
            _options = options ?? throw new ArgumentNullException(nameof(options)); _automated = automated; _deadlineSeconds = deadlineSeconds; _resultPath = resultPath;
            _host = options.Host; _port = options.Port.ToString(); _tls = options.UseTls; _pin = options.CertificateSha256;
            _runId = runId;
        }

        private void Start()
        {
            global::UnityEngine.Application.runInBackground = true;
            if (_options == null) ConfigureFromArguments(Environment.GetCommandLineArgs());
            _started = true;
            if (!_automated)
            {
                var prefab = Resources.Load<TopologyClientView>("TopologyClientUI");
                if (prefab == null) { Debug.LogError("Missing Resources/TopologyClientUI prefab."); }
                else { _view = Instantiate(prefab); _view.Bind(this); }
            }
            if (_automated) Run(async () =>
            {
                _username = "unity_" + Guid.NewGuid().ToString("N").Substring(0, 16); _password = Guid.NewGuid().ToString("N");
                await ConnectAccountAsync(register: true);
                _party = await _backend.CreatePartyAsync(_lifetime.Token); _party = await _backend.ReadyAsync(_party, true, _lifetime.Token);
                await QueueAsync();
            });
        }

        private void ConfigureFromArguments(string[] args)
        {
            _automated = Array.IndexOf(args, "--ainative-topology-smoke") >= 0;
            _host = Option(args, "--ainative-gate-host", "127.0.0.1"); _port = Option(args, "--ainative-gate-port", "23001"); _pin = Option(args, "--ainative-gate-pin", "");
            _tls = Array.IndexOf(args, "--ainative-gate-plaintext") < 0;
            _resultPath = Option(args, "--ainative-result", "");
            if (float.TryParse(Option(args, "--ainative-topology-timeout", "120"), out float timeout) && timeout >= 30 && timeout <= 3600) _deadlineSeconds = timeout;
            if (_automated) _options = new GateConnectionOptions(_host, int.Parse(_port), _tls, _pin);
        }
        private static string Option(string[] args, string key, string fallback) { int index = Array.IndexOf(args, key); return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback; }

        private async Task ConnectAccountAsync(bool register)
        {
            if (register && Battle != null) throw new GateCallException("account-mismatch-active-battle");
            string previousPlayer = PlayerId, previousState = State;
            if (Battle == null) State = "Authenticating";
            _options = new GateConnectionOptions(_host, int.Parse(_port), _tls, _pin);
            var candidate = new GateBackendSession(await FantasyGateClient.ConnectAsync(_options, _lifetime.Token));
            try
            {
                if (register) await candidate.RegisterAsync(_username, _password, _lifetime.Token);
                else await candidate.LoginAsync(_username, _password, _lifetime.Token);
                if (Battle != null && candidate.PlayerId != previousPlayer) throw new GateCallException("account-mismatch-active-battle");
                GateProfile profile = await candidate.ProfileAsync(_lifetime.Token);
                _lifetime.Token.ThrowIfCancellationRequested();
                _backend?.Dispose(); _backend = candidate; Profile = profile;
                if (Battle != null) BackendReconnectCount++;
                if (Battle == null)
                {
                    if (previousPlayer != candidate.PlayerId) { _baselinePlayed = profile.Played; _party = null; _requestId = ""; State = "Lobby"; }
                    else State = previousState == "Queued" && _requestId.Length > 0 ? "Queued" : "Lobby";
                }
                else State = Completed ? "Finished" : previousState == "Settling" ? "Settling" : "Battle";
                Error = ""; BackendStatus = "";
            }
            catch { candidate.Dispose(); throw; }
        }

        private async Task QueueAsync()
        {
            _requestId = Guid.NewGuid().ToString("N");
            State = "Queued"; _queueStarted = Time.unscaledTime;
            try { await _backend.QueueAsync(_party, _requestId, _lifetime.Token); }
            catch (GateCallException exception) when (exception.Code == "party_not_ready" || exception.Code == "leader_required" ||
                exception.Code == "version_conflict" || exception.Code == "party_not_found" || exception.Code == "invalid_request" ||
                exception.Code == "invalid_queue" || exception.Code == "player_busy" || exception.Code == "queue_capacity")
            {
                _requestId = ""; State = "Lobby"; throw;
            }
            // Timeout or unknown outcome retains this request ID for status/cancel reconciliation.
        }

        private async Task ReturnToLobbyAsync()
        {
            if (_automated || !Completed || !SettlementConfirmed || Battle == null) throw new GateCallException("state-changed");
            // Gate may already have observed the terminal status and cleared the party queue.
            // Ask Lobby first: querying a pruned Match entry again would otherwise strand this UI.
            GateParty refreshedParty = null;
            if (_party != null)
            {
                try { refreshedParty = await _backend.GetPartyAsync(_party.PartyId, _lifetime.Token); }
                catch (GateCallException exception) when (exception.Code == "party_not_found" || exception.Code == "forbidden") { }
                if (refreshedParty != null && refreshedParty.QueueRequestId.Length > 0)
                {
                    if (refreshedParty.QueueRequestId != _requestId) throw new GateCallException("state-changed");
                    try
                    {
                        GateMatch terminal = await _backend.MatchAsync(_requestId, _lifetime.Token);
                        if (terminal.State != "failed" && terminal.State != "cancelled") throw new GateCallException("match-still-active");
                    }
                    catch (GateCallException exception) when (exception.Code == "queue_expired" || exception.Code == "queue_not_found")
                    {
                        // Existing abort fencing releases an expired queue with its original roster.
                        await _backend.CancelQueueAsync(_requestId, _lifetime.Token);
                    }
                    refreshedParty = await _backend.GetPartyAsync(_party.PartyId, _lifetime.Token);
                    if (refreshedParty.QueueRequestId.Length > 0) throw new GateCallException("match-still-active");
                }
            }
            GateProfile profile = await _backend.ProfileAsync(_lifetime.Token);
            _lifetime.Token.ThrowIfCancellationRequested();
            BattleClientSession finished = Battle;
            await finished.DisposeAsync();
            _lifetime.Token.ThrowIfCancellationRequested();
            ClearManualInput();
            Battle = null; _match = null; _requestId = ""; _party = refreshedParty;
            Profile = profile; _baselinePlayed = profile.Played;
            Completed = SettlementConfirmed = Reconnected = false;
            _resumePending = _reconnectRequested = _observedReconnecting = false;
            _battleReceiveComplete = _successExitPending = _evidenceSuccess = false;
            _successExitElapsed = _elapsed = _acknowledgementStall = 0;
            _inputTick = _priorSession = 0; _priorEntity = _priorAcknowledgement = _observedAcknowledgement = 0;
            PreparedInputs = 0; MaxAcknowledgementStallSeconds = 0; BackendReconnectCount = 0;
            _nextPoll = 0; State = "Lobby"; Error = ""; BackendStatus = "请重新准备后开始下一局。";
        }

        // Sample devices in Update (Input System dynamic update). Fixed ticks consume
        // the snapshot without querying devices from the wrong input-update phase.
        private Vector2Int _manualMove;
        private bool _manualFire, _manualAim;
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
        private bool CanSampleCharacterInput => !_automated && HasGameFocus && !_textInputFocused &&
            !_paused && !_resumePending && !Completed && State == "Battle" &&
            Battle != null && Battle.State == BattleClientState.Active && Battle.IsPredictionInitialized &&
            Battle.ArenaPhase != ArenaMatchPhase.Finished;

        private void ClearManualInput()
        {
            _manualMove = Vector2Int.zero; _manualFire = _manualAim = false;
            _characterInput.Clear();
        }

        private void SampleManualInput()
        {
            Keyboard keyboard = Keyboard.current;
            _manualMove = keyboard == null ? Vector2Int.zero : new Vector2Int(
                (keyboard.dKey.isPressed ? 1000 : 0) - (keyboard.aKey.isPressed ? 1000 : 0),
                (keyboard.wKey.isPressed ? 1000 : 0) - (keyboard.sKey.isPressed ? 1000 : 0));
            Mouse mouse = Mouse.current;
            _manualFire = mouse != null && mouse.leftButton.isPressed;
            _manualAim = mouse != null && mouse.rightButton.isPressed;
            if (!CanSampleCharacterInput || _pointerOverUi) { _characterInput.Clear(); return; }
            // Hold RMB to orbit/aim; an unlocked cursor keeps the lobby and battle UI usable.
            if (_manualAim && CharacterCamera != null) CharacterCamera.ApplyLook(mouse.delta.ReadValue());
            _characterInput.Sample(keyboard != null && keyboard.spaceKey.wasPressedThisFrame,
                keyboard != null && keyboard.digit1Key.wasPressedThisFrame ? ArenaWeaponId.Machinegun :
                keyboard != null && keyboard.digit2Key.wasPressedThisFrame ? ArenaWeaponId.Shotgun :
                keyboard != null && keyboard.digit3Key.wasPressedThisFrame ? ArenaWeaponId.Rocket : ArenaWeaponId.None,
                keyboard != null && keyboard.rKey.wasPressedThisFrame);
        }

        private void OnApplicationFocus(bool focused)
        {
            if (!focused) ClearManualInput();
        }

        private void Update()
        {
            if (_disposed || _paused) return;
            _view?.RefreshInputBlock();
            SampleManualInput();
            if (Completed || State == "Settling" || State == "AwaitingResult")
            {
                if (!_battleReceiveComplete && Battle != null) _battleReceiveComplete = Battle.DrainAcceptedPackets();
                AdvanceTerminalCompletion(Time.unscaledDeltaTime);
                if (Completed)
                {
                    // Keep the account connection alive while the player reads the result screen.
                    if (!_automated && !_busy && _backend != null && _backend.IsAuthenticated && Time.unscaledTime >= _nextPoll)
                    {
                        _nextPoll = Time.unscaledTime + 5;
                        Run(async () => Profile = await _backend.ProfileAsync(_lifetime.Token));
                    }
                    return;
                }
            }
            float elapsed = _skipElapsedFrame ? 0 : Time.unscaledDeltaTime; _skipElapsedFrame = false;
            _elapsed += elapsed;
            if (_automated && _elapsed >= _deadlineSeconds) { Fail("topology-timeout"); return; }
            if (_resumePending)
            {
                // Admission renewal is async. Do not spend KCP retries on the expired background ticket.
                if (!_busy && _backend != null && Time.unscaledTime >= _nextPoll) { _nextPoll = Time.unscaledTime + 1; Run(PollAsync); }
                return;
            }
            if (State != "Settling" && State != "AwaitingResult") Battle?.Pump(elapsed);
            if (Battle != null)
            {
                if (_automated && State == "Battle" && Battle.State == BattleClientState.Active && Battle.IsPredictionInitialized && Battle.ArenaPhase == ArenaMatchPhase.Active)
                {
                    _acknowledgementStall = Battle.LastAcknowledgedSequence > _observedAcknowledgement ? 0 : _acknowledgementStall + Time.unscaledDeltaTime;
                    _observedAcknowledgement = Battle.LastAcknowledgedSequence;
                    MaxAcknowledgementStallSeconds = Math.Max(MaxAcknowledgementStallSeconds, _acknowledgementStall);
                }
                else { _acknowledgementStall = 0; _observedAcknowledgement = 0; }
                if (State != "Settling" && Battle.State == BattleClientState.Faulted)
                {
                    State = "AwaitingResult";
                    BackendStatus = "战斗连接中断，正在查询原对局状态和结算。";
                }
                if (_automated && State == "Battle" && !_reconnectRequested && Battle.IsPredictionInitialized && Battle.LastAcknowledgedSequence >= 30)
                {
                    _reconnectRequested = true; _priorSession = Battle.SessionId; _priorEntity = Battle.EntityId; _priorAcknowledgement = Battle.LastAcknowledgedSequence; Battle.RequestReconnect();
                    _observedReconnecting = Battle.State == BattleClientState.Reconnecting;
                }
                if (_reconnectRequested && _observedReconnecting && Battle.State == BattleClientState.Active && Battle.LastAcknowledgedSequence > _priorAcknowledgement)
                {
                    if (Battle.EntityId != _priorEntity || Battle.SessionId != _priorSession) { Fail("reconnect-identity-changed"); return; } Reconnected = true;
                }
            }
            if (!_busy && _backend != null && Time.unscaledTime >= _nextPoll)
            {
                _nextPoll = Time.unscaledTime + (Battle == null ? 1f : 5f); Run(PollAsync);
            }
        }

        private void FixedUpdate()
        {
            _view?.RefreshInputBlock();
            if (_paused || _resumePending || Completed || State == "Settling" || State == "AwaitingResult" || Battle == null || Battle.ArenaPhase == ArenaMatchPhase.Finished || Battle.State != BattleClientState.Active || !Battle.IsPredictionInitialized) { _characterInput.Clear(); return; }
            // Authoritative room time prevents backlog after network stalls from scheduling far-future inputs.
            _inputTick = Math.Max(_inputTick + 1, Battle.LastReceivedTick + 1);
            bool acceptInput = HasGameFocus && !_textInputFocused;
            if (!acceptInput || _pointerOverUi) _characterInput.Clear();
            int x = _automated ? ((_inputTick / 60) % 2 == 0 ? 1000 : -1000) : acceptInput ? _manualMove.x : 0;
            int z = !_automated && acceptInput ? _manualMove.y : 0;
            AiNative.Client.Prediction.PredictionPrepareStatus prepared;
            if (Battle.TryGetArenaState(out var state))
            {
                int yaw = 0, pitch = 0;
                if (!_automated && CharacterCamera != null && acceptInput)
                {
                    Vector2Int move = NetworkCharacterInput.RotateMove(new Vector2Int(x, z), CharacterCamera.Yaw);
                    x = move.x; z = move.y;
                    if (!_pointerOverUi)
                    {
                        var presentation = GetComponent<BattleClientCompositionRoot>();
                        if (presentation != null) NetworkCharacterInput.AimDelta(state, presentation.GetAimPoint(), out yaw, out pitch);
                        else NetworkCharacterInput.LookDelta(state, CharacterCamera.Yaw, CharacterCamera.Pitch, out yaw, out pitch);
                    }
                }
                ArenaButtons buttons = !_automated && acceptInput && !_pointerOverUi
                    ? _characterInput.Buttons(_manualFire) : ArenaButtons.None;
                prepared = Battle.PredictAndQueueArenaInput(_inputTick, x, z, yaw, pitch, buttons,
                    _automated ? ArenaWeaponId.Machinegun : _characterInput.Weapon);
            }
            else prepared = Battle.PredictAndQueueInput(_inputTick, x, z);
            if (prepared == AiNative.Client.Prediction.PredictionPrepareStatus.Prepared)
            {
                PreparedInputs++;
                _characterInput.Commit();
            }
        }

        private async Task PollAsync()
        {
            try { await PollCoreAsync(); }
            finally { if (!_disposed) WriteCheckpoint(); }
        }

        private async Task PollCoreAsync()
        {
            if (_paused) return;
            if (!_backend.IsAuthenticated)
            {
                if (Battle != null && (_automated || (_resumePending && _username.Length > 0 && _password.Length > 0))) await ConnectAccountAsync(register: false);
                else { BackendStatus = "后台连接或登录凭证已失效，请重新登录；当前对局继续。"; return; }
            }
            if (_resumePending && Battle != null)
            {
                int generation = _pauseGeneration;
                GateMatch renewed = await _backend.MatchAsync(_requestId, _lifetime.Token);
                if (renewed.Failure == "queue_lost" && renewed.EntryTicket.Length == 0)
                {
                    // A restarted Lobby may forget the queue, while Player still owns the original receipt.
                    // Missing allocation data can never authorize KCP; any supplied conflicting identity is rejected.
                    ValidateOriginalMatch(renewed, allowMissing: true);
                    if (_paused || generation != _pauseGeneration) return;
                    await PollOriginalSettlementAsync();
                    if (!Completed) BackendStatus = "原排队记录已失效，正在等待原对局结算；不会创建新对局。";
                    return;
                }
                ValidateOriginalMatch(renewed);
                if (_paused || generation != _pauseGeneration) return;
                if (renewed.EntryTicket.Length > 0)
                {
                    Battle.ResumeOriginalAllocation(new BattleAdmissionInfo(renewed.RoomId, renewed.BootEpoch, renewed.EntryTicket, renewed.NodeId, PlayerId));
                    if (State != "Settling") State = "Battle";
                    ResumeCompletedCount++;
                    _resumePending = false; BackendStatus = "";
                    return;
                }
                if (renewed.Failure == "room_released") { _resumePending = false; State = "Settling"; }
                else { BackendStatus = "正在等待原对局的新接入凭证。"; return; }
            }
            if (Battle != null)
            {
                if (await PollOriginalSettlementAsync()) return;
                GateMatch refreshed = await _backend.MatchAsync(_requestId, _lifetime.Token);
                if (refreshed.EntryTicket.Length > 0)
                {
                    ValidateOriginalMatch(refreshed);
                    Battle.UpdateAdmission(new BattleAdmissionInfo(refreshed.RoomId, refreshed.BootEpoch, refreshed.EntryTicket, refreshed.NodeId, PlayerId));
                }
                else if (refreshed.Failure == "room_released" && refreshed.MatchId == MatchId && refreshed.RoomId == RoomId && refreshed.BootEpoch == _match.BootEpoch && refreshed.NodeId == _match.NodeId)
                {
                    // The final unreliable snapshot can be lost when the room closes. Only this
                    // allocation's release permits settlement polling; a profile increment is still required.
                    State = "Settling";
                }
                else if (refreshed.Failure == "queue_lost") BackendStatus = "匹配后台已重启；当前对局继续，使用原对局查询结算。";
                else if (refreshed.Failure.Length > 0) Fail("match-ended:" + refreshed.Failure);
                return;
            }
            if (State == "Queued")
            {
                _match = await _backend.MatchAsync(_requestId, _lifetime.Token);
                // A completed response may resume after OnDestroy has already released this flow.
                if (_disposed) return;
                if (_match.Failure.Length > 0)
                {
                    State = "Lobby"; _requestId = "";
                    if (_party != null) _party = await _backend.GetPartyAsync(_party.PartyId, _lifetime.Token);
                    BackendStatus = "排队状态已失效，请重新确认队伍和准备状态。";
                    return;
                }
                if (_match.EntryTicket.Length > 0)
                {
                    int split = _match.Address.LastIndexOf(':'); if (split < 1 || !int.TryParse(_match.Address.Substring(split + 1), out int port)) throw new InvalidDataException("invalid-battle-address");
                    Battle = new BattleClientSession(_match.Address.Substring(0, split), port, new BattleAdmissionInfo(_match.RoomId, _match.BootEpoch, _match.EntryTicket, _match.NodeId, PlayerId));
                    Battle.Start(); State = "Battle";
                }
                return;
            }
            if (State == "Lobby")
            {
                Profile = await _backend.ProfileAsync(_lifetime.Token);
                _invitations = await _backend.InvitationsAsync(_lifetime.Token);
                if (_party != null)
                {
                    _party = await _backend.GetPartyAsync(_party.PartyId, _lifetime.Token);
                    if (_party.QueueRequestId.Length > 0) { _requestId = _party.QueueRequestId; State = "Queued"; _queueStarted = Time.unscaledTime; }
                }
            }
        }

        private async Task<bool> PollOriginalSettlementAsync()
        {
            Profile = await _backend.ProfileAsync(_lifetime.Token); // Also keeps the TCP session active.
            BackendStatus = "";
            GateSettlement settlement;
            try { settlement = await _backend.SettlementAsync(MatchId, _lifetime.Token); }
            catch (InvalidDataException exception) when (exception.Message == "gate-settlement-identity")
            {
                throw new GateCallException("settlement-mismatch-active-battle");
            }
            if (settlement.MatchId != MatchId) throw new GateCallException("settlement-mismatch-active-battle");
            if (settlement.Confirmed) { SettlementConfirmed = true; State = "Settling"; }
            if (Battle.ArenaPhase != ArenaMatchPhase.Finished && State != "Settling") return false;
            State = "Settling";
            if (settlement.Confirmed && Profile.Played == _baselinePlayed + 1)
            {
                if (_automated && !Reconnected) { Fail("reconnect-not-observed"); return true; }
                _resumePending = false; Error = ""; Completed = true; State = "Finished"; WriteEvidence(true);
            }
            else if (Profile.Played > _baselinePlayed + 1) Fail("unexpected-profile-increment");
            return true;
        }

        private void ValidateOriginalMatch(GateMatch candidate, bool allowMissing = false)
        {
            if (_match == null || Mismatch(candidate.MatchId, _match.MatchId, allowMissing) || Mismatch(candidate.RoomId, _match.RoomId, allowMissing) ||
                Mismatch(candidate.NodeId, _match.NodeId, allowMissing) || Mismatch(candidate.BootEpoch, _match.BootEpoch, allowMissing))
                throw new GateCallException("allocation-mismatch-active-battle");
        }
        private static bool Mismatch(string candidate, string original, bool allowMissing) => candidate != original && !(allowMissing && candidate.Length == 0);

        private void OnApplicationPause(bool paused)
        {
            if (_disposed || Completed || paused == _paused) return; // Initial false and duplicate callbacks have no effect.
            _paused = paused;
            if (paused) { PauseCount++; _pauseGeneration++; ClearManualInput(); }
            else
            {
                ResumeCount++; _skipElapsedFrame = true;
                _resumePending = Battle != null; _nextPoll = 0;
                if (_resumePending) BackendStatus = "正在验证原对局并恢复连接。";
            }
            WriteCheckpoint();
        }

        private async void Run(Func<Task> action, bool foreground = false)
        {
            if (_busy || _disposed) return; _busy = true;
            try { await action(); }
            catch (OperationCanceledException)
            {
                if (!_disposed && Battle != null) { BackendStatus = "后台请求超时；当前对局继续。"; _nextPoll = Time.unscaledTime + 1; }
                else if (!_disposed)
                {
                    if (foreground) Error = "操作超时，请重试。";
                    else BackendStatus = "后台请求超时，正在重试。";
                    if (State == "Authenticating") State = "Disconnected";
                }
            }
            catch (Exception exception)
            {
                if (_disposed) return;
                string code = exception is GateCallException gate ? gate.Code : exception.GetType().Name;
                if (Battle != null && (code == "allocation-mismatch-active-battle" || code == "account-mismatch-active-battle" || code == "settlement-mismatch-active-battle"))
                {
                    Error = code; BackendStatus = "恢复身份与原对局不一致，已拒绝更换玩家或对局。"; _nextPoll = Time.unscaledTime + 1; return;
                }
                if (_backend != null && (code == "session-expired" || code == "invalid_session" || code == "invalid-session" || code == "unauthorized"))
                {
                    BackendStatus = "登录凭证已失效，请重新登录；当前对局继续，结算会重试。";
                    _nextPoll = Time.unscaledTime + 1;
                    return;
                }
                if (Battle == null && code == "party_not_found")
                {
                    _party = null; _requestId = ""; State = "Lobby"; BackendStatus = "大厅状态已重置，请重新建队和准备。"; return;
                }
                if (Battle == null && (code == "queue_not_found" || code == "queue_expired"))
                {
                    _requestId = ""; State = "Lobby"; BackendStatus = "匹配状态已重置，请重新确认队伍和准备状态。"; return;
                }
                if (!foreground && Battle != null && (exception is IOException || exception is SocketException || exception is TimeoutException || code == "timeout" || code == "unavailable" || code == "authority_unavailable" || code == "allocation_uncertain" || code == "queue_expired" || code == "queue_not_found"))
                {
                    BackendStatus = "后台暂时不可用；当前对局继续，结算会重试。"; _nextPoll = Time.unscaledTime + 1; return;
                }
                if (_automated) Fail(code);
                else
                {
                    if (foreground) Error = code;
                    else BackendStatus = "后台刷新失败：" + ErrorMessage(code);
                    // A failed cancel/queue request must not pretend the server released the queue.
                    if (State == "Authenticating") State = _backend?.IsAuthenticated == true ? "Lobby" : "Disconnected";
                }
            }
            finally
            {
                _busy = false;
                if (foreground) _uiBusy = false;
                if (!_disposed && _pendingAction != null)
                {
                    var pending = _pendingAction; _pendingAction = null;
                    Run(pending, foreground: true);
                }
            }
        }
        private void Fail(string error) { Error = error; State = "Failed"; if (_automated) { Completed = true; WriteEvidence(false); } }

        private static string ErrorMessage(string code)
        {
            switch (code)
            {
                case "invalid_credentials": return "账号或密码不符合要求，或登录信息不正确。账号须为 3–32 位英文字母、数字或下划线；密码须为 6–32 位。";
                case "username_exists": return "账号已存在，请使用“登录 / 恢复后台连接”，或换一个账号注册。";
                case "unavailable": case "authority_unavailable": return "服务暂时不可用，请确认服务器全部就绪后重试。";
                case "SocketException": case "IOException": return "连接失败，请检查服务器是否启动，以及地址、端口和 TLS 设置。";
                case "timeout": case "TimeoutException": return "请求超时，请检查服务器是否就绪或停在 VS 断点，然后重试。";
                case "invalid-address": return "请输入有效的服务器地址和端口（1–65535）。";
                case "version_conflict": return "队伍状态刚刚发生变化，请稍后重试。";
                case "leader_required": return "这项操作需要队长执行。";
                case "party_not_ready": return "请等待所有队员准备。";
                case "allocation_claimed": return "服务器正在分配房间，暂时无法取消，请等待结果。";
                case "invitation_expired": return "邀请已失效，请对方重新邀请。";
                case "state-changed": return "当前状态已变化，请按界面提示操作。";
                case "match-still-active": return "服务器尚未结束原对局，请稍后再返回大厅。";
                default: return code;
            }
        }

        private void AdvanceTerminalCompletion(float elapsedSeconds)
        {
            if (!_successExitPending) return;
            if (Battle?.ArenaPhase == ArenaMatchPhase.Finished && _battleReceiveComplete)
            {
                _successExitPending = false;
                WriteEvidence(true);
                return;
            }
            _successExitElapsed += elapsedSeconds;
            if (_successExitElapsed < TerminalReceiveExitTimeoutSeconds) return;
            _successExitPending = false;
            Error = "terminal-receive-timeout";
            WriteEvidence(false);
        }

        private void WriteEvidence(bool success)
        {
            if (_resultPath.Length == 0) return;
            // Settlement remains completed immediately. Only the automated evidence/process exit waits.
            if (success && Battle != null && (Battle.ArenaPhase != ArenaMatchPhase.Finished || !_battleReceiveComplete))
            {
                if (!_successExitPending) { _successExitPending = true; _successExitElapsed = 0; }
                WriteCheckpoint();
                return;
            }
            _evidenceSuccess = success;
            if (!WriteCheckpoint(success)) { success = false; _evidenceSuccess = false; }
#if !UNITY_EDITOR
            global::UnityEngine.Application.Quit(success ? 0 : 1);
#endif
        }
        private bool WriteCheckpoint(bool success = false)
        {
            if (_resultPath.Length == 0) return true;
            try
            {
                string fullPath = Path.GetFullPath(_resultPath); Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
                File.WriteAllText(fullPath, JsonUtility.ToJson(new Evidence { success = success || _evidenceSuccess, completed = Completed, finalReceived = Battle?.ArenaPhase == ArenaMatchPhase.Finished, receiveDrainCompleted = _battleReceiveComplete, state = State, runId = _runId, paused = _paused,
                    pauseCount = PauseCount, resumeCount = ResumeCount, resumeCompletedCount = ResumeCompletedCount, resumePending = _resumePending,
                    error = Error, playerId = PlayerId, matchId = MatchId, roomId = RoomId, nodeId = _match?.NodeId ?? "", bootEpoch = _match?.BootEpoch ?? "", reconnected = Reconnected, settlementConfirmed = SettlementConfirmed, backendReconnectCount = BackendReconnectCount, preparedInputs = PreparedInputs, maxAcknowledgementStallSeconds = MaxAcknowledgementStallSeconds, played = Profile?.Played ?? 0, lastTick = Battle?.LastReceivedTick.ToString() ?? "0", lastAcknowledgedSequence = Battle?.LastAcknowledgedSequence ?? 0 }, true));
                return true;
            }
            catch (Exception exception) { Error = "evidence-write:" + exception.GetType().Name; return false; }
        }
        [Serializable] private sealed class Evidence { public bool success, reconnected, settlementConfirmed, completed, paused, resumePending, finalReceived, receiveDrainCompleted; public string error, playerId, matchId, roomId, nodeId, bootEpoch, lastTick, state, runId; public long played, preparedInputs; public uint lastAcknowledgedSequence; public int backendReconnectCount, pauseCount, resumeCount, resumeCompletedCount; public float maxAcknowledgementStallSeconds; }
        private async void OnDestroy()
        {
            if (_disposed) return;
            _disposed = true; _pendingAction = null; _password = "";
            if (_view != null) Destroy(_view.gameObject);
            _lifetime.Cancel(); _backend?.Dispose();
            try { if (Battle != null) await Battle.DisposeAsync(); }
            finally { _lifetime.Dispose(); }
        }
    }
}
