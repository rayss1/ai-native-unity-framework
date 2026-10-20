using AiNative.Client.Protocol;
using System;
using System.Threading;
using System.Threading.Tasks;
using AiNative.Gameplay;
using AiNative.Realtime;

namespace AiNative.Client.Prediction
{
    public enum ArenaPredictionPrepareStatus : byte
    {
        Prepared = 0,
        NotInitialized = 1,
        BufferTooSmall = 2,
        SequenceExhausted = 3,
        Disposed = 4,
        ArithmeticOverflow = 5,
    }

    public readonly struct ArenaPredictionPrepareResult
    {
        public ArenaPredictionPrepareResult(
            ArenaPredictionPrepareStatus status,
            int writtenBytes,
            in ArenaPlayerState predictedState,
            bool droppedOldestInput)
        {
            Status = status;
            WrittenBytes = writtenBytes;
            PredictedState = predictedState;
            DroppedOldestInput = droppedOldestInput;
        }

        public ArenaPredictionPrepareStatus Status { get; }
        public int WrittenBytes { get; }
        public ArenaPlayerState PredictedState { get; }
        public bool DroppedOldestInput { get; }
    }

    public enum ArenaSnapshotApplyStatus : byte
    {
        Initialized = 0,
        Reconciled = 1,
        WrongChannel = 2,
        Malformed = 3,
        ProtocolMismatch = 4,
        PlayerMissing = 5,
        Disposed = 6,
    }

    public readonly struct ArenaSnapshotApplyResult
    {
        public ArenaSnapshotApplyResult(
            ArenaSnapshotApplyStatus status,
            in ArenaPlayerState state,
            in ReconciliationResult reconciliation)
        {
            Status = status;
            State = state;
            Reconciliation = reconciliation;
        }

        public ArenaSnapshotApplyStatus Status { get; }
        public ArenaPlayerState State { get; }
        public ReconciliationResult Reconciliation { get; }
    }

    /// <summary>
    /// Client-side prediction adapter for the arena FPS state. It keeps transport
    /// and Unity types outside the Shared Gameplay rules and uses caller-owned buffers.
    /// </summary>
    public sealed class ArenaClientPredictionAdapter : IAsyncDisposable
    {
        public int RequiredInputBufferBytes => _codec.MaximumInputFrameBytes;
        private readonly IBattleProtocolCodec _codec;
        private readonly BattlePacketBuffer _decoded = new BattlePacketBuffer();

        private readonly IRealtimeTransport _transport;
        private readonly ArenaPredictionHistory _history;
        private readonly bool _ownsTransport;
        private readonly uint _entityId;
        private ulong _authoritativeTick;
        private uint _authoritativeAcknowledgement;
        private uint _connectionEpoch;
        private uint _nextSequence = 1;
        private bool _initialized;
        private bool _disposed;
        private long _acceptedSnapshots;
        private long _historyMisses;
        private long _corrections;

        public ArenaClientPredictionAdapter(
            IRealtimeTransport transport,
            IBattleProtocolCodec codec,
            uint entityId,
            int historyCapacity = 256,
            bool ownsTransport = false)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _codec = codec ?? throw new ArgumentNullException(nameof(codec));
            if (entityId == 0) throw new ArgumentOutOfRangeException(nameof(entityId));
            _entityId = entityId;
            _history = new ArenaPredictionHistory(historyCapacity);
            _ownsTransport = ownsTransport;
        }

        public bool IsInitialized => _initialized;
        public uint EntityId => _entityId;
        public ArenaPlayerState Current => _history.Current;
        public long AcceptedSnapshots => _acceptedSnapshots;
        public long HistoryMisses => _historyMisses;
        public long Corrections => _corrections;

        public void Initialize(in ArenaPlayerState state)
        {
            if (_disposed) return;
            _history.Initialize(state);
            _authoritativeTick = checked((ulong)state.Tick);
            _authoritativeAcknowledgement = state.LastProcessedInputSequence;
            _initialized = true;
            EnsureSequenceAfter(state.LastProcessedInputSequence);
        }

        /// <summary>Predicts locally while stamping admission time from the latest authority snapshot.
        /// The clientTick argument is retained for source compatibility; it does not control the wire clock.</summary>
        public ArenaPredictionPrepareResult PrepareInput(
            ulong clientTick,
            int moveXMilli,
            int moveZMilli,
            int lookYawMilli,
            int lookPitchMilli,
            ArenaButtons buttons,
            ArenaWeaponId weapon,
            Span<byte> destination)
        {
            if (_disposed) return new ArenaPredictionPrepareResult(ArenaPredictionPrepareStatus.Disposed, 0, default, false);
            if (!_initialized) return new ArenaPredictionPrepareResult(ArenaPredictionPrepareStatus.NotInitialized, 0, default, false);
            if (destination.Length < RequiredInputBufferBytes) return new ArenaPredictionPrepareResult(ArenaPredictionPrepareStatus.BufferTooSmall, 0, default, false);
            if (_nextSequence == 0) return new ArenaPredictionPrepareResult(ArenaPredictionPrepareStatus.SequenceExhausted, 0, default, false);

            if (!CanReplaySafely(_history.Current))
                return new ArenaPredictionPrepareResult(ArenaPredictionPrepareStatus.ArithmeticOverflow, 0, default, false);
            uint sequence = _nextSequence;
            clientTick = checked(_authoritativeTick + 1);
            ArenaInput input = new(sequence, clientTick, moveXMilli, moveZMilli, lookYawMilli, lookPitchMilli, buttons, weapon);
            if (!_codec.TryEncodeInput(input, destination, out int writtenBytes))
                return new ArenaPredictionPrepareResult(ArenaPredictionPrepareStatus.BufferTooSmall, 0, default, false);
            ArenaPlayerState predicted = _history.Predict(input, out bool droppedOldest);
            _nextSequence = sequence == uint.MaxValue ? 0 : sequence + 1;

            return new ArenaPredictionPrepareResult(ArenaPredictionPrepareStatus.Prepared, writtenBytes, predicted, droppedOldest);
        }

        /// <summary>
        /// Sends the caller's encoded input without advancing prediction. The caller must
        /// keep the frame unchanged until completion and observe the returned send result.
        /// A rejected send may retry the same frame; do not prepare another input for a retry.
        /// Call outside the fixed-Tick critical path. Cancellation and transport exceptions
        /// propagate to the caller, just as they do on IRealtimeTransport.
        /// </summary>
        public ValueTask<SendResult> SendPreparedAsync(
            ReadOnlyMemory<byte> frame,
            CancellationToken cancellationToken = default)
        {
            if (_disposed) return new ValueTask<SendResult>(new SendResult(SendStatus.Closed, 0));
            if (frame.IsEmpty) throw new ArgumentException("A prepared input frame must not be empty.", nameof(frame));
            if (frame.Length > RequiredInputBufferBytes)
                return new ValueTask<SendResult>(new SendResult(SendStatus.PayloadTooLarge, 0));
            if (cancellationToken.IsCancellationRequested)
                return new ValueTask<SendResult>(Task.FromCanceled<SendResult>(cancellationToken));
            return _transport.SendAsync(_codec.InputChannel, frame, cancellationToken);
        }

        public ArenaSnapshotApplyResult ApplySnapshot(ReadOnlySpan<byte> frame, in ReceivedPacket packet)
        {
            if (_disposed) return new ArenaSnapshotApplyResult(ArenaSnapshotApplyStatus.Disposed, default, default);
            if (!packet.IsComplete || packet.WrittenBytes > frame.Length)
            {
                return new ArenaSnapshotApplyResult(ArenaSnapshotApplyStatus.Malformed, default, default);
            }

            if (!packet.Channel.Equals(_codec.SnapshotChannel) && !packet.Channel.Equals(_codec.ControlChannel))
                return new ArenaSnapshotApplyResult(ArenaSnapshotApplyStatus.WrongChannel, default, default);
            if (_codec.TryDecode(frame.Slice(0, packet.WrittenBytes), _decoded) != BattleDecodeStatus.Accepted)
                return new ArenaSnapshotApplyResult(ArenaSnapshotApplyStatus.Malformed, default, default);
            return ApplyDecoded(_decoded, packet);
        }

        public ArenaSnapshotApplyResult ApplyDecoded(BattlePacketBuffer message, in ReceivedPacket packet)
        {
            if (_disposed) return new ArenaSnapshotApplyResult(ArenaSnapshotApplyStatus.Disposed, default, default);
            bool reconnect = message.Kind == BattleMessageKind.ReconnectResponse;
            if (!packet.Channel.Equals(reconnect ? _codec.ControlChannel : _codec.SnapshotChannel))
                return new ArenaSnapshotApplyResult(ArenaSnapshotApplyStatus.WrongChannel, default, default);
            uint epoch = reconnect ? message.ConnectionEpoch : packet.ConnectionEpoch;
            if (epoch == 0 || epoch < _connectionEpoch || (reconnect && epoch != packet.ConnectionEpoch))
                return new ArenaSnapshotApplyResult(ArenaSnapshotApplyStatus.Malformed, default, default);
            if (message.Kind != BattleMessageKind.Snapshot && message.Kind != BattleMessageKind.ReconnectResponse)
                return new ArenaSnapshotApplyResult(ArenaSnapshotApplyStatus.Malformed, default, default);
            if (!message.TryGetPlayer(_entityId, out DecodedArenaSnapshot decoded))
                return new ArenaSnapshotApplyResult(ArenaSnapshotApplyStatus.PlayerMissing, default, default);
            if (decoded.State.Tick < 0) return new ArenaSnapshotApplyResult(ArenaSnapshotApplyStatus.Malformed, default, default);
            if (_initialized && ((ulong)decoded.State.Tick < _authoritativeTick || decoded.Acknowledgement < _authoritativeAcknowledgement))
            {
                var current = _history.Current;
                var kinematic = new KinematicState(current.Tick, current.LastProcessedInputSequence, current.PositionXMillimetres, current.PositionZMillimetres);
                var stale = new ReconciliationResult(ReconciliationStatus.StaleSnapshotIgnored, kinematic, kinematic, 0, 0, 0, 0);
                return new ArenaSnapshotApplyResult(ArenaSnapshotApplyStatus.Reconciled, current, stale);
            }
            // Shared history commits its ring before replay. Reject only states whose
            // numeric ranges cannot safely accommodate pending replay and a next step.
            if (!CanReplaySafely(decoded.State))
                return new ArenaSnapshotApplyResult(ArenaSnapshotApplyStatus.Malformed, default, default);
            _acceptedSnapshots++;
            _authoritativeTick = checked((ulong)decoded.State.Tick);
            _authoritativeAcknowledgement = decoded.Acknowledgement;
            _connectionEpoch = epoch;
            if (!_initialized)
            {
                _history.Initialize(decoded.State);
                _initialized = true;
                EnsureSequenceAfter(decoded.Acknowledgement);
                return new ArenaSnapshotApplyResult(ArenaSnapshotApplyStatus.Initialized, decoded.State, default);
            }

            ReconciliationResult reconciliation = _history.Reconcile(decoded.State);
            if (reconciliation.Status == ReconciliationStatus.HistoryMiss) _historyMisses++;
            if (reconciliation.Status == ReconciliationStatus.Corrected) _corrections++;
            EnsureSequenceAfter(decoded.Acknowledgement);
            return new ArenaSnapshotApplyResult(ArenaSnapshotApplyStatus.Reconciled, _history.Current, reconciliation);
        }

        /// <summary>Pure numeric admission check, also used before committing a transport epoch.
        /// Call and apply on the owner thread without preparing intervening inputs.</summary>
        public bool CanReplaySafely(in ArenaPlayerState state)
        {
            if (_disposed) return false;
            long steps = (_initialized ? _history.Count : 0) + 1L;
            if (state.Tick < 0 || state.Tick > long.MaxValue - steps) return false;
            long horizontalSpeed = Math.Max(ArenaMovement.MaximumGroundSpeedMillimetresPerSecond,
                Math.Max(Math.Abs((long)state.VelocityXMillimetresPerSecond), Math.Abs((long)state.VelocityZMillimetresPerSecond)));
            // Accelerate subtracts the current velocity from a bounded target.
            if (horizontalSpeed > int.MaxValue - ArenaMovement.MaximumGroundSpeedMillimetresPerSecond) return false;
            long verticalSpeed = Math.Max(Math.Abs((long)state.VelocityYMillimetresPerSecond), ArenaMovement.JumpSpeedMillimetresPerSecond) +
                steps * (ArenaMovement.GravityMillimetresPerSecondSquared / ArenaMovement.TickRate);
            if (verticalSpeed > int.MaxValue) return false;
            long horizontalMargin = steps * (horizontalSpeed / ArenaMovement.TickRate + 1) +
                ArenaTrainingMap.HalfExtent + ArenaTrainingMap.PlayerRadius;
            long verticalMargin = steps * (verticalSpeed / ArenaMovement.TickRate + 1) + ArenaTrainingMap.PlayerHeight;
            long x = state.PositionXMillimetres, y = state.PositionYMillimetres, z = state.PositionZMillimetres;
            if (x < int.MinValue + horizontalMargin || x > int.MaxValue - horizontalMargin ||
                z < int.MinValue + horizontalMargin || z > int.MaxValue - horizontalMargin ||
                y < int.MinValue + verticalMargin || y > int.MaxValue - verticalMargin) return false;
            if (!_initialized) return true;
            var current = _history.Current;
            return Math.Abs(x - current.PositionXMillimetres) + horizontalMargin <= int.MaxValue &&
                Math.Abs(z - current.PositionZMillimetres) + horizontalMargin <= int.MaxValue &&
                Math.Abs((long)current.PositionXMillimetres) + ArenaTrainingMap.HalfExtent < int.MaxValue &&
                Math.Abs((long)current.PositionZMillimetres) + ArenaTrainingMap.HalfExtent < int.MaxValue;
        }

        public ValueTask DisposeAsync()
        {
            if (_disposed) return default;
            _disposed = true;
            return _ownsTransport ? _transport.DisposeAsync() : default;
        }

        private void EnsureSequenceAfter(uint acknowledgement)
        {
            if (_nextSequence == 0) return; // Exhaustion is terminal for this session.
            if (acknowledgement == uint.MaxValue)
            {
                _nextSequence = 0;
                return;
            }

            uint candidate = acknowledgement + 1;
            if (candidate > _nextSequence) _nextSequence = candidate;
        }
    }
}
