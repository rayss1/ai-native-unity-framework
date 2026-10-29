using System;
using System.Collections;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AiNative.Client.Assets;
using AiNative.Client.Assets.AssetBundle;
using AiNative.Client.Fantasy;
using AiNative.Client.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem.UI;
using UnityEngine.TestTools;

namespace AiNative.Client.Application.PlayModeTests
{
    public sealed class ClientSceneEntryQuitTests
    {
        private GameObject _owner;
        private FrameworkClientRoot _root;

        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            if (_root != null)
            {
                yield return Await(_root.StopAsync().AsTask());
            }
            if (_owner != null)
            {
                UnityEngine.Object.Destroy(_owner);
            }
            yield return null;
        }

        [UnityTest]
        [Timeout(60000)]
        public IEnumerator DirectEntryStartsOnceAndDefersRepeatedOsQuitUntilRealResourcesAreReleased()
        {
            ClientSceneEntry entry = Create(ClientSceneEntryMode.Direct);
            yield return Until(() => _root.IsRunning, "直接入口必须显式启动根。");
            Task<IAssetService> assetTask = _root.AssetsAsync(CancellationToken.None).AsTask();
            Task<UiService> uiTask = _root.UiAsync(CancellationToken.None).AsTask();
            yield return Await(Task.WhenAll(assetTask, uiTask));
            var assets = (LocalAssetBundleService)assetTask.Result;
            UiService ui = uiTask.Result;
            var eventSystem = ui.EventSystem;
            object startup = Field(_root, "_start");
            Assert.That(Field(entry, "_ownsDirectStartup"), Is.True);
            Assert.That(ui.OpenCount, Is.EqualTo(1));
            Assert.That(assets.ActiveHandleCount, Is.GreaterThan(0));
            Assert.That(assets.LoadedBundleCount, Is.GreaterThan(0));
            Invoke(entry, "Start");
            Invoke(entry, "Start");
            yield return null;
            Assert.That(Field(_root, "_start"), Is.SameAs(startup));
            Assert.That(
                _owner.GetComponentsInChildren<MultiplayerEventSystem>(true).Length,
                Is.EqualTo(1)
            );
            Assert.That(ui.OpenCount, Is.EqualTo(1));

            Assert.That((bool)Invoke(entry, "HandleWantsToQuit"), Is.False);
            object quitCompletion = Field(entry, "_quitCompletion");
            Assert.That(quitCompletion, Is.Not.Null);
            Assert.That(Field(entry, "_quitAllowed"), Is.False);
            Assert.That((bool)Invoke(entry, "HandleWantsToQuit"), Is.False);
            Task firstQuit = ((ValueTask)Invoke(entry, "RequestQuitAsync", 0)).AsTask();
            Task repeatedQuit = ((ValueTask)Invoke(entry, "RequestQuitAsync", 0)).AsTask();
            Assert.That(Field(entry, "_quitCompletion"), Is.SameAs(quitCompletion));
            Assert.That(
                firstQuit.IsCompleted,
                Is.False,
                "真实 UI/EventSystem 释放需要等待销毁帧。"
            );
            yield return Await(Task.WhenAll(firstQuit, repeatedQuit));
            Assert.That(_root.IsRunning, Is.False);
            Assert.That(ui.OpenCount, Is.Zero);
            Assert.That(assets.ActiveHandleCount, Is.Zero);
            Assert.That(assets.LoadedBundleCount, Is.Zero);
            Assert.That(eventSystem == null, Is.True);
            Assert.That(Field(entry, "_quitAllowed"), Is.True);
            Assert.That((bool)Invoke(entry, "HandleWantsToQuit"), Is.True);
            yield return Await(((ValueTask)Invoke(entry, "RequestQuitAsync", 0)).AsTask());
            Assert.That(Field(entry, "_quitCompletion"), Is.SameAs(quitCompletion));
            UnityEngine.Object.Destroy(entry);
            yield return null;
            Assert.That(Field(entry, "_destroyed"), Is.True);
        }

        [UnityTest]
        [Timeout(60000)]
        public IEnumerator ExternalEntryDoesNotStartOrTakeOwnershipOfQuit()
        {
            ClientSceneEntry entry = Create(ClientSceneEntryMode.External);
            yield return null;
            yield return null;
            Assert.That(_root.IsRunning, Is.False);
            Assert.That(Field(_root, "_start"), Is.Null);
            Assert.That(Field(entry, "_ownsDirectStartup"), Is.False);
            Assert.That(Field(entry, "_quitCompletion"), Is.Null);
            Assert.That(_owner.GetComponentsInChildren<MultiplayerEventSystem>(true), Is.Empty);
            Assert.That((bool)Invoke(entry, "HandleWantsToQuit"), Is.True);
            Assert.That(Field(entry, "_quitCompletion"), Is.Null);
            Assert.That(Field(_root, "_start"), Is.Null);
            UnityEngine.Object.Destroy(entry);
            yield return null;
            Assert.That(Field(entry, "_destroyed"), Is.True);
            Assert.That(Field(entry, "_ownsDirectStartup"), Is.False);
        }

        private ClientSceneEntry Create(ClientSceneEntryMode mode)
        {
            _owner = new GameObject("SceneEntryQuitFixture");
            _owner.SetActive(false);
            var flow = _owner.AddComponent<TopologyClientFlow>();
            flow.Initialize(new GateConnectionOptions("127.0.0.1", 23001, false));
            _root = FrameworkClientRoot.GetOrCreate(_owner);
            var entry = _owner.AddComponent<ClientSceneEntry>();
            typeof(ClientSceneEntry)
                .GetField("_mode", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(entry, mode);
            _owner.SetActive(true);
            return entry;
        }

        private static IEnumerator Await(Task operation)
        {
            yield return Until(() => operation.IsCompleted, "退出或清理操作必须在 30 秒内完成。");
            operation.GetAwaiter().GetResult();
        }

        private static IEnumerator Until(Func<bool> condition, string message)
        {
            float deadline = Time.realtimeSinceStartup + 30f;
            while (!condition() && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }
            Assert.That(condition(), Is.True, message);
        }

        private static object Field(object instance, string name) =>
            instance
                .GetType()
                .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(instance);

        private static object Invoke(object instance, string name, params object[] arguments) =>
            instance
                .GetType()
                .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(instance, arguments);
    }
}
