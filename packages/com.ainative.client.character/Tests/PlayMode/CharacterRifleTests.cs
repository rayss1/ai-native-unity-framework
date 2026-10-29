using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace AiNative.Client.Character.Tests
{
    public sealed class CharacterRifleTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();
        private readonly Vector3 _origin = new Vector3(2000, 2000, 2000);
        private CharacterRifle _rifle;
        private CharacterHealth _target;

        private GameObject Box(Vector3 position, Vector3 scale)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _objects.Add(go);
            go.transform.position = position;
            go.transform.localScale = scale;
            return go;
        }

        [SetUp]
        public void SetUp()
        {
            var owner = Box(_origin, Vector3.one);
            _rifle = owner.AddComponent<CharacterRifle>();
            _target = Box(_origin + Vector3.forward * 10, new Vector3(2, 2, .01f))
                .AddComponent<CharacterHealth>();
            Physics.SyncTransforms();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _objects)
            {
                Object.DestroyImmediate(go);
            }

            _objects.Clear();
        }

        [Test]
        public void FastProjectileHitsThinTargetOnceAndIgnoresOwner()
        {
            _rifle.Launch(_origin, Vector3.forward);
            _rifle.Advance(.2f);
            _rifle.Advance(.2f);
            Assert.That(_target.Current, Is.EqualTo(75));
            Assert.That(_rifle.Hits, Is.EqualTo(1));
        }

        [Test]
        public void WallStopsDamage()
        {
            Box(_origin + Vector3.forward * 5, new Vector3(3, 3, .01f));
            Physics.SyncTransforms();
            _rifle.Launch(_origin, Vector3.forward);
            _rifle.Advance(1);
            Assert.That(_target.Current, Is.EqualTo(100));
            Assert.That(_rifle.Hits, Is.Zero);
        }

        [Test]
        public void MuzzleInsideWallCannotShootThroughIt()
        {
            Box(_origin, Vector3.one * 2);
            Physics.SyncTransforms();
            _rifle.Launch(_origin, Vector3.forward);
            _rifle.Advance(1);
            Assert.That(_target.Current, Is.EqualTo(100));
        }

        [Test]
        public void FourHitsKillAndDeadTargetCannotTakeFurtherDamage()
        {
            for (int i = 0; i < 5; i++)
            {
                _rifle.Launch(_origin, Vector3.forward);
                _rifle.Advance(1);
            }

            Assert.That(_target.Current, Is.Zero);
            Assert.That(_rifle.Hits, Is.EqualTo(4));
            _target.ResetHealth();
            Assert.That(_target.Current, Is.EqualTo(100));
        }

        [Test]
        public void DisablingWeaponClearsInFlightBullets()
        {
            _rifle.Launch(_origin, Vector3.forward);
            _rifle.enabled = false;
            _rifle.Advance(1);
            Assert.That(_target.Current, Is.EqualTo(100));
        }

        [Test]
        public void OnlyAcceptedGroundedRifleAttackEmitsShot()
        {
            var player = new GameObject("Shooter");
            _objects.Add(player);
            player.transform.position = _origin + Vector3.right * 10;
            var capsule = player.AddComponent<CharacterController>();
            capsule.height = 2;
            capsule.center = Vector3.up;
            Box(player.transform.position + Vector3.down * .5f, new Vector3(5, 1, 5));
            var motor = player.AddComponent<CharacterMotor>();
            int shots = 0;
            motor.ShotFired += () => shots++;
            Physics.SyncTransforms();
            for (int i = 0; i < 30; i++)
            {
                motor.Simulate(default, 0, 1f / 60);
            }

            motor.Simulate(new CharacterInputFrame { Attack = true }, 0, 1f / 60);
            Assert.That(shots, Is.Zero);
            motor.Simulate(
                new CharacterInputFrame
                {
                    ChangeMode = true,
                    Mode = CharacterMode.Shooter,
                    Attack = true,
                },
                0,
                1f / 60
            );
            Assert.That(shots, Is.EqualTo(1));
            motor.Simulate(default, 0, 1f / 60);
            motor.Simulate(new CharacterInputFrame { Roll = true, Attack = true }, 0, 1f / 60);
            motor.Simulate(new CharacterInputFrame { Attack = true }, 0, 1f / 60);
            Assert.That(shots, Is.EqualTo(1));
        }
    }
}
