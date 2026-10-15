using System.Collections;
using System.Reflection;
using AiNative.Client.Fantasy;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace AiNative.Client.Application.PlayModeTests
{
    public sealed class TopologyInputSystemTests : InputTestFixture
    {
        private GameObject _owner;
        private TopologyClientFlow _flow;
        private Keyboard _keyboard;
        private Mouse _mouse;

        public override void Setup()
        {
            base.Setup();
            _keyboard = InputSystem.AddDevice<Keyboard>();
            _mouse = InputSystem.AddDevice<Mouse>();
            _owner = new GameObject("TopologyInputSystemFixture");
            _flow = _owner.AddComponent<TopologyClientFlow>();
            _flow.Initialize(new GateConnectionOptions("127.0.0.1", 23001, false));
        }

        [UnityTearDown]
        public IEnumerator DestroyUiBeforeRestoringInputSystem()
        {
            if (_owner != null) Object.Destroy(_owner);
            _owner = null;
            // Flow -> view -> owned EventSystem destruction spans three frames.
            yield return null; yield return null; yield return null;
        }

        public override void TearDown()
        {
            if (_owner != null) Object.DestroyImmediate(_owner);
            base.TearDown();
        }

        [Test]
        public void WasdAndFireUseNewDevicesAndCancelOppositeKeys()
        {
            Press(_keyboard.wKey); Press(_keyboard.dKey); Press(_mouse.leftButton);
            Invoke(_flow, "SampleManualInput");
            Assert.That(Field(_flow, "_manualMove"), Is.EqualTo(new Vector2Int(1000, 1000)));
            Assert.That(Field(_flow, "_manualFire"), Is.True);
            Press(_keyboard.aKey); Press(_keyboard.sKey); Release(_mouse.leftButton);
            Invoke(_flow, "SampleManualInput");
            Assert.That(Field(_flow, "_manualMove"), Is.EqualTo(Vector2Int.zero));
            Assert.That(Field(_flow, "_manualFire"), Is.False);
        }

        [Test]
        public void MissingDevicesAndFocusLossClearCachedInput()
        {
            Press(_keyboard.wKey); Press(_mouse.leftButton);
            Invoke(_flow, "SampleManualInput");
            Invoke(_flow, "OnApplicationFocus", false);
            Assert.That(Field(_flow, "_manualMove"), Is.EqualTo(Vector2Int.zero));
            Assert.That(Field(_flow, "_manualFire"), Is.False);
            InputSystem.RemoveDevice(_keyboard); InputSystem.RemoveDevice(_mouse);
            Assert.DoesNotThrow(() => Invoke(_flow, "SampleManualInput"));
            Assert.That(Field(_flow, "_manualMove"), Is.EqualTo(Vector2Int.zero));
            Assert.That(Field(_flow, "_manualFire"), Is.False);
        }

        [UnityTest]
        public IEnumerator LoginUiHasNewModuleAndTabShiftTabEnterWork()
        {
            yield return null;
            var view = (TopologyClientView)Field(_flow, "_view");
            yield return null;
            var module = EventSystem.current.GetComponent<InputSystemUIInputModule>();
            Assert.That(module, Is.Not.Null);
            Assert.That(module.enabled, Is.True);
            Assert.That(module.actionsAsset, Is.Not.Null);
            Assert.That(EventSystem.current.GetComponent<StandaloneInputModule>(), Is.Null);

            Press(_keyboard.tabKey); yield return null;
            Assert.That(EventSystem.current.currentSelectedGameObject, Is.EqualTo(view.Username.gameObject));
            Release(_keyboard.tabKey); yield return null;
            Press(_keyboard.leftShiftKey); Press(_keyboard.tabKey); yield return null;
            Assert.That(EventSystem.current.currentSelectedGameObject, Is.EqualTo(view.AdvancedButton.gameObject));
            Release(_keyboard.tabKey); Release(_keyboard.leftShiftKey); yield return null;

            view.Username.text = "ab"; view.Password.text = "short";
            view.Password.Select(); view.Password.ActivateInputField();
            yield return null;
            Press(_keyboard.enterKey); yield return null;
            Assert.That(_flow.Error, Is.EqualTo("invalid_credentials"), "Enter must reach validation without sending a network request.");
            Release(_keyboard.enterKey); yield return null;
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator PointerClickUsesNewModuleAndDestroyedUiDoesNotLeakEventSystem()
        {
            yield return null; yield return null;
            var view = (TopologyClientView)Field(_flow, "_view");
            GameObject eventSystem = EventSystem.current.gameObject;
            Canvas.ForceUpdateCanvases();
            var rect = (RectTransform)view.AdvancedButton.transform;
            Vector2 position = RectTransformUtility.WorldToScreenPoint(null, rect.TransformPoint(rect.rect.center));
            bool prior = view.Advanced.activeSelf;
            Set(_mouse.position, position); yield return null;
            Press(_mouse.leftButton); yield return null;
            Release(_mouse.leftButton); yield return null;
            Assert.That(view.Advanced.activeSelf, Is.Not.EqualTo(prior), "A real Input System pointer click must reach the UGUI button.");
            Object.Destroy(_owner); _owner = null;
            yield return null; yield return null; yield return null;
            Assert.That(eventSystem == null, Is.True);
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator BorrowedLegacyEventSystemMigratesWithoutBeingDestroyed()
        {
            EventSystem previous = EventSystem.current;
            if (previous != null) previous.gameObject.SetActive(false);
            var borrowed = new GameObject("Borrowed EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            EventSystem.current = borrowed.GetComponent<EventSystem>();
            try
            {
                // Bind before the first EventSystem.Update can poll the disabled legacy backend.
                var prefab = Resources.Load<GameObject>("TopologyClientUI");
                var ui = Object.Instantiate(prefab);
                var view = ui.GetComponent<TopologyClientView>();
                view.Bind(_flow);
                Assert.That(borrowed.GetComponent<StandaloneInputModule>().enabled, Is.False);
                Assert.That(borrowed.GetComponent<InputSystemUIInputModule>().enabled, Is.True);
                Object.Destroy(ui);
                yield return null; yield return null;
                Assert.That(borrowed != null, Is.True);
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                Object.DestroyImmediate(borrowed);
                if (previous != null) previous.gameObject.SetActive(true);
            }
        }

        private static object Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
        private static void Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args);
    }
}
