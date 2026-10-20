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
        private float sensitivity = .12f,
            distance = 4f,
            aimDistance = 2.2f;

        [SerializeField]
        private float minPitch = -35,
            maxPitch = 70,
            collisionRadius = .2f;

        [SerializeField]
        private LayerMask obstacles = ~4;
        private float yaw,
            pitch = 15,
            currentDistance = 4;
        private Vector3 pivot;
        private bool initialized;
        private Transform presentationTarget;
        private CharacterMotionState presentationState;
        private bool externallyDriven;
        public float Yaw => yaw;

        /// <summary>Camera pitch in Unity Euler degrees (positive looks down).</summary>
        public float Pitch => pitch;

        public void Configure(CharacterMotor motor, CharacterInputReader reader)
        {
            target = motor;
            input = reader;
            externallyDriven = false;
            presentationTarget = null;
            Snap();
        }

        /// <summary>Follows application-owned motion without requiring a CharacterMotor.</summary>
        public void ConfigurePresentation(Transform followTarget)
        {
            presentationTarget = followTarget;
            externallyDriven = true;
            target = null;
            input = null;
            presentationState = default;
            Snap();
        }

        public void SetPresentationState(CharacterMotionState state) => presentationState = state;

        public void ApplyLook(Vector2 delta)
        {
            yaw = Mathf.Repeat(yaw + delta.x * sensitivity, 360);
            pitch = Mathf.Clamp(pitch - delta.y * sensitivity, minPitch, maxPitch);
        }

        public void Snap() => initialized = false;

        private void LateUpdate()
        {
            var followTarget =
                externallyDriven ? presentationTarget
                : target != null ? target.transform
                : null;
            if (followTarget == null)
                return;
            float dt = Time.deltaTime;
            var state = externallyDriven ? presentationState : target.State;
            Vector3 desiredPivot =
                followTarget.position + Vector3.up * (state.Crouched ? .9f : 1.45f);
            if (!initialized)
            {
                pivot = desiredPivot;
                currentDistance = distance;
                initialized = true;
            }
            pivot = Vector3.Lerp(pivot, desiredPivot, 1 - Mathf.Exp(-18 * dt));
            Quaternion rotation = Quaternion.Euler(pitch, yaw, 0);
            Vector3 shoulder = state.Aiming ? rotation * new Vector3(.45f, 0, 0) : Vector3.zero;
            // Test both the shoulder offset and the full camera boom from the unobstructed pivot.
            Vector3 desiredOffset =
                shoulder - rotation * Vector3.forward * (state.Aiming ? aimDistance : distance);
            float wanted = desiredOffset.magnitude;
            Vector3 direction = desiredOffset.normalized;
            float allowed = wanted;
            if (
                Physics.SphereCast(
                    pivot,
                    collisionRadius,
                    direction,
                    out var hit,
                    wanted,
                    obstacles,
                    QueryTriggerInteraction.Ignore
                )
            )
                allowed = Mathf.Max(0, hit.distance - .05f);
            currentDistance =
                allowed < currentDistance
                    ? allowed
                    : Mathf.Lerp(currentDistance, allowed, 1 - Mathf.Exp(-8 * dt));
            transform.SetPositionAndRotation(pivot + direction * currentDistance, rotation);
        }
    }
}
