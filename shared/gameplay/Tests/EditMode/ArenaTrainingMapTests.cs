using NUnit.Framework;

namespace AiNative.Gameplay.Tests
{
    public sealed class ArenaTrainingMapTests
    {
        [Test]
        public void SpawnsAreInsideBoundsAndOutsideAllSolids()
        {
            for (int i = 0; i < 8; i++)
            {
                ArenaTrainingMap.GetSpawn(i, out int x, out int y, out int z);
                Assert.That(x, Is.InRange(-19550, 19550));
                Assert.That(z, Is.InRange(-19550, 19550));
                for (int b = 0; b < ArenaTrainingMap.ObstacleCount; b++)
                {
                    var box = ArenaTrainingMap.GetObstacle(b);
                    Assert.That(
                        x + 450 <= box.MinX
                            || x - 450 >= box.MaxX
                            || z + 450 <= box.MinZ
                            || z - 450 >= box.MaxZ,
                        Is.True
                    );
                }
            }
        }

        [Test]
        public void MovementStopsAtSpawnScreenAndSlidesAlongIt()
        {
            var state = new ArenaPlayerState(0, -16000, 0, -12000);
            for (uint i = 1; i <= 100; i++)
                state = ArenaMovement.Step(
                    state,
                    new ArenaInput(i, i, 1000, 0, 0, 0, ArenaButtons.None, ArenaWeaponId.None)
                );
            Assert.That(state.PositionXMillimetres, Is.EqualTo(-12950));
            Assert.That(state.VelocityXMillimetresPerSecond, Is.Zero);
            var next = ArenaMovement.Step(
                state,
                new ArenaInput(101, 101, 1000, 1000, 0, 0, ArenaButtons.None, ArenaWeaponId.None)
            );
            Assert.That(next.PositionXMillimetres, Is.EqualTo(-12950));
            Assert.That(next.PositionZMillimetres, Is.GreaterThan(state.PositionZMillimetres));
        }

        [Test]
        public void SweptMovementCannotTunnelThroughThinScreensOrOuterBoundary()
        {
            var old = new ArenaPlayerState(0, -16000, 0, -12000);
            var next = old;
            next.PositionXMillimetres = 16000;
            ArenaTrainingMap.ResolveMovement(old, ref next);
            Assert.That(next.PositionXMillimetres, Is.EqualTo(-12950));
            old = new ArenaPlayerState(0, 18000, 0, -17000);
            next = old;
            next.PositionXMillimetres = 25000;
            ArenaTrainingMap.ResolveMovement(old, ref next);
            Assert.That(next.PositionXMillimetres, Is.EqualTo(19550));
        }

        [Test]
        public void LandsOnLowCoverAndFallsWhenLeavingItsEdge()
        {
            var old = new ArenaPlayerState(0, 10000, 1000, 0) { Grounded = false };
            var next = old;
            next.PositionYMillimetres = 800;
            next.VelocityYMillimetresPerSecond = -6000;
            ArenaTrainingMap.ResolveMovement(old, ref next);
            Assert.That(next.PositionYMillimetres, Is.EqualTo(900));
            Assert.That(next.Grounded, Is.True);
            Assert.That(next.VelocityYMillimetresPerSecond, Is.Zero);
            old = next;
            next.PositionZMillimetres = 3000;
            ArenaTrainingMap.ResolveMovement(old, ref next);
            Assert.That(next.Grounded, Is.False);
        }

        [Test]
        public void JumpDoesNotClipThroughTallCoverButCanClearLowCover()
        {
            var old = new ArenaPlayerState(0, -12950, 0, -12000);
            var next = ArenaMovement.Step(
                old,
                new ArenaInput(1, 1, 1000, 0, 0, 0, ArenaButtons.Jump, ArenaWeaponId.None)
            );
            Assert.That(next.PositionXMillimetres, Is.EqualTo(-12950));
            old = new ArenaPlayerState(0, 8000, 1000, 0) { Grounded = false };
            next = old;
            next.PositionXMillimetres = 10000;
            ArenaTrainingMap.ResolveMovement(old, ref next);
            Assert.That(next.PositionXMillimetres, Is.EqualTo(10000));
        }

        [Test]
        public void RaysRespectNearestCoverFloorAndProjectilePadding()
        {
            Assert.That(
                ArenaTrainingMap.Raycast(-16000, 600, -12000, 1, 0, 0, 32000, out double hit),
                Is.True
            );
            Assert.That(hit, Is.EqualTo(3500));
            Assert.That(
                ArenaTrainingMap.Raycast(-16000, 600, -12000, 1, 0, 0, 32000, out hit, 300),
                Is.True
            );
            Assert.That(hit, Is.EqualTo(3200));
            Assert.That(ArenaTrainingMap.Raycast(0, 1000, 0, 0, -1, 0, 2000, out hit), Is.True);
            Assert.That(hit, Is.EqualTo(1000));
            Assert.That(
                ArenaTrainingMap.Raycast(-16000, 600, -17000, 1, 0, 0, 32000, out _),
                Is.False
            );
            Assert.That(
                ArenaTrainingMap.Occluded(-16000, 600, -12000, 16000, 600, -12000),
                Is.True
            );
        }

        [Test]
        public void MirroredRoutesCollideAtSymmetricPositions()
        {
            var left = new ArenaPlayerState(0, -16000, 0, -12000);
            var right = new ArenaPlayerState(0, 16000, 0, -12000);
            for (uint i = 1; i <= 100; i++)
            {
                left = ArenaMovement.Step(
                    left,
                    new ArenaInput(i, i, 1000, 0, 0, 0, ArenaButtons.None, ArenaWeaponId.None)
                );
                right = ArenaMovement.Step(
                    right,
                    new ArenaInput(i, i, -1000, 0, 0, 0, ArenaButtons.None, ArenaWeaponId.None)
                );
                Assert.That(left.PositionXMillimetres, Is.EqualTo(-right.PositionXMillimetres));
            }
        }
    }
}
