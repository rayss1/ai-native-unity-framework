using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AiNative.Client.Assets;
using AiNative.Client.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem.UI;
using UnityEngine.TestTools;

namespace AiNative.Client.Application.PlayModeTests
{
    public sealed class FrameworkExplicitStartupTests
    {
        private readonly List<FrameworkClientRoot> _roots = new List<FrameworkClientRoot>();

        [UnityTearDown]
        public IEnumerator ReleaseApplications()
        {
            foreach (FrameworkClientRoot root in _roots)
            {
                if (root == null)
                {
                    continue;
                }
                yield return Complete(root.StopAsync().AsTask());
                UnityEngine.Object.Destroy(root.gameObject);
            }
            _roots.Clear();
            yield return null;
        }

        [UnityTest]
        public IEnumerator AssemblyAndEnabledFramesDoNotImplicitlyStartServices()
        {
            FrameworkClientRoot root = Create("AssembledOnly", withFlow: true);
            yield return null;
            yield return null;
            Assert.That(root.IsRunning, Is.False);
            Assert.That(root.GetComponentsInChildren<MultiplayerEventSystem>(true), Is.Empty);
            Task<IAssetService> assets = root.AssetsAsync(CancellationToken.None).AsTask();
            Task<UiService> ui = root.UiAsync(CancellationToken.None).AsTask();
            yield return Observe(assets);
            yield return Observe(ui);
            Assert.That(assets.IsFaulted, Is.True);
            Assert.That(ui.IsFaulted, Is.True);
            Assert.That(
                assets.Exception.GetBaseException().Message,
                Is.EqualTo("client-not-started")
            );
            Assert.That(ui.Exception.GetBaseException().Message, Is.EqualTo("client-not-started"));
        }

        [UnityTest]
        public IEnumerator RepeatedStartupSharesResourcesAndConsumerCancellationIsIsolated()
        {
            FrameworkClientRoot root = Create("RepeatedExplicitStart");
            Task first = root.StartAsync().AsTask();
            Task second = root.StartAsync().AsTask();
            using var consumer = new CancellationTokenSource();
            consumer.Cancel();
            Task<IAssetService> cancelled = root.AssetsAsync(consumer.Token).AsTask();
            Task<IAssetService> retained = root.AssetsAsync(CancellationToken.None).AsTask();
            yield return Complete(Task.WhenAll(first, second, retained));
            yield return Observe(cancelled);
            Assert.That(cancelled.IsCanceled, Is.True);
            Assert.That(root.IsRunning, Is.True);
            Assert.That(
                root.GetComponentsInChildren<MultiplayerEventSystem>(true).Length,
                Is.EqualTo(1)
            );
            Task<IAssetService> repeated = root.AssetsAsync(CancellationToken.None).AsTask();
            yield return Complete(repeated);
            Assert.That(repeated.Result, Is.SameAs(retained.Result));
        }

        [UnityTest]
        public IEnumerator StopAndRestartCreateNewResourcesAndOnlyOneOwnedInputSystem()
        {
            FrameworkClientRoot root = Create("RestartExplicitApp");
            yield return Complete(root.StartAsync().AsTask());
            Task<IAssetService> firstAssets = root.AssetsAsync(CancellationToken.None).AsTask();
            Task<UiService> firstUi = root.UiAsync(CancellationToken.None).AsTask();
            yield return Complete(Task.WhenAll(firstAssets, firstUi));
            var oldEvents = firstUi.Result.EventSystem;
            Task firstStop = root.StopAsync().AsTask();
            Task repeatedStop = root.StopAsync().AsTask();
            Assert.That(root.IsRunning, Is.False);
            yield return Complete(Task.WhenAll(firstStop, repeatedStop));
            Assert.That(oldEvents == null, Is.True);
            yield return Complete(root.StartAsync().AsTask());
            Task<IAssetService> nextAssets = root.AssetsAsync(CancellationToken.None).AsTask();
            yield return Complete(nextAssets);
            Assert.That(nextAssets.Result, Is.Not.SameAs(firstAssets.Result));
            Assert.That(root.IsRunning, Is.True);
            Assert.That(
                root.GetComponentsInChildren<MultiplayerEventSystem>(true).Length,
                Is.EqualTo(1)
            );
        }

        [UnityTest]
        public IEnumerator CancelledStartupCanRetryWithoutAffectingAnotherApplication()
        {
            FrameworkClientRoot failed = Create("FailedExplicitApp");
            FrameworkClientRoot healthy = Create("HealthyExplicitApp");
            using var startupCancellation = new CancellationTokenSource();
            Task failure = failed.StartAsync(startupCancellation.Token).AsTask();
            Assert.That(failure.IsCompleted, Is.False, "真实内容准备应仍在异步启动中。");
            startupCancellation.Cancel();
            yield return Observe(failure);
            Assert.That(failure.IsCanceled, Is.True);
            Assert.That(failed.IsRunning, Is.False);
            Assert.That(failed.GetComponentsInChildren<MultiplayerEventSystem>(true), Is.Empty);
            yield return Complete(healthy.StartAsync().AsTask());
            Task<UiService> healthyUi = healthy.UiAsync(CancellationToken.None).AsTask();
            yield return Complete(healthyUi);
            var healthyEvents = healthyUi.Result.EventSystem;
            yield return Complete(failed.RetryStartupAsync().AsTask());
            Assert.That(failed.IsRunning, Is.True);
            Assert.That(healthy.IsRunning, Is.True);
            Assert.That(healthyUi.Result.EventSystem, Is.SameAs(healthyEvents));
            yield return Complete(failed.StopAsync().AsTask());
            Assert.That(healthyEvents != null && healthyEvents.isActiveAndEnabled, Is.True);
        }

        private FrameworkClientRoot Create(string name, bool withFlow = false)
        {
            var owner = new GameObject(name);
            if (withFlow)
            {
                owner.AddComponent<TopologyClientFlow>();
            }
            FrameworkClientRoot root = FrameworkClientRoot.GetOrCreate(owner);
            _roots.Add(root);
            return root;
        }

        private static IEnumerator Complete(Task operation)
        {
            yield return Observe(operation);
            operation.GetAwaiter().GetResult();
        }

        private static IEnumerator Observe(Task operation)
        {
            float deadline = Time.realtimeSinceStartup + 30f;
            while (!operation.IsCompleted && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }
            Assert.That(operation.IsCompleted, Is.True, "生命周期结果必须有界可观察。");
        }
    }
}
