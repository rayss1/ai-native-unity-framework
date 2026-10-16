using AiNative.Client.Character;
using AiNative.Gameplay;
using UnityEngine;

namespace AiNative.Client.Application
{
    /// <summary>Application adapter: simulation owns movement/health; this component only poses the avatar.</summary>
    [DisallowMultipleComponent]
    public sealed class NetworkCharacterPresenter : MonoBehaviour
    {
        private CharacterAnimationDriver _animation;
        private uint _shots;
        private Light _muzzleFlash;
        private float _flashUntil;
        public CharacterMotionState MotionState { get; private set; }
        public ArenaPlayerState State { get; private set; }
        private void Awake()
        {
            _animation = GetComponentInChildren<CharacterAnimationDriver>(true);
            foreach (var child in GetComponentsInChildren<Transform>(true))
            {
                if (child.name != "Muzzle") continue;
                _muzzleFlash = child.gameObject.AddComponent<Light>();
                _muzzleFlash.color = new Color(1, .65f, .15f);
                _muzzleFlash.range = 2;
                _muzzleFlash.intensity = 3;
                _muzzleFlash.enabled = false;
                break;
            }
        }
        public void NotifyShot()
        {
            unchecked { _shots++; }
            _flashUntil = Time.unscaledTime + .075f;
            if (_muzzleFlash != null) _muzzleFlash.enabled = true;
        }
        private void Update()
        {
            if (_muzzleFlash != null && Time.unscaledTime >= _flashUntil) _muzzleFlash.enabled = false;
        }
        private void OnDisable() { if (_muzzleFlash != null) _muzzleFlash.enabled = false; }
        public void Apply(ArenaPlayerState state) => Apply(state, true);
        public void Apply(ArenaPlayerState state, bool aiming)
        {
            State = state;
            var velocity = new Vector3(state.VelocityXMillimetresPerSecond, state.VelocityYMillimetresPerSecond, state.VelocityZMillimetresPerSecond) / 1000f;
            var local = Quaternion.Euler(0, -state.YawMillidegrees / 1000f, 0) * velocity;
            var direction = new Vector2(local.x, local.z);
            if (direction.sqrMagnitude > 1) direction.Normalize();
            MotionState = new CharacterMotionState
            {
                Velocity = velocity, LocalMove = direction, Grounded = state.Grounded,
                Mode = CharacterMode.Shooter, Aiming = aiming, ShotSequence = _shots
            };
            if (_animation == null) _animation = GetComponentInChildren<CharacterAnimationDriver>(true);
            _animation?.SetPresentationState(MotionState);
            // Renderer state follows authoritative/predicted snapshot, never local health scripts.
            if (gameObject.activeSelf != state.Alive) gameObject.SetActive(state.Alive);
        }
    }
}
