using System;
using System.Collections;
using System.Reflection;
using AiNative.Client.Character;
using AiNative.Client.Prediction;
using AiNative.Gameplay;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace AiNative.Client.Application.PlayModeTests
{
    public sealed class NetworkCharacterPrefabTests
    {
        [UnityTest]
        public IEnumerator NetworkRigUsesSnapshotMotionAndHasNoLocalSimulation()
        {
            var prefab = Resources.Load<GameObject>("BattleClient/NetworkCharacter");
            Assert.That(prefab, Is.Not.Null);
            var instance = UnityEngine.Object.Instantiate(prefab);
            try
            {
                Assert.That(instance.GetComponentInChildren<CharacterMotor>(true), Is.Null);
                Assert.That(instance.GetComponentInChildren<CharacterController>(true), Is.Null);
                Assert.That(instance.GetComponentInChildren<CharacterRifle>(true), Is.Null);
                Assert.That(instance.GetComponentInChildren<CharacterInputReader>(true), Is.Null);
                var animator = instance.GetComponentInChildren<Animator>(true);
                Assert.That(animator, Is.Not.Null);
                Assert.That(animator.avatar.isHuman, Is.True);
                Assert.That(animator.applyRootMotion, Is.False);
                var presenter = instance.GetComponent<NetworkCharacterPresenter>();
                Assert.That(presenter, Is.Not.Null);
                var state = new ArenaPlayerState(12, 1000, 0, 2000)
                {
                    YawMillidegrees = 90000,
                    VelocityXMillimetresPerSecond = 7000,
                };
                presenter.Apply(state);
                Assert.That(presenter.MotionState.LocalMove.y, Is.EqualTo(1).Within(0.001));
                Assert.That(presenter.MotionState.Velocity.x, Is.EqualTo(7));
                Assert.That(presenter.MotionState.Grounded, Is.True);
                uint shots = presenter.MotionState.ShotSequence;
                presenter.NotifyShot();
                presenter.Apply(state);
                Assert.That(presenter.MotionState.ShotSequence, Is.EqualTo(shots + 1));
                var position = instance.transform.position;
                yield return null;
                yield return null;
                Assert.That(
                    instance.transform.position,
                    Is.EqualTo(position),
                    "Animation may not simulate position."
                );
                state.Alive = false;
                presenter.Apply(state);
                Assert.That(instance.activeSelf, Is.False);
                state.Alive = true;
                presenter.Apply(state);
                Assert.That(instance.activeSelf, Is.True);
            }
            finally
            {
                UnityEngine.Object.Destroy(instance);
            }
        }

        [UnityTest]
        public IEnumerator RemoteRigPoolUsesFeetCoordinatesAndReusesAvatarAfterRespawn()
        {
            var prefab = Resources.Load<GameObject>("BattleClient/NetworkCharacter");
            Assert.That(prefab, Is.Not.Null);
            Type type = typeof(BattleClientCompositionRoot).Assembly.GetType(
                "AiNative.Client.Application.ArenaRemoteVisualPool"
            );
            var pool = (IDisposable)
                Activator.CreateInstance(
                    type,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    new object[] { null, prefab },
                    null
                );
            GameObject visual = null;
            try
            {
                var apply = type.GetMethod("Apply");
                var get = type.GetMethod("GetVisual");
                var state = new ArenaPlayerState(1, 1500, 500, -2000)
                {
                    VelocityXMillimetresPerSecond = 7000,
                };
                var poses = new[] { new ArenaRemotePose(42, 1500, 500, -2000, 0, state) };
                apply.Invoke(pool, new object[] { poses, 1 });
                visual = (GameObject)get.Invoke(pool, new object[] { 42u });
                Assert.That(visual.transform.position, Is.EqualTo(new Vector3(1.5f, .5f, -2)));
                Assert.That(visual.transform.parent, Is.Null);
                var presenter = (NetworkCharacterPresenter)
                    type.GetMethod("GetPresenter").Invoke(pool, new object[] { 42u });
                Assert.That(presenter.State, Is.EqualTo(state));
                foreach (var collider in visual.GetComponentsInChildren<Collider>(true))
                    Assert.That(collider.enabled, Is.False);
                state.Alive = false;
                poses[0] = new ArenaRemotePose(42, 1500, 500, -2000, 0, state);
                apply.Invoke(pool, new object[] { poses, 1 });
                Assert.That(visual.activeSelf, Is.False);
                state.Alive = true;
                poses[0] = new ArenaRemotePose(42, 1500, 500, -2000, 0, state);
                apply.Invoke(pool, new object[] { poses, 1 });
                Assert.That(get.Invoke(pool, new object[] { 42u }), Is.SameAs(visual));
                Assert.That(visual.activeSelf, Is.True);
            }
            finally
            {
                pool.Dispose();
            }
            yield return null;
            Assert.That(visual == null, Is.True);
        }
    }
}
