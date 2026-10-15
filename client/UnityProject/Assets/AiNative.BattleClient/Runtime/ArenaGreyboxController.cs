using AiNative.Gameplay;
using UnityEngine;
using UnityEngine.InputSystem;
using Gyroscope = UnityEngine.InputSystem.Gyroscope;
using UnityEngine.UI;

namespace AiNative.Client.Application
{
    /// <summary>Small greybox harness for exercising arena movement and mobile controls.</summary>
    [DisallowMultipleComponent]
    public sealed class ArenaGreyboxController : MonoBehaviour
    {
        [SerializeField] private Camera playerCamera;
        [SerializeField] private Text hud;
        [SerializeField] private float lookSensitivity = 0.12f;
        [SerializeField] private bool enableGyro;
        private ArenaPlayerState _state;
        private Vector2 _move;
        private Vector2 _look;
        private int _weapon = 1;
        private Gyroscope _ownedGyro;

        public void SetMove(Vector2 value) => _move = Vector2.ClampMagnitude(value, 1f);
        public void SetLook(Vector2 value) => _look = value;
        public void SetGyroEnabled(bool enabled) => enableGyro = enabled;
        public void SwitchWeapon(int weapon) => _weapon = Mathf.Clamp(weapon, 1, 3);

        private void Awake()
        {
            _state = new ArenaPlayerState(1, 0, 0, 0);
            _state.Weapon = ArenaWeaponId.Machinegun;
            if (playerCamera is null) playerCamera = GetComponentInChildren<Camera>();
            if (playerCamera is not null) playerCamera.fieldOfView = 90f;
        }

        private void OnDisable() => ReleaseGyro();

        private void ReleaseGyro()
        {
            if (_ownedGyro != null && _ownedGyro.added) InputSystem.DisableDevice(_ownedGyro);
            _ownedGyro = null;
        }

        private void Update()
        {
            Vector2 move = _move;
            if (move == Vector2.zero)
            {
                Keyboard keyboard = Keyboard.current;
                move = keyboard == null ? Vector2.zero : new Vector2(
                    (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed ? 1 : 0) - (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed ? 1 : 0),
                    (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed ? 1 : 0) - (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed ? 1 : 0));
                move = Vector2.ClampMagnitude(move, 1f);
            }
            Vector2 look = _look;
            Gyroscope sensor = enableGyro ? Gyroscope.current : null;
            if (_ownedGyro != null && _ownedGyro != sensor) ReleaseGyro();
            if (sensor != null)
            {
                if (!sensor.enabled) { InputSystem.EnableDevice(sensor); _ownedGyro = sensor; }
                Vector3 gyro = sensor.angularVelocity.ReadValue();
                look += new Vector2(gyro.x, gyro.y) * 0.15f;
            }
            _state.YawMillidegrees += Mathf.RoundToInt(look.x * lookSensitivity * 1000f);
            _state.PitchMillidegrees = Mathf.Clamp(
                _state.PitchMillidegrees - Mathf.RoundToInt(look.y * lookSensitivity * 1000f),
                -ArenaMovement.MaximumPitchMillidegrees, ArenaMovement.MaximumPitchMillidegrees);
            _look = Vector2.zero;
            _state.Weapon = (ArenaWeaponId)_weapon;
            _state = ArenaMovement.Step(_state, new ArenaInput(1, (ulong)(Time.frameCount),
                Mathf.RoundToInt(move.x * 1000f), Mathf.RoundToInt(move.y * 1000f), 0, 0,
                ArenaButtons.None, _state.Weapon));
            transform.position = new Vector3(_state.PositionXMillimetres / 1000f,
                _state.PositionYMillimetres / 1000f, _state.PositionZMillimetres / 1000f);
            transform.rotation = Quaternion.Euler(0, _state.YawMillidegrees / 1000f, 0);
            if (playerCamera is not null) playerCamera.transform.localRotation =
                Quaternion.Euler(_state.PitchMillidegrees / 1000f, 0, 0);
            if (hud is not null) hud.text = $"HP {_state.Health}  AR {_state.Armor}  {_state.Weapon}  K {_state.Kills}";
        }
    }
}
