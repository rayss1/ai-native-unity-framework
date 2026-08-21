using System;
using NUnit.Framework;

namespace AiNative.Gameplay.Tests
{
    public sealed class DeterminismContractTests
    {
        [Test]
        public void Pcg32MatchesPublishedReferenceVector()
        {
            Pcg32Random random = new Pcg32Random(42, 54);

            uint[] actual = new uint[5];
            for (int index = 0; index < actual.Length; index++)
            {
                actual[index] = random.NextUInt32();
            }

            Assert.That(actual, Is.EqualTo(new uint[]
            {
                0xa15c02b7,
                0x7b47f409,
                0xba1d3330,
                0x83d2f293,
                0xbfa4784b,
            }));
        }

        [Test]
        public void CapturedRandomStateReplaysTheSameSequence()
        {
            Pcg32Random random = new Pcg32Random(0x12345678, 7);
            RandomState checkpoint = random.CaptureState();

            uint first = random.NextUInt32();
            uint second = random.NextUInt32();
            random.RestoreState(checkpoint);

            Assert.That(random.NextUInt32(), Is.EqualTo(first));
            Assert.That(random.NextUInt32(), Is.EqualTo(second));
        }

        [Test]
        public void XxHash64MatchesCanonicalEmptyVector()
        {
            XxHash64StateHasher hasher = new XxHash64StateHasher();

            ulong hash = hasher.ComputeHash(ReadOnlySpan<byte>.Empty);

            Assert.That(hash, Is.EqualTo(0xef46db3751d8e999UL));
        }
    }
}
