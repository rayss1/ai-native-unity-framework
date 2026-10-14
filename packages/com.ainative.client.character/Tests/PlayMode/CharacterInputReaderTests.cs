using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;

namespace AiNative.Client.Character.Tests
{
    public sealed class CharacterInputReaderTests : InputTestFixture
    {
        private GameObject owner;
        private CharacterInputReader reader;
        private Keyboard keyboard;
        private Mouse mouse;

        public override void Setup()
        {
            base.Setup();
            keyboard = InputSystem.AddDevice<Keyboard>();
            mouse = InputSystem.AddDevice<Mouse>();
            owner = new GameObject("Character input test");
            reader = owner.AddComponent<CharacterInputReader>();
        }

        public override void TearDown()
        {
            Object.DestroyImmediate(owner);
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            base.TearDown();
        }

        [Test]
        public void DiagonalMovementIsNormalizedAndReleaseClearsContinuousInput()
        {
            Press(keyboard.wKey);
            Press(keyboard.dKey);
            Press(keyboard.leftShiftKey);
            var frame = reader.ConsumeFrame();
            Assert.That(frame.Move.magnitude, Is.EqualTo(1).Within(.0001f));
            Assert.That(frame.Move.x, Is.GreaterThan(0));
            Assert.That(frame.Move.y, Is.GreaterThan(0));
            Assert.That(frame.Sprint, Is.True);
            Release(keyboard.wKey);
            Release(keyboard.dKey);
            Release(keyboard.leftShiftKey);
            frame = reader.ConsumeFrame();
            Assert.That(frame.Move, Is.EqualTo(Vector2.zero));
            Assert.That(frame.Sprint, Is.False);
        }

        [Test]
        public void JumpIsConsumedOnceAndCanBePressedAgainAfterRelease()
        {
            Press(keyboard.spaceKey);
            Assert.That(reader.ConsumeFrame().Jump, Is.True);
            Assert.That(reader.ConsumeFrame().Jump, Is.False);
            InputSystem.Update();
            Assert.That(reader.ConsumeFrame().Jump, Is.False);
            Release(keyboard.spaceKey);
            Press(keyboard.spaceKey);
            Assert.That(reader.ConsumeFrame().Jump, Is.True);
        }

        [TestCase(Key.Digit1, CharacterMode.Free)]
        [TestCase(Key.Digit2, CharacterMode.Shooter)]
        [TestCase(Key.Digit3, CharacterMode.Melee)]
        public void NumberKeySwitchesMotorModeOncePerPress(Key key, CharacterMode mode)
        {
            var motor = owner.AddComponent<CharacterMotor>();
            motor.Configure(null, null);
            // Start in another mode so the default Free value cannot hide a lost input.
            motor.Simulate(new CharacterInputFrame { ChangeMode = true,
                Mode = mode == CharacterMode.Free ? CharacterMode.Melee : CharacterMode.Free }, 0, 1f / 60);
            for (int i = 0; i < 2; i++)
            {
                Press(keyboard[key]);
                var frame = reader.ConsumeFrame();
                Assert.That(frame.ChangeMode, Is.True);
                Assert.That(frame.Mode, Is.EqualTo(mode));
                motor.Simulate(frame, 0, 1f / 60);
                Assert.That(motor.State.Mode, Is.EqualTo(mode));
                Assert.That(reader.ConsumeFrame().ChangeMode, Is.False);
                InputSystem.Update();
                Assert.That(reader.ConsumeFrame().ChangeMode, Is.False);
                Release(keyboard[key]);
                Assert.That(reader.ConsumeFrame().ChangeMode, Is.False);
            }
        }

        [Test]
        public void ReleasingCaptureDisablesGameplayKeepsUiAndClearsPendingCommands()
        {
            Press(keyboard.spaceKey);
            Press(keyboard.wKey);
            reader.SetCaptured(false);
            Assert.That(reader.IsCaptured, Is.False);
            Assert.That(reader.Actions.FindActionMap("Gameplay").enabled, Is.False);
            Assert.That(reader.Actions.FindActionMap("UI").enabled, Is.True);
            Assert.That(reader.ConsumeFrame().Jump, Is.False);
            Assert.That(reader.ConsumeFrame().Move, Is.EqualTo(Vector2.zero));
        }

        [Test]
        public void FocusLossClearsCommandsAndDoesNotAutomaticallyRecapture()
        {
            Press(keyboard.spaceKey);
            owner.SendMessage("OnApplicationFocus", false);
            Assert.That(reader.IsCaptured, Is.False);
            Assert.That(reader.ConsumeFrame().Jump, Is.False);
            owner.SendMessage("OnApplicationFocus", true);
            Assert.That(reader.IsCaptured, Is.False);
        }

        [Test]
        public void DisableEnableClearsOldCommandsAndRestoresSingleConsumption()
        {
            Press(keyboard.spaceKey);
            reader.enabled = false;
            Assert.That(reader.Actions.FindActionMap("Gameplay").enabled, Is.False);
            Assert.That(reader.Actions.FindActionMap("UI").enabled, Is.False);
            Release(keyboard.spaceKey);
            reader.enabled = true;
            Assert.That(reader.ConsumeFrame().Jump, Is.False);
            Press(keyboard.spaceKey);
            Assert.That(reader.ConsumeFrame().Jump, Is.True);
            Assert.That(reader.ConsumeFrame().Jump, Is.False);
        }

        [UnityTest]
        public IEnumerator RecaptureClickRequiresReleaseAndNeverBecomesAnAttack()
        {
            reader.SetCaptured(false);
            Press(mouse.leftButton);
            yield return null;
            Assert.That(reader.IsCaptured, Is.False);
            Assert.That(reader.ConsumeFrame().Attack, Is.False);
            Release(mouse.leftButton);
            yield return null;
            Assert.That(reader.IsCaptured, Is.True);
            Assert.That(reader.ConsumeFrame().Attack, Is.False);
            Press(mouse.leftButton);
            // InputTestFixture.Set always queues events in a UnityTest; unlike a plain
            // Test, Press does not process them until the following player-loop update.
            yield return null;
            Assert.That(mouse.leftButton.isPressed, Is.True, "The player loop must process the queued second click.");
            Assert.That(reader.IsCaptured, Is.True, "Capture must survive the second click.");
            var attack = reader.Actions.FindAction("Gameplay/Attack", true);
            Assert.That(attack.enabled, Is.True, "Gameplay attack must be enabled after recapture.");
            Assert.That(attack.phase, Is.EqualTo(InputActionPhase.Performed), "Device pressed but Attack did not perform.");
            Assert.That(reader.ConsumeFrame().Attack, Is.True, "Performed attack must reach the pending input frame.");
        }
    }
}
