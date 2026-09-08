using System;
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
        public const int RequiredInputBufferBytes = ArenaClientProtocolV1.MaxInputFrameBytes;

        private readonly IRealtimeTransport _transport;
        private readonly ArenaPredictionHistory _history;
        private readonly byte[] _sendBuffer = new byte[RequiredInputBufferBytes];
        private readonly bool _ownsTransport;
        private readonly uint _entityId;
        private uint _nextSequence = 1;
        private bool _initialized;
        private bool _disposed;
        private long _acceptedSnapshots;
        private long _historyMisses;
        private long _corrections;

        public ArenaClientPredictionAdapter(
            IRealtimeTransport transport,
            uint entityId,
            int historyCapacity = 256,
            bool ownsTransport = false)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
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
            _initialized = true;
        }

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

            uint sequence = _nextSequence;
            ArenaInput input = new(sequence, clientTick, moveXMilli, moveZMilli, lookYawMilli, lookPitchMilli, buttons, weapon);
            ArenaPlayerState predicted = _history.Predict(input, out bool droppedOldest);
            _nextSequence = sequence == uint.MaxValue ? 0 : sequence + 1;
            if (!ArenaClientProtocolV1.TryEncodeInput(input, destination, out int writtenBytes))
            {
                throw new InvalidOperationException("The arena protocol input buffer contract was violated.");
            }

            return new ArenaPredictionPrepareResult(ArenaPredictionPrepareStatus.Prepared, writtenBytes, predicted, droppedOldest);
        }

        public ValueTask<SendResult> SendPreparedAsync(int writtenBytes)
        {
            if (_disposed) return new ValueTask<SendResult>(new SendResult(SendStatus.Closed, 0));
            if (writtenBytes < 0 || writtenBytes > _sendBuffer.Length) throw new ArgumentOutOfRangeException(nameof(writtenBytes));
            return _transport.SendAsync(ArenaClientProtocolV1.InputChannel, _sendBuffer.AsMemory(0, writtenBytes));
        }

        public ArenaPredictionPrepareResult PrepareAndSendInput(
            ulong clientTick,
            int moveXMilli,
            int moveZMilli,
            int lookYawMilli,
            int lookPitchMilli,
            ArenaButtons buttons,
            ArenaWeaponId weapon)
        {
            ArenaPredictionPrepareResult result = PrepareInput(
                clientTick, moveXMilli, moveZMilli, lookYawMilli, lookPitchMilli, buttons, weapon, _sendBuffer);
            if (result.Status == ArenaPredictionPrepareStatus.Prepared)
            {
                _ = _transport.SendAsync(ArenaClientProtocolV1.InputChannel, _sendBuffer.AsMemory(0, result.WrittenBytes));
            }

            return result;
        }

        public ArenaSnapshotApplyResult ApplySnapshot(ReadOnlySpan<byte> frame, in ReceivedPacket packet)
        {
            if (_disposed) return new ArenaSnapshotApplyResult(ArenaSnapshotApplyStatus.Disposed, default, default);
            if (!packet.IsComplete || packet.WrittenBytes > frame.Length)
            {
                return new ArenaSnapshotApplyResult(ArenaSnapshotApplyStatus.Malformed, default, default);
            }

            if (!ArenaClientProtocolV1.TryDecodeSnapshot(frame.Slice(0, packet.WrittenBytes), _entityId, out DecodedArenaSnapshot decoded))
            {
                return new ArenaSnapshotApplyResult(ArenaSnapshotApplyStatus.Malformed, default, default);
            }

            if (!packet.Channel.Equals(ArenaClientProtocolV1.SnapshotChannel))
            {
                return new ArenaSnapshotApplyResult(ArenaSnapshotApplyStatus.WrongChannel, default, default);
            }

            _acceptedSnapshots++;
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

        public ValueTask DisposeAsync()
        {
            if (_disposed) return default;
            _disposed = true;
            return _ownsTransport ? _transport.DisposeAsync() : default;
        }

        private void EnsureSequenceAfter(uint acknowledgement)
        {
            if (acknowledgement == uint.MaxValue)
            {
                _nextSequence = 0;
                return;
            }

            uint candidate = acknowledgement + 1;
            if (_nextSequence == 0 || candidate > _nextSequence) _nextSequence = candidate;
        }
    }
}
