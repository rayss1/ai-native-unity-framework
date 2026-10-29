using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace AiNative.Client.UI.Tests
{
    public sealed class UiFocusPlayModeTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            foreach (GameObject instance in _objects)
            {
                if (instance != null)
                {
                    UnityEngine.Object.Destroy(instance);
                }
            }
            _objects.Clear();
            yield return null;
        }

        [UnityTest]
        public IEnumerator ScopedInputFieldsKeepDelayedActivationInsideTheirOwnedEventSystems()
        {
            EventSystem firstEvents = Create("FirstInputEvents").AddComponent<EventSystem>();
            EventSystem secondEvents = Create("SecondInputEvents").AddComponent<EventSystem>();
            ScopedInputField first = Input("FirstInput", firstEvents);
            ScopedInputField second = Input("SecondInput", secondEvents);
            EventSystem.current = secondEvents;
            firstEvents.SetSelectedGameObject(first.gameObject);
            secondEvents.SetSelectedGameObject(second.gameObject);
            yield return null;
            yield return null;
            Assert.That(first.textComponent.canvas, Is.Not.Null);
            Assert.That(second.textComponent.canvas, Is.Not.Null);
            Assert.That(first.isFocused, Is.True);
            Assert.That(second.isFocused, Is.True);
            Assert.That(firstEvents.currentSelectedGameObject, Is.SameAs(first.gameObject));
            Assert.That(secondEvents.currentSelectedGameObject, Is.SameAs(second.gameObject));
            Assert.That(EventSystem.current, Is.SameAs(secondEvents));
            firstEvents.SetSelectedGameObject(null);
            Assert.That(secondEvents.currentSelectedGameObject, Is.SameAs(second.gameObject));
            Assert.That(EventSystem.current, Is.SameAs(secondEvents));
            firstEvents.gameObject.SetActive(false);
            UnityEngine.Object.Destroy(firstEvents.gameObject);
            first.ActivateInputField();
            yield return null;
            yield return null;
            Assert.That(secondEvents.currentSelectedGameObject, Is.SameAs(second.gameObject));
            Assert.That(EventSystem.current, Is.SameAs(secondEvents));
        }

        private ScopedInputField Input(string name, EventSystem owner)
        {
            GameObject canvasObject = Create(name + "Canvas");
            Canvas canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            GameObject instance = Create(name);
            RectTransform inputRect = instance.AddComponent<RectTransform>();
            inputRect.SetParent(canvasObject.transform, false);
            inputRect.sizeDelta = new Vector2(240, 40);
            GameObject textObject = Create(name + "Text");
            RectTransform textRect = textObject.AddComponent<RectTransform>();
            textRect.SetParent(inputRect, false);
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.sizeDelta = Vector2.zero;
            Text label = textObject.AddComponent<Text>();
            label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            ScopedInputField input = instance.AddComponent<ScopedInputField>();
            input.textComponent = label;
            input.ConfigureEventSystem(owner);
            return input;
        }

        [Test]
        public async Task ExplicitEventSystemsRestoreOnlyTheirOwnFocus()
        {
            EventSystem firstEvents = Create("FirstEvents").AddComponent<EventSystem>();
            EventSystem secondEvents = Create("SecondEvents").AddComponent<EventSystem>();
            var first = new UiService(firstEvents);
            var second = new UiService(secondEvents);
            GameObject firstFocus = Create("FirstFocus");
            GameObject secondFocus = Create("SecondFocus");
            firstEvents.SetSelectedGameObject(firstFocus);
            secondEvents.SetSelectedGameObject(secondFocus);
            first.Register(
                "modal",
                UiLayer.Modal,
                _ =>
                {
                    UiView view = View("FirstModal");
                    firstEvents.SetSelectedGameObject(view.Root);
                    return new ValueTask<UiView>(view);
                }
            );
            try
            {
                EventSystem.current = secondEvents;
                await first.OpenAsync("modal");
                Assert.That(secondEvents.currentSelectedGameObject, Is.SameAs(secondFocus));
                await first.CloseAsync("modal");
                Assert.That(firstEvents.currentSelectedGameObject, Is.SameAs(firstFocus));
                Assert.That(secondEvents.currentSelectedGameObject, Is.SameAs(secondFocus));
                await first.DisposeAsync();
                Assert.That(second.EventSystem, Is.SameAs(secondEvents));
                Assert.That(secondEvents.currentSelectedGameObject, Is.SameAs(secondFocus));
            }
            finally
            {
                await first.DisposeAsync();
                await second.DisposeAsync();
            }
        }

        [Test]
        public async Task ClosingViewRestoresFocusCapturedBeforeAsyncFactorySelectsNewControl()
        {
            EventSystem originalEventSystem = EventSystem.current;
            EventSystem eventSystem = Create("FocusTestEventSystem").AddComponent<EventSystem>();
            EventSystem.current = eventSystem;
            var service = new UiService();
            GameObject previous = Create("PreviousButton");
            previous.AddComponent<Button>();
            UiView modal = View("ModalButton");
            modal.Root.AddComponent<Button>();
            var completion = new TaskCompletionSource<UiView>();
            service.Register(
                "modal",
                UiLayer.Modal,
                _ =>
                {
                    eventSystem.SetSelectedGameObject(modal.Root);
                    return new ValueTask<UiView>(completion.Task);
                }
            );
            try
            {
                eventSystem.SetSelectedGameObject(previous);
                Task opening = service.OpenAsync("modal").AsTask();
                Assert.That(eventSystem.currentSelectedGameObject, Is.SameAs(modal.Root));
                Assert.That(opening.IsCompleted, Is.False);
                completion.SetResult(modal);
                await opening;
                await service.CloseAsync("modal");
                Assert.That(eventSystem.currentSelectedGameObject, Is.SameAs(previous));
                Assert.That(service.OpenCount, Is.Zero);
            }
            finally
            {
                completion.TrySetResult(modal);
                await service.DisposeAsync();
                if (originalEventSystem != null && originalEventSystem.isActiveAndEnabled)
                {
                    EventSystem.current = originalEventSystem;
                }
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task FailedOrCancelledFactoryRestoresPreviousFocus(bool cancel)
        {
            EventSystem originalEventSystem = EventSystem.current;
            EventSystem eventSystem = Create("FailedFocusEventSystem").AddComponent<EventSystem>();
            EventSystem.current = eventSystem;
            var service = new UiService();
            GameObject previous = Create("PreviousButton");
            previous.AddComponent<Button>();
            int released = 0;
            UiView modal = View("FailedModalButton", () => released++);
            modal.Root.AddComponent<Button>();
            var completion = new TaskCompletionSource<UiView>();
            using var cancellation = new CancellationTokenSource();
            service.Register(
                "modal",
                UiLayer.Modal,
                _ =>
                {
                    eventSystem.SetSelectedGameObject(modal.Root);
                    return new ValueTask<UiView>(completion.Task);
                }
            );
            try
            {
                eventSystem.SetSelectedGameObject(previous);
                Task opening = service.OpenAsync("modal", cancellation.Token).AsTask();
                Assert.That(eventSystem.currentSelectedGameObject, Is.SameAs(modal.Root));
                if (cancel)
                {
                    cancellation.Cancel();
                    completion.SetResult(modal);
                }
                else
                {
                    // 工厂未交付所有权时，仍由工厂负责清理自身的部分结果。
                    await modal.DisposeAsync();
                    completion.SetException(new InvalidOperationException("factory-failed"));
                }
                Exception failure = await FailureOf(opening);
                if (cancel)
                {
                    Assert.That(failure, Is.InstanceOf<OperationCanceledException>());
                }
                else
                {
                    Assert.That(failure, Is.InstanceOf<InvalidOperationException>());
                    Assert.That(failure.Message, Is.EqualTo("factory-failed"));
                }
                Assert.That(eventSystem.currentSelectedGameObject, Is.SameAs(previous));
                Assert.That(service.OpenCount, Is.Zero);
                Assert.That(released, Is.EqualTo(1));
            }
            finally
            {
                completion.TrySetResult(modal);
                await service.DisposeAsync();
                await modal.DisposeAsync();
                if (originalEventSystem != null && originalEventSystem.isActiveAndEnabled)
                {
                    EventSystem.current = originalEventSystem;
                }
            }
        }

        private GameObject Create(string name)
        {
            var instance = new GameObject(name);
            _objects.Add(instance);
            return instance;
        }

        private UiView View(string name, Action onRelease = null)
        {
            GameObject instance = Create(name);
            return new UiView(
                instance,
                () =>
                {
                    onRelease?.Invoke();
                    instance.SetActive(false);
                    UnityEngine.Object.Destroy(instance);
                    return default;
                }
            );
        }

        private static async Task<Exception> FailureOf(Task task)
        {
            try
            {
                await task;
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        }
    }
}
