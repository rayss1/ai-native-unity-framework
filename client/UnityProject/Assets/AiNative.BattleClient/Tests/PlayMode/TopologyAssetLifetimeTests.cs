using System;
using System.Collections;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AiNative.Client.Assets;
using AiNative.Client.Fantasy;
using AiNative.Client.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace AiNative.Client.Application.PlayModeTests
{
    public sealed class TopologyAssetLifetimeTests
    {
        [UnityTest]
        public IEnumerator ClosingDuringNativeLoadReleasesLateHandleWithoutCreatingView()
        {
            var owner = new GameObject("TopologyLateAssetFixture");
            var prefab = new GameObject("UnusedLateUiPrefab");
            prefab.SetActive(false);
            var assets = new DelayedAssets();
            var ui = new UiService();
            try
            {
                var flow = owner.AddComponent<TopologyClientFlow>();
                flow.Initialize(new GateConnectionOptions("127.0.0.1", 23001, false));
                flow.ConfigureUi(assets, ui);
                yield return null;
                Assert.That(assets.Loads, Is.EqualTo(1));
                Task stopped = flow.StopAsync().AsTask();
                Assert.That(
                    stopped.IsCompleted,
                    Is.False,
                    "停止必须等待不可取消的原生加载完成并释放迟到结果。"
                );
                assets.Complete(prefab);
                float deadline = Time.realtimeSinceStartup + 5f;
                while (!stopped.IsCompleted && Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                }
                Assert.That(stopped.IsCompleted, Is.True, "停止等待不得无限挂起。");
                stopped.GetAwaiter().GetResult();
                Assert.That(assets.Releases, Is.EqualTo(1));
                Assert.That(ui.OpenCount, Is.Zero);
                Assert.That(
                    typeof(TopologyClientFlow)
                        .GetField("_view", BindingFlags.Instance | BindingFlags.NonPublic)
                        .GetValue(flow),
                    Is.Null
                );
                Assert.That(flow.StopAsync().IsCompleted, Is.True);
            }
            finally
            {
                assets.Complete(prefab);
                UnityEngine.Object.Destroy(owner);
                UnityEngine.Object.Destroy(prefab);
            }
            Task disposed = ui.DisposeAsync().AsTask();
            while (!disposed.IsCompleted)
            {
                yield return null;
            }
            disposed.GetAwaiter().GetResult();
        }

        private sealed class DelayedAssets : IAssetService
        {
            private readonly TaskCompletionSource<GameObject> _loaded =
                new TaskCompletionSource<GameObject>();
            public int Loads { get; private set; }
            public int Releases { get; private set; }

            public void Complete(GameObject prefab)
            {
                _loaded.TrySetResult(prefab);
            }

            public async ValueTask<AssetHandle<T>> LoadAsync<T>(
                AssetId id,
                CancellationToken cancellationToken = default
            )
            {
                Assert.That(id.Value, Is.EqualTo("ui.topology"));
                Loads++;
                // 模拟无法中断的原生加载；调用方必须处理取消后才完成的结果。
                GameObject prefab = await _loaded.Task;
                return new AssetHandle<T>(id, (T)(object)prefab, this, () => Releases++);
            }

            public bool TryGetLoaded<T>(AssetId id, out AssetHandle<T> handle)
            {
                handle = null;
                return false;
            }

            public ValueTask PreloadAsync(
                AssetGroupId group,
                CancellationToken cancellationToken = default
            )
            {
                throw new NotSupportedException();
            }

            public ValueTask ReleaseAsync(AssetHandle handle)
            {
                handle.Release(this);
                return default;
            }
        }
    }
}
