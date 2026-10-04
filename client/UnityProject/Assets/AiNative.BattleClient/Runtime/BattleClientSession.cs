using System;
using System.Threading;
using System.Threading.Tasks;
using AiNative.Client.Fantasy;
using AiNative.Client.Prediction;
using AiNative.Gameplay;
using AiNative.Realtime;

namespace AiNative.Client.Application
{
    /// <summary>Immutable credentials issued by Gate for one Battle allocation. The ticket is never logged.</summary>
    public sealed class BattleAdmissionInfo
    {
        public BattleAdmissionInfo(string roomId, string bootEpoch, string entryTicket, string nodeId = "", string playerId = "")
        {
            if (string.IsNullOrWhiteSpace(roomId)) throw new ArgumentException("Global room is required.", nameof(roomId));
            if (string.IsNullOrWhiteSpace(bootEpoch)) throw new ArgumentException("Boot epoch is required.", nameof(bootEpoch));
            if (string.IsNullOrWhiteSpace(entryTicket)) throw new ArgumentException("Entry ticket is required.", nameof(entryTicket));
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
            Func<uint, bool> tryAdvanceConnectionEpoch)
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
            CancellationToken cancellationToken);
    }

    internal sealed class FantasyBattleTransportConnector : IBattleTransportConnector
    {
        public async ValueTask<BattleTransportConnection> ConnectAsync(
            string host,
            int port,
            int timeoutMilliseconds,
            CancellationToken cancellationToken)
        {
            FantasyKcpConnectResult result = await FantasyKcpRealtimeTransport.ConnectAsync(
                new FantasyKcpTransportOptions(host, port, timeoutMilliseconds),
                cancellationToken);
            if (result.Status != FantasyKcpConnectStatus.Connected || result.Transport is null)
            {
                return default;
            }

            return new BattleTransportConnection(
                result.Transport,
                result.Transport.TryAdvanceConnectionEpoch);
        }
    }

    /// <summary>
    /// Product-level protocol and prediction composition. Call <see cref="Pump"/> from
    /// Update and <see cref="PredictAndQueueInput"/> from FixedUpdate.
    /// </summary>
    public sealed class BattleClientSession : IAsyncDisposable
    {
        public const uint ProtocolMajor = 1;
        public const uint RoomId = 1;
        public const uint TickRate = 60;
        public const int PhaseTimeoutMilliseconds = 5000;
        public const int DefaultInputRingCapacity = 256;

        private static readonly float[] ReconnectDelaySeconds = { 0.25f, 0.5f, 1.0f };

        private readonly string _host;
        private readonly int _port;
        private readonly string _clientBuild;
        private readonly IBattleTransportConnector _connector;
        private BattleAdmissionInfo _admission;
        private bool _topologyReconnecting;
        private uint _localRoomId;
        private readonly ReplaceableRealtimeTransportSlot _transportSlot = new ReplaceableRealtimeTransportSlot();
        private readonly InputFrameRing _inputRing;
        private readonly ArenaRemotePresentation _remotePresentation = new ArenaRemotePresentation();
        private readonly PresentationCorrectionSmoother _presentation =
            new PresentationCorrectionSmoother();
        private readonly byte[] _receiveBuffer = new byte[BattleClientProtocolV1.MaxFrameBytes];
        private readonly byte[] _controlBuffer = new byte[BattleClientProtocolV1.MaxFrameBytes];
        private CancellationTokenSource _connectCancellation;
        private Task<BattleTransportConnection> _connectTask;
        private ClientPredictionAdapter _prediction;
        private ArenaClientPredictionAdapter _arenaPrediction;
        private float _phaseElapsedSeconds;
        private float _retryDelayRemainingSeconds;
        private int _reconnectAttempts;
        private bool _awaitingReconnectResponse;
        private bool _disposed;
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

        public BattleClientSession(
            string host,
            int port,
            string clientBuild = "ws26",
            int inputRingCapacity = DefaultInputRingCapacity)
            : this(host, port, clientBuild, inputRingCapacity, new FantasyBattleTransportConnector())
        {
        }

        public BattleClientSession(
            string host,
            int port,
            BattleAdmissionInfo admission,
            string clientBuild = "topology",
            int inputRingCapacity = DefaultInputRingCapacity)
            : this(host, port, clientBuild, inputRingCapacity, new FantasyBattleTransportConnector(),
                admission ?? throw new ArgumentNullException(nameof(admission)))
        {
        }

        internal BattleClientSession(
            string host,
            int port,
            string clientBuild,
            int inputRingCapacity,
            IBattleTransportConnector connector,
            BattleAdmissionInfo admission = null)
        {
            if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("Host is required.", nameof(host));
            if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
            _connector = connector ?? throw new ArgumentNullException(nameof(connector));
            _admission = admission;
            _host = host;
            _port = port;
            _clientBuild = clientBuild ?? string.Empty;
            _inputRing = new InputFrameRing(inputRingCapacity, Math.Max(ClientPredictionAdapter.RequiredInputBufferBytes, ArenaClientPredictionAdapter.RequiredInputBufferBytes));
            State = BattleClientState.Connecting;
        }

        public BattleClientState State { get; private set; }

        public string FaultReason => _faultReason;

        public ulong SessionId => _sessionId;

        public uint EntityId => _entityId;

        public int AdvanceRemotePresentation(float deltaSeconds, Span<ArenaRemotePose> destination)
            => _remotePresentation.Advance(deltaSeconds, destination);

        public uint ConnectionEpoch => _connectionEpoch;

        public uint InitialConnectionEpoch => _initialConnectionEpoch;

        public uint LocalRoomId => _localRoomId;

        public bool UsesTopologyAdmission => _admission is not null;

        /// <summary>Refreshes short-lived credentials without changing the allocation. Call on the Pump thread.</summary>
        public void UpdateAdmission(BattleAdmissionInfo admission)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(BattleClientSession));
            if (admission is null) throw new ArgumentNullException(nameof(admission));
            if (_admission is null ||
                !string.Equals(_admission.RoomId, admission.RoomId, StringComparison.Ordinal) ||
                !string.Equals(_admission.BootEpoch, admission.BootEpoch, StringComparison.Ordinal) ||
                !string.Equals(_admission.NodeId, admission.NodeId, StringComparison.Ordinal) ||
                !string.Equals(_admission.PlayerId, admission.PlayerId, StringComparison.Ordinal))
                throw new ArgumentException("Admission refresh must retain the same room, boot, node and player.", nameof(admission));
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

        public bool IsPredictionInitialized => _arenaPrediction?.IsInitialized == true || _prediction?.IsInitialized == true;

        public PredictionDiagnostics PredictionDiagnostics => _prediction?.Diagnostics ?? default;

        public PresentationCorrectionDiagnostics PresentationDiagnostics =>
            _presentation.Diagnostics;

        public bool ResetPredictionDiagnostics()
        {
            if (_disposed || _prediction is null || !_prediction.IsInitialized) return false;
            _prediction.ResetDiagnostics();
            return true;
        }

        public bool ResetPresentationDiagnostics()
        {
            if (_disposed || !_presentation.IsInitialized) return false;
            _presentation.ResetDiagnostics();
            return true;
        }

        public bool TryAdvancePresentation(
            float unscaledDeltaSeconds,
            out PresentationPosition position)
        {
            if (!_disposed && _arenaPrediction?.IsInitialized == true)
            {
                ArenaPlayerState arena = _arenaPrediction.Current;
                position = _presentation.Advance(ToKinematic(arena), unscaledDeltaSeconds);
                return true;
            }
            if (_disposed || _prediction is null ||
                !_prediction.TryGetPredictedState(out KinematicState simulationState))
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
            if (_disposed) return;
            if (unscaledDeltaSeconds < 0 || float.IsNaN(unscaledDeltaSeconds))
            {
                throw new ArgumentOutOfRangeException(nameof(unscaledDeltaSeconds));
            }

            if (_connectTask is not null && _connectTask.IsCompleted)
            {
                CompleteConnect();
            }

            if (State == BattleClientState.Reconnecting &&
                _connectTask is null && !_awaitingReconnectResponse &&
                _retryDelayRemainingSeconds > 0)
            {
                _retryDelayRemainingSeconds -= unscaledDeltaSeconds;
                if (_retryDelayRemainingSeconds <= 0)
                {
                    BeginConnect();
                }
            }

            if (State == BattleClientState.Active &&
                _transportSlot.State is TransportState.Closed or TransportState.Faulted)
            {
                BeginReconnect();
            }

            PumpReceive();
            if (State == BattleClientState.Active && !IsArenaFinished)
            {
                FlushInputRing();
            }

            if (State is BattleClientState.LoggingIn or BattleClientState.JoiningRoom ||
                (State == BattleClientState.Reconnecting && _awaitingReconnectResponse))
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
            int moveZMilli)
        {
            if (_arenaPrediction?.IsInitialized == true)
                return PredictAndQueueArenaInput(roomTick, moveXMilli, moveZMilli, 0, 0, ArenaButtons.None, _arenaPrediction.Current.Weapon);
            if (_disposed) return PredictionPrepareStatus.Disposed;
            if (State != BattleClientState.Active || _prediction is null || !_prediction.IsInitialized)
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
                buffer);
            if (result.Status == PredictionPrepareStatus.Prepared)
            {
                _inputRing.CommitWrite(result.WrittenBytes);
            }

            return result.Status;
        }

        /// <summary>Queues an arena command using the latest authority tick and shared arena prediction.</summary>
        public PredictionPrepareStatus PredictAndQueueArenaInput(
            ulong roomTick,
            int moveXMilli,
            int moveZMilli,
            int lookYawMilli,
            int lookPitchMilli,
            ArenaButtons buttons,
            ArenaWeaponId weapon)
        {
            if (_disposed) return PredictionPrepareStatus.Disposed;
            if (State != BattleClientState.Active || !IsPredictionInitialized || IsArenaFinished)
            {
                return PredictionPrepareStatus.NotInitialized;
            }

            if (!_inputRing.TryGetWriteBuffer(out byte[] buffer))
            {
                _droppedInputFrames++;
                return PredictionPrepareStatus.BufferTooSmall;
            }

            if (_arenaPrediction?.IsInitialized != true) return PredictionPrepareStatus.NotInitialized;
            ulong stampedTick = checked(_lastReceivedTick + 1);
            ArenaPredictionPrepareResult predicted = _arenaPrediction.PrepareInput(
                stampedTick, moveXMilli, moveZMilli, lookYawMilli, lookPitchMilli, buttons, weapon, buffer);
            if (predicted.Status == ArenaPredictionPrepareStatus.Prepared)
            {
                _arenaState = predicted.PredictedState;
                _inputRing.CommitWrite(predicted.WrittenBytes);
            }
            return (PredictionPrepareStatus)predicted.Status;
        }

        public void RequestReconnect()
        {
            if (_disposed || State != BattleClientState.Active) return;
            BeginReconnect();
        }

        /// <summary>Restarts only the original topology allocation after Gate has renewed its admission.
        /// Call on the Pump thread; an exhausted connection can recover without starting another match.</summary>
        public bool ResumeOriginalAllocation(BattleAdmissionInfo admission)
        {
            if (admission == null || string.IsNullOrEmpty(admission.EntryTicket))
                throw new ArgumentException("A renewed entry ticket is required.", nameof(admission));
            UpdateAdmission(admission); // Validate identity before altering any connection or prediction state.
            if (IsArenaFinished) return false;
            _connectCancellation?.Cancel();
            Task<BattleTransportConnection> abandoned = _connectTask;
            _connectTask = null;
            if (abandoned != null) _ = DisposeAbandonedConnectionAsync(abandoned);
            State = BattleClientState.Reconnecting;
            _faultReason = "";
            _presentation.ResetState();
            _remotePresentation.Reset();
            _topologyReconnecting = true;
            // Unsent commands from the suspended owner must never cross the new authenticated connection.
            _inputRing.Clear();
            if (_prediction != null) _ = _prediction.DisposeAsync();
            if (_arenaPrediction != null) _ = _arenaPrediction.DisposeAsync();
            _prediction = null; _arenaPrediction = null;
            _hasArenaState = false; _arenaState = default; _arenaPhase = default;
            _arenaRemainingTicks = 0; _arenaLeaderEntityId = 0;
            _lastAcknowledgedSequence = 0; _lastReceivedTick = 0;
            _sessionId = 0; _entityId = 0; _localRoomId = 0;
            _awaitingReconnectResponse = false;
            _phaseElapsedSeconds = 0;
            _reconnectAttempts = 1;
            _retryDelayRemainingSeconds = ReconnectDelaySeconds[0];
            _transportSlot.DetachAndDispose();
            return true;
        }

        private static async Task DisposeAbandonedConnectionAsync(Task<BattleTransportConnection> abandoned)
        {
            try
            {
                BattleTransportConnection connection = await abandoned;
                if (connection.Transport != null) await connection.Transport.DisposeAsync();
            }
            catch (Exception) { /* The canceled connector owns its partial connection. */ }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            State = BattleClientState.Disposed;
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
                    // Expected when disposal interrupts an in-flight connection.
                }
                catch (Exception)
                {
                    // Disposal remains best-effort; the connector owns failed partial sessions.
                }
            }

            _connectCancellation?.Dispose();
            if (_prediction is not null)
            {
                await _prediction.DisposeAsync();
            }

            if (_arenaPrediction is not null) await _arenaPrediction.DisposeAsync();
            await _transportSlot.DisposeAsync();
        }

        private void BeginConnect()
        {
            _connectCancellation?.Dispose();
            _connectCancellation = new CancellationTokenSource();
            _phaseElapsedSeconds = 0;
            _connectTask = _connector.ConnectAsync(
                    _host,
                    _port,
                    PhaseTimeoutMilliseconds,
                    _connectCancellation.Token)
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
                if (!BattleClientProtocolV1.TryEncodeReconnect(
                        _sessionId,
                        _connectionEpoch,
                        _lastReceivedTick,
                        _controlBuffer,
                        out int reconnectBytes) ||
                    !TrySendControl(reconnectBytes))
                {
                    ScheduleReconnectRetry("Reconnect request was not accepted.");
                }

                return;
            }

            State = BattleClientState.LoggingIn;
            _phaseElapsedSeconds = 0;
            if (!BattleClientProtocolV1.TryEncodeLogin(
                    _clientBuild,
                    _admission,
                    _controlBuffer,
                    out int loginBytes) ||
                !TrySendControl(loginBytes))
            {
                if (_topologyReconnecting) ScheduleReconnectRetry("Login request was not accepted.");
                else Fail("Login request was not accepted.");
            }
        }

        private bool TrySendControl(int frameBytes)
        {
            try
            {
                ValueTask<SendResult> pending = _transportSlot.SendAsync(
                    BattleClientProtocolV1.ControlChannel,
                    _controlBuffer.AsMemory(0, frameBytes));
                return pending.IsCompletedSuccessfully &&
                       pending.Result.Status == SendStatus.Accepted &&
                       pending.Result.AcceptedBytes == frameBytes;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void PumpReceive()
        {
            int budget = 256;
            while (budget-- > 0 && _transportSlot.TryReceive(_receiveBuffer, out ReceivedPacket packet))
            {
                if (!packet.IsComplete || packet.WrittenBytes > _receiveBuffer.Length)
                {
                    continue;
                }

                ReadOnlySpan<byte> frame = _receiveBuffer.AsSpan(0, packet.WrittenBytes);
                ushort messageId = BattleClientProtocolV1.ReadMessageId(frame);
                if (State == BattleClientState.LoggingIn &&
                    packet.Channel.Equals(BattleClientProtocolV1.ControlChannel) &&
                    messageId == BattleClientProtocolV1.LoginResponseMessageId)
                {
                    HandleLogin(frame);
                }
                else if (State == BattleClientState.JoiningRoom &&
                         packet.ConnectionEpoch == _connectionEpoch &&
                         packet.Channel.Equals(BattleClientProtocolV1.ControlChannel) &&
                         messageId == BattleClientProtocolV1.JoinRoomResponseMessageId)
                {
                    HandleJoin(frame);
                }
                else if (State == BattleClientState.Reconnecting &&
                         packet.Channel.Equals(BattleClientProtocolV1.ControlChannel) &&
                         messageId == BattleClientProtocolV1.ReconnectResponseMessageId)
                {
                    HandleReconnect(frame, packet);
                }
                else if (State == BattleClientState.Active &&
                         messageId == BattleClientProtocolV1.SnapshotMessageId)
                {
                    ApplySnapshot(frame, packet);
                }
            }
        }

        private void HandleLogin(ReadOnlySpan<byte> frame)
        {
            if (!BattleClientProtocolV1.TryDecodeLoginResponse(
                    frame,
                    out ulong sessionId,
                    out uint epoch,
                    out string globalRoomId,
                    out string bootEpoch,
                    out ulong roomTick) ||
                (_admission is not null &&
                 (!string.Equals(globalRoomId, _admission.RoomId, StringComparison.Ordinal) ||
                  !string.Equals(bootEpoch, _admission.BootEpoch, StringComparison.Ordinal))) ||
                !_transportSlot.TryAdvanceConnectionEpoch(epoch))
            {
                Fail("Malformed login response or invalid connection epoch.");
                return;
            }

            _sessionId = sessionId;
            _connectionEpoch = epoch;
            if (_initialConnectionEpoch == 0) _initialConnectionEpoch = epoch;
            if (_admission is not null) _lastReceivedTick = roomTick;
            State = BattleClientState.JoiningRoom;
            _phaseElapsedSeconds = 0;
            if (!BattleClientProtocolV1.TryEncodeJoin(
                    sessionId,
                    RoomId,
                    _controlBuffer,
                    out int joinBytes) ||
                !TrySendControl(joinBytes))
            {
                if (_topologyReconnecting) ScheduleReconnectRetry("Join-room request was not accepted.");
                else Fail("Join-room request was not accepted.");
            }
        }

        private void HandleJoin(ReadOnlySpan<byte> frame)
        {
            if (!BattleClientProtocolV1.TryDecodeJoinResponse(
                    frame,
                    out uint roomId,
                    out uint entityId,
                    out uint tickRate) ||
                (_admission is null && roomId != RoomId) || tickRate != TickRate)
            {
                Fail("Malformed or incompatible join-room response.");
                return;
            }

            _remotePresentation.Reset();
            _entityId = entityId;
            _localRoomId = roomId;
            _prediction = new ClientPredictionAdapter(_transportSlot, entityId);
            _topologyReconnecting = false;
            _reconnectAttempts = 0;
            State = BattleClientState.Active;
            _phaseElapsedSeconds = 0;
        }

        private void HandleReconnect(ReadOnlySpan<byte> frame, in ReceivedPacket packet)
        {
            if (!BattleClientProtocolV1.TryDecodeReconnectResponse(
                    frame,
                    out uint epoch,
                    out ulong resumeTick) ||
                epoch <= _connectionEpoch ||
                !_transportSlot.TryAdvanceConnectionEpoch(epoch))
            {
                ScheduleReconnectRetry("Malformed reconnect response or stale connection epoch.");
                return;
            }

            if (_arenaPrediction is not null)
            {
                Span<byte> snapshot = stackalloc byte[ArenaClientProtocolV1.MaxDatagramBytes];
                if (!ArenaClientProtocolV1.TryExtractReconnectSnapshot(frame, snapshot, out int length) ||
                    !ArenaClientProtocolV1.TryDecodeSnapshot(snapshot.Slice(0, length), _entityId, out var arena) || !arena.HasArenaData || (ulong)arena.State.Tick < _lastReceivedTick || (ulong)arena.State.Tick != resumeTick)
                {
                    ScheduleReconnectRetry("Arena reconnect snapshot was rejected.");
                    return;
                }
                ReceivedPacket arenaPacket = new ReceivedPacket(ArenaClientProtocolV1.SnapshotChannel, length, length, packet.Sequence, epoch);
                _connectionEpoch = epoch;
                ApplySnapshot(snapshot.Slice(0, length), arenaPacket);
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
                epoch);
            SnapshotApplyResult applied = _prediction.ApplyPacket(frame, rebound);
            if (applied.Status is not (SnapshotApplyStatus.Initialized or SnapshotApplyStatus.Reconciled))
            {
                ScheduleReconnectRetry("Reconnect snapshot was rejected: " + applied.Status);
                return;
            }

            ApplyPresentationReconciliation(applied);

            _connectionEpoch = epoch;
            _lastReceivedTick = resumeTick;
            _awaitingReconnectResponse = false;
            _reconnectAttempts = 0;
            State = BattleClientState.Active;
        }

        private void ApplySnapshot(ReadOnlySpan<byte> frame, in ReceivedPacket packet)
        {
            if (IsArenaFinished) return;
            if (packet.ConnectionEpoch != _connectionEpoch || !packet.Channel.Equals(BattleClientProtocolV1.SnapshotChannel)) return;
            if (_admission is not null &&
                (!BattleClientProtocolV1.TryReadSnapshotMetadata(frame, out ulong admittedTick, out uint admittedAcknowledgement) ||
                 admittedTick < _lastReceivedTick || admittedAcknowledgement < _lastAcknowledgedSequence)) return;
            if (ArenaClientProtocolV1.TryDecodeSnapshot(frame, _entityId, out DecodedArenaSnapshot arena) && arena.HasArenaData)
            {
                Span<ArenaSnapshotPlayer> validatedPlayers = stackalloc ArenaSnapshotPlayer[ArenaClientProtocolV1.MaxSnapshotPlayers];
                if (!ArenaClientProtocolV1.TryDecodePlayers(frame, validatedPlayers, out _, out _)) return;
                if ((ulong)arena.State.Tick < _lastReceivedTick || arena.Acknowledgement < _lastAcknowledgedSequence) return;
                _arenaPrediction ??= new ArenaClientPredictionAdapter(_transportSlot, _entityId);
                ArenaSnapshotApplyResult result = _arenaPrediction.ApplySnapshot(frame, packet);
                if (result.Status is not (ArenaSnapshotApplyStatus.Initialized or ArenaSnapshotApplyStatus.Reconciled)) return;
                _remotePresentation.ApplySnapshot(frame, _entityId);
                bool finished = arena.Phase == ArenaMatchPhase.Finished;
                if (finished)
                {
                    // The terminal authority state supersedes every unacknowledged local command.
                    _arenaPrediction.Initialize(arena.State);
                    _inputRing.Clear();
                }
                _arenaState = finished ? arena.State : result.State;
                _arenaPhase = arena.Phase;
                _arenaRemainingTicks = arena.RemainingTicks;
                _arenaLeaderEntityId = arena.LeaderEntityId;
                _hasArenaState = true;
                _lastReceivedTick = (ulong)arena.State.Tick;
                _lastAcknowledgedSequence = arena.Acknowledgement;
                if (finished || result.Status == ArenaSnapshotApplyStatus.Initialized) _presentation.Initialize(ToKinematic(_arenaState));
                else _presentation.ApplyReconciliation(result.Reconciliation);
                return;
            }
            if (_arenaPrediction is not null) return;
            SnapshotApplyResult applied = _prediction.ApplyPacket(frame, packet);
            if (applied.Status is SnapshotApplyStatus.Initialized or SnapshotApplyStatus.Reconciled)
            {
                ApplyPresentationReconciliation(applied);
                _connectionEpoch = applied.ConnectionEpoch;
                if (BattleClientProtocolV1.TryReadSnapshotMetadata(
                        frame,
                        out ulong tick,
                        out uint acknowledgement))
                {
                    _lastReceivedTick = tick;
                    _lastAcknowledgedSequence = acknowledgement;
                }
            }
        }

        private void FlushInputRing()
        {
            int budget = _inputRing.Count;
            while (budget-- > 0 && _inputRing.TryPeek(out byte[] frame, out int length))
            {
                ValueTask<SendResult> pending;
                try
                {
                    pending = _transportSlot.SendAsync(
                        BattleClientProtocolV1.InputChannel,
                        frame.AsMemory(0, length));
                }
                catch (Exception)
                {
                    BeginReconnect();
                    return;
                }

                if (!pending.IsCompletedSuccessfully) return;
                SendResult result = pending.Result;
                if (result.Status == SendStatus.WouldBlock) return;
                _inputRing.Pop();
                if (result.Status != SendStatus.Accepted || result.AcceptedBytes != length)
                {
                    _droppedInputFrames++;
                    if (result.Status is SendStatus.Closed or SendStatus.Faulted)
                    {
                        BeginReconnect();
                        return;
                    }
                }
            }
        }

        private void BeginReconnect()
        {
            if (IsArenaFinished) return;
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
            _presentation.ResetState();
            _remotePresentation.Reset();
            _awaitingReconnectResponse = false;
            _reconnectAttempts = 1;
            _retryDelayRemainingSeconds = ReconnectDelaySeconds[0];
            _transportSlot.DetachAndDispose();
        }

        private void ScheduleReconnectRetry(string reason)
        {
            _awaitingReconnectResponse = false;
            _transportSlot.DetachAndDispose();
            if (_reconnectAttempts >= ReconnectDelaySeconds.Length)
            {
                Fail(reason);
                return;
            }

            State = BattleClientState.Reconnecting;
            _retryDelayRemainingSeconds = ReconnectDelaySeconds[_reconnectAttempts];
            _reconnectAttempts++;
            _phaseElapsedSeconds = 0;
        }

        private void Fail(string reason)
        {
            _faultReason = string.IsNullOrWhiteSpace(reason) ? "Unknown battle client failure." : reason;
            State = BattleClientState.Faulted;
            _connectCancellation?.Cancel();
            _presentation.ResetState();
            _remotePresentation.Reset();
        }

        private static KinematicState ToKinematic(in ArenaPlayerState state)
            => new KinematicState(state.Tick, state.LastProcessedInputSequence, state.PositionXMillimetres, state.PositionZMillimetres);

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
                _tryAdvanceEpoch = tryAdvanceEpoch ?? throw new ArgumentNullException(nameof(tryAdvanceEpoch));
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
                CancellationToken cancellationToken = default) =>
                _current is null
                    ? new ValueTask<SendResult>(new SendResult(SendStatus.Closed))
                    : _current.SendAsync(channel, payload, cancellationToken);

            public bool TryReceive(Span<byte> destination, out ReceivedPacket packet)
            {
                if (_current is not null) return _current.TryReceive(destination, out packet);
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
                if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
                _buffers = new byte[capacity][];
                _lengths = new int[capacity];
                for (int index = 0; index < capacity; index++)
                {
                    _buffers[index] = new byte[frameBytes];
                }
            }

            internal int Count => _count;

            internal void Clear() { _head = 0; _count = 0; }

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
                if (_count == 0) return;
                _head = (_head + 1) % _buffers.Length;
                _count--;
            }
        }
    }
}
