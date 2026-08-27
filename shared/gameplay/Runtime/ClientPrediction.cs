using System;

namespace AiNative.Gameplay
{
    public readonly struct KinematicInput
    {
        public KinematicInput(uint sequence, int moveXMilli, int moveZMilli)
        {
            if (sequence == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sequence));
            }

            Sequence = sequence;
            MoveXMilli = moveXMilli;
            MoveZMilli = moveZMilli;
        }

        public uint Sequence { get; }

        public int MoveXMilli { get; }

        public int MoveZMilli { get; }
    }

    public readonly struct KinematicState : IEquatable<KinematicState>
    {
        public KinematicState(
            long tick,
            uint lastProcessedInputSequence,
            int positionXMillimetres,
            int positionZMillimetres)
        {
            if (tick < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(tick));
            }

            Tick = tick;
            LastProcessedInputSequence = lastProcessedInputSequence;
            PositionXMillimetres = positionXMillimetres;
            PositionZMillimetres = positionZMillimetres;
        }

        public long Tick { get; }

        public uint LastProcessedInputSequence { get; }

        public int PositionXMillimetres { get; }

        public int PositionZMillimetres { get; }

        public bool Equals(KinematicState other)
            => Tick == other.Tick &&
               LastProcessedInputSequence == other.LastProcessedInputSequence &&
               PositionXMillimetres == other.PositionXMillimetres &&
               PositionZMillimetres == other.PositionZMillimetres;

        public override bool Equals(object obj)
            => obj is KinematicState other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = Tick.GetHashCode();
                hash = (hash * 397) ^ (int)LastProcessedInputSequence;
                hash = (hash * 397) ^ PositionXMillimetres;
                hash = (hash * 397) ^ PositionZMillimetres;
                return hash;
            }
        }

        public static bool operator ==(KinematicState left, KinematicState right)
            => left.Equals(right);

        public static bool operator !=(KinematicState left, KinematicState right)
            => !left.Equals(right);
    }

    public static class KinematicMovement
    {
        public const int MaximumInputMagnitude = 1000;
        public const int FullInputMillimetresPerTick = 50;

        public static KinematicState Step(in KinematicState state, in KinematicInput input)
        {
            if (input.Sequence <= state.LastProcessedInputSequence)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(input),
                    "Prediction input sequences must increase monotonically.");
            }

            return new KinematicState(
                checked(state.Tick + 1),
                input.Sequence,
                ApplyAxis(state.PositionXMillimetres, input.MoveXMilli),
                ApplyAxis(state.PositionZMillimetres, input.MoveZMilli));
        }

        public static int ApplyAxis(int positionMillimetres, int inputMilli)
        {
            int clamped = Math.Max(
                -MaximumInputMagnitude,
                Math.Min(MaximumInputMagnitude, inputMilli));
            int displacement = clamped * FullInputMillimetresPerTick / MaximumInputMagnitude;
            return checked(positionMillimetres + displacement);
        }
    }

    public enum ReconciliationStatus
    {
        Matched = 0,
        Corrected = 1,
        StaleSnapshotIgnored = 2,
        AuthoritativeAhead = 3,
        HistoryMiss = 4,
    }

    public readonly struct ReconciliationResult
    {
        public ReconciliationResult(
            ReconciliationStatus status,
            in KinematicState before,
            in KinematicState after,
            int errorXMillimetres,
            int errorZMillimetres,
            int discardedInputCount,
            int replayedInputCount)
        {
            Status = status;
            Before = before;
            After = after;
            ErrorXMillimetres = errorXMillimetres;
            ErrorZMillimetres = errorZMillimetres;
            DiscardedInputCount = discardedInputCount;
            ReplayedInputCount = replayedInputCount;
        }

        public ReconciliationStatus Status { get; }

        public KinematicState Before { get; }

        public KinematicState After { get; }

        public int ErrorXMillimetres { get; }

        public int ErrorZMillimetres { get; }

        public int DiscardedInputCount { get; }

        public int ReplayedInputCount { get; }
    }

    public sealed class ClientPredictionHistory
    {
        private const int MaximumCapacity = 1024;
        private readonly KinematicInput[] _inputs;
        private readonly KinematicState[] _states;
        private KinematicState _baseline;
        private KinematicState _current;
        private int _start;
        private int _count;
        private bool _initialized;

        public ClientPredictionHistory(int capacity)
        {
            if (capacity < 2 || capacity > MaximumCapacity)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            _inputs = new KinematicInput[capacity];
            _states = new KinematicState[capacity];
        }

        public int Capacity => _inputs.Length;

        public int Count => _count;

        public long DroppedInputCount { get; private set; }

        public KinematicState Current
        {
            get
            {
                EnsureInitialized();
                return _current;
            }
        }

        public void Initialize(in KinematicState authoritativeState)
        {
            _baseline = authoritativeState;
            _current = authoritativeState;
            _start = 0;
            _count = 0;
            _initialized = true;
        }

        public KinematicState Predict(in KinematicInput input, out bool droppedOldest)
        {
            EnsureInitialized();
            KinematicState predicted = KinematicMovement.Step(_current, input);
            droppedOldest = false;

            if (_count == Capacity)
            {
                _baseline = _states[_start];
                _start = (_start + 1) % Capacity;
                _count--;
                DroppedInputCount++;
                droppedOldest = true;
            }

            int writeIndex = (_start + _count) % Capacity;
            _inputs[writeIndex] = input;
            _states[writeIndex] = predicted;
            _count++;
            _current = predicted;
            return predicted;
        }

        public ReconciliationResult Reconcile(in KinematicState authoritativeState)
        {
            EnsureInitialized();
            KinematicState before = _current;

            if (authoritativeState.LastProcessedInputSequence <
                    _baseline.LastProcessedInputSequence ||
                (authoritativeState.LastProcessedInputSequence ==
                    _baseline.LastProcessedInputSequence &&
                 authoritativeState.Tick < _baseline.Tick))
            {
                return new ReconciliationResult(
                    ReconciliationStatus.StaleSnapshotIgnored,
                    before,
                    before,
                    0,
                    0,
                    0,
                    0);
            }

            if (authoritativeState.LastProcessedInputSequence >
                _current.LastProcessedInputSequence)
            {
                Initialize(authoritativeState);
                return new ReconciliationResult(
                    ReconciliationStatus.AuthoritativeAhead,
                    before,
                    _current,
                    0,
                    0,
                    0,
                    0);
            }

            KinematicState predictedAtAcknowledgement = _baseline;
            int discardCount = 0;
            if (authoritativeState.LastProcessedInputSequence !=
                _baseline.LastProcessedInputSequence)
            {
                int acknowledgementOffset = FindSequence(
                    authoritativeState.LastProcessedInputSequence);
                if (acknowledgementOffset < 0)
                {
                    Initialize(authoritativeState);
                    return new ReconciliationResult(
                        ReconciliationStatus.HistoryMiss,
                        before,
                        _current,
                        0,
                        0,
                        0,
                        0);
                }

                int acknowledgementIndex = (_start + acknowledgementOffset) % Capacity;
                predictedAtAcknowledgement = _states[acknowledgementIndex];
                discardCount = acknowledgementOffset + 1;
            }

            int errorX = checked(
                authoritativeState.PositionXMillimetres -
                predictedAtAcknowledgement.PositionXMillimetres);
            int errorZ = checked(
                authoritativeState.PositionZMillimetres -
                predictedAtAcknowledgement.PositionZMillimetres);

            _start = (_start + discardCount) % Capacity;
            _count -= discardCount;
            _baseline = authoritativeState;
            KinematicState replayed = authoritativeState;
            for (int offset = 0; offset < _count; offset++)
            {
                int index = (_start + offset) % Capacity;
                replayed = KinematicMovement.Step(replayed, _inputs[index]);
                _states[index] = replayed;
            }

            _current = replayed;
            ReconciliationStatus status = errorX == 0 && errorZ == 0
                ? ReconciliationStatus.Matched
                : ReconciliationStatus.Corrected;
            return new ReconciliationResult(
                status,
                before,
                _current,
                errorX,
                errorZ,
                discardCount,
                _count);
        }

        private int FindSequence(uint sequence)
        {
            for (int offset = 0; offset < _count; offset++)
            {
                int index = (_start + offset) % Capacity;
                if (_inputs[index].Sequence == sequence)
                {
                    return offset;
                }
            }

            return -1;
        }

        private void EnsureInitialized()
        {
            if (!_initialized)
            {
                throw new InvalidOperationException(
                    "Prediction history must be initialized from an authoritative state.");
            }
        }
    }
}
