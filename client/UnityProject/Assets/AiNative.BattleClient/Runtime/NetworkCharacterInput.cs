using AiNative.Gameplay;
using UnityEngine;

namespace AiNative.Client.Application
{
    /// <summary>Bridges dynamic device frames to accepted fixed-tick network commands.</summary>
    internal sealed class NetworkCharacterInput
    {
        private bool _jump;
        public ArenaWeaponId Weapon { get; private set; }

        public void Sample(bool jumpPressed, ArenaWeaponId weapon)
        {
            _jump |= jumpPressed;
            if (weapon != ArenaWeaponId.None) Weapon = weapon;
        }

        public ArenaButtons Buttons(bool fireHeld) =>
            (_jump ? ArenaButtons.Jump : ArenaButtons.None) | (fireHeld ? ArenaButtons.Fire : ArenaButtons.None);

        // A rejected prepare leaves edges buffered for the next tick. Focus/UI/session
        // boundaries instead discard them, so reconnect never replays a stale action.
        public void Commit() => Clear();
        public void Clear() { _jump = false; Weapon = ArenaWeaponId.None; }

        public static Vector2Int RotateMove(Vector2Int move, float yawDegrees)
        {
            Vector2 normalized = Vector2.ClampMagnitude(new Vector2(move.x, move.y), 1000);
            float yaw = yawDegrees * Mathf.Deg2Rad;
            return new Vector2Int(Mathf.RoundToInt(normalized.x * Mathf.Cos(yaw) + normalized.y * Mathf.Sin(yaw)),
                Mathf.RoundToInt(normalized.y * Mathf.Cos(yaw) - normalized.x * Mathf.Sin(yaw)));
        }

        public static void LookDelta(ArenaPlayerState predicted, float cameraYaw, float cameraPitch, out int yaw, out int pitch)
        {
            yaw = Mathf.RoundToInt(Mathf.DeltaAngle(predicted.YawMillidegrees / 1000f, cameraYaw) * 1000);
            int targetPitch = Mathf.RoundToInt(Mathf.Clamp(-cameraPitch, -89, 89) * 1000);
            pitch = targetPitch - predicted.PitchMillidegrees;
        }
    }
}
