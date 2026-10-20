using UnityEngine;

namespace AiNative.Client.Character
{
    [DisallowMultipleComponent]
    public sealed class CharacterAnimationDriver : MonoBehaviour
    {
        [SerializeField]
        private CharacterMotor motor;

        [SerializeField]
        private Animator animator;

        [SerializeField]
        private GameObject rifle;

        [SerializeField]
        private Transform leftHandGrip;

        [SerializeField]
        private Vector3 hipPosition,
            aimPosition;

        [SerializeField]
        private Quaternion hipRotation = Quaternion.identity,
            aimRotation = Quaternion.identity;
        private readonly CharacterConfiguration presentationConfiguration =
            new CharacterConfiguration();
        private CharacterMotionState presentationState;
        private bool externallyDriven;
        private float aimWeight;
        private uint lastShot;
        private bool wasRolling,
            wasMelee,
            wasGrounded = true;
        private int armsLayer,
            upperLayer,
            shotLayer,
            fullLayer;
        private static readonly int InputMagnitude = Animator.StringToHash("InputMagnitude"),
            InputHorizontal = Animator.StringToHash("InputHorizontal"),
            InputVertical = Animator.StringToHash("InputVertical"),
            IsGrounded = Animator.StringToHash("IsGrounded"),
            IsCrouching = Animator.StringToHash("IsCrouching"),
            IsStrafing = Animator.StringToHash("IsStrafing"),
            IsSprinting = Animator.StringToHash("IsSprinting"),
            IsAiming = Animator.StringToHash("IsAiming"),
            CanAim = Animator.StringToHash("CanAim"),
            VerticalVelocity = Animator.StringToHash("VerticalVelocity"),
            GroundDistance = Animator.StringToHash("GroundDistance"),
            MoveSetId = Animator.StringToHash("MoveSet_ID"),
            UpperBodyId = Animator.StringToHash("UpperBody_ID"),
            ShotId = Animator.StringToHash("Shot_ID"),
            ActionState = Animator.StringToHash("ActionState"),
            Shoot = Animator.StringToHash("Shoot");
        private const string Free = "Base Layer.Locomotion.Free Locomotion.Free Movement";
        private const string Crouch = "Base Layer.Locomotion.Free Locomotion.Free Crouch";
        private const string Strafe = "Base Layer.Locomotion.Strafe Locomotion.Strafing Movement";
        private const string StrafeCrouch =
            "Base Layer.Locomotion.Strafe Locomotion.Strafing Crouch";
        private const string Roll = "Base Layer.Actions.Roll.Roll";
        private const string Jump = "Base Layer.Airborne.Jump.JumpMove";
        private const string Fall = "Base Layer.Airborne.Falling.Falling";
        private const string Land = "Base Layer.Airborne.Landing.LandLow";
        private const string Attack = "FullBody.Attacks.WeakAttacks.Unarmed.C";

        public void Configure(
            CharacterMotor source,
            Animator rig,
            GameObject weapon,
            Transform grip
        )
        {
            externallyDriven = false;
            motor = source;
            animator = rig;
            rifle = weapon;
            leftHandGrip = grip;
            if (weapon != null)
            {
                hipPosition = aimPosition = weapon.transform.localPosition;
                hipRotation = aimRotation = weapon.transform.localRotation;
            }
        }

        /// <summary>
        /// Drives animation and IK from application-owned motion without running a local motor.
        /// Preserves the serialized animator, weapon, grip and avatar-specific poses.
        /// Configure restores local motor control; external locomotion uses default speed thresholds.
        /// </summary>
        public void SetPresentationState(CharacterMotionState state)
        {
            presentationState = state;
            externallyDriven = true;
        }

        /// <summary>Avatar-specific hand-local weapon pose; supplied by the application, not shared simulation.</summary>
        public void ConfigureAimPose(Vector3 localPosition, Quaternion localRotation)
        {
            aimPosition = localPosition;
            aimRotation = localRotation;
        }

        private void Awake()
        {
            // Disable before the first animation evaluation, not only at Start.
            if (animator != null)
                animator.applyRootMotion = false;
        }

        private void Start()
        {
            if (animator == null)
            {
                enabled = false;
                return;
            }
            animator.applyRootMotion = false;
            armsLayer = animator.GetLayerIndex("OnlyArms");
            upperLayer = animator.GetLayerIndex("UpperBody");
            shotLayer = animator.GetLayerIndex("Shot");
            fullLayer = animator.GetLayerIndex("FullBody");
            Weight(animator.GetLayerIndex("RightArm"), 0);
            Weight(animator.GetLayerIndex("LeftArm"), 0);
            Weight(animator.GetLayerIndex("UnderBody"), 0);
        }

        private void Update()
        {
            if ((!externallyDriven && motor == null) || animator == null)
                return;
            var state = externallyDriven ? presentationState : motor.State;
            var velocity = state.Velocity;
            velocity.y = 0;
            float speed = velocity.magnitude;
            var config = externallyDriven ? presentationConfiguration : motor.Configuration;
            float magnitude =
                speed <= config.WalkSpeed ? .5f * speed / Mathf.Max(config.WalkSpeed, .01f)
                : speed <= config.RunSpeed
                    ? Mathf.Lerp(
                        .5f,
                        1,
                        Mathf.InverseLerp(config.WalkSpeed, config.RunSpeed, speed)
                    )
                : Mathf.Lerp(
                    1,
                    1.5f,
                    Mathf.InverseLerp(config.RunSpeed, config.SprintSpeed, speed)
                );
            bool armed = state.Mode == CharacterMode.Shooter;
            animator.SetFloat(InputMagnitude, magnitude, .1f, Time.deltaTime);
            animator.SetFloat(InputHorizontal, state.LocalMove.x * magnitude, .08f, Time.deltaTime);
            animator.SetFloat(InputVertical, state.LocalMove.y * magnitude, .08f, Time.deltaTime);
            animator.SetFloat(VerticalVelocity, state.Velocity.y);
            animator.SetFloat(GroundDistance, state.Grounded ? 0 : 1);
            animator.SetBool(IsGrounded, state.Grounded);
            animator.SetBool(IsCrouching, state.Crouched);
            animator.SetBool(IsStrafing, state.Aiming);
            animator.SetBool(IsSprinting, speed > config.RunSpeed + .1f);
            animator.SetBool(IsAiming, state.Aiming);
            animator.SetBool(CanAim, true);
            animator.SetFloat(MoveSetId, armed ? 1 : 0);
            animator.SetFloat(UpperBodyId, armed ? 2 : 0);
            animator.SetFloat(ShotId, 2);
            // The imported graph's absent Invector scripts used to dispatch action states.
            // Our motor owns action starts/ends; reuse those exact existing states without
            // allowing root motion or the imported action duration to move the capsule.
            animator.SetInteger(
                ActionState,
                state.Rolling || (!state.Grounded && state.Velocity.y > 0) ? 1 : 0
            );
            string locomotion = state.Aiming
                ? (state.Crouched ? StrafeCrouch : Strafe)
                : (state.Crouched ? Crouch : Free);
            if (state.Rolling && !wasRolling)
                animator.CrossFadeInFixedTime(Roll, .08f, 0);
            else if (!state.Rolling && wasRolling)
                animator.CrossFadeInFixedTime(state.Grounded ? locomotion : Fall, .08f, 0);
            else if (!state.Rolling && !state.Grounded && wasGrounded)
                animator.CrossFadeInFixedTime(state.Velocity.y > 0 ? Jump : Fall, .08f, 0);
            else if (!state.Rolling && state.Grounded && !wasGrounded)
                animator.CrossFadeInFixedTime(Land, .08f, 0);
            if (state.Melee && !wasMelee && fullLayer >= 0)
                animator.CrossFadeInFixedTime(Attack, .08f, fullLayer);
            wasRolling = state.Rolling;
            wasGrounded = state.Grounded;
            wasMelee = state.Melee;
            Weight(armsLayer, armed && !state.Rolling ? 1 : 0);
            Weight(upperLayer, armed && !state.Rolling ? 1 : 0);
            Weight(shotLayer, armed && !state.Rolling ? 1 : 0);
            Weight(fullLayer, state.Melee ? 1 : 0);
            if (rifle != null && rifle.activeSelf != armed)
                rifle.SetActive(armed);
            if (rifle != null)
            {
                aimWeight = Mathf.MoveTowards(
                    aimWeight,
                    state.Aiming ? 1 : 0,
                    Time.deltaTime / .08f
                );
                rifle.transform.localPosition = Vector3.Lerp(hipPosition, aimPosition, aimWeight);
                rifle.transform.localRotation = Quaternion.Slerp(
                    hipRotation,
                    aimRotation,
                    aimWeight
                );
            }
            if (state.ShotSequence != lastShot)
            {
                lastShot = state.ShotSequence;
                animator.SetTrigger(Shoot);
            }
        }

        private void Weight(int layer, float weight)
        {
            if (layer >= 0)
                animator.SetLayerWeight(layer, weight);
        }

        private void OnAnimatorIK(int layerIndex)
        {
            if ((!externallyDriven && motor == null) || animator == null)
                return;
            var state = externallyDriven ? presentationState : motor.State;
            float weight =
                state.Mode == CharacterMode.Shooter && !state.Rolling && leftHandGrip != null
                    ? 1
                    : 0;
            animator.SetIKPositionWeight(AvatarIKGoal.LeftHand, weight);
            animator.SetIKRotationWeight(AvatarIKGoal.LeftHand, weight);
            if (weight > 0)
            {
                animator.SetIKPosition(AvatarIKGoal.LeftHand, leftHandGrip.position);
                animator.SetIKRotation(AvatarIKGoal.LeftHand, leftHandGrip.rotation);
            }
        }
    }
}
