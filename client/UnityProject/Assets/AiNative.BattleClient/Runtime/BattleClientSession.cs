using System;
using System.Threading;
using System.Threading.Tasks;
using AiNative.Client.Fantasy;
using AiNative.Client.Prediction;
using AiNative.Client.Protocol;
using AiNative.Gameplay;
using AiNative.Realtime;

namespace AiNative.Client.Application
{
    /// <summary>Gate 为单次 Battle 分配签发的不可变凭证；票据不得写入日志。</summary>
    public sealed class BattleAdmissionInfo
    {
        public BattleAdmissionInfo(
            string roomId,
            string bootEpoch,
            string entryTicket,
            string nodeId = "",
            string playerId = ""
        )
        {
            if (string.IsNullOrWhiteSpace(roomId))
            {
                throw new ArgumentException("Global room is required.", nameof(roomId));
            }

            if (string.IsNullOrWhiteSpace(bootEpoch))
            {
                throw new ArgumentException("Boot epoch is required.", nameof(bootEpoch));
            }

            if (string.IsNullOrWhiteSpace(entryTicket))
            {
                throw new ArgumentException("Entry ticket is required.", nameof(entryTicket));
            }

            RoomId = roomId;
            BootEpoch = bootEpoch;
            EntryTicket = entryTicket;
            NodeId = nodeId ?? string.Empty;
            PlayerId = playerId ?? string.Empty;
        }

        public string RoomId { get; }
        public string BootEpoch { get; }
        public string EntryTicket { get; }
        public string NodeId { get; }
        public string PlayerId { get; }
    }

    public enum BattleClientState : byte
    {
        Connecting = 0,
        LoggingIn = 1,
        JoiningRoom = 2,
        Active = 3,
        Reconnecting = 4,
        Faulted = 5,
        Disposed = 6,
    }

    internal readonly struct BattleTransportConnection
    {
        internal BattleTransportConnection(
            IRealtimeTransport transport,
            Func<uint, bool> tryAdvanceConnectionEpoch
        )
        {
            Transport = transport;
            TryAdvanceConnectionEpoch = tryAdvanceConnectionEpoch;
        }

        internal IRealtimeTransport Transport { get; }
        internal Func<uint, bool> TryAdvanceConnectionEpoch { get; }
        internal bool IsConnected => Transport is not null;
    }

    internal interface IBattleTransportConnector
    {
        ValueTask<BattleTransportConnection> ConnectAsync(
            string host,
            int port,
            int timeoutMilliseconds,
            CancellationToken cancellationToken
        );
    }

    internal sealed class FantasyBattleTransportConnector : IBattleTransportConnector
    {
        public async ValueTask<BattleTransportConnection> ConnectAsync(
            string host,
            int port,
            int timeoutMilliseconds,
            CancellationToken cancellationToken
        )
        {
            FantasyKcpConnectResult result = await FantasyKcpRealtimeTransport.ConnectAsync(
                new FantasyKcpTransportOptions(host, port, timeoutMilliseconds),
                cancellationToken
            );
            if (result.Status != FantasyKcpConnectStatus.Connected || result.Transport is null)
            {
                // 只记录传输建立阶段；此处尚未发送登录票据，不改变失败返回或重试语义。
                UnityEngine.Debug.LogWarning(
                    $"Battle connection attempt failed: status={result.Status}; "
                        + $"timeoutMilliseconds={timeoutMilliseconds}; "
                        + $"callerCancelled={cancellationToken.IsCancellationRequested}; "
                        + $"error={result.Error}"
                );
                return default;
            }

            return new BattleTransportConnection(
                result.Transport,
                result.Transport.TryAdvanceConnectionEpoch
            );
        }
    }

    /// <summary>
    /// 产品层协议与预测组合；在 Update 调用 <see cref="Pump"/>，
    /// 在 FixedUpdate 调用 <see cref="PredictAndQueueInput"/>。
    /// </summary>
    public sealed class BattleClientSession : IAsyncDisposable
    {
        public const uint ProtocolMajor = 1;
        public const uint RoomId = 1;
        public const uint TickRate = 60;
        public const int PhaseTimeoutMilliseconds = 5000;
        public const int DefaultInputRingCapacity = 256;
        public const int CombatEventCapacity = 256;
        private static readonly float[] _reconnectDelaySeconds = { 0.25f, 0.5f, 1.0f };
        private readonly string _host;
        private readonly int _port;
        private readonly string _clientBuild;
        private readonly IBattleTransportConnector _connector;
        private BattleAdmissionInfo _admission;
        private bool _topologyReconnecting;
        private uint _localRoomId;
        private readonly ReplaceableRealtimeTransportSlot _transportSlot =
            new ReplaceableRealtimeTransportSlot();
        private readonly InputFrameRing _inputRing;
        private readonly ArenaRemotePresentation _remotePresentation =
            new ArenaRemotePresentation();
        private readonly PresentationCorrectionSmoother _presentation =
            new PresentationCorrectionSmoother();
        private readonly IBattleProtocolCodec _codec;
        private readonly BattlePacketBuffer _decoded = new BattlePacketBuffer();
        private readonly byte[] _receiveBuffer;
        private readonly byte[] _controlBuffer;

        // 发送完成前由传输层借用此内存；重连清空环形队列不得影响它。
        private readonly byte[] _inputSendBuffer;
        private Task<SendResult> _pendingInputSend;
        private int _pendingInputLength;
        private ulong _inputGeneration;
        private ulong _pendingInputGeneration;
        private CancellationTokenSource _connectCancellation;
        private Task<BattleTransportConnection> _connectTask;
        private ClientPredictionAdapter _prediction;
        private ArenaClientPredictionAdapter _arenaPrediction;
        private float _phaseElapsedSeconds;
        private float _retryDelayRemainingSeconds;
        private int _reconnectAttempts;
        private bool _awaitingReconnectResponse;
        private bool _disposed;
        private bool _sendFailed;
        private string _faultReason = string.Empty;
        private ulong _sessionId;
        private uint _entityId;
        private uint _connectionEpoch;
        private uint _initialConnectionEpoch;
        private ulong _lastReceivedTick;
        private uint _lastAcknowledgedSequence;
        private long _droppedInputFrames;
        private ArenaPlayerState _arenaState;
        private ArenaMatchPhase _arenaPhase;
        private uint _arenaRemainingTicks;
        private uint _arenaLeaderEntityId;
        private bool _hasArenaState;
        private readonly ArenaPresentationEvent[] _combatEvents = new ArenaPresentationEvent[
            CombatEventCapacity
        ];
        private int _combatEventHead;
        private int _combatEventCount;
        private uint _lastCombatSequence;
        private ulong _lastCombatTick;
        private bool _hasCombatSequence;
        public long DroppedCombatEvents { get; private set; }

        /// <summary>在 Pump 所属线程消费权威反馈；队列溢出时丢弃最早反馈。</summary>
        public int DrainCombatEvents(Span<ArenaPresentationEvent> output)
        {
            int count = Math.Min(output.Length, _combatEventCount);
            for (int i = 0; i < count; i++)
            {
                output[i] = _combatEvents[_combatEventHead];
                _combatEvents[_combatEventHead] = default;
                _combatEventHead = (_combatEventHead + 1) % CombatEventCapacity;
            }

            _combatEventCount -= count;
            return count;
        }

        private void ClearCombatEvents()
        {
            Array.Clear(_combatEvents, 0, _combatEvents.Length);
            _combatEventHead = _combatEventCount = 0;
            // 序号属于房间；同一分配重连时保留序号水位。
        }

        private void ApplyCombatEvent(BattlePacketBuffer message, in ReceivedPacket packet)
        {
            var item = message.CombatEvent;
            if (
                packet.ConnectionEpoch != _connectionEpoch
                || !packet.Channel.Equals(_codec.EventChannel)
                || (
                    _hasCombatSequence
                    && (
                        unchecked((int)(item.Sequence - _lastCombatSequence)) <= 0
                        || item.Tick < _lastCombatTick
                    )
                )
            )
            {
                return;
            }

            _hasCombatSequence = true;
            _lastCombatSequence = item.Sequence;
            _lastCombatTick = item.Tick;
            if (_combatEventCount == CombatEventCapacity)
            {
                _combatEventHead = (_combatEventHead + 1) % CombatEventCapacity;
                _combatEventCount--;
                DroppedCombatEvents++;
            }

            _combatEvents[(_combatEventHead + _combatEventCount) % CombatEventCapacity] = item;
            _combatEventCount++;
        }

        public BattleClientSession(
            IBattleProtocolCodec codec,
            string host,
            int port,
            string clientBuild = "ws26",
            int inputRingCapacity = DefaultInputRingCapacity
        )
            : this(
                codec,
                host,
                port,
                clientBuild,
                inputRingCapacity,
                new FantasyBattleTransportConnector()
            ) { }

        public BattleClientSession(
            IBattleProtocolCodec codec,
            string host,
            int port,
            BattleAdmissionInfo admission,
            string clientBuild = "topology",
            int inputRingCapacity = DefaultInputRingCapacity
        )
            : this(
                codec,
                host,
                port,
                clientBuild,
                inputRingCapacity,
                new FantasyBattleTransportConnector(),
                admission ?? throw new ArgumentNullException(nameof(admission))
            ) { }

        internal BattleClientSession(
            IBattleProtocolCodec codec,
            string host,
            int port,
            string clientBuild,
            int inputRingCapacity,
            IBattleTransportConnector connector,
            BattleAdmissionInfo admission = null
        )
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                throw new ArgumentException("Host is required.", nameof(host));
            }

            if (port is < 1 or > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(port));
            }

            _codec = codec ?? throw new ArgumentNullException(nameof(codec));
            _receiveBuffer = new byte[codec.MaximumFrameBytes];
            _controlBuffer = new byte[codec.MaximumFrameBytes];
            _inputSendBuffer = new byte[codec.MaximumInputFrameBytes];
            _connector = connector ?? throw new ArgumentNullException(nameof(connector));
            _admission = admission;
            _host = host;
            _port = port;
            _clientBuild = clientBuild ?? string.Empty;
            _inputRing = new InputFrameRing(inputRingCapacity, codec.MaximumInputFrameBytes);
            State = BattleClientState.Connecting;
        }

        public BattleClientState State { get; private set; }
        public string FaultReason => _faultReason;
        public ulong SessionId => _sessionId;
        public uint EntityId => _entityId;

        public int AdvanceRemotePresentation(
            float deltaSeconds,
            Span<ArenaRemotePose> destination
        ) => _remotePresentation.Advance(deltaSeconds, destination);

        public uint ConnectionEpoch => _connectionEpoch;
        public uint InitialConnectionEpoch => _initialConnectionEpoch;
        public uint LocalRoomId => _localRoomId;
        public bool UsesTopologyAdmission => _admission is not null;

        /// <summary>在不改变房间分配的前提下刷新短期凭证；只能在 Pump 所属线程调用。</summary>
        public void UpdateAdmission(BattleAdmissionInfo admission)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(BattleClientSession));
            }

            if (admission is null)
            {
                throw new ArgumentNullException(nameof(admission));
            }

            if (
                _admission is null
                || !string.Equals(_admission.RoomId, admission.RoomId, StringComparison.Ordinal)
                || !string.Equals(
                    _admission.BootEpoch,
                    admission.BootEpoch,
                    StringComparison.Ordinal
                )
                || !string.Equals(_admission.NodeId, admission.NodeId, StringComparison.Ordinal)
                || !string.Equals(_admission.PlayerId, admission.PlayerId, StringComparison.Ordinal)
            )
            {
                throw new ArgumentException(
                    "Admission refresh must retain the same room, boot, node and player.",
                    nameof(admission)
                );
            }

            _admission = admission;
        }

        public ulong LastReceivedTick => _lastReceivedTick;
        public uint LastAcknowledgedSequence => _lastAcknowledgedSequence;
        public long DroppedInputFrames => _droppedInputFrames;
        public ArenaMatchPhase ArenaPhase => _arenaPhase;
        public uint ArenaRemainingTicks => _arenaRemainingTicks;
        public uint ArenaLeaderEntityId => _arenaLeaderEntityId;
        private bool IsArenaFinished => _hasArenaState && _arenaPhase == ArenaMatchPhase.Finished;

        public bool TryGetArenaState(out ArenaPlayerState state)
        {
            state = _arenaState;
            return _hasArenaState;
        }

        public int QueuedInputFrames => _inputRing.Count;
        public bool IsPredictionInitialized =>
            _arenaPrediction?.IsInitialized == true || _prediction?.IsInitialized == true;
        public PredictionDiagnostics PredictionDiagnostics => _prediction?.Diagnostics ?? default;
        public PresentationCorrectionDiagnostics PresentationDiagnostics =>
            _presentation.Diagnostics;

        public bool ResetPredictionDiagnostics()
        {
            if (_disposed || _prediction is null || !_prediction.IsInitialized)
            {
                return false;
            }

            _prediction.ResetDiagnostics();
            return true;
        }

        public bool ResetPresentationDiagnostics()
        {
            if (_disposed || !_presentation.IsInitialized)
            {
                return false;
            }

            _presentation.ResetDiagnostics();
            return true;
        }

        public bool TryAdvancePresentation(
            float unscaledDeltaSeconds,
            out PresentationPosition position
        )
        {
            if (!_disposed && _arenaPrediction?.IsInitialized == true)
            {
                ArenaPlayerState arena = _arenaPrediction.Current;
                position = _presentation.Advance(ToKinematic(arena), unscaledDeltaSeconds);
                return true;
            }

            if (
                _disposed
                || _prediction is null
                || !_prediction.TryGetPredictedState(out KinematicState simulationState)
            )
            {
                position = default;
                return false;
            }

            position = _presentation.Advance(simulationState, unscaledDeltaSeconds);
            return true;
        }

        internal ClientPredictionAdapter PredictionAdapter => _prediction;

        public void Start()
        {
            if (_disposed || _connectTask is not null || State != BattleClientState.Connecting)
            {
                return;
            }

            BeginConnect();
        }

        public void Pump(float unscaledDeltaSeconds)
        {
            if (_disposed)
            {
                return;
            }

            if (unscaledDeltaSeconds < 0 || float.IsNaN(unscaledDeltaSeconds))
            {
                throw new ArgumentOutOfRangeException(nameof(unscaledDeltaSeconds));
            }

            if (_connectTask is not null && _connectTask.IsCompleted)
            {
                CompleteConnect();
            }

            if (
                State == BattleClientState.Reconnecting
                && _connectTask is null
                && !_awaitingReconnectResponse
                && _retryDelayRemainingSeconds > 0
            )
            {
                _retryDelayRemainingSeconds -= unscaledDeltaSeconds;
                if (_retryDelayRemainingSeconds <= 0)
                {
                    BeginConnect();
                }
            }

            // 先观察关闭状态再消费队列；此边界之后不再接收新包。
            // 若关闭与本次 Pump 并发，保留所有权，直到下一次有界接收完成。
            bool reconnectPending =
                _sendFailed
                || _transportSlot.State is TransportState.Closed or TransportState.Faulted;
            bool inboundDrained = PumpReceive();
            if (
                reconnectPending
                && inboundDrained
                && State == BattleClientState.Active
                && !IsArenaFinished
            )
            {
                _sendFailed = false;
                BeginReconnect();
            }

            if (
                State == BattleClientState.Active
                && !IsArenaFinished
                && !_sendFailed
                && _transportSlot.State == TransportState.Connected
            )
            {
                FlushInputRing();
            }

            if (
                State is BattleClientState.LoggingIn or BattleClientState.JoiningRoom
                || (State == BattleClientState.Reconnecting && _awaitingReconnectResponse)
            )
            {
                _phaseElapsedSeconds += unscaledDeltaSeconds;
                if (_phaseElapsedSeconds >= PhaseTimeoutMilliseconds / 1000f)
                {
                    if (State == BattleClientState.Reconnecting || _topologyReconnecting)
                    {
                        ScheduleReconnectRetry("Reconnect response timed out.");
                    }
                    else
                    {
                        Fail("Protocol handshake timed out.");
                    }
                }
            }
        }

        public PredictionPrepareStatus PredictAndQueueInput(
            ulong roomTick,
            int moveXMilli,
            int moveZMilli
        )
        {
            if (_arenaPrediction?.IsInitialized == true)
            {
                return PredictAndQueueArenaInput(
                    roomTick,
                    moveXMilli,
                    moveZMilli,
                    0,
                    0,
                    ArenaButtons.None,
                    _arenaPrediction.Current.Weapon
                );
            }

            if (_disposed)
            {
                return PredictionPrepareStatus.Disposed;
            }

            if (
                State != BattleClientState.Active
                || _prediction is null
                || !_prediction.IsInitialized
            )
            {
                return PredictionPrepareStatus.NotInitialized;
            }

            if (!_inputRing.TryGetWriteBuffer(out byte[] buffer))
            {
                _droppedInputFrames++;
                return PredictionPrepareStatus.BufferTooSmall;
            }

            PredictionPrepareResult result = _prediction.PrepareInput(
                roomTick,
                moveXMilli,
                moveZMilli,
                buffer
            );
            if (result.Status == PredictionPrepareStatus.Prepared)
            {
                _inputRing.CommitWrite(result.WrittenBytes);
            }

            return result.Status;
        }

        /// <summary>使用最新权威 Tick 和共享 Arena 预测，将输入命令加入队列。</summary>
        public PredictionPrepareStatus PredictAndQueueArenaInput(
            ulong roomTick,
            int moveXMilli,
            int moveZMilli,
            int lookYawMilli,
            int lookPitchMilli,
            ArenaButtons buttons,
            ArenaWeaponId weapon
        )
        {
            if (_disposed)
            {
                return PredictionPrepareStatus.Disposed;
            }

            if (State != BattleClientState.Active || !IsPredictionInitialized || IsArenaFinished)
            {
                return PredictionPrepareStatus.NotInitialized;
            }

            if (!_inputRing.TryGetWriteBuffer(out byte[] buffer))
            {
                _droppedInputFrames++;
                return PredictionPrepareStatus.BufferTooSmall;
            }

            if (_arenaPrediction?.IsInitialized != true)
            {
                return PredictionPrepareStatus.NotInitialized;
            }

            ulong stampedTick = checked(_lastReceivedTick + 1);
            ArenaPredictionPrepareResult predicted = _arenaPrediction.PrepareInput(
                stampedTick,
                moveXMilli,
                moveZMilli,
                lookYawMilli,
                lookPitchMilli,
                buttons,
                weapon,
                buffer
            );
            if (predicted.Status == ArenaPredictionPrepareStatus.Prepared)
            {
                _arenaState = predicted.PredictedState;
                _inputRing.CommitWrite(predicted.WrittenBytes);
            }

            return (PredictionPrepareStatus)predicted.Status;
        }

        public void RequestReconnect()
        {
            if (_disposed || State != BattleClientState.Active)
            {
                return;
            }

            BeginReconnect();
        }

        /// <summary>仅在 Gate 续签准入后恢复原拓扑分配。
        /// 只能在 Pump 所属线程调用；连接重试耗尽后可恢复，无需创建新比赛。</summary>
        // 原分配重连不创建新房间，身份与节点 epoch 约束见 Docs/ADR/0017-single-region-service-topology.md。
        public bool ResumeOriginalAllocation(BattleAdmissionInfo admission)
        {
            if (admission == null || string.IsNullOrEmpty(admission.EntryTicket))
            {
                throw new ArgumentException(
                    "A renewed entry ticket is required.",
                    nameof(admission)
                );
            }

            UpdateAdmission(admission); // Validate identity before altering any connection or prediction state.
            if (IsArenaFinished)
            {
                return false;
            }

            _connectCancellation?.Cancel();
            Task<BattleTransportConnection> abandoned = _connectTask;
            _connectTask = null;
            if (abandoned != null)
            {
                _ = DisposeAbandonedConnectionAsync(abandoned);
            }

            State = BattleClientState.Reconnecting;
            _faultReason = "";
            _sendFailed = false;
            _presentation.ResetState();
            _remotePresentation.Reset();
            ClearCombatEvents();
            _topologyReconnecting = true;
            // 旧会话挂起前未发送的命令不得进入新认证连接。
            ClearInputRing();
            if (_prediction != null)
            {
                _ = _prediction.DisposeAsync();
            }

            if (_arenaPrediction != null)
            {
                _ = _arenaPrediction.DisposeAsync();
            }

            _prediction = null;
            _arenaPrediction = null;
            _hasArenaState = false;
            _arenaState = default;
            _arenaPhase = default;
            _arenaRemainingTicks = 0;
            _arenaLeaderEntityId = 0;
            _lastAcknowledgedSequence = 0;
            _lastReceivedTick = 0;
            _sessionId = 0;
            _entityId = 0;
            _localRoomId = 0;
            _awaitingReconnectResponse = false;
            _phaseElapsedSeconds = 0;
            _reconnectAttempts = 1;
            _retryDelayRemainingSeconds = _reconnectDelaySeconds[0];
            _transportSlot.DetachAndDispose();
            return true;
        }

        private static async Task DisposeAbandonedConnectionAsync(
            Task<BattleTransportConnection> abandoned
        )
        {
            try
            {
                BattleTransportConnection connection = await abandoned;
                if (connection.Transport != null)
                {
                    await connection.Transport.DisposeAsync();
                }
            }
            catch (Exception)
            { /* The canceled connector owns its partial connection. */
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            State = BattleClientState.Disposed;
            ClearCombatEvents();
            _remotePresentation.Reset();
            _connectCancellation?.Cancel();
            Task<BattleTransportConnection> pendingConnect = _connectTask;
            _connectTask = null;
            if (pendingConnect is not null)
            {
                try
                {
                    BattleTransportConnection orphaned = await pendingConnect;
                    if (orphaned.Transport is not null)
                    {
                        await orphaned.Transport.DisposeAsync();
                    }
                }
                catch (OperationCanceledException)
                {
                    // 释放操作中断正在建立的连接时，此取消符合预期。
                }
                catch (Exception)
                {
                    // 释放仍尽力执行；连接器负责清理建立失败的部分会话。
                }
            }

            _connectCancellation?.Dispose();
            if (_prediction is not null)
            {
                await _prediction.DisposeAsync();
            }

            if (_arenaPrediction is not null)
            {
                await _arenaPrediction.DisposeAsync();
            }

            await _transportSlot.DisposeAsync();
            _codec.Dispose();
            if (_pendingInputSend != null)
            {
                _ = ObserveAbandonedInputSend(_pendingInputSend);
            }
        }

        private void BeginConnect()
        {
            _codec.Reset();
            _connectCancellation?.Dispose();
            _connectCancellation = new CancellationTokenSource();
            _phaseElapsedSeconds = 0;
            _connectTask = _connector
                .ConnectAsync(_host, _port, PhaseTimeoutMilliseconds, _connectCancellation.Token)
                .AsTask();
        }

        private void CompleteConnect()
        {
            Task<BattleTransportConnection> completed = _connectTask;
            _connectTask = null;
            BattleTransportConnection connection;
            try
            {
                connection = completed.GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                if (State == BattleClientState.Reconnecting)
                {
                    ScheduleReconnectRetry(exception.Message);
                }
                else
                {
                    Fail("Connection failed: " + exception.Message);
                }

                return;
            }

            if (!connection.IsConnected)
            {
                if (State == BattleClientState.Reconnecting)
                {
                    ScheduleReconnectRetry("Connection failed.");
                }
                else
                {
                    Fail("Connection failed.");
                }

                return;
            }

            _transportSlot.Replace(connection.Transport, connection.TryAdvanceConnectionEpoch);
            if (State == BattleClientState.Reconnecting && _admission is null)
            {
                _awaitingReconnectResponse = true;
                _phaseElapsedSeconds = 0;
                if (
                    !_codec.TryEncodeReconnect(
                        _sessionId,
                        _connectionEpoch,
                        _lastReceivedTick,
                        _controlBuffer,
                        out int reconnectBytes
                    ) || !TrySendControl(reconnectBytes)
                )
                {
                    ScheduleReconnectRetry("Reconnect request was not accepted.");
                }

                return;
            }

            State = BattleClientState.LoggingIn;
            _phaseElapsedSeconds = 0;
            if (
                !_codec.TryEncodeLogin(
                    _clientBuild,
                    new BattleAdmission(_admission?.RoomId ?? "", _admission?.EntryTicket ?? ""),
                    _controlBuffer,
                    out int loginBytes
                ) || !TrySendControl(loginBytes)
            )
            {
                if (_topologyReconnecting)
                {
                    ScheduleReconnectRetry("Login request was not accepted.");
                }
                else
                {
                    Fail("Login request was not accepted.");
                }
            }
        }

        private bool TrySendControl(int frameBytes)
        {
            try
            {
                ValueTask<SendResult> pending = _transportSlot.SendAsync(
                    _codec.ControlChannel,
                    _controlBuffer.AsMemory(0, frameBytes)
                );
                return pending.IsCompletedSuccessfully
                    && pending.Result.Status == SendStatus.Accepted
                    && pending.Result.AcceptedBytes == frameBytes;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>结算尾部接收阶段；不建立连接、不发送输入，也不重试控制消息。</summary>
        internal bool DrainAcceptedPackets()
        {
            if (_disposed)
            {
                return true;
            }

            bool closedBeforeRead =
                _transportSlot.State is TransportState.Closed or TransportState.Faulted;
            bool drained = PumpReceive(receiveOnly: true);
            return closedBeforeRead && drained;
        }

        private bool PumpReceive(bool receiveOnly = false)
        {
            int budget = 256;
            while (budget-- > 0)
            {
                if (!_transportSlot.TryReceive(_receiveBuffer, out ReceivedPacket packet))
                {
                    return true;
                }

                if (!packet.IsComplete || packet.WrittenBytes > _receiveBuffer.Length)
                {
                    continue;
                }

                ReadOnlySpan<byte> frame = _receiveBuffer.AsSpan(0, packet.WrittenBytes);
                if (_codec.TryDecode(frame, _decoded) != BattleDecodeStatus.Accepted)
                {
                    continue;
                }

                BattleMessageKind messageId = _decoded.Kind;
                if (receiveOnly)
                {
                    if (
                        _prediction != null
                        && (State is BattleClientState.Active or BattleClientState.Faulted)
                        && messageId == BattleMessageKind.Snapshot
                    )
                    {
                        ApplySnapshot(_decoded, packet);
                    }
                    else if (
                        _prediction != null
                        && (State is BattleClientState.Active or BattleClientState.Faulted)
                        && messageId == BattleMessageKind.CombatEvent
                    )
                    {
                        ApplyCombatEvent(_decoded, packet);
                    }

                    continue;
                }

                if (
                    State == BattleClientState.LoggingIn
                    && packet.Channel.Equals(_codec.ControlChannel)
                    && messageId == BattleMessageKind.LoginResponse
                )
                {
                    HandleLogin(_decoded);
                }
                else if (
                    State == BattleClientState.JoiningRoom
                    && packet.ConnectionEpoch == _connectionEpoch
                    && packet.Channel.Equals(_codec.ControlChannel)
                    && messageId == BattleMessageKind.JoinResponse
                )
                {
                    HandleJoin(_decoded);
                }
                else if (
                    State == BattleClientState.Reconnecting
                    && packet.Channel.Equals(_codec.ControlChannel)
                    && messageId == BattleMessageKind.ReconnectResponse
                )
                {
                    HandleReconnect(_decoded, packet);
                }
                else if (
                    State == BattleClientState.Active
                    && messageId == BattleMessageKind.CombatEvent
                )
                {
                    ApplyCombatEvent(_decoded, packet);
                }
                else if (
                    State == BattleClientState.Active
                    && messageId == BattleMessageKind.Snapshot
                )
                {
                    ApplySnapshot(_decoded, packet);
                }
            }

            // 预算耗尽不代表队列为空；剩余消息必须保留至下一帧。
            return false;
        }

        private void HandleLogin(BattlePacketBuffer message)
        {
            ulong sessionId = message.SessionId,
                roomTick = message.Tick;
            uint epoch = message.ConnectionEpoch;
            string globalRoomId = message.GlobalRoomId,
                bootEpoch = message.BootEpoch;
            if (
                (
                    _admission is not null
                    && (
                        !string.Equals(globalRoomId, _admission.RoomId, StringComparison.Ordinal)
                        || !string.Equals(bootEpoch, _admission.BootEpoch, StringComparison.Ordinal)
                    )
                ) || !_transportSlot.TryAdvanceConnectionEpoch(epoch)
            )
            {
                Fail("Malformed login response or invalid connection epoch.");
                return;
            }

            _sessionId = sessionId;
            _connectionEpoch = epoch;
            if (_initialConnectionEpoch == 0)
            {
                _initialConnectionEpoch = epoch;
            }

            if (_admission is not null)
            {
                _lastReceivedTick = roomTick;
            }

            State = BattleClientState.JoiningRoom;
            _phaseElapsedSeconds = 0;
            if (
                !_codec.TryEncodeJoin(sessionId, RoomId, _controlBuffer, out int joinBytes)
                || !TrySendControl(joinBytes)
            )
            {
                if (_topologyReconnecting)
                {
                    ScheduleReconnectRetry("Join-room request was not accepted.");
                }
                else
                {
                    Fail("Join-room request was not accepted.");
                }
            }
        }

        private void HandleJoin(BattlePacketBuffer message)
        {
            uint roomId = message.RoomId,
                entityId = message.EntityId,
                tickRate = message.TickRate;
            if ((_admission is null && roomId != RoomId) || tickRate != TickRate)
            {
                Fail("Malformed or incompatible join-room response.");
                return;
            }

            _remotePresentation.Reset();
            _entityId = entityId;
            _localRoomId = roomId;
            _prediction = new ClientPredictionAdapter(_transportSlot, _codec, entityId);
            _topologyReconnecting = false;
            _reconnectAttempts = 0;
            State = BattleClientState.Active;
            _phaseElapsedSeconds = 0;
        }

        private void HandleReconnect(BattlePacketBuffer message, in ReceivedPacket packet)
        {
            uint epoch = message.ConnectionEpoch;
            ulong resumeTick = message.Tick;
            // 推进连接状态前，必须验证完整快照。
            if (
                IsArenaFinished
                || epoch <= _connectionEpoch
                || !message.TryGetPlayer(_entityId, out var player)
                || resumeTick < _lastReceivedTick
                || message.Acknowledgement < _lastAcknowledgedSequence
                || (_arenaPrediction != null && !message.HasArenaData)
            )
            {
                ScheduleReconnectRetry("Malformed reconnect response or stale snapshot.");
                return;
            }

            bool replaySafe =
                _arenaPrediction != null
                    ? _arenaPrediction.CanReplaySafely(player.State)
                    : player.State.Tick >= 0
                        && _prediction != null
                        && _prediction.CanReplaySafely(ToKinematic(player.State));
            if (!replaySafe)
            {
                ScheduleReconnectRetry(
                    "Reconnect snapshot exceeds safe prediction numeric ranges."
                );
                return;
            }

            if (!_transportSlot.TryAdvanceConnectionEpoch(epoch))
            {
                ScheduleReconnectRetry("Reconnect epoch was rejected.");
                return;
            }

            if (_arenaPrediction is not null)
            {
                ReceivedPacket arenaPacket = new ReceivedPacket(
                    _codec.ControlChannel,
                    packet.WrittenBytes,
                    packet.RequiredBytes,
                    packet.Sequence,
                    epoch
                );
                if (!ApplySnapshot(message, arenaPacket, reconnect: true))
                {
                    ScheduleReconnectRetry("Reconnect Arena snapshot was rejected.");
                    return;
                }

                _connectionEpoch = epoch;
                _awaitingReconnectResponse = false;
                _reconnectAttempts = 0;
                State = BattleClientState.Active;
                return;
            }

            ReceivedPacket rebound = new ReceivedPacket(
                packet.Channel,
                packet.WrittenBytes,
                packet.RequiredBytes,
                packet.Sequence,
                epoch
            );
            SnapshotApplyResult applied = _prediction.ApplyDecoded(message, rebound);
            if (
                applied.Status
                is not (SnapshotApplyStatus.Initialized or SnapshotApplyStatus.Reconciled)
            )
            {
                ScheduleReconnectRetry("Reconnect snapshot was rejected: " + applied.Status);
                return;
            }

            ApplyPresentationReconciliation(applied);
            _connectionEpoch = epoch;
            _lastReceivedTick = resumeTick;
            _lastAcknowledgedSequence = message.Acknowledgement;
            _awaitingReconnectResponse = false;
            _reconnectAttempts = 0;
            State = BattleClientState.Active;
        }

        private bool ApplySnapshot(
            BattlePacketBuffer message,
            in ReceivedPacket packet,
            bool reconnect = false
        )
        {
            if (IsArenaFinished)
            {
                return false;
            }

            if (
                (!reconnect && packet.ConnectionEpoch != _connectionEpoch)
                || !packet.Channel.Equals(
                    reconnect ? _codec.ControlChannel : _codec.SnapshotChannel
                )
            )
            {
                return false;
            }

            if (
                message.Tick < _lastReceivedTick
                || message.Acknowledgement < _lastAcknowledgedSequence
            )
            {
                return false;
            }

            if (!message.TryGetPlayer(_entityId, out var arena))
            {
                return false;
            }

            if (message.HasArenaData)
            {
                _arenaPrediction ??= new ArenaClientPredictionAdapter(
                    _transportSlot,
                    _codec,
                    _entityId
                );
                ArenaSnapshotApplyResult result = _arenaPrediction.ApplyDecoded(message, packet);
                if (
                    result.Status
                    is not (
                        ArenaSnapshotApplyStatus.Initialized
                        or ArenaSnapshotApplyStatus.Reconciled
                    )
                )
                {
                    return false;
                }

                _remotePresentation.ApplySnapshot(
                    message.PlayerSpan,
                    (long)message.Tick,
                    _entityId
                );
                bool finished = arena.Phase == ArenaMatchPhase.Finished;
                if (finished)
                {
                    // 终局权威状态取代所有尚未确认的本地命令。
                    _arenaPrediction.Initialize(arena.State);
                    ClearInputRing();
                }

                _arenaState = finished ? arena.State : result.State;
                _arenaPhase = arena.Phase;
                _arenaRemainingTicks = arena.RemainingTicks;
                _arenaLeaderEntityId = arena.LeaderEntityId;
                _hasArenaState = true;
                _lastReceivedTick = (ulong)arena.State.Tick;
                _lastAcknowledgedSequence = arena.Acknowledgement;
                if (finished || result.Status == ArenaSnapshotApplyStatus.Initialized)
                {
                    _presentation.Initialize(ToKinematic(_arenaState));
                }
                else
                {
                    _presentation.ApplyReconciliation(result.Reconciliation);
                }

                return true;
            }

            if (_arenaPrediction is not null)
            {
                return false;
            }

            SnapshotApplyResult applied = _prediction.ApplyDecoded(message, packet);
            if (applied.Status is SnapshotApplyStatus.Initialized or SnapshotApplyStatus.Reconciled)
            {
                ApplyPresentationReconciliation(applied);
                _connectionEpoch = applied.ConnectionEpoch;
                _lastReceivedTick = message.Tick;
                _lastAcknowledgedSequence = message.Acknowledgement;
                return true;
            }

            return false;
        }

        private void ClearInputRing()
        {
            _inputRing.Clear();
            _inputGeneration++;
        }

        private static async Task ObserveAbandonedInputSend(Task<SendResult> send)
        {
            try
            {
                await send;
            }
            catch (Exception)
            { /* Transport disposal may cancel or fault its outstanding send. */
            }
        }

        private void FlushInputRing()
        {
            if (_pendingInputSend != null)
            {
                if (!_pendingInputSend.IsCompleted)
                {
                    return;
                }

                Task<SendResult> completed = _pendingInputSend;
                _pendingInputSend = null;
                try
                {
                    SendResult result = completed.GetAwaiter().GetResult();
                    if (
                        _pendingInputGeneration == _inputGeneration
                        && !CompleteInputSend(result, _pendingInputLength)
                    )
                    {
                        return;
                    }
                }
                catch (Exception)
                {
                    if (_pendingInputGeneration == _inputGeneration)
                    {
                        _sendFailed = true;
                        return;
                    }
                }
            }

            int budget = _inputRing.Count;
            while (budget-- > 0 && _inputRing.TryPeek(out byte[] frame, out int length))
            {
                try
                {
                    ulong generation = _inputGeneration;
                    frame.AsSpan(0, length).CopyTo(_inputSendBuffer);
                    ValueTask<SendResult> pending = _transportSlot.SendAsync(
                        _codec.InputChannel,
                        _inputSendBuffer.AsMemory(0, length)
                    );
                    if (!pending.IsCompletedSuccessfully)
                    {
                        // 此 ValueTask 只能消费一次；完成前不得重试或复用缓冲区。
                        _pendingInputLength = length;
                        _pendingInputGeneration = generation;
                        _pendingInputSend = pending.AsTask();
                        return;
                    }

                    if (
                        generation != _inputGeneration
                        || !CompleteInputSend(pending.Result, length)
                    )
                    {
                        return;
                    }
                }
                catch (Exception)
                {
                    _sendFailed = true;
                    return;
                }
            }
        }

        private bool CompleteInputSend(SendResult result, int length)
        {
            if (result.Status == SendStatus.WouldBlock)
            {
                return false;
            }

            _inputRing.Pop();
            if (result.Status != SendStatus.Accepted || result.AcceptedBytes != length)
            {
                _droppedInputFrames++;
                if (result.Status is SendStatus.Closed or SendStatus.Faulted)
                {
                    _sendFailed = true;
                    return false;
                }
            }

            return true;
        }

        private void BeginReconnect()
        {
            if (IsArenaFinished)
            {
                return;
            }

            if (_sessionId == 0 || _prediction is null)
            {
                Fail("Connection closed before a resumable session was established.");
                return;
            }

            if (_admission is not null)
            {
                ResumeOriginalAllocation(_admission);
                return;
            }

            State = BattleClientState.Reconnecting;
            _sendFailed = false;
            ClearInputRing();
            _presentation.ResetState();
            _remotePresentation.Reset();
            ClearCombatEvents();
            _awaitingReconnectResponse = false;
            _reconnectAttempts = 1;
            _retryDelayRemainingSeconds = _reconnectDelaySeconds[0];
            _transportSlot.DetachAndDispose();
        }

        private void ScheduleReconnectRetry(string reason)
        {
            _awaitingReconnectResponse = false;
            _transportSlot.DetachAndDispose();
            if (_reconnectAttempts >= _reconnectDelaySeconds.Length)
            {
                Fail(reason);
                return;
            }

            State = BattleClientState.Reconnecting;
            _retryDelayRemainingSeconds = _reconnectDelaySeconds[_reconnectAttempts];
            _reconnectAttempts++;
            _phaseElapsedSeconds = 0;
        }

        private void Fail(string reason)
        {
            _faultReason = string.IsNullOrWhiteSpace(reason)
                ? "Unknown battle client failure."
                : reason;
            State = BattleClientState.Faulted;
            _connectCancellation?.Cancel();
            _sendFailed = false;
            _presentation.ResetState();
            _remotePresentation.Reset();
        }

        private static KinematicState ToKinematic(in ArenaPlayerState state) =>
            new KinematicState(
                state.Tick,
                state.LastProcessedInputSequence,
                state.PositionXMillimetres,
                state.PositionZMillimetres
            );

        private void ApplyPresentationReconciliation(in SnapshotApplyResult applied)
        {
            if (applied.HasReconciliation)
            {
                _presentation.ApplyReconciliation(applied.Reconciliation);
                return;
            }

            if (_prediction.TryGetPredictedState(out KinematicState initializedState))
            {
                _presentation.Initialize(initializedState);
            }
        }

        private sealed class ReplaceableRealtimeTransportSlot : IRealtimeTransport
        {
            private IRealtimeTransport _current;
            private Func<uint, bool> _tryAdvanceEpoch;
            public TransportState State => _current?.State ?? TransportState.Closed;

            internal void Replace(IRealtimeTransport transport, Func<uint, bool> tryAdvanceEpoch)
            {
                _current = transport ?? throw new ArgumentNullException(nameof(transport));
                _tryAdvanceEpoch =
                    tryAdvanceEpoch ?? throw new ArgumentNullException(nameof(tryAdvanceEpoch));
            }

            internal bool TryAdvanceConnectionEpoch(uint epoch) =>
                _tryAdvanceEpoch?.Invoke(epoch) == true;

            internal void DetachAndDispose()
            {
                IRealtimeTransport detached = _current;
                _current = null;
                _tryAdvanceEpoch = null;
                if (detached is not null)
                {
                    _ = detached.DisposeAsync();
                }
            }

            public ValueTask<SendResult> SendAsync(
                TransportChannel channel,
                ReadOnlyMemory<byte> payload,
                CancellationToken cancellationToken = default
            ) =>
                _current is null
                    ? new ValueTask<SendResult>(new SendResult(SendStatus.Closed))
                    : _current.SendAsync(channel, payload, cancellationToken);

            public bool TryReceive(Span<byte> destination, out ReceivedPacket packet)
            {
                if (_current is not null)
                {
                    return _current.TryReceive(destination, out packet);
                }

                packet = default;
                return false;
            }

            public ValueTask DisposeAsync()
            {
                IRealtimeTransport detached = _current;
                _current = null;
                _tryAdvanceEpoch = null;
                return detached?.DisposeAsync() ?? default;
            }
        }

        private sealed class InputFrameRing
        {
            private readonly byte[][] _buffers;
            private readonly int[] _lengths;
            private int _head;
            private int _count;

            internal InputFrameRing(int capacity, int frameBytes)
            {
                if (capacity < 1)
                {
                    throw new ArgumentOutOfRangeException(nameof(capacity));
                }

                _buffers = new byte[capacity][];
                _lengths = new int[capacity];
                for (int index = 0; index < capacity; index++)
                {
                    _buffers[index] = new byte[frameBytes];
                }
            }

            internal int Count => _count;

            internal void Clear()
            {
                _head = 0;
                _count = 0;
            }

            internal bool TryGetWriteBuffer(out byte[] buffer)
            {
                if (_count == _buffers.Length)
                {
                    buffer = null;
                    return false;
                }

                buffer = _buffers[(_head + _count) % _buffers.Length];
                return true;
            }

            internal void CommitWrite(int length)
            {
                int tail = (_head + _count) % _buffers.Length;
                _lengths[tail] = length;
                _count++;
            }

            internal bool TryPeek(out byte[] buffer, out int length)
            {
                if (_count == 0)
                {
                    buffer = null;
                    length = 0;
                    return false;
                }

                buffer = _buffers[_head];
                length = _lengths[_head];
                return true;
            }

            internal void Pop()
            {
                if (_count == 0)
                {
                    return;
                }

                _head = (_head + 1) % _buffers.Length;
                _count--;
            }
        }
    }
}
