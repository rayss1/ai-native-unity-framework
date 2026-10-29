#if UNITY_EDITOR
using AiNative.Client.Character;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace AiNative.CharacterDemo.Tests
{
    /// <summary>应用资产回归：烘焙的平面根位移不得导致可见髋部
    /// 偏离由代码驱动的 CharacterController。</summary>
    public sealed class CharacterAnimationRegressionTests
    {
        private const string RigPath =
            "Assets/AiNative.CharacterDemo/Generated/CharacterDemoRig.prefab";
        private const string OriginalControllerPath =
            "Assets/Art/Animations/Shooter/Invector@ShooterMelee.controller";
        private GameObject _rig;
        private GameObject _floor;

        [TearDown]
        public void TearDown()
        {
            if (_rig != null)
            {
                Object.DestroyImmediate(_rig);
            }

            if (_floor != null)
            {
                Object.DestroyImmediate(_floor);
            }

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        [Test]
        public void RunningAcrossSeveralLoopsKeepsHipsNearCodeDrivenRoot()
        {
            const float dt = 1f / 60f;
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(RigPath);
            Assert.That(
                prefab,
                Is.Not.Null,
                "Generate the character demo assets before running the regression."
            );
            _rig = Object.Instantiate(prefab);
            var motor = _rig.GetComponentInChildren<CharacterMotor>(true);
            var animator = _rig.GetComponentInChildren<Animator>(true);
            Assert.That(motor, Is.Not.Null);
            Assert.That(animator, Is.Not.Null);
            Assert.That(
                animator.applyRootMotion,
                Is.False,
                "Root motion must be disabled in the saved rig, not merely overridden by this test."
            );
            Assert.That(animator.runtimeAnimatorController, Is.Not.Null);
            Assert.That(
                animator.runtimeAnimatorController.name,
                Does.Contain("Invector@ShooterMelee")
            );
            var original = AssetDatabase.LoadAssetAtPath<UnityEditor.Animations.AnimatorController>(
                OriginalControllerPath
            );
            Assert.That(original, Is.Not.Null);
            Assert.That(
                original.layers.Length,
                Is.EqualTo(8),
                "Source controller layer contract changed."
            );
            Assert.That(
                animator.layerCount,
                Is.EqualTo(original.layers.Length),
                "The complete source controller layers must be retained."
            );
            // 显式推进动画，排除渲染循环输入、相机或驱动器写入的干扰。
            foreach (var behaviour in _rig.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour != motor)
                {
                    behaviour.enabled = false;
                }
            }

            motor.Configure(null, null);
            motor.transform.position = new Vector3(1200, 1000.05f, 1000);
            motor.transform.rotation = Quaternion.identity;
            _floor = new GameObject("Animation regression ground");
            _floor.transform.position = new Vector3(1200, 999.5f, 1000);
            _floor.AddComponent<BoxCollider>().size = new Vector3(100, 1, 100);
            Physics.SyncTransforms();
            for (int i = 0; i < 30; i++)
            {
                motor.Simulate(default, 0, dt);
            }

            Assert.That(motor.State.Grounded, Is.True);
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.SetBool("IsGrounded", true);
            animator.SetBool("IsStrafing", false);
            animator.SetBool("IsSprinting", false);
            animator.SetBool("IsCrouching", false);
            animator.SetFloat("GroundDistance", 0);
            animator.SetFloat("InputMagnitude", 1);
            animator.SetFloat("InputVertical", 1);
            animator.SetFloat("InputHorizontal", 0);
            var frame = new CharacterInputFrame { Move = Vector2.up };
            for (int i = 0; i < 90; i++)
            {
                motor.Simulate(frame, 0, dt);
                animator.Update(dt);
            }

            var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            Assert.That(hips, Is.Not.Null, "Regression requires the intended Humanoid avatar.");
            var initialState = animator.GetCurrentAnimatorStateInfo(0);
            Assert.That(
                initialState.loop,
                Is.True,
                "Warmup must reach a looping locomotion state."
            );
            bool hasRunClip = false;
            foreach (var clip in animator.GetCurrentAnimatorClipInfo(0))
            {
                if (
                    clip.weight > .5f
                    && clip.clip.name.IndexOf("Run", System.StringComparison.OrdinalIgnoreCase) >= 0
                )
                {
                    hasRunClip = true;
                }
            }

            Assert.That(
                hasRunClip,
                Is.True,
                "The regression must sample Run, not accidentally pass on idle."
            );
            Vector3 previousOffset = RelativeHips(motor, hips);
            float minForward = previousOffset.z,
                maxForward = previousOffset.z;
            float minSide = previousOffset.x,
                maxSide = previousOffset.x;
            int frames = Mathf.CeilToInt(Mathf.Max(4f, initialState.length * 3f) / dt);
            for (int i = 0; i < frames; i++)
            {
                Vector3 before = motor.transform.position;
                motor.Simulate(frame, 0, dt);
                Vector3 afterMotor = motor.transform.position;
                Assert.That(
                    afterMotor.z - before.z,
                    Is.EqualTo(motor.Configuration.RunSpeed * dt).Within(.002f),
                    "Motor movement at sample " + i
                );
                animator.Update(dt);
                Assert.That(
                    Vector3.Distance(motor.transform.position, afterMotor),
                    Is.LessThan(.001f),
                    "Animator must not move the controller root."
                );
                Vector3 offset = RelativeHips(motor, hips);
                float planarJump = new Vector2(
                    offset.x - previousOffset.x,
                    offset.z - previousOffset.z
                ).magnitude;
                Assert.That(
                    planarJump,
                    Is.LessThan(.25f),
                    "Visible hips snapped at sample "
                        + i
                        + ", normalized time "
                        + animator.GetCurrentAnimatorStateInfo(0).normalizedTime
                );
                minForward = Mathf.Min(minForward, offset.z);
                maxForward = Mathf.Max(maxForward, offset.z);
                minSide = Mathf.Min(minSide, offset.x);
                maxSide = Mathf.Max(maxSide, offset.x);
                previousOffset = offset;
            }

            var finalState = animator.GetCurrentAnimatorStateInfo(0);
            Assert.That(finalState.fullPathHash, Is.EqualTo(initialState.fullPathHash));
            Assert.That(
                finalState.normalizedTime - initialState.normalizedTime,
                Is.GreaterThanOrEqualTo(2f),
                "Must cross at least two complete animation loops."
            );
            Assert.That(
                maxForward - minForward,
                Is.LessThan(.4f),
                "Forward hips drift indicates planar root motion was baked into the pose."
            );
            Assert.That(
                maxSide - minSide,
                Is.LessThan(.4f),
                "Lateral hips drift must also stay bounded."
            );
        }

        private static Vector3 RelativeHips(CharacterMotor motor, Transform hips)
        {
            // 使用世界坐标米，不受模型导入缩放影响。
            return Quaternion.Inverse(motor.transform.rotation)
                * (hips.position - motor.transform.position);
        }
    }
}
#endif
