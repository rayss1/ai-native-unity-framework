using UnityEngine;

namespace AiNative.Client.Character
{
    /// <summary>本地 PhysX 演示 Motor，不属于网络预测或权威模拟适配器。</summary>
    [RequireComponent(typeof(CharacterController)), DisallowMultipleComponent]
    public sealed class CharacterMotor : MonoBehaviour
    {
        [SerializeField]
        private CharacterInputReader input;

        [SerializeField]
        private CharacterOrbitCamera cameraRig;

        [SerializeField]
        private CharacterConfiguration configuration = new CharacterConfiguration();
        private CharacterController _controller;
        private CharacterMotionState _state;
        private Vector3 _planarVelocity;
        private Vector3 _rollDirection;
        private Vector3 _spawn;
        private float _verticalSpeed;
        private float _jumpAge = float.PositiveInfinity;
        private float _groundedAge = float.PositiveInfinity;
        private float _rollRemaining;
        private float _meleeRemaining;
        private readonly Collider[] _overlaps = new Collider[16];

        /// <summary>当前运动状态的值副本；在 Unity 主线程读取。</summary>
        public CharacterMotionState State => _state;

        /// <summary>主线程本地开火通知；订阅者负责在自身生命周期结束时退订。</summary>
        public event System.Action ShotFired;

        /// <summary>借用当前可变配置；须在主线程访问，不转移所有权。</summary>
        public CharacterConfiguration Configuration => configuration;

        /// <summary>在主线程绑定借用的输入与相机组件；不创建或销毁它们，由场景拥有者管理生命周期。</summary>
        public void Configure(CharacterInputReader reader, CharacterOrbitCamera orbit)
        {
            input = reader;
            cameraRig = orbit;
        }

        private void Awake()
        {
            _controller = GetComponent<CharacterController>();
            _spawn = transform.position;
        }

        private void Update()
        {
            if (input == null || cameraRig == null)
            {
                return;
            }

            var frame = input.ConsumeFrame();
            cameraRig.ApplyLook(frame.Look);
            Simulate(frame, cameraRig.Yaw, Mathf.Min(Time.deltaTime, .05f));
        }

        /// <summary>仅限主线程，使用秒和角度；用于确定性命令驱动的本地测试。</summary>
        public void Simulate(CharacterInputFrame frame, float cameraYaw, float deltaSeconds)
        {
            if (deltaSeconds <= 0 || !isActiveAndEnabled)
            {
                return;
            }

            if (_controller == null)
            {
                _controller = GetComponent<CharacterController>();
            }

            float dt = Mathf.Min(deltaSeconds, .05f);
            var c = configuration;
            bool grounded = _controller.isGrounded && _verticalSpeed <= 0;
            _groundedAge = grounded ? 0 : _groundedAge + dt;
            _jumpAge = frame.Jump ? 0 : _jumpAge + dt;
            bool wasBusy = _rollRemaining > 0 || _meleeRemaining > 0;
            _rollRemaining = Mathf.Max(0, _rollRemaining - dt);
            _meleeRemaining = Mathf.Max(0, _meleeRemaining - dt);
            if (!wasBusy && frame.ChangeMode)
            {
                _state.Mode = frame.Mode;
            }

            if (!wasBusy && frame.Crouch && grounded)
            {
                if (!_state.Crouched || CanStand())
                {
                    _state.Crouched = !_state.Crouched;
                }
            }

            _controller.height = _state.Crouched ? c.CrouchingHeight : c.StandingHeight;
            _controller.center = Vector3.up * (_controller.height * .5f);
            Vector2 move = Vector2.ClampMagnitude(frame.Move, 1f);
            Vector3 wish = Quaternion.Euler(0, cameraYaw, 0) * new Vector3(move.x, 0, move.y);
            _state.Aiming = _state.Mode == CharacterMode.Shooter && frame.Aim && !wasBusy;
            if (!wasBusy && grounded && frame.Roll && !_state.Crouched)
            {
                _rollRemaining = c.RollDuration;
                _rollDirection = wish.sqrMagnitude > .001f ? wish.normalized : transform.forward;
            }
            else if (!wasBusy && grounded && frame.Attack)
            {
                if (_state.Mode == CharacterMode.Melee)
                {
                    _meleeRemaining = c.MeleeDuration;
                }

                if (_state.Mode == CharacterMode.Shooter)
                {
                    _state.ShotSequence++;
                    ShotFired?.Invoke();
                }
            }

            _state.Rolling = _rollRemaining > 0;
            _state.Melee = _meleeRemaining > 0;
            if (_state.Rolling || _state.Melee)
            {
                _state.Aiming = false;
            }

            if (grounded && _verticalSpeed < 0)
            {
                _verticalSpeed = -2f;
            }

            if (
                !_state.Rolling
                && !_state.Melee
                && !_state.Crouched
                && _jumpAge <= c.JumpBuffer
                && _groundedAge <= c.CoyoteTime
            )
            {
                _verticalSpeed = Mathf.Sqrt(2 * c.Gravity * c.JumpHeight);
                _jumpAge = _groundedAge = float.PositiveInfinity;
                grounded = false;
            }

            _verticalSpeed -= c.Gravity * dt;
            float speed;
            if (_state.Crouched)
            {
                speed = c.CrouchSpeed;
            }
            else if (_state.Aiming)
            {
                speed = c.AimSpeed;
            }
            else if (frame.Walk)
            {
                speed = c.WalkSpeed;
            }
            else
            {
                speed = frame.Sprint ? c.SprintSpeed : c.RunSpeed;
            }
            if (_state.Rolling)
            {
                _planarVelocity = _rollDirection * (c.RollDistance / c.RollDuration);
            }
            else if (_state.Melee)
            {
                _planarVelocity = Vector3.zero;
            }
            else
            {
                _planarVelocity = Vector3.MoveTowards(
                    _planarVelocity,
                    wish * speed,
                    c.Acceleration * dt
                );
            }

            Vector3 face;
            if (_state.Rolling)
            {
                face = _rollDirection;
            }
            else
            {
                face = _state.Aiming ? Quaternion.Euler(0, cameraYaw, 0) * Vector3.forward : wish;
            }
            if (face.sqrMagnitude > .001f && !_state.Melee)
            {
                transform.rotation = Quaternion.RotateTowards(
                    transform.rotation,
                    Quaternion.LookRotation(face),
                    c.TurnSpeed * dt
                );
            }

            Vector3 before = transform.position;
            var flags = _controller.Move((_planarVelocity + Vector3.up * _verticalSpeed) * dt);
            if ((flags & CollisionFlags.Above) != 0 && _verticalSpeed > 0)
            {
                _verticalSpeed = 0;
            }

            _state.Grounded = (flags & CollisionFlags.Below) != 0 && _verticalSpeed <= 0;
            _state.Velocity = (transform.position - before) / dt;
            var local = transform.InverseTransformDirection(_state.Velocity);
            _state.LocalMove = Vector2.ClampMagnitude(
                new Vector2(local.x, local.z) / Mathf.Max(speed, .01f),
                1
            );
            if (transform.position.y < -12)
            {
                Respawn();
            }
        }

        public bool CanStand()
        {
            var c = configuration;
            float radius = _controller.radius * .95f;
            Vector3 bottom = transform.position + Vector3.up * (_controller.radius + .02f);
            Vector3 top = transform.position + Vector3.up * (c.StandingHeight - _controller.radius);
            int count = Physics.OverlapCapsuleNonAlloc(
                bottom,
                top,
                radius,
                _overlaps,
                c.EnvironmentMask,
                QueryTriggerInteraction.Ignore
            );
            if (count == _overlaps.Length)
            {
                return false;
            }

            for (int i = 0; i < count; i++)
            {
                if (_overlaps[i] != _controller && !_overlaps[i].transform.IsChildOf(transform))
                {
                    return false;
                }
            }

            return true;
        }

        public void Respawn()
        {
            _controller.enabled = false;
            transform.position = _spawn;
            _controller.enabled = true;
            _state = default;
            _planarVelocity = Vector3.zero;
            _verticalSpeed = _rollRemaining = _meleeRemaining = 0;
            _jumpAge = _groundedAge = float.PositiveInfinity;
            input?.Clear();
            cameraRig?.Snap();
        }
    }
}
