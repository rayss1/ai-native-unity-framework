using System;

namespace AiNative.Gameplay
{
    public interface IStateHasher
    {
        ulong ComputeHash(ReadOnlySpan<byte> canonicalState);
    }
}
