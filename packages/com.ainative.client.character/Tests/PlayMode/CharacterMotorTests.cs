using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace AiNative.Client.Character.Tests
{
    public sealed class CharacterMotorTests
    {
        private const float Step = 1f / 60f;
        private readonly List<GameObject> objects = new List<GameObject>();
        private readonly Vector3 origin = new Vector3(1000, 1000, 1000);
        private CharacterMotor motor;

        [SetUp]
        public void SetUp()
        {
            Box("Ground", origin + Vector3.down * .5f, new Vector3(40, 1, 40));
            var owner = new GameObject("Character motor test");
            objects.Add(owner);
            owner.transform.position = origin + Vector3.up * .05f;
            var controller = owner.AddComponent<CharacterController>();
            controller.radius = .3f;
            controller.height = 1.8f;
            controller.center = Vector3.up * .9f;
            controller.skinWidth = .02f;
            controller.minMoveDistance = 0;
            motor = owner.AddComponent<CharacterMotor>();
            Physics.SyncTransforms();
            Simulate(default, 30);
            Assert.That(motor.State.Grounded, Is.True, "Fixture must settle onto its ground before testing actions.");
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var item in objects) Object.DestroyImmediate(item);
            objects.Clear();
        }

        [Test]
        public void DiagonalMovementDoesNotExceedConfiguredRunSpeed()
        {
            Simulate(new CharacterInputFrame { Move = Vector2.one }, 60);
            var velocity = motor.State.Velocity;
            var horizontalSpeed = new Vector2(velocity.x, velocity.z).magnitude;
            Assert.That(horizontalSpeed, Is.EqualTo(motor.Configuration.RunSpeed).Within(.025f));
        }

        [Test]
        public void TwoSecondsOfMovementIsConsistentAt30And60And120FramesPerSecond()
        {
            float minDistance = float.PositiveInfinity, maxDistance = 0;
            foreach (int rate in new[] { 30, 60, 120 })
            {
                motor.Respawn();
                Simulate(default, 30);
                Vector3 start = motor.transform.position;
                var frame = new CharacterInputFrame { Move = Vector2.up };
                for (int i = 0; i < rate * 2; i++) motor.Simulate(frame, 0, 1f / rate);
                float distance = motor.transform.position.z - start.z;
                minDistance = Mathf.Min(minDistance, distance);
                maxDistance = Mathf.Max(maxDistance, distance);
                Assert.That(distance, Is.InRange(7.5f, 7.9f), "Two-second run at " + rate + " FPS");
                Assert.That(motor.State.Grounded, Is.True, "Ground contact at " + rate + " FPS");
            }
            // Integration of acceleration is discrete; allow less than one 30 Hz
            // running step while rejecting a frame-rate-scaled displacement bug.
            Assert.That(maxDistance - minDistance, Is.LessThan(.08f));
        }

        [Test]
        public void RollWinsSimultaneousAttackAndLocksModeUntilFinished()
        {
            Simulate(new CharacterInputFrame { ChangeMode = true, Mode = CharacterMode.Melee }, 1);
            Simulate(new CharacterInputFrame { Roll = true, Attack = true }, 1);
            Assert.That(motor.State.Rolling, Is.True);
            Assert.That(motor.State.Melee, Is.False);
            Simulate(new CharacterInputFrame { ChangeMode = true, Mode = CharacterMode.Shooter, Attack = true }, 1);
            Assert.That(motor.State.Mode, Is.EqualTo(CharacterMode.Melee));
            Assert.That(motor.State.ShotSequence, Is.Zero);
            Simulate(default, 45);
            Simulate(new CharacterInputFrame { ChangeMode = true, Mode = CharacterMode.Shooter }, 1);
            Assert.That(motor.State.Mode, Is.EqualTo(CharacterMode.Shooter));
        }

        [Test]
        public void MeleeLocksMovementAndRejectsRollAndModeSwitch()
        {
            Simulate(new CharacterInputFrame { ChangeMode = true, Mode = CharacterMode.Melee, Attack = true }, 1);
            var position = motor.transform.position;
            Simulate(new CharacterInputFrame { Move = Vector2.up, Roll = true, ChangeMode = true, Mode = CharacterMode.Free }, 10);
            Assert.That(motor.State.Melee, Is.True);
            Assert.That(motor.State.Rolling, Is.False);
            Assert.That(motor.State.Mode, Is.EqualTo(CharacterMode.Melee));
            Assert.That(motor.transform.position.z, Is.EqualTo(position.z).Within(.001f));
        }

        [Test]
        public void JumpLeavesGroundAndRejectsAirborneRollAndAttack()
        {
            Simulate(new CharacterInputFrame { ChangeMode = true, Mode = CharacterMode.Shooter, Jump = true }, 1);
            Assert.That(motor.State.Grounded, Is.False);
            Assert.That(motor.State.Velocity.y, Is.GreaterThan(0));
            Simulate(new CharacterInputFrame { Roll = true, Attack = true }, 1);
            Assert.That(motor.State.Rolling, Is.False);
            Assert.That(motor.State.ShotSequence, Is.Zero);
        }

        [Test]
        public void RollStopsAtSolidWall()
        {
            Box("Wall", origin + new Vector3(0, 1.5f, 1), new Vector3(5, 3, .2f));
            Physics.SyncTransforms();
            Simulate(new CharacterInputFrame { Roll = true, Move = Vector2.up }, 1);
            Assert.That(motor.State.Rolling, Is.True);
            Simulate(default, 35);
            float distance = motor.transform.position.z - origin.z;
            Assert.That(distance, Is.GreaterThan(.2f));
            Assert.That(distance, Is.LessThan(.7f), "Controller capsule must remain in front of the wall.");
        }

        [Test]
        public void LowCeilingPreventsStandingUntilCleared()
        {
            Simulate(new CharacterInputFrame { Crouch = true }, 1);
            Assert.That(motor.State.Crouched, Is.True);
            var ceiling = Box("Low ceiling", origin + Vector3.up * 1.45f, new Vector3(3, .2f, 3));
            Physics.SyncTransforms();
            Assert.That(motor.CanStand(), Is.False);
            Simulate(new CharacterInputFrame { Crouch = true }, 1);
            Assert.That(motor.State.Crouched, Is.True);
            ceiling.SetActive(false);
            Physics.SyncTransforms();
            Assert.That(motor.CanStand(), Is.True);
            Simulate(new CharacterInputFrame { Crouch = true }, 1);
            Assert.That(motor.State.Crouched, Is.False);
        }

        private void Simulate(CharacterInputFrame frame, int steps)
        {
            for (int i = 0; i < steps; i++) motor.Simulate(frame, 0, Step);
        }

        private GameObject Box(string name, Vector3 position, Vector3 size)
        {
            var item = new GameObject(name);
            objects.Add(item);
            item.transform.position = position;
            item.AddComponent<BoxCollider>().size = size;
            return item;
        }
    }
}
