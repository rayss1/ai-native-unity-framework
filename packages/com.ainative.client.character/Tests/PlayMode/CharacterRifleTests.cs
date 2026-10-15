using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace AiNative.Client.Character.Tests
{
    public sealed class CharacterRifleTests
    {
        private readonly List<GameObject> objects = new List<GameObject>();
        private readonly Vector3 origin = new Vector3(2000, 2000, 2000);
        private CharacterRifle rifle;
        private CharacterHealth target;
        private GameObject Box(Vector3 position, Vector3 scale)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube); objects.Add(go);
            go.transform.position = position; go.transform.localScale = scale; return go;
        }
        [SetUp] public void SetUp()
        {
            var owner = Box(origin, Vector3.one);
            rifle = owner.AddComponent<CharacterRifle>();
            target = Box(origin + Vector3.forward * 10, new Vector3(2, 2, .01f)).AddComponent<CharacterHealth>();
            Physics.SyncTransforms();
        }
        [TearDown] public void TearDown()
        {
            foreach (var go in objects) Object.DestroyImmediate(go);
            objects.Clear();
        }
        [Test] public void FastProjectileHitsThinTargetOnceAndIgnoresOwner()
        {
            rifle.Launch(origin, Vector3.forward);
            rifle.Advance(.2f); rifle.Advance(.2f);
            Assert.That(target.Current, Is.EqualTo(75)); Assert.That(rifle.Hits, Is.EqualTo(1));
        }
        [Test] public void WallStopsDamage()
        {
            Box(origin + Vector3.forward * 5, new Vector3(3, 3, .01f)); Physics.SyncTransforms();
            rifle.Launch(origin, Vector3.forward); rifle.Advance(1);
            Assert.That(target.Current, Is.EqualTo(100)); Assert.That(rifle.Hits, Is.Zero);
        }
        [Test] public void MuzzleInsideWallCannotShootThroughIt()
        {
            Box(origin, Vector3.one * 2); Physics.SyncTransforms();
            rifle.Launch(origin, Vector3.forward); rifle.Advance(1);
            Assert.That(target.Current, Is.EqualTo(100));
        }
        [Test] public void FourHitsKillAndDeadTargetCannotTakeFurtherDamage()
        {
            for (int i = 0; i < 5; i++) { rifle.Launch(origin, Vector3.forward); rifle.Advance(1); }
            Assert.That(target.Current, Is.Zero); Assert.That(rifle.Hits, Is.EqualTo(4));
            target.ResetHealth(); Assert.That(target.Current, Is.EqualTo(100));
        }
        [Test] public void DisablingWeaponClearsInFlightBullets()
        {
            rifle.Launch(origin, Vector3.forward); rifle.enabled = false; rifle.Advance(1);
            Assert.That(target.Current, Is.EqualTo(100));
        }
        [Test] public void OnlyAcceptedGroundedRifleAttackEmitsShot()
        {
            var player = new GameObject("Shooter"); objects.Add(player);
            player.transform.position = origin + Vector3.right * 10;
            var capsule = player.AddComponent<CharacterController>();
            capsule.height = 2; capsule.center = Vector3.up;
            Box(player.transform.position + Vector3.down * .5f, new Vector3(5, 1, 5));
            var motor = player.AddComponent<CharacterMotor>(); int shots = 0;
            motor.ShotFired += () => shots++;
            Physics.SyncTransforms();
            for (int i = 0; i < 30; i++) motor.Simulate(default, 0, 1f / 60);
            motor.Simulate(new CharacterInputFrame { Attack = true }, 0, 1f / 60);
            Assert.That(shots, Is.Zero);
            motor.Simulate(new CharacterInputFrame { ChangeMode = true, Mode = CharacterMode.Shooter, Attack = true }, 0, 1f / 60);
            Assert.That(shots, Is.EqualTo(1));
            motor.Simulate(default, 0, 1f / 60);
            motor.Simulate(new CharacterInputFrame { Roll = true, Attack = true }, 0, 1f / 60);
            motor.Simulate(new CharacterInputFrame { Attack = true }, 0, 1f / 60);
            Assert.That(shots, Is.EqualTo(1));
        }
    }
}
