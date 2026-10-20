using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace AiNative.Client.Character.Tests
{
    public sealed class CharacterOrbitCameraTests
    {
        private readonly List<GameObject> objects = new List<GameObject>();
        private CharacterMotor motor;
        private CharacterOrbitCamera orbit;
        private Vector3 pivot;
        private Vector3 boomDirection;

        [SetUp]
        public void SetUp()
        {
            var character = Create("Camera test character");
            character.transform.position = new Vector3(1100, 1000, 1000);
            character.layer = 2; // Demo character hierarchy uses Ignore Raycast.
            var controller = character.AddComponent<CharacterController>();
            controller.center = Vector3.up * .9f;
            controller.height = 1.8f;
            controller.radius = .3f;
            motor = character.AddComponent<CharacterMotor>();
            orbit = Create("Camera test rig").AddComponent<CharacterOrbitCamera>();
            orbit.Configure(motor, null);
            pivot = character.transform.position + Vector3.up * 1.45f;
            boomDirection = -(Quaternion.Euler(15, 0, 0) * Vector3.forward);
            Physics.SyncTransforms();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var item in objects)
                if (item != null)
                    Object.DestroyImmediate(item);
            objects.Clear();
        }

        [UnityTest]
        public IEnumerator WallRetractsCameraAndRemovalSmoothlyRestoresDistance()
        {
            yield return null;
            yield return null;
            Assert.That(
                Vector3.Distance(orbit.transform.position, pivot),
                Is.EqualTo(4).Within(.02f)
            );
            var wall = Create("Camera obstacle");
            wall.transform.position = pivot + boomDirection * 2;
            wall.AddComponent<BoxCollider>().size = new Vector3(5, 5, .2f);
            Physics.SyncTransforms();
            yield return null;
            float blocked = Vector3.Distance(orbit.transform.position, pivot);
            Assert.That(blocked, Is.InRange(1.3f, 2f), "Camera must retract in front of the wall.");
            wall.SetActive(false);
            Physics.SyncTransforms();
            yield return null;
            float recovering = Vector3.Distance(orbit.transform.position, pivot);
            Assert.That(
                recovering,
                Is.GreaterThan(blocked),
                "Camera should start restoring after obstruction clears."
            );
            Assert.That(
                recovering,
                Is.LessThan(4f),
                "Recovery must be smoothed rather than snapped."
            );
            yield return new WaitForSeconds(.8f);
            Assert.That(
                Vector3.Distance(orbit.transform.position, pivot),
                Is.EqualTo(4).Within(.03f)
            );
        }

        [UnityTest]
        public IEnumerator IgnoreRaycastCharacterChildrenDoNotRetractCamera()
        {
            // Put a character-owned collider directly across the boom. It would
            // retract the camera if the demo's self-exclusion layer were included.
            var accessory = Create("Character accessory collider");
            accessory.transform.SetParent(motor.transform);
            accessory.layer = 2;
            accessory.transform.position = pivot + boomDirection;
            accessory.AddComponent<BoxCollider>().size = Vector3.one * .4f;
            Physics.SyncTransforms();
            yield return null;
            yield return null;
            Assert.That(
                Vector3.Distance(orbit.transform.position, pivot),
                Is.EqualTo(4).Within(.02f)
            );
            // Positive control: the same shape on the environment layer must block.
            accessory.layer = 0;
            Physics.SyncTransforms();
            yield return null;
            Assert.That(Vector3.Distance(orbit.transform.position, pivot), Is.LessThan(1));
        }

        [UnityTest]
        public IEnumerator ExternalPresentationFollowsTransformAndUsesAimAndCrouchWithoutMotor()
        {
            var remote = Create("Externally driven character");
            remote.transform.position = new Vector3(1200, 1000, 1000);
            orbit.ConfigurePresentation(remote.transform);
            orbit.SetPresentationState(new CharacterMotionState { Aiming = true, Crouched = true });
            orbit.ApplyLook(new Vector2(100, 50));
            yield return null;
            yield return null;
            var rotation = Quaternion.Euler(9, 12, 0);
            var expected =
                remote.transform.position
                + Vector3.up * .9f
                + rotation * new Vector3(.45f, 0, -2.2f);
            Assert.That(orbit.Yaw, Is.EqualTo(12).Within(.001f));
            Assert.That(orbit.Pitch, Is.EqualTo(9).Within(.001f));
            Assert.That(Vector3.Distance(orbit.transform.position, expected), Is.LessThan(.02f));
            Assert.That(remote.GetComponent<CharacterMotor>(), Is.Null);
        }

        [UnityTest]
        public IEnumerator ReturningToLocalMotorDiscardsExternalTargetAndPresentationState()
        {
            var remote = Create("Temporary external target");
            remote.transform.position = new Vector3(1200, 1000, 1000);
            orbit.ConfigurePresentation(remote.transform);
            orbit.SetPresentationState(new CharacterMotionState { Aiming = true, Crouched = true });
            yield return null;
            orbit.Configure(motor, null);
            yield return null;
            yield return null;
            Assert.That(
                Vector3.Distance(orbit.transform.position, pivot),
                Is.EqualTo(4).Within(.02f)
            );
            Assert.That(
                Vector3.Distance(orbit.transform.position, pivot + boomDirection * 4),
                Is.LessThan(.02f)
            );
        }

        private GameObject Create(string name)
        {
            var item = new GameObject(name);
            objects.Add(item);
            return item;
        }
    }
}
