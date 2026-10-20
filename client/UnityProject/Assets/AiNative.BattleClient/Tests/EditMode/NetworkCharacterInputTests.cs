using AiNative.Gameplay;
using NUnit.Framework;
using UnityEngine;

namespace AiNative.Client.Application.Tests
{
    public sealed class NetworkCharacterInputTests
    {
        [Test]
        public void ReloadIsBufferedUntilCommitAndClearedAtInputBoundaries()
        {
            var input = new NetworkCharacterInput();
            input.Sample(false, ArenaWeaponId.None, true);
            input.Sample(false, ArenaWeaponId.None);
            Assert.That(input.Buttons(true), Is.EqualTo(ArenaButtons.Fire | ArenaButtons.Reload));
            input.Commit();
            Assert.That(input.Buttons(false), Is.EqualTo(ArenaButtons.None));
            input.Sample(false, ArenaWeaponId.None, true);
            input.Clear();
            Assert.That(input.Buttons(false), Is.EqualTo(ArenaButtons.None));
        }

        [TestCase(2f)]
        [TestCase(10f)]
        [TestCase(30f)]
        public void ShoulderCameraAimConvergesOnVisibleCapsule(float distance)
        {
            var state = new ArenaPlayerState(0, 0, 0, 0);
            var camera = new Vector3(.45f, 1.45f, -2.2f);
            var feet = new Vector3(0, 0, distance);
            Vector3 direction = (feet + Vector3.up * 1.2f - camera).normalized;
            Vector3 relative = (camera - feet) * 1000;
            Assert.That(
                ArenaHitGeometry.RayCapsule(
                    relative.x,
                    relative.y,
                    relative.z,
                    direction.x,
                    direction.y,
                    direction.z,
                    100000,
                    out double t
                ),
                Is.True
            );
            var point = camera + direction * ((float)t / 1000 + .02f);
            NetworkCharacterInput.AimDelta(state, point, out int yaw, out int pitch);
            Vector3 shot = Quaternion.Euler(-pitch / 1000f, yaw / 1000f, 0) * Vector3.forward;
            Assert.That(
                ArenaHitGeometry.RayCapsule(
                    0,
                    600,
                    -distance * 1000,
                    shot.x,
                    shot.y,
                    shot.z,
                    100000,
                    out _
                ),
                Is.True
            );
        }

        [TestCase(0, 0, 1000)]
        [TestCase(90, 1000, 0)]
        [TestCase(180, 0, -1000)]
        [TestCase(270, -1000, 0)]
        public void ForwardUsesCameraWorldHeading(float yaw, int x, int z)
        {
            Assert.That(
                NetworkCharacterInput.RotateMove(new Vector2Int(0, 1000), yaw),
                Is.EqualTo(new Vector2Int(x, z))
            );
        }

        [Test]
        public void DiagonalIsNormalizedBeforeRotation()
        {
            Vector2Int move = NetworkCharacterInput.RotateMove(new Vector2Int(1000, 1000), 45);
            Assert.That(move.x, Is.EqualTo(1000));
            Assert.That(move.y, Is.Zero);
        }

        [Test]
        public void LookUsesShortestYawAndInvertsUnityPitchWithoutRepeatingDelta()
        {
            var state = new ArenaPlayerState
            {
                YawMillidegrees = 359000,
                PitchMillidegrees = 10000,
            };
            NetworkCharacterInput.LookDelta(state, 1, -20, out int yaw, out int pitch);
            Assert.That(yaw, Is.EqualTo(2000));
            Assert.That(pitch, Is.EqualTo(10000));
            state.YawMillidegrees = 1000;
            state.PitchMillidegrees = 20000;
            NetworkCharacterInput.LookDelta(state, 1, -20, out yaw, out pitch);
            Assert.That(yaw, Is.Zero);
            Assert.That(pitch, Is.Zero);
        }

        [Test]
        public void JumpAndWeaponSurviveDynamicFramesUntilAcceptedTick()
        {
            var input = new NetworkCharacterInput();
            input.Sample(true, ArenaWeaponId.Shotgun);
            input.Sample(false, ArenaWeaponId.None);
            Assert.That(input.Buttons(true), Is.EqualTo(ArenaButtons.Jump | ArenaButtons.Fire));
            Assert.That(input.Buttons(false), Is.EqualTo(ArenaButtons.Jump));
            Assert.That(input.Weapon, Is.EqualTo(ArenaWeaponId.Shotgun));
            input.Commit();
            Assert.That(input.Buttons(true), Is.EqualTo(ArenaButtons.Fire));
            Assert.That(input.Weapon, Is.EqualTo(ArenaWeaponId.None));
        }

        [Test]
        public void InputBoundaryDiscardsBufferedActions()
        {
            var input = new NetworkCharacterInput();
            input.Sample(true, ArenaWeaponId.Rocket);
            input.Clear();
            Assert.That(input.Buttons(false), Is.EqualTo(ArenaButtons.None));
            Assert.That(input.Weapon, Is.EqualTo(ArenaWeaponId.None));
        }
    }
}
