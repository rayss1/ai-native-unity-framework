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
