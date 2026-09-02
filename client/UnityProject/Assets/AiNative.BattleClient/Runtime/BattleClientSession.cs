using System;
using System.Threading;
using System.Threading.Tasks;
using AiNative.Client.Fantasy;
using AiNative.Client.Prediction;
using AiNative.Realtime;

namespace AiNative.Client.Application
{
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
        private readonly ReplaceableRealtimeTransportSlot _transportSlot = new ReplaceableRealtimeTransportSlot();
        private readonly InputFrameRing _inputRing;
        private readonly byte[] _receiveBuffer = new byte[BattleClientProtocolV1.MaxFrameBytes];
        private readonly byte[] _controlBuffer = new byte[BattleClientProtocolV1.MaxFrameBytes];
        private CancellationTokenSource _connectCancellation;
        private Task<BattleTransportConnection> _connectTask;
        private ClientPredictionAdapter _prediction;
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

        public BattleClientSession(
            string host,
            int port,
            string clientBuild = "ws26",
            int inputRingCapacity = DefaultInputRingCapacity)
            : this(host, port, clientBuild, inputRingCapacity, new FantasyBattleTransportConnector())
        {
        }

        internal BattleClientSession(
            string host,
            int port,
            string clientBuild,
            int inputRingCapacity,
            IBattleTransportConnector connector)
        {
            if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("Host is required.", nameof(host));
            if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
            _connector = connector ?? throw new ArgumentNullException(nameof(connector));
            _host = host;
            _port = port;
            _clientBuild = clientBuild ?? string.Empty;
            _inputRing = new InputFrameRing(inputRingCapacity, ClientPredictionAdapter.RequiredInputBufferBytes);
            State = BattleClientState.Connecting;
        }

        public BattleClientState State { get; private set; }

        public string FaultReason => _faultReason;

        public ulong SessionId => _sessionId;

        public uint EntityId => _entityId;

        public uint ConnectionEpoch => _connectionEpoch;

        public uint InitialConnectionEpoch => _initialConnectionEpoch;

        public ulong LastReceivedTick => _lastReceivedTick;

        public uint LastAcknowledgedSequence => _lastAcknowledgedSequence;

        public long DroppedInputFrames => _droppedInputFrames;

        public int QueuedInputFrames => _inputRing.Count;

        public bool IsPredictionInitialized => _prediction?.IsInitialized == true;

        public PredictionDiagnostics PredictionDiagnostics => _prediction?.Diagnostics ?? default;

        public bool ResetPredictionDiagnostics()
        {
            if (_disposed || _prediction is null || !_prediction.IsInitialized) return false;
            _prediction.ResetDiagnostics();
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
            if (State == BattleClientState.Active)
            {
                FlushInputRing();
            }

            if (State is BattleClientState.LoggingIn or BattleClientState.JoiningRoom ||
                (State == BattleClientState.Reconnecting && _awaitingReconnectResponse))
            {
                _phaseElapsedSeconds += unscaledDeltaSeconds;
                if (_phaseElapsedSeconds >= PhaseTimeoutMilliseconds / 1000f)
                {
                    if (State == BattleClientState.Reconnecting)
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

        public void RequestReconnect()
        {
            if (_disposed || State != BattleClientState.Active) return;
            BeginReconnect();
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            State = BattleClientState.Disposed;
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
            if (State == BattleClientState.Reconnecting)
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
                    _controlBuffer,
                    out int loginBytes) ||
                !TrySendControl(loginBytes))
            {
                Fail("Login request was not accepted.");
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
                    out uint epoch) ||
                !_transportSlot.TryAdvanceConnectionEpoch(epoch))
            {
                Fail("Malformed login response or invalid connection epoch.");
                return;
            }

            _sessionId = sessionId;
            _connectionEpoch = epoch;
            _initialConnectionEpoch = epoch;
            State = BattleClientState.JoiningRoom;
            _phaseElapsedSeconds = 0;
            if (!BattleClientProtocolV1.TryEncodeJoin(
                    sessionId,
                    RoomId,
                    _controlBuffer,
                    out int joinBytes) ||
                !TrySendControl(joinBytes))
            {
                Fail("Join-room request was not accepted.");
            }
        }

        private void HandleJoin(ReadOnlySpan<byte> frame)
        {
            if (!BattleClientProtocolV1.TryDecodeJoinResponse(
                    frame,
                    out uint roomId,
                    out uint entityId,
                    out uint tickRate) ||
                roomId != RoomId || tickRate != TickRate)
            {
                Fail("Malformed or incompatible join-room response.");
                return;
            }

            _entityId = entityId;
            _prediction = new ClientPredictionAdapter(_transportSlot, entityId);
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

            _connectionEpoch = epoch;
            _lastReceivedTick = resumeTick;
            _awaitingReconnectResponse = false;
            _reconnectAttempts = 0;
            State = BattleClientState.Active;
        }

        private void ApplySnapshot(ReadOnlySpan<byte> frame, in ReceivedPacket packet)
        {
            SnapshotApplyResult applied = _prediction.ApplyPacket(frame, packet);
            if (applied.Status is SnapshotApplyStatus.Initialized or SnapshotApplyStatus.Reconciled)
            {
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
            if (_sessionId == 0 || _prediction is null)
            {
                Fail("Connection closed before a resumable session was established.");
                return;
            }

            State = BattleClientState.Reconnecting;
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
