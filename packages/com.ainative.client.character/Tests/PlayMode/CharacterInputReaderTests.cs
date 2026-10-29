using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;

namespace AiNative.Client.Character.Tests
{
    public sealed class CharacterInputReaderTests : InputTestFixture
    {
        private GameObject _owner;
        private CharacterInputReader _reader;
        private Keyboard _keyboard;
        private Mouse _mouse;

        public override void Setup()
        {
            base.Setup();
            _keyboard = InputSystem.AddDevice<Keyboard>();
            _mouse = InputSystem.AddDevice<Mouse>();
            _owner = new GameObject("Character input test");
            _reader = _owner.AddComponent<CharacterInputReader>();
        }

        public override void TearDown()
        {
            Object.DestroyImmediate(_owner);
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            base.TearDown();
        }

        [Test]
        public void DiagonalMovementIsNormalizedAndReleaseClearsContinuousInput()
        {
            Press(_keyboard.wKey);
            Press(_keyboard.dKey);
            Press(_keyboard.leftShiftKey);
            var frame = _reader.ConsumeFrame();
            Assert.That(frame.Move.magnitude, Is.EqualTo(1).Within(.0001f));
            Assert.That(frame.Move.x, Is.GreaterThan(0));
            Assert.That(frame.Move.y, Is.GreaterThan(0));
            Assert.That(frame.Sprint, Is.True);
            Release(_keyboard.wKey);
            Release(_keyboard.dKey);
            Release(_keyboard.leftShiftKey);
            frame = _reader.ConsumeFrame();
            Assert.That(frame.Move, Is.EqualTo(Vector2.zero));
            Assert.That(frame.Sprint, Is.False);
        }

        [Test]
        public void JumpIsConsumedOnceAndCanBePressedAgainAfterRelease()
        {
            Press(_keyboard.spaceKey);
            Assert.That(_reader.ConsumeFrame().Jump, Is.True);
            Assert.That(_reader.ConsumeFrame().Jump, Is.False);
            InputSystem.Update();
            Assert.That(_reader.ConsumeFrame().Jump, Is.False);
            Release(_keyboard.spaceKey);
            Press(_keyboard.spaceKey);
            Assert.That(_reader.ConsumeFrame().Jump, Is.True);
        }

        [TestCase(Key.Digit1, CharacterMode.Free)]
        [TestCase(Key.Digit2, CharacterMode.Shooter)]
        [TestCase(Key.Digit3, CharacterMode.Melee)]
        public void NumberKeySwitchesMotorModeOncePerPress(Key key, CharacterMode mode)
        {
            var motor = _owner.AddComponent<CharacterMotor>();
            motor.Configure(null, null);
            // 从其他模式开始，防止默认 Free 值掩盖输入丢失。
            motor.Simulate(
                new CharacterInputFrame
                {
                    ChangeMode = true,
                    Mode = mode == CharacterMode.Free ? CharacterMode.Melee : CharacterMode.Free,
                },
                0,
                1f / 60
            );
            for (int i = 0; i < 2; i++)
            {
                Press(_keyboard[key]);
                var frame = _reader.ConsumeFrame();
                Assert.That(frame.ChangeMode, Is.True);
                Assert.That(frame.Mode, Is.EqualTo(mode));
                motor.Simulate(frame, 0, 1f / 60);
                Assert.That(motor.State.Mode, Is.EqualTo(mode));
                Assert.That(_reader.ConsumeFrame().ChangeMode, Is.False);
                InputSystem.Update();
                Assert.That(_reader.ConsumeFrame().ChangeMode, Is.False);
                Release(_keyboard[key]);
                Assert.That(_reader.ConsumeFrame().ChangeMode, Is.False);
            }
        }

        [Test]
        public void ReleasingCaptureDisablesGameplayKeepsUiAndClearsPendingCommands()
        {
            Press(_keyboard.spaceKey);
            Press(_keyboard.wKey);
            _reader.SetCaptured(false);
            Assert.That(_reader.IsCaptured, Is.False);
            Assert.That(_reader.Actions.FindActionMap("Gameplay").enabled, Is.False);
            Assert.That(_reader.Actions.FindActionMap("UI").enabled, Is.True);
            Assert.That(_reader.ConsumeFrame().Jump, Is.False);
            Assert.That(_reader.ConsumeFrame().Move, Is.EqualTo(Vector2.zero));
        }

        [Test]
        public void FocusLossClearsCommandsAndDoesNotAutomaticallyRecapture()
        {
            Press(_keyboard.spaceKey);
            _owner.SendMessage("OnApplicationFocus", false);
            Assert.That(_reader.IsCaptured, Is.False);
            Assert.That(_reader.ConsumeFrame().Jump, Is.False);
            _owner.SendMessage("OnApplicationFocus", true);
            Assert.That(_reader.IsCaptured, Is.False);
        }

        [Test]
        public void DisableEnableClearsOldCommandsAndRestoresSingleConsumption()
        {
            Press(_keyboard.spaceKey);
            _reader.enabled = false;
            Assert.That(_reader.Actions.FindActionMap("Gameplay").enabled, Is.False);
            Assert.That(_reader.Actions.FindActionMap("UI").enabled, Is.False);
            Release(_keyboard.spaceKey);
            _reader.enabled = true;
            Assert.That(_reader.ConsumeFrame().Jump, Is.False);
            Press(_keyboard.spaceKey);
            Assert.That(_reader.ConsumeFrame().Jump, Is.True);
            Assert.That(_reader.ConsumeFrame().Jump, Is.False);
        }

        [UnityTest]
        public IEnumerator RecaptureClickRequiresReleaseAndNeverBecomesAnAttack()
        {
            _reader.SetCaptured(false);
            Press(_mouse.leftButton);
            yield return null;
            Assert.That(_reader.IsCaptured, Is.False);
            Assert.That(_reader.ConsumeFrame().Attack, Is.False);
            Release(_mouse.leftButton);
            yield return null;
            Assert.That(_reader.IsCaptured, Is.True);
            Assert.That(_reader.ConsumeFrame().Attack, Is.False);
            Press(_mouse.leftButton);
            // UnityTest 中的 InputTestFixture.Set 始终将事件入队；与普通
            // Test 不同，Press 要到下一次 PlayerLoop 更新才处理事件。
            yield return null;
            Assert.That(
                _mouse.leftButton.isPressed,
                Is.True,
                "The player loop must process the queued second click."
            );
            Assert.That(_reader.IsCaptured, Is.True, "Capture must survive the second click.");
            var attack = _reader.Actions.FindAction("Gameplay/Attack", true);
            Assert.That(
                attack.enabled,
                Is.True,
                "Gameplay attack must be enabled after recapture."
            );
            Assert.That(
                attack.phase,
                Is.EqualTo(InputActionPhase.Performed),
                "Device pressed but Attack did not perform."
            );
            Assert.That(
                _reader.ConsumeFrame().Attack,
                Is.True,
                "Performed attack must reach the pending input frame."
            );
        }
    }
}
