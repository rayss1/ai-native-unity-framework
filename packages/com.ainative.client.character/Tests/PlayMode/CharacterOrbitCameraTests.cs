using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace AiNative.Client.Character.Tests
{
    public sealed class CharacterOrbitCameraTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();
        private CharacterMotor _motor;
        private CharacterOrbitCamera _orbit;
        private Vector3 _pivot;
        private Vector3 _boomDirection;

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
            _motor = character.AddComponent<CharacterMotor>();
            _orbit = Create("Camera test rig").AddComponent<CharacterOrbitCamera>();
            _orbit.Configure(_motor, null);
            _pivot = character.transform.position + Vector3.up * 1.45f;
            _boomDirection = -(Quaternion.Euler(15, 0, 0) * Vector3.forward);
            Physics.SyncTransforms();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var item in _objects)
            {
                if (item != null)
                {
                    Object.DestroyImmediate(item);
                }
            }

            _objects.Clear();
        }

        [UnityTest]
        public IEnumerator WallRetractsCameraAndRemovalSmoothlyRestoresDistance()
        {
            yield return null;
            yield return null;
            Assert.That(
                Vector3.Distance(_orbit.transform.position, _pivot),
                Is.EqualTo(4).Within(.02f)
            );
            var wall = Create("Camera obstacle");
            wall.transform.position = _pivot + _boomDirection * 2;
            wall.AddComponent<BoxCollider>().size = new Vector3(5, 5, .2f);
            Physics.SyncTransforms();
            yield return null;
            float blocked = Vector3.Distance(_orbit.transform.position, _pivot);
            Assert.That(blocked, Is.InRange(1.3f, 2f), "Camera must retract in front of the wall.");
            wall.SetActive(false);
            Physics.SyncTransforms();
            yield return null;
            float recovering = Vector3.Distance(_orbit.transform.position, _pivot);
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
                Vector3.Distance(_orbit.transform.position, _pivot),
                Is.EqualTo(4).Within(.03f)
            );
        }

        [UnityTest]
        public IEnumerator IgnoreRaycastCharacterChildrenDoNotRetractCamera()
        {
            // 将角色自身碰撞体放在相机臂上；若错误包含角色排除层，
            // 它将导致相机回缩。
            var accessory = Create("Character accessory collider");
            accessory.transform.SetParent(_motor.transform);
            accessory.layer = 2;
            accessory.transform.position = _pivot + _boomDirection;
            accessory.AddComponent<BoxCollider>().size = Vector3.one * .4f;
            Physics.SyncTransforms();
            yield return null;
            yield return null;
            Assert.That(
                Vector3.Distance(_orbit.transform.position, _pivot),
                Is.EqualTo(4).Within(.02f)
            );
            // 正向对照：相同形状置于环境层时必须遮挡相机。
            accessory.layer = 0;
            Physics.SyncTransforms();
            yield return null;
            Assert.That(Vector3.Distance(_orbit.transform.position, _pivot), Is.LessThan(1));
        }

        [UnityTest]
        public IEnumerator ExternalPresentationFollowsTransformAndUsesAimAndCrouchWithoutMotor()
        {
            var remote = Create("Externally driven character");
            remote.transform.position = new Vector3(1200, 1000, 1000);
            _orbit.ConfigurePresentation(remote.transform);
            _orbit.SetPresentationState(
                new CharacterMotionState { Aiming = true, Crouched = true }
            );
            _orbit.ApplyLook(new Vector2(100, 50));
            yield return null;
            yield return null;
            var rotation = Quaternion.Euler(9, 12, 0);
            var expected =
                remote.transform.position
                + Vector3.up * .9f
                + rotation * new Vector3(.45f, 0, -2.2f);
            Assert.That(_orbit.Yaw, Is.EqualTo(12).Within(.001f));
            Assert.That(_orbit.Pitch, Is.EqualTo(9).Within(.001f));
            Assert.That(Vector3.Distance(_orbit.transform.position, expected), Is.LessThan(.02f));
            Assert.That(remote.GetComponent<CharacterMotor>(), Is.Null);
        }

        [UnityTest]
        public IEnumerator ReturningToLocalMotorDiscardsExternalTargetAndPresentationState()
        {
            var remote = Create("Temporary external target");
            remote.transform.position = new Vector3(1200, 1000, 1000);
            _orbit.ConfigurePresentation(remote.transform);
            _orbit.SetPresentationState(
                new CharacterMotionState { Aiming = true, Crouched = true }
            );
            yield return null;
            _orbit.Configure(_motor, null);
            yield return null;
            yield return null;
            Assert.That(
                Vector3.Distance(_orbit.transform.position, _pivot),
                Is.EqualTo(4).Within(.02f)
            );
            Assert.That(
                Vector3.Distance(_orbit.transform.position, _pivot + _boomDirection * 4),
                Is.LessThan(.02f)
            );
        }

        private GameObject Create(string name)
        {
            var item = new GameObject(name);
            _objects.Add(item);
            return item;
        }
    }
}
