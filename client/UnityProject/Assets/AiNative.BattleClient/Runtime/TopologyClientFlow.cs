using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using AiNative.Client.Fantasy;
using AiNative.Gameplay;
using UnityEngine;

namespace AiNative.Client.Application
{
    /// <summary>Product composition for Gate account/party/match and direct authoritative Battle gameplay.</summary>
    public sealed class TopologyClientFlow : MonoBehaviour
    {
        private GateBackendSession _backend;
        private GateConnectionOptions _options;
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private GateParty _party;
        private GateParty[] _invitations = Array.Empty<GateParty>();
        private GateMatch _match;
        private string _requestId = "", _username = "", _password = "", _invitePlayer = "";
        private string _host = "127.0.0.1", _port = "23001", _pin = "", _resultPath = "";
        private bool _tls, _busy, _automated, _started, _disposed, _reconnectRequested, _observedReconnecting;
        private bool _paused, _resumePending, _skipElapsedFrame;
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
                else State = previousState == "Settling" ? "Settling" : "Battle";
                Error = ""; BackendStatus = "";
            }
            catch { candidate.Dispose(); throw; }
        }

        private async Task QueueAsync()
        {
            _requestId = Guid.NewGuid().ToString("N");
            await _backend.QueueAsync(_party, _requestId, _lifetime.Token); State = "Queued";
        }

        private void Update()
        {
            if (_disposed || _paused) return;
            if (Completed || State == "Settling" || State == "AwaitingResult")
            {
                if (!_battleReceiveComplete && Battle != null) _battleReceiveComplete = Battle.DrainAcceptedPackets();
                AdvanceTerminalCompletion(Time.unscaledDeltaTime);
                if (Completed) return;
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
            if (_paused || _resumePending || Completed || State == "Settling" || State == "AwaitingResult" || Battle == null || Battle.ArenaPhase == ArenaMatchPhase.Finished || Battle.State != BattleClientState.Active || !Battle.IsPredictionInitialized) return;
            // Authoritative room time prevents backlog after network stalls from scheduling far-future inputs.
            _inputTick = Math.Max(_inputTick + 1, Battle.LastReceivedTick + 1);
            int x = _automated ? ((_inputTick / 60) % 2 == 0 ? 1000 : -1000) : (Input.GetKey(KeyCode.D) ? 1000 : 0) - (Input.GetKey(KeyCode.A) ? 1000 : 0);
            int z = _automated ? 0 : (Input.GetKey(KeyCode.W) ? 1000 : 0) - (Input.GetKey(KeyCode.S) ? 1000 : 0);
            var prepared = Battle.TryGetArenaState(out _) ? Battle.PredictAndQueueArenaInput(_inputTick, x, z, 0, 0, Input.GetMouseButton(0) ? ArenaButtons.Fire : ArenaButtons.None, ArenaWeaponId.Machinegun) : Battle.PredictAndQueueInput(_inputTick, x, z);
            if (prepared == AiNative.Client.Prediction.PredictionPrepareStatus.Prepared) PreparedInputs++;
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
                    if (_party.QueueRequestId.Length > 0) { _requestId = _party.QueueRequestId; State = "Queued"; }
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
            if (paused) { PauseCount++; _pauseGeneration++; }
            else
            {
                ResumeCount++; _skipElapsedFrame = true;
                _resumePending = Battle != null; _nextPoll = 0;
                if (_resumePending) BackendStatus = "正在验证原对局并恢复连接。";
            }
            WriteCheckpoint();
        }

        private async void Run(Func<Task> action)
        {
            if (_busy || _disposed) return; _busy = true;
            try { await action(); }
            catch (OperationCanceledException)
            {
                if (!_disposed && Battle != null) { BackendStatus = "后台请求超时；当前对局继续。"; _nextPoll = Time.unscaledTime + 1; }
                else if (!_disposed) Error = "操作超时，请重试。";
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
                if (Battle != null && (exception is IOException || exception is SocketException || exception is TimeoutException || code == "timeout" || code == "unavailable" || code == "authority_unavailable" || code == "allocation_uncertain" || code == "queue_expired" || code == "queue_not_found"))
                {
                    BackendStatus = "后台暂时不可用；当前对局继续，结算会重试。"; _nextPoll = Time.unscaledTime + 1; return;
                }
                if (_automated) Fail(code);
                else { Error = code; if (Battle == null) State = _backend?.PlayerId.Length > 0 ? "Lobby" : "Disconnected"; }
            }
            finally { _busy = false; }
        }
        private void Fail(string error) { Error = error; State = "Failed"; if (_automated) { Completed = true; WriteEvidence(false); } }

        private void OnGUI()
        {
            GUILayout.BeginArea(new Rect(16, 115, 600, Math.Max(450, Screen.height - 130)), GUI.skin.box);
            GUILayout.Label("在线对战  ·  " + State);
            if (Error.Length > 0) GUILayout.Label(Error);
            if (BackendStatus.Length > 0) GUILayout.Label(BackendStatus);
            GUI.enabled = !_busy && !Completed;
            if (AuthenticationRequired)
            {
                GUILayout.Label("接入地址"); _host = GUILayout.TextField(_host); _port = GUILayout.TextField(_port); _tls = GUILayout.Toggle(_tls, "使用 TLS");
                if (_tls) { GUILayout.Label("测试证书 SHA256（正式证书可留空）"); _pin = GUILayout.TextField(_pin); }
                GUILayout.Label("账号"); _username = GUILayout.TextField(_username, 64); GUILayout.Label("密码"); _password = GUILayout.PasswordField(_password, '*', 256);
                if (Battle == null && GUILayout.Button("注册并登录")) Run(() => ConnectAccountAsync(true));
                if (GUILayout.Button("登录 / 恢复后台连接")) Run(() => ConnectAccountAsync(false));
            }
            else
            {
                GUILayout.Label("玩家 ID（邀请时使用）"); GUILayout.TextField(PlayerId);
                if (Profile != null) GUILayout.Label($"已完成 {Profile.Played} 局 · 胜利 {Profile.Won} · 击杀 {Profile.Kills}");
                if (State == "Lobby")
                {
                    if (_party == null)
                    {
                        if (GUILayout.Button("创建队伍")) Run(async () => _party = await _backend.CreatePartyAsync(_lifetime.Token));
                        foreach (GateParty invitation in _invitations) if (GUILayout.Button("接受邀请：" + invitation.LeaderId)) Run(async () => _party = await _backend.AcceptAsync(invitation, _lifetime.Token));
                    }
                    else
                    {
                        GUILayout.Label("队伍：" + _party.PartyId);
                        foreach (GatePartyMember member in _party.Members) GUILayout.Label(member.PlayerId + (member.Ready ? " 已准备" : " 未准备"));
                        _invitePlayer = GUILayout.TextField(_invitePlayer);
                        if (GUILayout.Button("邀请玩家")) Run(async () => _party = await _backend.InviteAsync(_party, _invitePlayer, _lifetime.Token));
                        if (GUILayout.Button("准备")) Run(async () => _party = await _backend.ReadyAsync(_party, true, _lifetime.Token));
                        if (GUILayout.Button("取消准备")) Run(async () => _party = await _backend.ReadyAsync(_party, false, _lifetime.Token));
                        if (GUILayout.Button("开始匹配")) Run(QueueAsync);
                        if (GUILayout.Button("退出队伍")) Run(async () => { await _backend.LeaveAsync(_party, _lifetime.Token); _party = null; });
                    }
                }
                if (State == "Queued" && GUILayout.Button("取消匹配")) Run(async () => { await _backend.CancelQueueAsync(_requestId, _lifetime.Token); _requestId = ""; State = "Lobby"; });
                if (Battle != null)
                {
                    GUILayout.Label("房间：" + RoomId);
                    if (GUILayout.Button("重新连接战斗")) { _resumePending = true; _nextPoll = 0; }
                }
            }
            GUI.enabled = true; GUILayout.EndArea();
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
            _disposed = true; _lifetime.Cancel(); _backend?.Dispose(); if (Battle != null) await Battle.DisposeAsync(); _lifetime.Dispose();
        }
    }
}
