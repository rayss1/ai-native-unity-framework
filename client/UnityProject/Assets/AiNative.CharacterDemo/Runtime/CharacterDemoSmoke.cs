using System;
using System.Collections;
using AiNative.Client.Character;
using UnityEngine;
using UnityEngine.InputSystem.UI;

namespace AiNative.CharacterDemo
{
    /// <summary>Opt-in built Player smoke; never runs during ordinary interactive play.</summary>
    public sealed class CharacterDemoSmoke : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void StartIfRequested()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--character-demo-smoke") >= 0)
                new GameObject("Character demo smoke").AddComponent<CharacterDemoSmoke>();
        }

        private IEnumerator Start()
        {
            yield return null;
            var motor = FindFirstObjectByType<CharacterMotor>();
            var reader = FindFirstObjectByType<CharacterInputReader>();
            var animator = motor != null ? motor.GetComponentInChildren<Animator>() : null;
            if (motor == null || reader == null || animator == null || !animator.isHuman ||
                FindFirstObjectByType<InputSystemUIInputModule>() == null)
            { Fail("Missing demo composition or Humanoid rig"); yield break; }
            reader.SetCaptured(false);
            motor.Configure(null, null); // Explicitly drive this opt-in smoke, never double-step automatic Update.
            for (int i = 0; i < 30; i++) { motor.Simulate(default, 0, 1f / 60); yield return null; }
            if (!motor.State.Grounded) { Fail("Did not settle on ground"); yield break; }
            Vector3 start = motor.transform.position;
            for (int i = 0; i < 60; i++) { motor.Simulate(new CharacterInputFrame { Move = Vector2.up }, 0, 1f / 60); yield return null; }
            if (motor.transform.position.z - start.z < 3) { Fail("Movement did not advance"); yield break; }
            motor.Simulate(new CharacterInputFrame { Jump = true }, 0, 1f / 60);
            if (motor.State.Grounded) { Fail("Jump did not leave ground"); yield break; }
            for (int i = 0; i < 90; i++) { motor.Simulate(default, 0, 1f / 60); yield return null; }
            var rifle = motor.GetComponent<CharacterRifle>();
            var target = FindFirstObjectByType<CharacterHealth>();
            if (rifle == null || target == null) { Fail("Missing rifle or training targets"); yield break; }
            var camera = Camera.main;
            camera.GetComponent<CharacterOrbitCamera>().enabled = false;
            camera.transform.position = motor.transform.position + Vector3.up * 1.5f - Vector3.forward * 3;
            camera.transform.LookAt(target.transform.position);
            motor.Simulate(new CharacterInputFrame { ChangeMode = true, Mode = CharacterMode.Shooter, Aim = true, Attack = true }, 0, 1f / 60);
            if (motor.State.ShotSequence != 1) { Fail("Rifle action did not fire"); yield break; }
            float deadline = Time.realtimeSinceStartup + 3;
            while (target.Current == target.Maximum && Time.realtimeSinceStartup < deadline)
            { motor.Simulate(new CharacterInputFrame { Aim = true }, 0, 1f / 60); yield return null; }
            if (rifle.Shots != 1 || target.Current != 75) { Fail("Accepted rifle shot did not deal exactly 25 damage: " + rifle.LastHit); yield break; }
            motor.Simulate(new CharacterInputFrame { ChangeMode = true, Mode = CharacterMode.Melee, Attack = true }, 0, 1f / 60);
            if (!motor.State.Melee) { Fail("Melee action did not start"); yield break; }
            for (int i = 0; i < 70; i++) { motor.Simulate(default, 0, 1f / 60); yield return null; }
            Debug.Log("CHARACTER_DEMO_SMOKE_PASS: composition, grounded movement, jump, rifle projectile damage and melee animation");
            Application.Quit(0);
        }

        private static void Fail(string reason)
        {
            Debug.LogError("CHARACTER_DEMO_SMOKE_FAIL: " + reason);
            Application.Quit(1);
        }
    }
}
