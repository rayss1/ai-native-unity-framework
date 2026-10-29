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
        private Vector3 hipPosition;

        [SerializeField]
        private Vector3 aimPosition;

        [SerializeField]
        private Quaternion hipRotation = Quaternion.identity;

        [SerializeField]
        private Quaternion aimRotation = Quaternion.identity;
        private readonly CharacterConfiguration _presentationConfiguration =
            new CharacterConfiguration();
        private CharacterMotionState _presentationState;
        private bool _externallyDriven;
        private float _aimWeight;
        private uint _lastShot;
        private bool _wasRolling;
        private bool _wasMelee;
        private bool _wasGrounded = true;
        private int _armsLayer;
        private int _upperLayer;
        private int _shotLayer;
        private int _fullLayer;
        private static readonly int _inputMagnitude = Animator.StringToHash("InputMagnitude");
        private static readonly int _inputHorizontal = Animator.StringToHash("InputHorizontal");
        private static readonly int _inputVertical = Animator.StringToHash("InputVertical");
        private static readonly int _isGrounded = Animator.StringToHash("IsGrounded");
        private static readonly int _isCrouching = Animator.StringToHash("IsCrouching");
        private static readonly int _isStrafing = Animator.StringToHash("IsStrafing");
        private static readonly int _isSprinting = Animator.StringToHash("IsSprinting");
        private static readonly int _isAiming = Animator.StringToHash("IsAiming");
        private static readonly int _canAim = Animator.StringToHash("CanAim");
        private static readonly int _verticalVelocity = Animator.StringToHash("VerticalVelocity");
        private static readonly int _groundDistance = Animator.StringToHash("GroundDistance");
        private static readonly int _moveSetId = Animator.StringToHash("MoveSet_ID");
        private static readonly int _upperBodyId = Animator.StringToHash("UpperBody_ID");
        private static readonly int _shotId = Animator.StringToHash("Shot_ID");
        private static readonly int _actionState = Animator.StringToHash("ActionState");
        private static readonly int _shoot = Animator.StringToHash("Shoot");
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
            _externallyDriven = false;
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
        /// 使用应用持有的运动状态驱动动画与 IK，不运行本地 Motor。
        /// 保留序列化的 Animator、武器、握点及角色专属姿态。
        /// Configure 恢复本地 Motor 控制；外部运动使用默认速度阈值。
        /// </summary>
        public void SetPresentationState(CharacterMotionState state)
        {
            _presentationState = state;
            _externallyDriven = true;
        }

        /// <summary>角色专属的手部局部武器姿态，由应用提供，不属于共享模拟。</summary>
        public void ConfigureAimPose(Vector3 localPosition, Quaternion localRotation)
        {
            aimPosition = localPosition;
            aimRotation = localRotation;
        }

        private void Awake()
        {
            // 必须在首次动画计算前禁用，不能等到 Start。
            if (animator != null)
            {
                animator.applyRootMotion = false;
            }
        }

        private void Start()
        {
            if (animator == null)
            {
                enabled = false;
                return;
            }

            animator.applyRootMotion = false;
            _armsLayer = animator.GetLayerIndex("OnlyArms");
            _upperLayer = animator.GetLayerIndex("UpperBody");
            _shotLayer = animator.GetLayerIndex("Shot");
            _fullLayer = animator.GetLayerIndex("FullBody");
            Weight(animator.GetLayerIndex("RightArm"), 0);
            Weight(animator.GetLayerIndex("LeftArm"), 0);
            Weight(animator.GetLayerIndex("UnderBody"), 0);
        }

        private void Update()
        {
            if ((!_externallyDriven && motor == null) || animator == null)
            {
                return;
            }

            var state = _externallyDriven ? _presentationState : motor.State;
            var velocity = state.Velocity;
            velocity.y = 0;
            float speed = velocity.magnitude;
            var config = _externallyDriven ? _presentationConfiguration : motor.Configuration;
            float magnitude;
            if (speed <= config.WalkSpeed)
            {
                magnitude = .5f * speed / Mathf.Max(config.WalkSpeed, .01f);
            }
            else
            {
                if (speed <= config.RunSpeed)
                {
                    magnitude = Mathf.Lerp(
                        .5f,
                        1,
                        Mathf.InverseLerp(config.WalkSpeed, config.RunSpeed, speed)
                    );
                }
                else
                {
                    magnitude = Mathf.Lerp(
                        1,
                        1.5f,
                        Mathf.InverseLerp(config.RunSpeed, config.SprintSpeed, speed)
                    );
                }
            }
            bool armed = state.Mode == CharacterMode.Shooter;
            animator.SetFloat(_inputMagnitude, magnitude, .1f, Time.deltaTime);
            animator.SetFloat(
                _inputHorizontal,
                state.LocalMove.x * magnitude,
                .08f,
                Time.deltaTime
            );
            animator.SetFloat(_inputVertical, state.LocalMove.y * magnitude, .08f, Time.deltaTime);
            animator.SetFloat(_verticalVelocity, state.Velocity.y);
            animator.SetFloat(_groundDistance, state.Grounded ? 0 : 1);
            animator.SetBool(_isGrounded, state.Grounded);
            animator.SetBool(_isCrouching, state.Crouched);
            animator.SetBool(_isStrafing, state.Aiming);
            animator.SetBool(_isSprinting, speed > config.RunSpeed + .1f);
            animator.SetBool(_isAiming, state.Aiming);
            animator.SetBool(_canAim, true);
            animator.SetFloat(_moveSetId, armed ? 1 : 0);
            animator.SetFloat(_upperBodyId, armed ? 2 : 0);
            animator.SetFloat(_shotId, 2);
            // 导入状态图中缺失的 Invector 脚本原本负责派发动作状态。
            // 当前由 Motor 控制动作起止，复用已有状态，
            // 但不得让根运动或导入动作时长驱动胶囊体位移。
            animator.SetInteger(
                _actionState,
                state.Rolling || (!state.Grounded && state.Velocity.y > 0) ? 1 : 0
            );
            string locomotion;
            if (state.Aiming)
            {
                locomotion = state.Crouched ? StrafeCrouch : Strafe;
            }
            else
            {
                locomotion = state.Crouched ? Crouch : Free;
            }
            if (state.Rolling && !_wasRolling)
            {
                animator.CrossFadeInFixedTime(Roll, .08f, 0);
            }
            else if (!state.Rolling && _wasRolling)
            {
                animator.CrossFadeInFixedTime(state.Grounded ? locomotion : Fall, .08f, 0);
            }
            else if (!state.Rolling && !state.Grounded && _wasGrounded)
            {
                animator.CrossFadeInFixedTime(state.Velocity.y > 0 ? Jump : Fall, .08f, 0);
            }
            else if (!state.Rolling && state.Grounded && !_wasGrounded)
            {
                animator.CrossFadeInFixedTime(Land, .08f, 0);
            }

            if (state.Melee && !_wasMelee && _fullLayer >= 0)
            {
                animator.CrossFadeInFixedTime(Attack, .08f, _fullLayer);
            }

            _wasRolling = state.Rolling;
            _wasGrounded = state.Grounded;
            _wasMelee = state.Melee;
            Weight(_armsLayer, armed && !state.Rolling ? 1 : 0);
            Weight(_upperLayer, armed && !state.Rolling ? 1 : 0);
            Weight(_shotLayer, armed && !state.Rolling ? 1 : 0);
            Weight(_fullLayer, state.Melee ? 1 : 0);
            if (rifle != null && rifle.activeSelf != armed)
            {
                rifle.SetActive(armed);
            }

            if (rifle != null)
            {
                _aimWeight = Mathf.MoveTowards(
                    _aimWeight,
                    state.Aiming ? 1 : 0,
                    Time.deltaTime / .08f
                );
                rifle.transform.localPosition = Vector3.Lerp(hipPosition, aimPosition, _aimWeight);
                rifle.transform.localRotation = Quaternion.Slerp(
                    hipRotation,
                    aimRotation,
                    _aimWeight
                );
            }

            if (state.ShotSequence != _lastShot)
            {
                _lastShot = state.ShotSequence;
                animator.SetTrigger(_shoot);
            }
        }

        private void Weight(int layer, float weight)
        {
            if (layer >= 0)
            {
                animator.SetLayerWeight(layer, weight);
            }
        }

        private void OnAnimatorIK(int layerIndex)
        {
            if ((!_externallyDriven && motor == null) || animator == null)
            {
                return;
            }

            var state = _externallyDriven ? _presentationState : motor.State;
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
