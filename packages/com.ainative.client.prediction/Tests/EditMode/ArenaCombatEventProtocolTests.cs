using AiNative.Client.Protocol;
using System;
using System.Collections.Generic;
using AiNative.Gameplay;
using NUnit.Framework;

namespace AiNative.Client.Prediction.Tests
{
    public sealed class ArenaCombatEventProtocolTests
    {
        [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(4)] [TestCase(5)]
        public void AuthorityKindsDecodeWithSignedPositions(int kind)
        {
            Assert.That(ArenaCombatEventProtocolV1.TryDecode(Frame((byte)kind), out var value), Is.True);
            Assert.That((int)value.Kind, Is.EqualTo(kind));
            Assert.That(value.Sequence, Is.EqualTo(42));
            Assert.That(value.Tick, Is.EqualTo(100));
            Assert.That(value.SourceEntityId, Is.EqualTo(1));
            Assert.That(value.PositionXMillimetres, Is.EqualTo(-123));
            Assert.That(value.PositionYMillimetres, Is.EqualTo(456));
            Assert.That(value.PositionZMillimetres, Is.EqualTo(int.MinValue));
        }

        [Test]
        public void Proto3OmittedZeroKindIsFire()
        {
            var bytes = new List<byte>(Frame(0));
            bytes.RemoveRange(13, 2); // Outer event_type defaults to Fire.
            int nestedLength = bytes[14];
            bytes.RemoveRange(15, 2); // Inner event_type defaults to Fire.
            bytes[14] = (byte)(nestedLength - 2);
            Assert.That(ArenaCombatEventProtocolV1.TryDecode(bytes.ToArray(), out var value), Is.True);
            Assert.That(value.Kind, Is.EqualTo(ArenaPresentationEventKind.Fire));
        }

        [Test]
        public void TruncatedLengthsWrongWireDuplicateAndUnknownKindAreRejectedAtomically()
        {
            byte[] original = Frame(1);
            foreach (int length in new[] { 0, 1, 3, 5, 10, original.Length - 1 })
                AssertRejected(original.AsSpan(0, length).ToArray());
            var wrongWire = (byte[])original.Clone(); wrongWire[2] = 8; AssertRejected(wrongWire);
            var mismatch = (byte[])original.Clone(); mismatch[14] = 2; AssertRejected(mismatch);
            var duplicate = new List<byte>(original); duplicate.AddRange(new byte[] { 0x10, 42 }); AssertRejected(duplicate.ToArray());
            AssertRejected(Frame(6));
            var zeroSequence = (byte[])original.Clone(); zeroSequence[12] = 0; AssertRejected(zeroSequence);
            var hugeLength = (byte[])original.Clone(); hugeLength[16] = 127; AssertRejected(hugeLength);
        }

        [Test]
        public void UnknownFieldsAreSkippedButOverflowVarintsAndMissingActorsAreRejected()
        {
            var unknown = new List<byte>(Frame(0)); unknown.AddRange(new byte[] { 0xa0, 6, 7 });
            Assert.That(ArenaCombatEventProtocolV1.TryDecode(unknown.ToArray(), out _), Is.True);
            unknown.AddRange(new byte[] { 0xa0, 6, 255,255,255,255,255,255,255,255,255,2 }); AssertRejected(unknown.ToArray());
            var source = Frame(0); source[29] = 0; AssertRejected(source);
        }

        private static void AssertRejected(byte[] bytes)
        {
            Assert.That(ArenaCombatEventProtocolV1.TryDecode(bytes, out var result), Is.False);
            Assert.That(result.Sequence, Is.Zero);
        }

        private static byte[] Frame(byte kind)
        {
            var combat = new List<byte> { 8, kind, 17, 100,0,0,0,0,0,0,0, 24,1, 32,
                (byte)(kind == 1 || kind == 2 || kind == 4 ? 2 : 0), 40, (byte)(kind == 4 ? 0 : 1), 48, 10,
                56,245,1, 64,144,7, 72,255,255,255,255,15 };
            var frame = new List<byte> { 0x4e,4, 9,100,0,0,0,0,0,0,0, 16,42, 24,kind, 42,(byte)combat.Count };
            frame.AddRange(combat);
            return frame.ToArray();
        }
    }
}
