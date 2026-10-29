using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace AiNative.Client.Character.Tests
{
    public sealed class CharacterMotorTests
    {
        private const float Step = 1f / 60f;
        private readonly List<GameObject> _objects = new List<GameObject>();
        private readonly Vector3 _origin = new Vector3(1000, 1000, 1000);
        private CharacterMotor _motor;

        [SetUp]
        public void SetUp()
        {
            Box("Ground", _origin + Vector3.down * .5f, new Vector3(40, 1, 40));
            var owner = new GameObject("Character motor test");
            _objects.Add(owner);
            owner.transform.position = _origin + Vector3.up * .05f;
            var controller = owner.AddComponent<CharacterController>();
            controller.radius = .3f;
            controller.height = 1.8f;
            controller.center = Vector3.up * .9f;
            controller.skinWidth = .02f;
            controller.minMoveDistance = 0;
            _motor = owner.AddComponent<CharacterMotor>();
            Physics.SyncTransforms();
            Simulate(default, 30);
            Assert.That(
                _motor.State.Grounded,
                Is.True,
                "Fixture must settle onto its ground before testing actions."
            );
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var item in _objects)
            {
                Object.DestroyImmediate(item);
            }

            _objects.Clear();
        }

        [Test]
        public void DiagonalMovementDoesNotExceedConfiguredRunSpeed()
        {
            Simulate(new CharacterInputFrame { Move = Vector2.one }, 60);
            var velocity = _motor.State.Velocity;
            var horizontalSpeed = new Vector2(velocity.x, velocity.z).magnitude;
            Assert.That(horizontalSpeed, Is.EqualTo(_motor.Configuration.RunSpeed).Within(.025f));
        }

        [Test]
        public void TwoSecondsOfMovementIsConsistentAt30And60And120FramesPerSecond()
        {
            float minDistance = float.PositiveInfinity,
                maxDistance = 0;
            foreach (int rate in new[] { 30, 60, 120 })
            {
                _motor.Respawn();
                Simulate(default, 30);
                Vector3 start = _motor.transform.position;
                var frame = new CharacterInputFrame { Move = Vector2.up };
                for (int i = 0; i < rate * 2; i++)
                {
                    _motor.Simulate(frame, 0, 1f / rate);
                }

                float distance = _motor.transform.position.z - start.z;
                minDistance = Mathf.Min(minDistance, distance);
                maxDistance = Mathf.Max(maxDistance, distance);
                Assert.That(distance, Is.InRange(7.5f, 7.9f), "Two-second run at " + rate + " FPS");
                Assert.That(_motor.State.Grounded, Is.True, "Ground contact at " + rate + " FPS");
            }

            // 加速度采用离散积分；允许小于一次 30 Hz 跑步步长的误差，
            // 同时拒绝位移随帧率错误缩放的问题。
            Assert.That(maxDistance - minDistance, Is.LessThan(.08f));
        }

        [Test]
        public void RollWinsSimultaneousAttackAndLocksModeUntilFinished()
        {
            Simulate(new CharacterInputFrame { ChangeMode = true, Mode = CharacterMode.Melee }, 1);
            Simulate(new CharacterInputFrame { Roll = true, Attack = true }, 1);
            Assert.That(_motor.State.Rolling, Is.True);
            Assert.That(_motor.State.Melee, Is.False);
            Simulate(
                new CharacterInputFrame
                {
                    ChangeMode = true,
                    Mode = CharacterMode.Shooter,
                    Attack = true,
                },
                1
            );
            Assert.That(_motor.State.Mode, Is.EqualTo(CharacterMode.Melee));
            Assert.That(_motor.State.ShotSequence, Is.Zero);
            Simulate(default, 45);
            Simulate(
                new CharacterInputFrame { ChangeMode = true, Mode = CharacterMode.Shooter },
                1
            );
            Assert.That(_motor.State.Mode, Is.EqualTo(CharacterMode.Shooter));
        }

        [Test]
        public void MeleeLocksMovementAndRejectsRollAndModeSwitch()
        {
            Simulate(
                new CharacterInputFrame
                {
                    ChangeMode = true,
                    Mode = CharacterMode.Melee,
                    Attack = true,
                },
                1
            );
            var position = _motor.transform.position;
            Simulate(
                new CharacterInputFrame
                {
                    Move = Vector2.up,
                    Roll = true,
                    ChangeMode = true,
                    Mode = CharacterMode.Free,
                },
                10
            );
            Assert.That(_motor.State.Melee, Is.True);
            Assert.That(_motor.State.Rolling, Is.False);
            Assert.That(_motor.State.Mode, Is.EqualTo(CharacterMode.Melee));
            Assert.That(_motor.transform.position.z, Is.EqualTo(position.z).Within(.001f));
        }

        [Test]
        public void JumpLeavesGroundAndRejectsAirborneRollAndAttack()
        {
            Simulate(
                new CharacterInputFrame
                {
                    ChangeMode = true,
                    Mode = CharacterMode.Shooter,
                    Jump = true,
                },
                1
            );
            Assert.That(_motor.State.Grounded, Is.False);
            Assert.That(_motor.State.Velocity.y, Is.GreaterThan(0));
            Simulate(new CharacterInputFrame { Roll = true, Attack = true }, 1);
            Assert.That(_motor.State.Rolling, Is.False);
            Assert.That(_motor.State.ShotSequence, Is.Zero);
        }

        [Test]
        public void RollStopsAtSolidWall()
        {
            Box("Wall", _origin + new Vector3(0, 1.5f, 1), new Vector3(5, 3, .2f));
            Physics.SyncTransforms();
            Simulate(new CharacterInputFrame { Roll = true, Move = Vector2.up }, 1);
            Assert.That(_motor.State.Rolling, Is.True);
            Simulate(default, 35);
            float distance = _motor.transform.position.z - _origin.z;
            Assert.That(distance, Is.GreaterThan(.2f));
            Assert.That(
                distance,
                Is.LessThan(.7f),
                "Controller capsule must remain in front of the wall."
            );
        }

        [Test]
        public void LowCeilingPreventsStandingUntilCleared()
        {
            Simulate(new CharacterInputFrame { Crouch = true }, 1);
            Assert.That(_motor.State.Crouched, Is.True);
            var ceiling = Box("Low ceiling", _origin + Vector3.up * 1.45f, new Vector3(3, .2f, 3));
            Physics.SyncTransforms();
            Assert.That(_motor.CanStand(), Is.False);
            Simulate(new CharacterInputFrame { Crouch = true }, 1);
            Assert.That(_motor.State.Crouched, Is.True);
            ceiling.SetActive(false);
            Physics.SyncTransforms();
            Assert.That(_motor.CanStand(), Is.True);
            Simulate(new CharacterInputFrame { Crouch = true }, 1);
            Assert.That(_motor.State.Crouched, Is.False);
        }

        private void Simulate(CharacterInputFrame frame, int steps)
        {
            for (int i = 0; i < steps; i++)
            {
                _motor.Simulate(frame, 0, Step);
            }
        }

        private GameObject Box(string name, Vector3 position, Vector3 size)
        {
            var item = new GameObject(name);
            _objects.Add(item);
            item.transform.position = position;
            item.AddComponent<BoxCollider>().size = size;
            return item;
        }
    }
}
