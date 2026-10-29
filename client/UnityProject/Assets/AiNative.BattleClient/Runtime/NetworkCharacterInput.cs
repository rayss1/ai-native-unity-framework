using AiNative.Gameplay;
using UnityEngine;

namespace AiNative.Client.Application
{
    /// <summary>将动态设备帧转换为固定 Tick 接受的网络命令。</summary>
    internal sealed class NetworkCharacterInput
    {
        private bool _jump;
        private bool _reload;
        public ArenaWeaponId Weapon { get; private set; }

        public void Sample(bool jumpPressed, ArenaWeaponId weapon, bool reloadPressed = false)
        {
            _jump |= jumpPressed;
            _reload |= reloadPressed;
            if (weapon != ArenaWeaponId.None)
            {
                Weapon = weapon;
            }
        }

        public ArenaButtons Buttons(bool fireHeld) =>
            (_jump ? ArenaButtons.Jump : ArenaButtons.None)
            | (fireHeld ? ArenaButtons.Fire : ArenaButtons.None)
            | (_reload ? ArenaButtons.Reload : ArenaButtons.None);

        // 准备失败时保留按键边沿，供下一 Tick 使用；焦点、UI 或会话边界
        // 则丢弃边沿，防止重连重放过期动作。
        public void Commit() => Clear();

        public void Clear()
        {
            _jump = _reload = false;
            Weapon = ArenaWeaponId.None;
        }

        public static void AimDelta(
            ArenaPlayerState predicted,
            Vector3 point,
            out int yaw,
            out int pitch
        )
        {
            Vector3 origin =
                new Vector3(
                    predicted.PositionXMillimetres,
                    predicted.PositionYMillimetres + ArenaHitGeometry.ShotHeight,
                    predicted.PositionZMillimetres
                ) / 1000f;
            Vector3 direction = point - origin;
            float heading = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
            float elevation =
                Mathf.Atan2(direction.y, new Vector2(direction.x, direction.z).magnitude)
                * Mathf.Rad2Deg;
            LookDelta(predicted, heading, -elevation, out yaw, out pitch);
        }

        public static Vector2Int RotateMove(Vector2Int move, float yawDegrees)
        {
            Vector2 normalized = Vector2.ClampMagnitude(new Vector2(move.x, move.y), 1000);
            float yaw = yawDegrees * Mathf.Deg2Rad;
            return new Vector2Int(
                Mathf.RoundToInt(normalized.x * Mathf.Cos(yaw) + normalized.y * Mathf.Sin(yaw)),
                Mathf.RoundToInt(normalized.y * Mathf.Cos(yaw) - normalized.x * Mathf.Sin(yaw))
            );
        }

        public static void LookDelta(
            ArenaPlayerState predicted,
            float cameraYaw,
            float cameraPitch,
            out int yaw,
            out int pitch
        )
        {
            yaw = Mathf.RoundToInt(
                Mathf.DeltaAngle(predicted.YawMillidegrees / 1000f, cameraYaw) * 1000
            );
            int targetPitch = Mathf.RoundToInt(Mathf.Clamp(-cameraPitch, -89, 89) * 1000);
            pitch = targetPitch - predicted.PitchMillidegrees;
        }
    }
}
