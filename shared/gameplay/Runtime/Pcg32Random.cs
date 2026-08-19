namespace AiNative.Gameplay
{
    /// <summary>
    /// Portable PCG-XSH-RR 32 implementation with explicit replay state.
    /// </summary>
    public sealed class Pcg32Random : IRandomSource
    {
        private const ulong Multiplier = 6364136223846793005UL;
        private ulong _state;
        private ulong _stream;

        public Pcg32Random(ulong seed, ulong sequence)
        {
            _stream = (sequence << 1) | 1UL;
            _state = 0;
            NextUInt32();
            _state = unchecked(_state + seed);
            NextUInt32();
        }

        public uint NextUInt32()
        {
            ulong oldState = _state;
            _state = unchecked(oldState * Multiplier + _stream);
            uint xorShifted = (uint)(((oldState >> 18) ^ oldState) >> 27);
            int rotation = (int)(oldState >> 59);
            return (xorShifted >> rotation) | (xorShifted << ((-rotation) & 31));
        }

        public RandomState CaptureState() => new RandomState(_state, _stream);

        public void RestoreState(in RandomState state)
        {
            _state = state.State;
            _stream = state.Stream;
        }
    }
}
