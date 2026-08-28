using System;
using System.Threading;
using System.Threading.Tasks;
using AiNative.Gameplay;
using AiNative.Realtime;

namespace AiNative.Client.Prediction
{
    public enum PredictionSendStatus : byte
    {
        Accepted = 0,
        NotInitialized = 1,
        TransportUnavailable = 2,
        WouldBlock = 3,
        DroppedByPolicy = 4,
        Closed = 5,
        PayloadTooLarge = 6,
        Faulted = 7,
        Cancelled = 8,
        ConcurrentSend = 9,
        SequenceExhausted = 10,
        Disposed = 11,
    }

    public readonly struct PredictionSendResult
    {
        public PredictionSendResult(
            PredictionSendStatus status,
            bool predicted,
            in KinematicState predictedState,
            bool droppedOldestInput)
        {
            Status = status;
            Predicted = predicted;
            PredictedState = predictedState;
            DroppedOldestInput = droppedOldestInput;
        }

        public PredictionSendStatus Status { get; }

        public bool Predicted { get; }

        public KinematicState PredictedState { get; }

        public bool DroppedOldestInput { get; }
    }

    public enum PredictionPrepareStatus : byte
    {
        Prepared = 0,
        NotInitialized = 1,
        BufferTooSmall = 2,
        SequenceExhausted = 3,
        Disposed = 4,
    }

    public readonly struct PredictionPrepareResult
    {
        public PredictionPrepareResult(
            PredictionPrepareStatus status,
            int writtenBytes,
            in KinematicState predictedState,
            bool droppedOldestInput)
        {
            Status = status;
            WrittenBytes = writtenBytes;
            PredictedState = predictedState;
            DroppedOldestInput = droppedOldestInput;
        }

        public PredictionPrepareStatus Status { get; }

        public int WrittenBytes { get; }

        public KinematicState PredictedState { get; }

        public bool DroppedOldestInput { get; }
    }

    public enum SnapshotApplyStatus : byte
    {
        Initialized = 0,
        Reconciled = 1,
        Truncated = 2,
        WrongChannel = 3,
        WrongMessage = 4,
        Malformed = 5,
        ProtocolMismatch = 6,
        PlayerMissing = 7,
        StaleConnectionEpoch = 8,
        ConnectionEpochMismatch = 9,
        ArithmeticOverflow = 10,
        Disposed = 11,
    }

    public readonly struct SnapshotApplyResult
    {
        public SnapshotApplyResult(
            SnapshotApplyStatus status,
            uint connectionEpoch,
            bool hasReconciliation,
            in ReconciliationResult reconciliation,
            int correctionMagnitudeMillimetres)
        {
            Status = status;
            ConnectionEpoch = connectionEpoch;
            HasReconciliation = hasReconciliation;
            Reconciliation = reconciliation;
            CorrectionMagnitudeMillimetres = correctionMagnitudeMillimetres;
        }

        public SnapshotApplyStatus Status { get; }

        public uint ConnectionEpoch { get; }

        public bool HasReconciliation { get; }

        public ReconciliationResult Reconciliation { get; }

        public int CorrectionMagnitudeMillimetres { get; }
    }

    public readonly struct PredictionDiagnostics
    {
        public PredictionDiagnostics(
            long acceptedSnapshots,
            long corrections,
            long correctionsOver250Millimetres,
            int maximumCorrectionMillimetres,
            long historyMisses,
            long staleSnapshots,
            long droppedInputs)
        {
            AcceptedSnapshots = acceptedSnapshots;
            Corrections = corrections;
            CorrectionsOver250Millimetres = correctionsOver250Millimetres;
            MaximumCorrectionMillimetres = maximumCorrectionMillimetres;
            HistoryMisses = historyMisses;
            StaleSnapshots = staleSnapshots;
            DroppedInputs = droppedInputs;
        }

        public long AcceptedSnapshots { get; }

        public long Corrections { get; }

        public long CorrectionsOver250Millimetres { get; }

        public int MaximumCorrectionMillimetres { get; }

        public long HistoryMisses { get; }

        public long StaleSnapshots { get; }

        public long DroppedInputs { get; }
    }

    /// <summary>
    /// Unity-ready composition adapter for protocol-v1 input, acknowledgement, and
    /// bounded prediction history. The caller owns frame routing and presentation.
    /// </summary>
    public sealed class ClientPredictionAdapter : IAsyncDisposable
    {
        public const int RequiredInputBufferBytes = ClientPredictionProtocolV1.MaxInputFrameBytes;

        private readonly IRealtimeTransport _transport;
        private readonly ClientPredictionHistory _history;
        private readonly byte[] _sendBuffer = new byte[ClientPredictionProtocolV1.MaxDatagramBytes];
        private readonly bool _ownsTransport;
        private readonly uint _entityId;
        private uint _nextInputSequence;
        private uint _connectionEpoch;
        private int _sendInFlight;
        private bool _initialized;
        private bool _disposed;
        private long _acceptedSnapshots;
        private long _corrections;
        private long _correctionsOver250Millimetres;
        private int _maximumCorrectionMillimetres;
        private long _historyMisses;
        private long _staleSnapshots;

        public ClientPredictionAdapter(
            IRealtimeTransport transport,
            uint entityId,
            int historyCapacity = 256,
            bool ownsTransport = false)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            if (entityId == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(entityId));
            }

            _entityId = entityId;
            _history = new ClientPredictionHistory(historyCapacity);
            _ownsTransport = ownsTransport;
        }

        public uint EntityId => _entityId;

        public uint ConnectionEpoch => _connectionEpoch;

        public bool IsInitialized => _initialized;

        public PredictionDiagnostics Diagnostics => new PredictionDiagnostics(
            _acceptedSnapshots,
            _corrections,
            _correctionsOver250Millimetres,
            _maximumCorrectionMillimetres,
            _historyMisses,
            _staleSnapshots,
            _history.DroppedInputCount);

        public bool TryGetPredictedState(out KinematicState state)
        {
            if (!_initialized)
            {
                state = default;
                return false;
            }

            state = _history.Current;
            return true;
        }

        public PredictionPrepareResult PrepareInput(
            ulong roomTick,
            int moveXMilli,
            int moveZMilli,
            Span<byte> destination)
        {
            if (_disposed)
            {
                return new PredictionPrepareResult(
                    PredictionPrepareStatus.Disposed,
                    0,
                    default,
                    false);
            }

            if (!_initialized)
            {
                return new PredictionPrepareResult(
                    PredictionPrepareStatus.NotInitialized,
                    0,
                    default,
                    false);
            }

            if (destination.Length < RequiredInputBufferBytes)
            {
                return new PredictionPrepareResult(
                    PredictionPrepareStatus.BufferTooSmall,
                    0,
                    default,
                    false);
            }

            if (_nextInputSequence == 0)
            {
                return new PredictionPrepareResult(
                    PredictionPrepareStatus.SequenceExhausted,
                    0,
                    default,
                    false);
            }

            uint sequence = _nextInputSequence;
            KinematicInput input = new KinematicInput(sequence, moveXMilli, moveZMilli);
            KinematicState predicted = _history.Predict(input, out bool droppedOldest);
            _nextInputSequence = sequence == uint.MaxValue ? 0 : sequence + 1;
            bool encoded = ClientPredictionProtocolV1.TryEncodeInput(
                roomTick,
                input,
                destination,
                out int writtenBytes);
            if (!encoded)
            {
                throw new InvalidOperationException(
                    "The fixed protocol-v1 input buffer contract was violated.");
            }

            return new PredictionPrepareResult(
                PredictionPrepareStatus.Prepared,
                writtenBytes,
                predicted,
                droppedOldest);
        }

        public ValueTask<PredictionSendResult> SendInputAsync(
            ulong roomTick,
            int moveXMilli,
            int moveZMilli,
            CancellationToken cancellationToken = default)
        {
            if (_disposed)
            {
                return Completed(PredictionSendStatus.Disposed);
            }

            if (!_initialized)
            {
                return Completed(PredictionSendStatus.NotInitialized);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return Completed(PredictionSendStatus.Cancelled);
            }

            if (_transport.State != TransportState.Connected)
            {
                return Completed(PredictionSendStatus.TransportUnavailable);
            }

            if (Interlocked.CompareExchange(ref _sendInFlight, 1, 0) != 0)
            {
                return Completed(PredictionSendStatus.ConcurrentSend);
            }

            PredictionPrepareResult prepared = PrepareInput(
                roomTick,
                moveXMilli,
                moveZMilli,
                _sendBuffer);
            if (prepared.Status != PredictionPrepareStatus.Prepared)
            {
                Volatile.Write(ref _sendInFlight, 0);
                return Completed(
                    prepared.Status == PredictionPrepareStatus.SequenceExhausted
                        ? PredictionSendStatus.SequenceExhausted
                        : PredictionSendStatus.Faulted);
            }

            try
            {
                ValueTask<SendResult> pending = _transport.SendAsync(
                    ClientPredictionProtocolV1.InputChannel,
                    _sendBuffer.AsMemory(0, prepared.WrittenBytes),
                    cancellationToken);
                if (pending.IsCompletedSuccessfully)
                {
                    SendResult send = pending.Result;
                    Volatile.Write(ref _sendInFlight, 0);
                    return Completed(
                        MapSendStatus(send, prepared.WrittenBytes),
                        true,
                        prepared.PredictedState,
                        prepared.DroppedOldestInput);
                }

                return AwaitSendAsync(
                    pending,
                    prepared.WrittenBytes,
                    prepared.PredictedState,
                    prepared.DroppedOldestInput);
            }
            catch (OperationCanceledException)
            {
                Volatile.Write(ref _sendInFlight, 0);
                return Completed(
                    PredictionSendStatus.Cancelled,
                    true,
                    prepared.PredictedState,
                    prepared.DroppedOldestInput);
            }
            catch (Exception)
            {
                Volatile.Write(ref _sendInFlight, 0);
                return Completed(
                    PredictionSendStatus.Faulted,
                    true,
                    prepared.PredictedState,
                    prepared.DroppedOldestInput);
            }
        }

        public SnapshotApplyResult ApplyPacket(
            ReadOnlySpan<byte> frame,
            in ReceivedPacket packet)
        {
            if (_disposed)
            {
                return Failure(SnapshotApplyStatus.Disposed, packet.ConnectionEpoch);
            }

            if (!packet.IsComplete || packet.WrittenBytes > frame.Length)
            {
                return Failure(SnapshotApplyStatus.Truncated, packet.ConnectionEpoch);
            }

            ReadOnlySpan<byte> packetFrame = frame.Slice(0, packet.WrittenBytes);
            ClientProtocolDecodeStatus decode = ClientPredictionProtocolV1.TryDecodeSnapshot(
                packetFrame,
                _entityId,
                out TransportChannel expectedChannel,
                out DecodedAuthoritativeSnapshot decoded);
            if (decode != ClientProtocolDecodeStatus.Accepted)
            {
                return Failure(MapDecodeStatus(decode), packet.ConnectionEpoch);
            }

            if (!packet.Channel.Equals(expectedChannel))
            {
                return Failure(SnapshotApplyStatus.WrongChannel, packet.ConnectionEpoch);
            }

            uint authoritativeEpoch = decoded.IsReconnect
                ? decoded.ReconnectConnectionEpoch
                : packet.ConnectionEpoch;
            if (authoritativeEpoch == 0 ||
                (decoded.IsReconnect && authoritativeEpoch != packet.ConnectionEpoch))
            {
                return Failure(SnapshotApplyStatus.ConnectionEpochMismatch, authoritativeEpoch);
            }

            if (_connectionEpoch != 0 && authoritativeEpoch < _connectionEpoch)
            {
                return Failure(SnapshotApplyStatus.StaleConnectionEpoch, authoritativeEpoch);
            }

            _acceptedSnapshots++;
            _connectionEpoch = authoritativeEpoch;
            EnsureNextSequenceAfter(decoded.State.LastProcessedInputSequence);
            if (!_initialized)
            {
                _history.Initialize(decoded.State);
                _initialized = true;
                return new SnapshotApplyResult(
                    SnapshotApplyStatus.Initialized,
                    authoritativeEpoch,
                    false,
                    default,
                    0);
            }

            try
            {
                ReconciliationResult reconciliation = _history.Reconcile(decoded.State);
                int magnitude = CalculateMagnitude(
                    reconciliation.ErrorXMillimetres,
                    reconciliation.ErrorZMillimetres);
                RecordDiagnostics(reconciliation.Status, magnitude);
                return new SnapshotApplyResult(
                    SnapshotApplyStatus.Reconciled,
                    authoritativeEpoch,
                    true,
                    reconciliation,
                    magnitude);
            }
            catch (OverflowException)
            {
                _history.Initialize(decoded.State);
                return Failure(SnapshotApplyStatus.ArithmeticOverflow, authoritativeEpoch);
            }
        }

        public ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return default;
            }

            _disposed = true;
            return _ownsTransport ? _transport.DisposeAsync() : default;
        }

        private async ValueTask<PredictionSendResult> AwaitSendAsync(
            ValueTask<SendResult> pending,
            int writtenBytes,
            KinematicState predicted,
            bool droppedOldest)
        {
            try
            {
                SendResult send = await pending.ConfigureAwait(false);
                return new PredictionSendResult(
                    MapSendStatus(send, writtenBytes),
                    true,
                    predicted,
                    droppedOldest);
            }
            catch (OperationCanceledException)
            {
                return new PredictionSendResult(
                    PredictionSendStatus.Cancelled,
                    true,
                    predicted,
                    droppedOldest);
            }
            catch (Exception)
            {
                return new PredictionSendResult(
                    PredictionSendStatus.Faulted,
                    true,
                    predicted,
                    droppedOldest);
            }
            finally
            {
                Volatile.Write(ref _sendInFlight, 0);
            }
        }

        private void EnsureNextSequenceAfter(uint acknowledgement)
        {
            if (acknowledgement == uint.MaxValue)
            {
                _nextInputSequence = 0;
                return;
            }

            uint candidate = acknowledgement + 1;
            if (_nextInputSequence == 0 || candidate > _nextInputSequence)
            {
                _nextInputSequence = candidate;
            }
        }

        private void RecordDiagnostics(ReconciliationStatus status, int magnitude)
        {
            switch (status)
            {
                case ReconciliationStatus.Corrected:
                    _corrections++;
                    if (magnitude > 250)
                    {
                        _correctionsOver250Millimetres++;
                    }

                    if (magnitude > _maximumCorrectionMillimetres)
                    {
                        _maximumCorrectionMillimetres = magnitude;
                    }

                    break;
                case ReconciliationStatus.HistoryMiss:
                    _historyMisses++;
                    break;
                case ReconciliationStatus.StaleSnapshotIgnored:
                    _staleSnapshots++;
                    break;
            }
        }

        private static int CalculateMagnitude(int x, int z)
        {
            long squared = (long)x * x + (long)z * z;
            return checked((int)Math.Ceiling(Math.Sqrt(squared)));
        }

        private static PredictionSendStatus MapSendStatus(SendResult result, int writtenBytes)
        {
            switch (result.Status)
            {
                case SendStatus.Accepted:
                    return result.AcceptedBytes == writtenBytes
                        ? PredictionSendStatus.Accepted
                        : PredictionSendStatus.Faulted;
                case SendStatus.WouldBlock:
                    return PredictionSendStatus.WouldBlock;
                case SendStatus.DroppedByPolicy:
                    return PredictionSendStatus.DroppedByPolicy;
                case SendStatus.Closed:
                    return PredictionSendStatus.Closed;
                case SendStatus.PayloadTooLarge:
                    return PredictionSendStatus.PayloadTooLarge;
                default:
                    return PredictionSendStatus.Faulted;
            }
        }

        private static SnapshotApplyStatus MapDecodeStatus(ClientProtocolDecodeStatus status)
        {
            switch (status)
            {
                case ClientProtocolDecodeStatus.FrameTooShort:
                    return SnapshotApplyStatus.Truncated;
                case ClientProtocolDecodeStatus.FrameTooLarge:
                case ClientProtocolDecodeStatus.MalformedPayload:
                    return SnapshotApplyStatus.Malformed;
                case ClientProtocolDecodeStatus.ProtocolMismatch:
                    return SnapshotApplyStatus.ProtocolMismatch;
                case ClientProtocolDecodeStatus.PlayerMissing:
                    return SnapshotApplyStatus.PlayerMissing;
                default:
                    return SnapshotApplyStatus.WrongMessage;
            }
        }

        private static ValueTask<PredictionSendResult> Completed(
            PredictionSendStatus status,
            bool predicted = false,
            KinematicState predictedState = default,
            bool droppedOldest = false)
            => new ValueTask<PredictionSendResult>(
                new PredictionSendResult(status, predicted, predictedState, droppedOldest));

        private static SnapshotApplyResult Failure(
            SnapshotApplyStatus status,
            uint connectionEpoch)
            => new SnapshotApplyResult(status, connectionEpoch, false, default, 0);
    }
}
