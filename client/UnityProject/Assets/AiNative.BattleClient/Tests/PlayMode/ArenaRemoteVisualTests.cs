using System;
using System.Collections;
using System.Reflection;
using AiNative.Client.Application;
using AiNative.Client.Prediction;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace AiNative.Client.Application.PlayModeTests
{
    public sealed class ArenaRemoteVisualTests
    {
        [UnityTest]
        public IEnumerator AimSelectsNearestVisibleBodyAndIgnoresDeadPlayers()
        {
            Type type = typeof(BattleClientCompositionRoot).Assembly.GetType("AiNative.Client.Application.ArenaRemoteVisualPool");
            var pool = (IDisposable)Activator.CreateInstance(type, new object[] { Resources.Load<Material>("BattleClient/Player") });
            var apply = type.GetMethod("Apply");
            var aim = type.GetMethod("AimPoint");
            var poses = new[] { new ArenaRemotePose(1, 0, 0, 10000, 0, true), new ArenaRemotePose(2, 0, 0, 5000, 0, true) };
            var ray = new Ray(new Vector3(.3f, 1.2f, -2), Vector3.forward);
            try
            {
                apply.Invoke(pool, new object[] { poses, 2 });
                var point = (Vector3)aim.Invoke(pool, new object[] { ray, 100f });
                Assert.That(point.z, Is.InRange(4.5f, 5f));
                poses[1] = new ArenaRemotePose(2, 0, 0, 5000, 0, false);
                apply.Invoke(pool, new object[] { poses, 2 });
                point = (Vector3)aim.Invoke(pool, new object[] { ray, 100f });
                Assert.That(point.z, Is.InRange(9.5f, 10f));
                apply.Invoke(pool, new object[] { poses, 0 });
                point = (Vector3)aim.Invoke(pool, new object[] { ray, 100f });
                Assert.That(point, Is.EqualTo(ray.GetPoint(100)));
            }
            finally { pool.Dispose(); }
            yield return null;
        }

        [UnityTest]
        public IEnumerator RemoteVisualPoolPositionsHidesAndDestroysOwnedObjects()
        {
            // Reflection keeps the application-only implementation out of the public framework API.
            Type type = typeof(BattleClientCompositionRoot).Assembly.GetType("AiNative.Client.Application.ArenaRemoteVisualPool");
            Assert.That(type, Is.Not.Null, "Remote visual pool must be composed by the Unity application.");
            var material = Resources.Load<Material>("BattleClient/Player");
            var pool = (IDisposable)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new object[] { material }, null);
            var poses = new[] { new ArenaRemotePose(42, 1500, 500, -2000, 90000, true) };
            var apply = type.GetMethod("Apply", BindingFlags.Instance | BindingFlags.Public);
            var get = type.GetMethod("GetVisual", BindingFlags.Instance | BindingFlags.Public);
            GameObject visual = null;
            try
            {
                apply.Invoke(pool, new object[] { poses, 1 });
                visual = (GameObject)get.Invoke(pool, new object[] { 42u });
                Assert.That(visual, Is.Not.Null);
                Assert.That(visual.transform.parent, Is.Null, "Remote pose must not inherit local player movement.");
                Assert.That(visual.transform.position, Is.EqualTo(new Vector3(1.5f, 1.5f, -2f)));
                Assert.That(Quaternion.Angle(visual.transform.rotation, Quaternion.Euler(0, 90, 0)), Is.LessThan(0.01));
                Assert.That(visual.activeSelf, Is.True);
                poses[0] = new ArenaRemotePose(42, 2500, 0, 0, 0, false);
                apply.Invoke(pool, new object[] { poses, 1 });
                Assert.That(visual.activeSelf, Is.False);
                poses[0] = new ArenaRemotePose(42, 3500, 0, 0, 0, true);
                apply.Invoke(pool, new object[] { poses, 1 });
                Assert.That(get.Invoke(pool, new object[] { 42u }), Is.SameAs(visual));
                Assert.That(visual.activeSelf, Is.True);
                apply.Invoke(pool, new object[] { poses, 0 });
                Assert.That(visual.activeSelf, Is.False);
            }
            finally { pool.Dispose(); }
            yield return null;
            Assert.That(visual == null, Is.True, "Disposal must destroy pooled objects.");
        }
    }
}
