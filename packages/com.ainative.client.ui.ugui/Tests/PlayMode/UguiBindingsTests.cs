using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AiNative.Client.Core.Mvvm;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace AiNative.Client.UI.Tests
{
    public sealed class UguiBindingsTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();
        private BindingScope _bindings;

        [SetUp]
        public void SetUp()
        {
            _bindings = new BindingScope();
        }

        [TearDown]
        public void TearDown()
        {
            _bindings.Dispose();
            foreach (GameObject instance in _objects)
            {
                if (instance != null)
                {
                    UnityEngine.Object.DestroyImmediate(instance);
                }
            }
            _objects.Clear();
        }

        [Test]
        public void InputSynchronizesBothWaysWithoutProgrammaticEcho()
        {
            InputField input = Create<InputField>();
            var value = new BindableProperty<string>("initial");
            int inputChanges = 0;
            int propertyChanges = 0;
            input.onValueChanged.AddListener(_ => inputChanges++);
            value.PropertyChanged += (_, __) => propertyChanges++;
            _bindings.Bind(input, value);
            Assert.That(input.text, Is.EqualTo("initial"));
            Assert.That(inputChanges, Is.Zero);
            value.Value = "model";
            Assert.That(input.text, Is.EqualTo("model"));
            Assert.That(inputChanges, Is.Zero);
            input.text = "user";
            Assert.That(value.Value, Is.EqualTo("user"));
            Assert.That(inputChanges, Is.EqualTo(1));
            Assert.That(propertyChanges, Is.EqualTo(2));
        }

        [Test]
        public void ClosingBindingDisconnectsBothDirectionsButPreservesOtherListeners()
        {
            InputField input = Create<InputField>();
            var value = new BindableProperty<string>("initial");
            int externalChanges = 0;
            input.onValueChanged.AddListener(_ => externalChanges++);
            _bindings.Bind(input, value);
            _bindings.Dispose();
            value.Value = "model-after-close";
            Assert.That(input.text, Is.EqualTo("initial"));
            input.text = "user-after-close";
            Assert.That(value.Value, Is.EqualTo("model-after-close"));
            Assert.That(externalChanges, Is.EqualTo(1));
        }

        [Test]
        public void ToggleProgrammaticWriteDoesNotReenterUnityEvent()
        {
            Toggle toggle = Create<Toggle>();
            var value = new BindableProperty<bool>(false);
            int events = 0;
            toggle.onValueChanged.AddListener(_ => events++);
            _bindings.Bind(toggle, value);
            value.Value = true;
            Assert.That(toggle.isOn, Is.True);
            Assert.That(events, Is.Zero);
            toggle.isOn = false;
            Assert.That(value.Value, Is.False);
            Assert.That(events, Is.EqualTo(1));
            _bindings.Dispose();
            value.Value = true;
            Assert.That(toggle.isOn, Is.False);
        }

        [Test]
        public async Task ButtonDisablesDuringCommandAndIgnoresLateCompletionAfterUnbind()
        {
            Button button = Create<Button>();
            var release = new TaskCompletionSource<bool>();
            using (var command = new AsyncCommand(async _ => await release.Task))
            {
                _bindings.Bind(button, command);
                Task pending = command.ExecuteAsync().AsTask();
                Assert.That(button.interactable, Is.False);
                _bindings.Dispose();
                release.SetResult(true);
                await pending;
                Assert.That(command.CanExecute, Is.True);
                Assert.That(button.interactable, Is.False);
            }
        }

        [Test]
        public void DisposedBindingsRemoveOnlyTheirButtonClick()
        {
            Button button = Create<Button>();
            int externalClicks = 0;
            int executions = 0;
            button.onClick.AddListener(() => externalClicks++);
            using (
                var command = new AsyncCommand(_ =>
                {
                    executions++;
                    return default;
                })
            )
            {
                _bindings.Bind(button, command);
                button.onClick.Invoke();
                Assert.That(executions, Is.EqualTo(1));
                _bindings.Dispose();
                button.onClick.Invoke();
                Assert.That(executions, Is.EqualTo(1));
                Assert.That(externalClicks, Is.EqualTo(2));
            }
        }

        private T Create<T>()
            where T : Component
        {
            var instance = new GameObject(typeof(T).Name, typeof(RectTransform));
            _objects.Add(instance);
            return instance.AddComponent<T>();
        }
    }
}
