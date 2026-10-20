using System;
using NUnit.Framework;

namespace AiNative.Gameplay.Tests
{
    public sealed class ArenaHitGeometryTests
    {
        [TestCase(600, 0, true)]
        [TestCase(1200, 400, true)]
        [TestCase(1200, 600, false)]
        [TestCase(1700, 0, true)]
        [TestCase(1900, 0, false)]
        public void HorizontalRayRespectsBodyAndHead(int height, int x, bool hit)
        {
            Assert.That(
                ArenaHitGeometry.RayCapsule(x, height, -10000, 0, 0, 1, 20000, out _),
                Is.EqualTo(hit)
            );
        }

        [Test]
        public void VerticalAndInsideRaysAndRangeAreBounded()
        {
            Assert.That(
                ArenaHitGeometry.RayCapsule(0, 3000, 0, 0, -1, 0, 2000, out double t),
                Is.True
            );
            Assert.That(t, Is.EqualTo(1200).Within(.001));
            Assert.That(ArenaHitGeometry.RayCapsule(0, 900, 0, 0, 1, 0, 1, out t), Is.True);
            Assert.That(t, Is.Zero);
            Assert.That(
                ArenaHitGeometry.RayCapsule(0, 600, -10000, 0, 0, 1, 9000, out _),
                Is.False
            );
            Assert.That(
                ArenaHitGeometry.RayCapsule(0, 600, -10000, 0, 0, -1, 20000, out _),
                Is.False
            );
        }
    }
}
