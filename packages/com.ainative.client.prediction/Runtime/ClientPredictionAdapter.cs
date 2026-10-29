using System;
using System.Threading;
using System.Threading.Tasks;
using AiNative.Client.Protocol;
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
            bool droppedOldestInput
        )
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
        ArithmeticOverflow = 5,
    }

    public readonly struct PredictionPrepareResult
    {
        public PredictionPrepareResult(
            PredictionPrepareStatus status,
            int writtenBytes,
            in KinematicState predictedState,
            bool droppedOldestInput
        )
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
            int correctionMagnitudeMillimetres
        )
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
            long droppedInputs
        )
            : this(
                acceptedSnapshots,
                corrections,
                correctionsOver250Millimetres,
                maximumCorrectionMillimetres,
                historyMisses,
                staleSnapshots,
                droppedInputs,
                0,
                0,
                0
            ) { }

        public PredictionDiagnostics(
            long acceptedSnapshots,
            long corrections,
            long correctionsOver250Millimetres,
            int maximumCorrectionMillimetres,
            long historyMisses,
            long staleSnapshots,
            long droppedInputs,
            long reconciliationSamples,
            int correctionP95Millimetres,
            int correctionP99Millimetres
        )
        {
            AcceptedSnapshots = acceptedSnapshots;
            Corrections = corrections;
            CorrectionsOver250Millimetres = correctionsOver250Millimetres;
            MaximumCorrectionMillimetres = maximumCorrectionMillimetres;
            HistoryMisses = historyMisses;
            StaleSnapshots = staleSnapshots;
            DroppedInputs = droppedInputs;
            ReconciliationSamples = reconciliationSamples;
            CorrectionP95Millimetres = correctionP95Millimetres;
            CorrectionP99Millimetres = correctionP99Millimetres;
        }

        public long AcceptedSnapshots { get; }
        public long Corrections { get; }
        public long CorrectionsOver250Millimetres { get; }
        public int MaximumCorrectionMillimetres { get; }
        public long HistoryMisses { get; }
        public long StaleSnapshots { get; }
        public long DroppedInputs { get; }
        public long ReconciliationSamples { get; }
        public int CorrectionP95Millimetres { get; }
        public int CorrectionP99Millimetres { get; }
    }

    /// <summary>
    /// 可用于 Unity 的 protocol-v1 输入、确认和有界预测历史组合适配器；
    /// 帧路由及表现由调用方负责。
    /// </summary>
    public sealed class ClientPredictionAdapter : IAsyncDisposable
    {
        public int RequiredInputBufferBytes => _codec.MaximumInputFrameBytes;

        private readonly IBattleProtocolCodec _codec;
        private readonly BattlePacketBuffer _decoded = new BattlePacketBuffer();
        private const int CorrectionHistogramMaximumMillimetres = 8192;
        private readonly IRealtimeTransport _transport;
        private readonly ClientPredictionHistory _history;
        private readonly byte[] _sendBuffer;
        private readonly bool _ownsTransport;
        private readonly uint _entityId;
        private readonly long[] _correctionHistogram = new long[
            CorrectionHistogramMaximumMillimetres + 2
        ];
        private uint _nextInputSequence = 1;
        private long _authoritativeTick;
        private uint _authoritativeAcknowledgement;
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
        private long _reconciliationSamples;
        private long _droppedInputBaseline;

        public ClientPredictionAdapter(
            IRealtimeTransport transport,
            IBattleProtocolCodec codec,
            uint entityId,
            int historyCapacity = 256,
            bool ownsTransport = false
        )
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _codec = codec ?? throw new ArgumentNullException(nameof(codec));
            _sendBuffer = new byte[codec.MaximumInputFrameBytes];
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
        public PredictionDiagnostics Diagnostics =>
            new PredictionDiagnostics(
                _acceptedSnapshots,
                _corrections,
                _correctionsOver250Millimetres,
                _maximumCorrectionMillimetres,
                _historyMisses,
                _staleSnapshots,
                _history.DroppedInputCount - _droppedInputBaseline,
                _reconciliationSamples,
                CalculateCorrectionPercentile(95),
                CalculateCorrectionPercentile(99)
            );

        /// <summary>
        /// 开始新的有界诊断窗口，不改变预测状态。
        /// </summary>
        public void ResetDiagnostics()
        {
            if (_disposed)
            {
                return;
            }

            _acceptedSnapshots = 0;
            _corrections = 0;
            _correctionsOver250Millimetres = 0;
            _maximumCorrectionMillimetres = 0;
            _historyMisses = 0;
            _staleSnapshots = 0;
            _reconciliationSamples = 0;
            _droppedInputBaseline = _history.DroppedInputCount;
            Array.Clear(_correctionHistogram, 0, _correctionHistogram.Length);
        }

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

        /// <summary>最近一次成功准备的输入所使用的序号。</summary>
        public uint LastPreparedInputSequence =>
            _nextInputSequence == 0 ? uint.MaxValue : _nextInputSequence - 1;

        public PredictionPrepareResult PrepareInput(
            ulong roomTick,
            int moveXMilli,
            int moveZMilli,
            Span<byte> destination
        )
        {
            if (_disposed)
            {
                return new PredictionPrepareResult(
                    PredictionPrepareStatus.Disposed,
                    0,
                    default,
                    false
                );
            }

            if (!_initialized)
            {
                return new PredictionPrepareResult(
                    PredictionPrepareStatus.NotInitialized,
                    0,
                    default,
                    false
                );
            }

            if (destination.Length < RequiredInputBufferBytes)
            {
                return new PredictionPrepareResult(
                    PredictionPrepareStatus.BufferTooSmall,
                    0,
                    default,
                    false
                );
            }

            if (_nextInputSequence == 0)
            {
                return new PredictionPrepareResult(
                    PredictionPrepareStatus.SequenceExhausted,
                    0,
                    default,
                    false
                );
            }

            if (!CanReplaySafely(_history.Current))
            {
                return new PredictionPrepareResult(
                    PredictionPrepareStatus.ArithmeticOverflow,
                    0,
                    default,
                    false
                );
            }

            uint sequence = _nextInputSequence;
            KinematicInput input = new KinematicInput(sequence, moveXMilli, moveZMilli);
            if (!_codec.TryEncodeLegacyInput(roomTick, input, destination, out int writtenBytes))
            {
                return new PredictionPrepareResult(
                    PredictionPrepareStatus.BufferTooSmall,
                    0,
                    default,
                    false
                );
            }

            KinematicState predicted = _history.Predict(input, out bool droppedOldest);
            _nextInputSequence = sequence == uint.MaxValue ? 0 : sequence + 1;
            return new PredictionPrepareResult(
                PredictionPrepareStatus.Prepared,
                writtenBytes,
                predicted,
                droppedOldest
            );
        }

        public ValueTask<PredictionSendResult> SendInputAsync(
            ulong roomTick,
            int moveXMilli,
            int moveZMilli,
            CancellationToken cancellationToken = default
        )
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
                _sendBuffer
            );
            if (prepared.Status != PredictionPrepareStatus.Prepared)
            {
                Volatile.Write(ref _sendInFlight, 0);
                return Completed(
                    prepared.Status == PredictionPrepareStatus.SequenceExhausted
                        ? PredictionSendStatus.SequenceExhausted
                        : PredictionSendStatus.Faulted
                );
            }

            try
            {
                ValueTask<SendResult> pending = _transport.SendAsync(
                    _codec.InputChannel,
                    _sendBuffer.AsMemory(0, prepared.WrittenBytes),
                    cancellationToken
                );
                if (pending.IsCompletedSuccessfully)
                {
                    SendResult send = pending.Result;
                    Volatile.Write(ref _sendInFlight, 0);
                    return Completed(
                        MapSendStatus(send, prepared.WrittenBytes),
                        true,
                        prepared.PredictedState,
                        prepared.DroppedOldestInput
                    );
                }

                return AwaitSendAsync(
                    pending,
                    prepared.WrittenBytes,
                    prepared.PredictedState,
                    prepared.DroppedOldestInput
                );
            }
            catch (OperationCanceledException)
            {
                Volatile.Write(ref _sendInFlight, 0);
                return Completed(
                    PredictionSendStatus.Cancelled,
                    true,
                    prepared.PredictedState,
                    prepared.DroppedOldestInput
                );
            }
            catch (Exception)
            {
                Volatile.Write(ref _sendInFlight, 0);
                return Completed(
                    PredictionSendStatus.Faulted,
                    true,
                    prepared.PredictedState,
                    prepared.DroppedOldestInput
                );
            }
        }

        public SnapshotApplyResult ApplyPacket(ReadOnlySpan<byte> frame, in ReceivedPacket packet)
        {
            if (_disposed)
            {
                return Failure(SnapshotApplyStatus.Disposed, packet.ConnectionEpoch);
            }

            if (!packet.IsComplete || packet.WrittenBytes > frame.Length)
            {
                return Failure(SnapshotApplyStatus.Truncated, packet.ConnectionEpoch);
            }

            BattleDecodeStatus decode = _codec.TryDecode(
                frame.Slice(0, packet.WrittenBytes),
                _decoded
            );
            if (decode != BattleDecodeStatus.Accepted)
            {
                return Failure(MapDecodeStatus(decode), packet.ConnectionEpoch);
            }

            return ApplyDecoded(_decoded, packet);
        }

        public SnapshotApplyResult ApplyDecoded(
            BattlePacketBuffer decoded,
            in ReceivedPacket packet
        )
        {
            if (_disposed)
            {
                return Failure(SnapshotApplyStatus.Disposed, packet.ConnectionEpoch);
            }

            bool reconnect = decoded.Kind == BattleMessageKind.ReconnectResponse;
            if (!reconnect && decoded.Kind != BattleMessageKind.Snapshot)
            {
                return Failure(SnapshotApplyStatus.WrongMessage, packet.ConnectionEpoch);
            }

            if (!decoded.TryGetPlayer(_entityId, out var player))
            {
                return Failure(SnapshotApplyStatus.PlayerMissing, packet.ConnectionEpoch);
            }

            if (player.State.Tick < 0)
            {
                return Failure(SnapshotApplyStatus.ArithmeticOverflow, packet.ConnectionEpoch);
            }

            var state = new KinematicState(
                player.State.Tick,
                player.Acknowledgement,
                player.State.PositionXMillimetres,
                player.State.PositionZMillimetres
            );
            var expectedChannel = reconnect ? _codec.ControlChannel : _codec.SnapshotChannel;
            if (!packet.Channel.Equals(expectedChannel))
            {
                return Failure(SnapshotApplyStatus.WrongChannel, packet.ConnectionEpoch);
            }

            uint authoritativeEpoch = reconnect ? decoded.ConnectionEpoch : packet.ConnectionEpoch;
            if (
                authoritativeEpoch == 0
                || (reconnect && authoritativeEpoch != packet.ConnectionEpoch)
            )
            {
                return Failure(SnapshotApplyStatus.ConnectionEpochMismatch, authoritativeEpoch);
            }

            if (_connectionEpoch != 0 && authoritativeEpoch < _connectionEpoch)
            {
                return Failure(SnapshotApplyStatus.StaleConnectionEpoch, authoritativeEpoch);
            }

            if (
                _initialized
                && (
                    state.Tick < _authoritativeTick
                    || state.LastProcessedInputSequence < _authoritativeAcknowledgement
                )
            )
            {
                var current = _history.Current;
                var stale = new ReconciliationResult(
                    ReconciliationStatus.StaleSnapshotIgnored,
                    current,
                    current,
                    0,
                    0,
                    0,
                    0
                );
                _staleSnapshots++;
                return new SnapshotApplyResult(
                    SnapshotApplyStatus.Reconciled,
                    _connectionEpoch,
                    true,
                    stale,
                    0
                );
            }

            // Reconcile 在重放前修改环形队列；必须先证明数值余量足够，
            // 包括下一次输入；拒绝时必须保留历史、epoch 和序号。
            if (!CanReplaySafely(state))
            {
                return Failure(SnapshotApplyStatus.ArithmeticOverflow, authoritativeEpoch);
            }

            _acceptedSnapshots++;
            _authoritativeTick = state.Tick;
            _authoritativeAcknowledgement = state.LastProcessedInputSequence;
            _connectionEpoch = authoritativeEpoch;
            EnsureNextSequenceAfter(state.LastProcessedInputSequence);
            if (!_initialized)
            {
                _history.Initialize(state);
                _initialized = true;
                return new SnapshotApplyResult(
                    SnapshotApplyStatus.Initialized,
                    authoritativeEpoch,
                    false,
                    default,
                    0
                );
            }

            {
                ReconciliationResult reconciliation = _history.Reconcile(state);
                int magnitude = CalculateMagnitude(
                    reconciliation.ErrorXMillimetres,
                    reconciliation.ErrorZMillimetres
                );
                RecordDiagnostics(reconciliation.Status, magnitude);
                return new SnapshotApplyResult(
                    SnapshotApplyStatus.Reconciled,
                    authoritativeEpoch,
                    true,
                    reconciliation,
                    magnitude
                );
            }
        }

        /// <summary>纯数值准入检查，也用于提交传输 epoch 之前。
        /// 在所属线程连续完成检查与应用，两者之间不得准备新输入。</summary>
        public bool CanReplaySafely(in KinematicState state)
        {
            if (_disposed)
            {
                return false;
            }

            long steps = (_initialized ? _history.Count : 0) + 1L;
            if (state.Tick < 0 || state.Tick > long.MaxValue - steps)
            {
                return false;
            }

            long margin = steps * KinematicMovement.FullInputMillimetresPerTick;
            long x = state.PositionXMillimetres,
                z = state.PositionZMillimetres;
            if (
                x < int.MinValue + margin
                || x > int.MaxValue - margin
                || z < int.MinValue + margin
                || z > int.MaxValue - margin
            )
            {
                return false;
            }

            if (!_initialized)
            {
                return true;
            }

            // 任意已确认历史位置距离 Current 最多 Count 步。
            // 执行 int 校正减法前，两个方向均须预留该距离。
            var current = _history.Current;
            return Math.Abs(x - current.PositionXMillimetres) + margin <= int.MaxValue
                && Math.Abs(z - current.PositionZMillimetres) + margin <= int.MaxValue;
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
            bool droppedOldest
        )
        {
            try
            {
                SendResult send = await pending.ConfigureAwait(false);
                return new PredictionSendResult(
                    MapSendStatus(send, writtenBytes),
                    true,
                    predicted,
                    droppedOldest
                );
            }
            catch (OperationCanceledException)
            {
                return new PredictionSendResult(
                    PredictionSendStatus.Cancelled,
                    true,
                    predicted,
                    droppedOldest
                );
            }
            catch (Exception)
            {
                return new PredictionSendResult(
                    PredictionSendStatus.Faulted,
                    true,
                    predicted,
                    droppedOldest
                );
            }
            finally
            {
                Volatile.Write(ref _sendInFlight, 0);
            }
        }

        private void EnsureNextSequenceAfter(uint acknowledgement)
        {
            if (_nextInputSequence == 0)
            {
                return; // Exhaustion is terminal for this session.
            }

            if (acknowledgement == uint.MaxValue)
            {
                _nextInputSequence = 0;
                return;
            }

            uint candidate = acknowledgement + 1;
            if (candidate > _nextInputSequence)
            {
                _nextInputSequence = candidate;
            }
        }

        private void RecordDiagnostics(ReconciliationStatus status, int magnitude)
        {
            switch (status)
            {
                case ReconciliationStatus.Matched:
                    RecordCorrectionSample(magnitude);
                    break;
                case ReconciliationStatus.Corrected:
                    RecordCorrectionSample(magnitude);
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

        private void RecordCorrectionSample(int magnitude)
        {
            int bucket =
                magnitude <= CorrectionHistogramMaximumMillimetres
                    ? magnitude
                    : CorrectionHistogramMaximumMillimetres + 1;
            _correctionHistogram[bucket]++;
            _reconciliationSamples++;
        }

        private int CalculateCorrectionPercentile(int percentile)
        {
            if (_reconciliationSamples == 0)
            {
                return 0;
            }

            long rank = (long)Math.Ceiling(_reconciliationSamples * (percentile / 100d));
            long cumulative = 0;
            for (int index = 0; index < _correctionHistogram.Length; index++)
            {
                cumulative += _correctionHistogram[index];
                if (cumulative >= rank)
                {
                    return index <= CorrectionHistogramMaximumMillimetres
                        ? index
                        : _maximumCorrectionMillimetres;
                }
            }

            return _maximumCorrectionMillimetres;
        }

        private static int CalculateMagnitude(int x, int z)
        {
            double squared = (double)x * x + (double)z * z;
            return (int)Math.Min(int.MaxValue, Math.Ceiling(Math.Sqrt(squared)));
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

        private static SnapshotApplyStatus MapDecodeStatus(BattleDecodeStatus status)
        {
            switch (status)
            {
                case BattleDecodeStatus.FrameTooShort:
                    return SnapshotApplyStatus.Truncated;
                case BattleDecodeStatus.FrameTooLarge:
                case BattleDecodeStatus.MalformedPayload:
                    return SnapshotApplyStatus.Malformed;
                case BattleDecodeStatus.ProtocolMismatch:
                    return SnapshotApplyStatus.ProtocolMismatch;
                case BattleDecodeStatus.PlayerMissing:
                    return SnapshotApplyStatus.PlayerMissing;
                default:
                    return SnapshotApplyStatus.WrongMessage;
            }
        }

        private static ValueTask<PredictionSendResult> Completed(
            PredictionSendStatus status,
            bool predicted = false,
            KinematicState predictedState = default,
            bool droppedOldest = false
        ) =>
            new ValueTask<PredictionSendResult>(
                new PredictionSendResult(status, predicted, predictedState, droppedOldest)
            );

        private static SnapshotApplyResult Failure(
            SnapshotApplyStatus status,
            uint connectionEpoch
        ) => new SnapshotApplyResult(status, connectionEpoch, false, default, 0);
    }
}
