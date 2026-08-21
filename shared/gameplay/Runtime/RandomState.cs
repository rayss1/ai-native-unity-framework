using System;

namespace AiNative.Gameplay
{
    public readonly struct RandomState : IEquatable<RandomState>
    {
        public RandomState(ulong state, ulong stream)
        {
            if ((stream & 1UL) == 0)
            {
                throw new ArgumentException("The PCG stream increment must be odd.", nameof(stream));
            }

            State = state;
            Stream = stream;
        }

        public ulong State { get; }

        public ulong Stream { get; }

        public bool Equals(RandomState other) => State == other.State && Stream == other.Stream;

        public override bool Equals(object obj) => obj is RandomState other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(State, Stream);
    }
}
