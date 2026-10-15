using UnityEngine;

namespace AiNative.Client.Character
{
    /// <summary>Local PhysX demo motor. Not a network prediction or authoritative simulation adapter.</summary>
    [RequireComponent(typeof(CharacterController)), DisallowMultipleComponent]
    public sealed class CharacterMotor : MonoBehaviour
    {
        [SerializeField] private CharacterInputReader input;
        [SerializeField] private CharacterOrbitCamera cameraRig;
        [SerializeField] private CharacterConfiguration configuration = new CharacterConfiguration();
        private CharacterController controller;
        private CharacterMotionState state;
        private Vector3 planarVelocity, rollDirection, spawn;
        private float verticalSpeed, jumpAge = float.PositiveInfinity, groundedAge = float.PositiveInfinity;
        private float rollRemaining, meleeRemaining;
        private readonly Collider[] overlaps = new Collider[16];
        public CharacterMotionState State => state;
        public event System.Action ShotFired;
        public CharacterConfiguration Configuration => configuration;
        public void Configure(CharacterInputReader reader, CharacterOrbitCamera orbit) { input = reader; cameraRig = orbit; }

        private void Awake()
        {
            controller = GetComponent<CharacterController>();
            spawn = transform.position;
        }

        private void Update()
        {
            if (input == null || cameraRig == null) return;
            var frame = input.ConsumeFrame();
            cameraRig.ApplyLook(frame.Look);
            Simulate(frame, cameraRig.Yaw, Mathf.Min(Time.deltaTime, .05f));
        }

        /// <summary>Main-thread only, seconds and degrees. Exposed for deterministic command-driven local tests.</summary>
        public void Simulate(CharacterInputFrame frame, float cameraYaw, float deltaSeconds)
        {
            if (deltaSeconds <= 0 || !isActiveAndEnabled) return;
            if (controller == null) controller = GetComponent<CharacterController>();
            float dt = Mathf.Min(deltaSeconds, .05f);
            var c = configuration;
            bool grounded = controller.isGrounded && verticalSpeed <= 0;
            groundedAge = grounded ? 0 : groundedAge + dt;
            jumpAge = frame.Jump ? 0 : jumpAge + dt;
            bool wasBusy = rollRemaining > 0 || meleeRemaining > 0;
            rollRemaining = Mathf.Max(0, rollRemaining - dt);
            meleeRemaining = Mathf.Max(0, meleeRemaining - dt);
            if (!wasBusy && frame.ChangeMode) state.Mode = frame.Mode;
            if (!wasBusy && frame.Crouch && grounded)
            {
                if (!state.Crouched || CanStand()) state.Crouched = !state.Crouched;
            }
            controller.height = state.Crouched ? c.CrouchingHeight : c.StandingHeight;
            controller.center = Vector3.up * (controller.height * .5f);

            Vector2 move = Vector2.ClampMagnitude(frame.Move, 1f);
            Vector3 wish = Quaternion.Euler(0, cameraYaw, 0) * new Vector3(move.x, 0, move.y);
            state.Aiming = state.Mode == CharacterMode.Shooter && frame.Aim && !wasBusy;
            if (!wasBusy && grounded && frame.Roll && !state.Crouched)
            {
                rollRemaining = c.RollDuration;
                rollDirection = wish.sqrMagnitude > .001f ? wish.normalized : transform.forward;
            }
            else if (!wasBusy && grounded && frame.Attack)
            {
                if (state.Mode == CharacterMode.Melee) meleeRemaining = c.MeleeDuration;
                if (state.Mode == CharacterMode.Shooter) { state.ShotSequence++; ShotFired?.Invoke(); }
            }
            state.Rolling = rollRemaining > 0;
            state.Melee = meleeRemaining > 0;
            if (state.Rolling || state.Melee) state.Aiming = false;
            if (grounded && verticalSpeed < 0) verticalSpeed = -2f;
            if (!state.Rolling && !state.Melee && !state.Crouched && jumpAge <= c.JumpBuffer && groundedAge <= c.CoyoteTime)
            {
                verticalSpeed = Mathf.Sqrt(2 * c.Gravity * c.JumpHeight);
                jumpAge = groundedAge = float.PositiveInfinity;
                grounded = false;
            }
            verticalSpeed -= c.Gravity * dt;
            float speed = state.Crouched ? c.CrouchSpeed : state.Aiming ? c.AimSpeed : frame.Walk ? c.WalkSpeed : frame.Sprint ? c.SprintSpeed : c.RunSpeed;
            if (state.Rolling) planarVelocity = rollDirection * (c.RollDistance / c.RollDuration);
            else if (state.Melee) planarVelocity = Vector3.zero;
            else planarVelocity = Vector3.MoveTowards(planarVelocity, wish * speed, c.Acceleration * dt);
            Vector3 face = state.Rolling ? rollDirection : state.Aiming ? Quaternion.Euler(0, cameraYaw, 0) * Vector3.forward : wish;
            if (face.sqrMagnitude > .001f && !state.Melee)
                transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(face), c.TurnSpeed * dt);
            Vector3 before = transform.position;
            var flags = controller.Move((planarVelocity + Vector3.up * verticalSpeed) * dt);
            if ((flags & CollisionFlags.Above) != 0 && verticalSpeed > 0) verticalSpeed = 0;
            state.Grounded = (flags & CollisionFlags.Below) != 0 && verticalSpeed <= 0;
            state.Velocity = (transform.position - before) / dt;
            var local = transform.InverseTransformDirection(state.Velocity);
            state.LocalMove = Vector2.ClampMagnitude(new Vector2(local.x, local.z) / Mathf.Max(speed, .01f), 1);
            if (transform.position.y < -12) Respawn();
        }

        public bool CanStand()
        {
            var c = configuration;
            float radius = controller.radius * .95f;
            Vector3 bottom = transform.position + Vector3.up * (controller.radius + .02f);
            Vector3 top = transform.position + Vector3.up * (c.StandingHeight - controller.radius);
            int count = Physics.OverlapCapsuleNonAlloc(bottom, top, radius, overlaps, c.EnvironmentMask, QueryTriggerInteraction.Ignore);
            if (count == overlaps.Length) return false;
            for (int i = 0; i < count; i++)
                if (overlaps[i] != controller && !overlaps[i].transform.IsChildOf(transform)) return false;
            return true;
        }

        public void Respawn()
        {
            controller.enabled = false;
            transform.position = spawn;
            controller.enabled = true;
            state = default;
            planarVelocity = Vector3.zero;
            verticalSpeed = rollRemaining = meleeRemaining = 0;
            jumpAge = groundedAge = float.PositiveInfinity;
            input?.Clear();
            cameraRig?.Snap();
        }
    }
}
