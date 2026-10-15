using UnityEngine;

namespace AiNative.Client.Character
{
    /// <summary>Optional sample overlay. No gameplay input is read by IMGUI.</summary>
    public sealed class CharacterDemoHud : MonoBehaviour
    {
        [SerializeField] private CharacterMotor motor;
        [SerializeField] private CharacterInputReader input;
        public void Configure(CharacterMotor source, CharacterInputReader reader) { motor = source; input = reader; }
        private void OnGUI()
        {
            if (motor == null || input == null) return;
            GUI.Box(new Rect(16, 16, 530, 150), "CHARACTER LAB | " + motor.State.Mode);
            GUI.Label(new Rect(30, 43, 510, 90),
                "WASD Move | Mouse Look | Shift Sprint | Ctrl Walk\n" +
                "Space Jump | C Crouch | Alt Roll | 1 Free / 2 Rifle / 3 Melee\n" +
                "RMB Aim | LMB Action | Esc Cursor\n" +
                (input.IsCaptured ? "Rifle: 25 damage / shot | Targets reset 3s after defeat" : "Click outside UI, then release to resume"));
            var rifle = motor.GetComponent<CharacterRifle>();
            if (rifle != null) GUI.Label(new Rect(30, 135, 510, 25), "Shots " + rifle.Shots + " | Hits " + rifle.Hits + " | " + rifle.LastHit);
            if (motor.State.Mode == CharacterMode.Shooter) GUI.Label(new Rect(Screen.width / 2f - 4, Screen.height / 2f - 10, 20, 20), "+");
        }
    }
}
