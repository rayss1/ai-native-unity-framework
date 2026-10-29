using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;

namespace AiNative.Client.Character.Tests
{
    public sealed class CharacterActionContractTests
    {
        [Test]
        public void ControlsExposeGameplayAndCompleteUiActionsAsIndependentInstances()
        {
            var first = new CharacterControls();
            var second = new CharacterControls();
            try
            {
                Assert.That(first.asset, Is.Not.SameAs(second.asset));
                var gameplay = first.asset.FindActionMap("Gameplay", true);
                var ui = first.asset.FindActionMap("UI", true);
                foreach (
                    var name in new[]
                    {
                        "Move",
                        "Look",
                        "Sprint",
                        "Walk",
                        "Jump",
                        "Crouch",
                        "Roll",
                        "Aim",
                        "Attack",
                        "FreeMode",
                        "ShooterMode",
                        "MeleeMode",
                        "ReleaseCursor",
                    }
                )
                {
                    Assert.That(gameplay.FindAction(name), Is.Not.Null, name);
                }

                foreach (
                    var name in new[] { "Point", "Click", "Scroll", "Navigate", "Submit", "Cancel" }
                )
                {
                    Assert.That(ui.FindAction(name), Is.Not.Null, name);
                }

                Assert.That(gameplay.FindAction("Move").expectedControlType, Is.EqualTo("Vector2"));
                Assert.That(gameplay.FindAction("Look").expectedControlType, Is.EqualTo("Vector2"));
                gameplay.Enable();
                Assert.That(second.asset.FindActionMap("Gameplay", true).enabled, Is.False);
            }
            finally
            {
                // EditMode 不能使用生成包装器在运行时延迟执行的 Destroy。
                Object.DestroyImmediate(first.asset);
                Object.DestroyImmediate(second.asset);
            }
        }
    }
}
