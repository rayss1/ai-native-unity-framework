using UnityEngine;

namespace AiNative.Client.Character
{
    [DisallowMultipleComponent]
    public sealed class CharacterOrbitCamera : MonoBehaviour
    {
        [SerializeField]
        private CharacterMotor target;

        [SerializeField]
        private CharacterInputReader input;

        [SerializeField]
        private float sensitivity = .12f;

        [SerializeField]
        private float distance = 4f;

        [SerializeField]
        private float aimDistance = 2.2f;

        [SerializeField]
        private float minPitch = -35;

        [SerializeField]
        private float maxPitch = 70;

        [SerializeField]
        private float collisionRadius = .2f;

        [SerializeField]
        private LayerMask obstacles = ~4;
        private float _yaw;
        private float _pitch = 15;
        private float _currentDistance = 4;
        private Vector3 _pivot;
        private bool _initialized;
        private Transform _presentationTarget;
        private CharacterMotionState _presentationState;
        private bool _externallyDriven;
        public float Yaw => _yaw;

        /// <summary>Unity 欧拉角形式的相机俯仰角，正值表示向下看。</summary>
        public float Pitch => _pitch;

        public void Configure(CharacterMotor motor, CharacterInputReader reader)
        {
            target = motor;
            input = reader;
            _externallyDriven = false;
            _presentationTarget = null;
            Snap();
        }

        /// <summary>跟随应用持有的运动状态，不要求存在 CharacterMotor。</summary>
        public void ConfigurePresentation(Transform followTarget)
        {
            _presentationTarget = followTarget;
            _externallyDriven = true;
            target = null;
            input = null;
            _presentationState = default;
            Snap();
        }

        public void SetPresentationState(CharacterMotionState state) => _presentationState = state;

        public void ApplyLook(Vector2 delta)
        {
            _yaw = Mathf.Repeat(_yaw + delta.x * sensitivity, 360);
            _pitch = Mathf.Clamp(_pitch - delta.y * sensitivity, minPitch, maxPitch);
        }

        public void Snap() => _initialized = false;

        private void LateUpdate()
        {
            Transform followTarget;
            if (_externallyDriven)
            {
                followTarget = _presentationTarget;
            }
            else
            {
                followTarget = target != null ? target.transform : null;
            }
            if (followTarget == null)
            {
                return;
            }

            float dt = Time.deltaTime;
            var state = _externallyDriven ? _presentationState : target.State;
            Vector3 desiredPivot =
                followTarget.position + Vector3.up * (state.Crouched ? .9f : 1.45f);
            if (!_initialized)
            {
                _pivot = desiredPivot;
                _currentDistance = distance;
                _initialized = true;
            }

            _pivot = Vector3.Lerp(_pivot, desiredPivot, 1 - Mathf.Exp(-18 * dt));
            Quaternion rotation = Quaternion.Euler(_pitch, _yaw, 0);
            Vector3 shoulder = state.Aiming ? rotation * new Vector3(.45f, 0, 0) : Vector3.zero;
            // 从未被遮挡的枢轴同时检查肩部偏移和完整相机臂。
            Vector3 desiredOffset =
                shoulder - rotation * Vector3.forward * (state.Aiming ? aimDistance : distance);
            float wanted = desiredOffset.magnitude;
            Vector3 direction = desiredOffset.normalized;
            float allowed = wanted;
            if (
                Physics.SphereCast(
                    _pivot,
                    collisionRadius,
                    direction,
                    out var hit,
                    wanted,
                    obstacles,
                    QueryTriggerInteraction.Ignore
                )
            )
            {
                allowed = Mathf.Max(0, hit.distance - .05f);
            }

            _currentDistance =
                allowed < _currentDistance
                    ? allowed
                    : Mathf.Lerp(_currentDistance, allowed, 1 - Mathf.Exp(-8 * dt));
            transform.SetPositionAndRotation(_pivot + direction * _currentDistance, rotation);
        }
    }
}
