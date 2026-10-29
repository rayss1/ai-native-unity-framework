using System.Collections;
using AiNative.Gameplay;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace AiNative.Client.Application.PlayModeTests
{
    public sealed class TrainingGroundPresentationTests
    {
        [UnityTest]
        public IEnumerator SharedObstaclesMatchPhysicsAndOwnedMaterialsAreReleased()
        {
            var root = new GameObject("TrainingGroundTest");
            Material ownedMaterial = null;
            try
            {
                root.AddComponent<ArenaTrainingGroundPresentation>();
                yield return null; // Decorative colliders are disabled immediately and destroyed at frame end.
                Physics.SyncTransforms();
                Assert.That(
                    root.GetComponentsInChildren<Collider>().Length,
                    Is.EqualTo(ArenaTrainingMap.ObstacleCount + 1)
                );
                for (int i = 0; i < ArenaTrainingMap.ObstacleCount; i++)
                {
                    ArenaMapBox expected = ArenaTrainingMap.GetObstacle(i);
                    Transform obstacle = root.transform.Find("SharedObstacle_" + i);
                    Assert.That(obstacle, Is.Not.Null);
                    var collider = obstacle.GetComponent<BoxCollider>();
                    Assert.That(collider, Is.Not.Null);
                    Assert.That(collider.enabled && !collider.isTrigger, Is.True);
                    Bounds actual = collider.bounds;
                    Assert.That(actual.min.x, Is.EqualTo(expected.MinX / 1000f).Within(.0001f));
                    Assert.That(actual.min.y, Is.EqualTo(expected.MinY / 1000f).Within(.0001f));
                    Assert.That(actual.min.z, Is.EqualTo(expected.MinZ / 1000f).Within(.0001f));
                    Assert.That(actual.max.x, Is.EqualTo(expected.MaxX / 1000f).Within(.0001f));
                    Assert.That(actual.max.y, Is.EqualTo(expected.MaxY / 1000f).Within(.0001f));
                    Assert.That(actual.max.z, Is.EqualTo(expected.MaxZ / 1000f).Within(.0001f));
                }

                var floor = root.transform.Find("TrainingDeck");
                Assert.That(
                    floor.GetComponent<BoxCollider>().bounds.max.y,
                    Is.EqualTo(0).Within(.0001f)
                );
                Assert.That(floor.localScale.x, Is.EqualTo(ArenaTrainingMap.HalfExtent / 500f));
                ownedMaterial = floor.GetComponent<Renderer>().sharedMaterial;
                Assert.That(
                    ownedMaterial,
                    Is.Not.SameAs(Resources.Load<Material>("BattleClient/Floor"))
                );
            }
            finally
            {
                Object.Destroy(root);
            }

            yield return null;
            yield return null;
            Assert.That(root == null, Is.True);
            Assert.That(
                ownedMaterial == null,
                Is.True,
                "Runtime material copies must be released with the arena."
            );
        }
    }
}
