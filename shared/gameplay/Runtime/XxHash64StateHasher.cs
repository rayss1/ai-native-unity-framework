using System;
using System.Buffers.Binary;

namespace AiNative.Gameplay
{
    /// <summary>
    /// xxHash64 over a caller-owned canonical byte representation.
    /// </summary>
    public sealed class XxHash64StateHasher : IStateHasher
    {
        public const ulong Seed = 0;
        private const ulong Prime1 = 11400714785074694791UL;
        private const ulong Prime2 = 14029467366897019727UL;
        private const ulong Prime3 = 1609587929392839161UL;
        private const ulong Prime4 = 9650029242287828579UL;
        private const ulong Prime5 = 2870177450012600261UL;

        public ulong ComputeHash(ReadOnlySpan<byte> canonicalState)
        {
            int offset = 0;
            ulong hash;

            if (canonicalState.Length >= 32)
            {
                ulong lane1 = unchecked(Seed + Prime1 + Prime2);
                ulong lane2 = unchecked(Seed + Prime2);
                ulong lane3 = Seed;
                ulong lane4 = unchecked(Seed - Prime1);
                int limit = canonicalState.Length - 32;

                do
                {
                    lane1 = Round(lane1, Read64(canonicalState, offset));
                    lane2 = Round(lane2, Read64(canonicalState, offset + 8));
                    lane3 = Round(lane3, Read64(canonicalState, offset + 16));
                    lane4 = Round(lane4, Read64(canonicalState, offset + 24));
                    offset += 32;
                }
                while (offset <= limit);

                hash = RotateLeft(lane1, 1)
                    + RotateLeft(lane2, 7)
                    + RotateLeft(lane3, 12)
                    + RotateLeft(lane4, 18);
                hash = MergeRound(hash, lane1);
                hash = MergeRound(hash, lane2);
                hash = MergeRound(hash, lane3);
                hash = MergeRound(hash, lane4);
            }
            else
            {
                hash = unchecked(Seed + Prime5);
            }

            hash = unchecked(hash + (ulong)canonicalState.Length);

            while (offset <= canonicalState.Length - 8)
            {
                ulong lane = Round(0, Read64(canonicalState, offset));
                hash ^= lane;
                hash = unchecked(RotateLeft(hash, 27) * Prime1 + Prime4);
                offset += 8;
            }

            if (offset <= canonicalState.Length - 4)
            {
                hash ^= unchecked((ulong)Read32(canonicalState, offset) * Prime1);
                hash = unchecked(RotateLeft(hash, 23) * Prime2 + Prime3);
                offset += 4;
            }

            while (offset < canonicalState.Length)
            {
                hash ^= unchecked(canonicalState[offset] * Prime5);
                hash = unchecked(RotateLeft(hash, 11) * Prime1);
                offset++;
            }

            hash ^= hash >> 33;
            hash = unchecked(hash * Prime2);
            hash ^= hash >> 29;
            hash = unchecked(hash * Prime3);
            hash ^= hash >> 32;
            return hash;
        }

        private static ulong Round(ulong accumulator, ulong input)
        {
            accumulator = unchecked(accumulator + input * Prime2);
            accumulator = RotateLeft(accumulator, 31);
            return unchecked(accumulator * Prime1);
        }

        private static ulong MergeRound(ulong accumulator, ulong value)
        {
            accumulator ^= Round(0, value);
            return unchecked(accumulator * Prime1 + Prime4);
        }

        private static ulong Read64(ReadOnlySpan<byte> source, int offset) =>
            BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(offset, 8));

        private static uint Read32(ReadOnlySpan<byte> source, int offset) =>
            BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(offset, 4));

        private static ulong RotateLeft(ulong value, int count) =>
            (value << count) | (value >> (64 - count));
    }
}
