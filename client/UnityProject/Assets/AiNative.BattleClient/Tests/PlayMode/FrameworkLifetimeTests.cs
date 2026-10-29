using System;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using AiNative.Client.Assets.AssetBundle;
using AiNative.Client.Fantasy;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem.UI;
using UnityEngine.TestTools;

namespace AiNative.Client.Application.PlayModeTests
{
#if UNITY_EDITOR
    [PrebuildSetup(typeof(AiNative.Client.Runtime.Tests.DomainReloadDisabledSetup))]
    [PostBuildCleanup(typeof(AiNative.Client.Runtime.Tests.DomainReloadDisabledSetup))]
#endif
    public sealed class FrameworkLifetimeTests
    {
        [UnityTest]
        public IEnumerator TwoApplicationsOwnIndependentEventSystemsAndClosingOnePreservesOtherInput()
        {
            var firstOwner = new GameObject("FirstIndependentUiApp");
            var secondOwner = new GameObject("SecondIndependentUiApp");
            FrameworkClientRoot firstRoot = null;
            FrameworkClientRoot secondRoot = null;
            try
            {
                var firstFlow = firstOwner.AddComponent<TopologyClientFlow>();
                var secondFlow = secondOwner.AddComponent<TopologyClientFlow>();
                firstFlow.enabled = false;
                secondFlow.enabled = false;
                var options = new GateConnectionOptions("127.0.0.1", 23001, false);
                firstFlow.Initialize(options);
                secondFlow.Initialize(options);
                firstRoot = FrameworkClientRoot.GetOrCreate(firstOwner);
                secondRoot = FrameworkClientRoot.GetOrCreate(secondOwner);
                Task<AiNative.Client.UI.UiService> firstStarting = firstRoot
                    .UiAsync(CancellationToken.None)
                    .AsTask();
                Task<AiNative.Client.UI.UiService> secondStarting = secondRoot
                    .UiAsync(CancellationToken.None)
                    .AsTask();
                yield return Await(Task.WhenAll(firstStarting, secondStarting));
                var firstUi = firstStarting.Result;
                var secondUi = secondStarting.Result;
                firstFlow.enabled = true;
                secondFlow.enabled = true;
                float deadline = Time.realtimeSinceStartup + 10f;
                while (
                    (firstUi.OpenCount == 0 || secondUi.OpenCount == 0)
                    && Time.realtimeSinceStartup < deadline
                )
                {
                    yield return null;
                }
                Assert.That(firstUi.OpenCount, Is.EqualTo(1));
                Assert.That(secondUi.OpenCount, Is.EqualTo(1));
                var firstEvents = (MultiplayerEventSystem)firstUi.EventSystem;
                var secondEvents = (MultiplayerEventSystem)secondUi.EventSystem;
                Assert.That(firstEvents, Is.Not.SameAs(secondEvents));
                Assert.That(
                    firstOwner.GetComponentsInChildren<MultiplayerEventSystem>(true).Length,
                    Is.EqualTo(1)
                );
                Assert.That(
                    secondOwner.GetComponentsInChildren<MultiplayerEventSystem>(true).Length,
                    Is.EqualTo(1)
                );
                var firstView = firstEvents.playerRoot.GetComponent<TopologyClientView>();
                var secondView = secondEvents.playerRoot.GetComponent<TopologyClientView>();
                Assert.That(firstView, Is.Not.SameAs(secondView));
                firstEvents.SetSelectedGameObject(firstView.Username.gameObject);
                secondEvents.SetSelectedGameObject(secondView.Password.gameObject);
                yield return null;
                Assert.That(
                    firstEvents.currentSelectedGameObject,
                    Is.SameAs(firstView.Username.gameObject)
                );
                Assert.That(
                    secondEvents.currentSelectedGameObject,
                    Is.SameAs(secondView.Password.gameObject)
                );
                yield return Await(firstRoot.StopAsync().AsTask());
                yield return null;
                Assert.That(firstEvents == null, Is.True);
                Assert.That(secondEvents != null, Is.True);
                Assert.That(
                    secondEvents.currentSelectedGameObject,
                    Is.SameAs(secondView.Password.gameObject)
                );
                Assert.That(
                    secondEvents.GetComponent<InputSystemUIInputModule>().isActiveAndEnabled,
                    Is.True
                );
                Assert.That(secondUi.OpenCount, Is.EqualTo(1));
                yield return Await(secondRoot.StopAsync().AsTask());
            }
            finally
            {
                UnityEngine.Object.Destroy(firstOwner);
                UnityEngine.Object.Destroy(secondOwner);
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator TwentyUiApplicationCyclesReleaseOwnedBundlesAndInstances()
        {
            // 第一轮预热；随后记录二十轮。此用例不冒充完整联机对局稳定性测试。
            for (int cycle = 0; cycle <= 20; cycle++)
            {
                var owner = new GameObject("FrameworkLifetimeFixture");
                var flow = owner.AddComponent<TopologyClientFlow>();
                flow.enabled = false;
                flow.Initialize(new GateConnectionOptions("127.0.0.1", 23001, false));
                var root = FrameworkClientRoot.GetOrCreate(owner);
                LocalAssetBundleService assets = null;
                try
                {
                    Task<AiNative.Client.Assets.IAssetService> starting = root.AssetsAsync(
                            CancellationToken.None
                        )
                        .AsTask();
                    yield return Await(starting);
                    assets = (LocalAssetBundleService)starting.GetAwaiter().GetResult();
                    var uiTask = root.UiAsync(CancellationToken.None).AsTask();
                    yield return Await(uiTask);
                    var ui = uiTask.GetAwaiter().GetResult();
                    flow.enabled = true;
                    float deadline = Time.realtimeSinceStartup + 10f;
                    while (ui.OpenCount == 0 && Time.realtimeSinceStartup < deadline)
                    {
                        yield return null;
                    }
                    Assert.That(ui.OpenCount, Is.EqualTo(1), "真实 Bundle UI 必须创建。");
                    Assert.That(assets.ActiveHandleCount, Is.GreaterThan(0));
                    Task stopping = root.StopAsync().AsTask();
                    yield return Await(stopping);
                    stopping.GetAwaiter().GetResult();
                    Assert.That(root.StopAsync().IsCompleted, Is.True);
                    Assert.That(ui.OpenCount, Is.Zero);
                    Assert.That(assets.ActiveHandleCount, Is.Zero);
                    Assert.That(assets.LoadedBundleCount, Is.Zero);
                }
                finally
                {
                    UnityEngine.Object.Destroy(owner);
                }
                yield return null;
                yield return null;
                Assert.That(
                    UnityEngine.Object.FindObjectsByType<TopologyClientView>(
                        FindObjectsSortMode.None
                    ),
                    Is.Empty
                );
                if (cycle > 0)
                {
                    TestContext.WriteLine(
                        "cycle="
                            + cycle
                            + ";managedBytes="
                            + GC.GetTotalMemory(false)
                            + ";unityAllocatedBytes="
                            + UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong()
                    );
                }
            }
        }

        private static IEnumerator Await(Task operation)
        {
            float deadline = Time.realtimeSinceStartup + 15f;
            while (!operation.IsCompleted && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }
            Assert.That(operation.IsCompleted, Is.True, "生命周期操作必须有界完成。");
            operation.GetAwaiter().GetResult();
        }
    }
}
