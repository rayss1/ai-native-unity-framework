using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
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
            FrameworkClientRoot.GetOrCreate(_owner);
            // UnityTest 在进入枚举器前也可能推进一帧；只由 UI 用例显式启动。
            _flow.enabled = false;
            _flow.Initialize(new GateConnectionOptions("127.0.0.1", 23001, false));
        }

        [UnityTearDown]
        public IEnumerator DestroyUiBeforeRestoringInputSystem()
        {
            if (_owner != null)
            {
                Task stopped = _flow.StopAsync().AsTask();
                yield return WaitForCompletion(stopped, "Flow cleanup");
                FrameworkClientRoot root = _owner.GetComponent<FrameworkClientRoot>();
                if (root != null)
                {
                    yield return WaitForCompletion(root.StopAsync().AsTask(), "Root cleanup");
                }
                Object.Destroy(_owner);
            }

            _owner = null;
            yield return null;
        }

        public override void TearDown()
        {
            if (_owner != null)
            {
                Object.DestroyImmediate(_owner);
            }

            base.TearDown();
        }

        [Test]
        public void WasdAndFireUseNewDevicesAndCancelOppositeKeys()
        {
            Press(_keyboard.wKey);
            Press(_keyboard.dKey);
            Press(_mouse.leftButton);
            Invoke(_flow, "SampleManualInput");
            Assert.That(Field(_flow, "_manualMove"), Is.EqualTo(new Vector2Int(1000, 1000)));
            Assert.That(Field(_flow, "_manualFire"), Is.True);
            Press(_keyboard.aKey);
            Press(_keyboard.sKey);
            Release(_mouse.leftButton);
            Invoke(_flow, "SampleManualInput");
            Assert.That(Field(_flow, "_manualMove"), Is.EqualTo(Vector2Int.zero));
            Assert.That(Field(_flow, "_manualFire"), Is.False);
        }

        [Test]
        public void MissingDevicesAndFocusLossClearCachedInput()
        {
            Press(_keyboard.wKey);
            Press(_mouse.leftButton);
            Invoke(_flow, "SampleManualInput");
            Invoke(_flow, "OnApplicationFocus", false);
            Assert.That(Field(_flow, "_manualMove"), Is.EqualTo(Vector2Int.zero));
            Assert.That(Field(_flow, "_manualFire"), Is.False);
            InputSystem.RemoveDevice(_keyboard);
            InputSystem.RemoveDevice(_mouse);
            Assert.DoesNotThrow(() => Invoke(_flow, "SampleManualInput"));
            Assert.That(Field(_flow, "_manualMove"), Is.EqualTo(Vector2Int.zero));
            Assert.That(Field(_flow, "_manualFire"), Is.False);
        }

        [UnityTest]
        public IEnumerator LoginUiHasNewModuleAndTabShiftTabEnterWork()
        {
            yield return WaitForView();
            var view = (TopologyClientView)Field(_flow, "_view");
            yield return null;
            var module = EventSystem.current.GetComponent<InputSystemUIInputModule>();
            Assert.That(module, Is.Not.Null);
            Assert.That(module.enabled, Is.True);
            Assert.That(module.actionsAsset, Is.Not.Null);
            Assert.That(EventSystem.current.GetComponent<StandaloneInputModule>(), Is.Null);
            Press(_keyboard.tabKey);
            yield return null;
            Assert.That(
                EventSystem.current.currentSelectedGameObject,
                Is.EqualTo(view.Username.gameObject)
            );
            Release(_keyboard.tabKey);
            yield return null;
            Press(_keyboard.leftShiftKey);
            Press(_keyboard.tabKey);
            yield return null;
            Assert.That(
                EventSystem.current.currentSelectedGameObject,
                Is.EqualTo(view.AdvancedButton.gameObject)
            );
            Release(_keyboard.tabKey);
            Release(_keyboard.leftShiftKey);
            yield return null;
            view.Username.text = "ab";
            view.Password.text = "short";
            view.Password.Select();
            view.Password.ActivateInputField();
            yield return null;
            Press(_keyboard.enterKey);
            yield return null;
            Assert.That(
                _flow.Error,
                Is.EqualTo("invalid_credentials"),
                "Enter must reach validation without sending a network request."
            );
            Release(_keyboard.enterKey);
            yield return null;
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator PointerClickUsesNewModuleAndDestroyedUiDoesNotLeakEventSystem()
        {
            yield return null;
            yield return WaitForView();
            var view = (TopologyClientView)Field(_flow, "_view");
            GameObject eventSystem = EventSystem.current.gameObject;
            Canvas.ForceUpdateCanvases();
            var rect = (RectTransform)view.AdvancedButton.transform;
            Vector2 position = RectTransformUtility.WorldToScreenPoint(
                null,
                rect.TransformPoint(rect.rect.center)
            );
            var hits = new List<RaycastResult>();
            EventSystem.current.RaycastAll(
                new PointerEventData(EventSystem.current) { position = position },
                hits
            );
            Assert.That(
                hits.Count,
                Is.GreaterThan(0),
                "Rendered button must receive a UI raycast."
            );
            Assert.That(
                hits[0].gameObject.GetComponentInParent<UnityEngine.UI.Button>(),
                Is.SameAs(view.AdvancedButton),
                "The visible click position must hit AdvancedButton."
            );
            Assert.That(view.AdvancedButton.IsInteractable(), Is.True);
            bool prior = view.Advanced.activeSelf;
            Set(_mouse.position, position);
            yield return null;
            Press(_mouse.leftButton);
            yield return null;
            Release(_mouse.leftButton);
            yield return null;
            Assert.That(
                view.Advanced.activeSelf,
                Is.Not.EqualTo(prior),
                "A real Input System pointer click must reach the UGUI button."
            );
            yield return WaitForCompletion(_flow.StopAsync().AsTask(), "Explicit UI cleanup");
            FrameworkClientRoot root = _owner.GetComponent<FrameworkClientRoot>();
            yield return WaitForCompletion(root.StopAsync().AsTask(), "Explicit root cleanup");
            Object.Destroy(_owner);
            _owner = null;
            yield return null;
            Assert.That(eventSystem == null, Is.True);
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator BorrowedLegacyEventSystemMigratesWithoutBeingDestroyed()
        {
            // 此用例显式创建视图，避免 Flow.Start 另行异步打开第二个 UI。
            _flow.enabled = false;
            EventSystem previous = EventSystem.current;
            if (previous != null)
            {
                previous.gameObject.SetActive(false);
            }

            var borrowed = new GameObject(
                "Borrowed EventSystem",
                typeof(EventSystem),
                typeof(StandaloneInputModule)
            );
            EventSystem.current = borrowed.GetComponent<EventSystem>();
            try
            {
                // 在 EventSystem 首次 Update 轮询已禁用的旧输入后端前完成绑定。
                var prefab = Resources.Load<GameObject>("TopologyClientUI");
                var ui = Object.Instantiate(prefab);
                var view = ui.GetComponent<TopologyClientView>();
                view.Bind(_flow);
                Assert.That(borrowed.GetComponent<StandaloneInputModule>().enabled, Is.False);
                Assert.That(borrowed.GetComponent<InputSystemUIInputModule>().enabled, Is.True);
                Object.Destroy(ui);
                yield return null;
                yield return null;
                Assert.That(borrowed != null, Is.True);
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                Object.DestroyImmediate(borrowed);
                if (previous != null)
                {
                    previous.gameObject.SetActive(true);
                }
            }
        }

        private IEnumerator WaitForView()
        {
            LogAssert.Expect(LogType.Log, "[Client] CLIENT_FRAMEWORK_STARTED: All modules ready");
            _flow.enabled = true;
            yield return FrameworkTestLifecycle.Start(_owner);
            float deadline = Time.realtimeSinceStartup + 10f;
            while (
                Field(_flow, "_view") == null
                && Time.realtimeSinceStartup < deadline
                && _flow.Error != "ui-load-failed"
            )
            {
                yield return null;
            }
            Assert.That(
                Field(_flow, "_view"),
                Is.Not.Null,
                "UI initialization must complete within 10 seconds; error=" + _flow.Error
            );
            // Bundle 回调可能在帧尾创建视图；等待其首轮 LateUpdate 和布局重建后采样控件坐标。
            yield return null;
            Canvas.ForceUpdateCanvases();
            yield return null;
        }

        private static IEnumerator WaitForCompletion(Task operation, string phase)
        {
            float deadline = Time.realtimeSinceStartup + 10f;
            while (!operation.IsCompleted && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }
            Assert.That(
                operation.IsCompleted,
                Is.True,
                phase + " must complete within 10 seconds."
            );
            operation.GetAwaiter().GetResult();
        }

        private static object Field(object target, string name) =>
            target
                .GetType()
                .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(target);

        private static void Invoke(object target, string name, params object[] args) =>
            target
                .GetType()
                .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(target, args);
    }
}
