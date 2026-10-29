using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AiNative.Client.Assets;
using AiNative.Client.Fantasy;
using AiNative.Client.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace AiNative.Client.Application.Tests
{
    public sealed class TopologyViewModelTests
    {
        private GameObject _owner;
        private TopologyClientFlow _flow;
        private TopologyViewModel _viewModel;

        [SetUp]
        public void SetUp()
        {
            _owner = new GameObject("TopologyViewModelFixture");
            _flow = _owner.AddComponent<TopologyClientFlow>();
            _flow.Initialize(new GateConnectionOptions("127.0.0.1", 23001, false));
            _viewModel = new TopologyViewModel(_flow, _ => { });
        }

        [TearDown]
        public async Task TearDown()
        {
            _viewModel.Dispose();
            await _flow.StopAsync();
            UnityEngine.Object.DestroyImmediate(_owner);
        }

        [Test]
        public async Task LoginCommandUsesBoundInputAndExposesValidationError()
        {
            _viewModel.Username.Value = "ab";
            _viewModel.Password.Value = "short";
            await _viewModel.Login.ExecuteAsync();
            Assert.That(_flow.Error, Is.EqualTo("invalid_credentials"));
            Assert.That(_viewModel.Error.Value, Is.EqualTo(_flow.DisplayError));
            Assert.That(_viewModel.Page.Value, Is.Zero);
            Assert.That(_viewModel.Login.IsExecuting, Is.False);
            Assert.That(_viewModel.Login.CanExecute, Is.True);
            Assert.That(Field("Backend"), Is.Null);
        }

        [Test]
        public async Task CommandRemainsExecutingWhileItsRequestIsQueuedBehindRefresh()
        {
            var background = new TaskCompletionSource<bool>();
            RunBackground(background.Task);
            _viewModel.Username.Value = "ab";
            _viewModel.Password.Value = "short";
            Task login = _viewModel.Login.ExecuteAsync().AsTask();
            Assert.That(_viewModel.Login.IsExecuting, Is.True);
            Assert.That(_viewModel.Login.CanExecute, Is.False);
            Assert.That(login.IsCompleted, Is.False);
            await _viewModel.Login.ExecuteAsync();
            background.SetResult(true);
            await login;
            Assert.That(_flow.Error, Is.EqualTo("invalid_credentials"));
            Assert.That(_viewModel.Login.IsExecuting, Is.False);
            Assert.That(_flow.UiBusy, Is.False);
        }

        [Test]
        public async Task ClosedModelDoesNotPublishLateOperationResults()
        {
            var background = new TaskCompletionSource<bool>();
            RunBackground(background.Task);
            Task login = _viewModel.Login.ExecuteAsync().AsTask();
            int notifications = 0;
            _viewModel.Error.PropertyChanged += (_, __) => notifications++;
            _viewModel.Dispose();
            await _flow.StopAsync();
            background.SetResult(true);
            await login;
            _viewModel.Refresh();
            Assert.That(notifications, Is.Zero);
            Assert.That(_viewModel.Password.Value, Is.Empty);
            Assert.That(_viewModel.Login.CanExecute, Is.False);
            Assert.That(Field("_pendingAction"), Is.Null);
        }

        [Test]
        public async Task LocalCommandsChangePresentationWithoutBackendRequests()
        {
            bool advanced = _viewModel.AdvancedVisible.Value;
            await _viewModel.ToggleAdvanced.ExecuteAsync();
            Assert.That(_viewModel.AdvancedVisible.Value, Is.Not.EqualTo(advanced));
            await _viewModel.ToggleDiagnostics.ExecuteAsync();
            Assert.That(_viewModel.DiagnosticsVisible.Value, Is.True);
            Assert.That(Field("Backend"), Is.Null);
        }

        [Test]
        public async Task ThrowingCancellationStillClosesUiAndSharesStopTask()
        {
            var owner = new GameObject("ThrowingCancellationFixture");
            var flow = owner.AddComponent<TopologyClientFlow>();
            var ui = new UiService();
            bool released = false;
            var page = new GameObject("OwnedUi");
            ui.Register(
                "topology",
                UiLayer.Page,
                _ => new ValueTask<UiView>(
                    new UiView(
                        page,
                        () =>
                        {
                            released = true;
                            UnityEngine.Object.DestroyImmediate(page);
                            return default;
                        }
                    )
                )
            );
            await ui.OpenAsync("topology");
            typeof(TopologyClientFlow)
                .GetField("_uiService", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(flow, ui);
            var lifetime = (CancellationTokenSource)
                typeof(TopologyClientFlow)
                    .GetField("_lifetime", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(flow);
            Task reentered = null;
            lifetime.Token.Register(() =>
            {
                reentered = flow.StopAsync().AsTask();
                throw new InvalidOperationException("cancellation-fixture");
            });
            Task stopped = flow.StopAsync().AsTask();
            try
            {
                AggregateException failure = Assert.ThrowsAsync<AggregateException>(async () =>
                    await stopped
                );
                Assert.That(failure.Flatten().InnerExceptions.Count, Is.EqualTo(1));
                Assert.That(
                    failure.Flatten().InnerExceptions[0].Message,
                    Is.EqualTo("cancellation-fixture")
                );
                Assert.That(reentered, Is.SameAs(stopped));
                Assert.That(flow.StopAsync().AsTask(), Is.SameAs(stopped));
                Assert.That(released, Is.True);
                Assert.That(ui.OpenCount, Is.Zero);
            }
            finally
            {
                await ui.DisposeAsync();
                // EditMode 中未运行的 MonoBehaviour 不保证触发 OnDestroy；异常已由显式停止调用观察。
                UnityEngine.Object.DestroyImmediate(owner);
                if (page != null)
                {
                    UnityEngine.Object.DestroyImmediate(page);
                }
            }
        }

        [Test]
        public async Task FailedUiFactoryCanRetrySameServiceAndRegisterReplacementService()
        {
            var first = new UiService();
            var replacement = new UiService();
            var assets = new FailingUiAssets();
            try
            {
                _flow.ConfigureUi(assets, first);
                await FailUiLoad();
                Assert.That(assets.Loads, Is.EqualTo(1));
                await FailUiLoad();
                Assert.That(
                    assets.Loads,
                    Is.EqualTo(2),
                    "重试必须再次进入工厂，不能因重复注册而失败。"
                );
                Assert.That(first.OpenCount, Is.Zero);
                _flow.ConfigureUi(assets, replacement);
                await FailUiLoad();
                Assert.That(assets.Loads, Is.EqualTo(3), "新管理器也必须注册工厂。");
                Assert.That(replacement.OpenCount, Is.Zero);
            }
            finally
            {
                await _flow.StopAsync();
                await first.DisposeAsync();
                await replacement.DisposeAsync();
            }
        }

        private async Task FailUiLoad()
        {
            LogAssert.Expect(LogType.Exception, "InvalidOperationException: ui-factory-fixture");
            await (Task)
                typeof(TopologyClientFlow)
                    .GetMethod("LoadUiAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(_flow, null);
            Assert.That(_flow.Error, Is.EqualTo("ui-load-failed"));
        }

        private sealed class FailingUiAssets : IAssetService
        {
            public int Loads { get; private set; }

            public ValueTask<AssetHandle<T>> LoadAsync<T>(
                AssetId id,
                CancellationToken cancellationToken = default
            )
            {
                Loads++;
                throw new InvalidOperationException("ui-factory-fixture");
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
                throw new NotSupportedException();
            }
        }

        private void RunBackground(Task operation)
        {
            typeof(TopologyClientFlow)
                .GetMethod("Run", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(_flow, new object[] { new Func<Task>(() => operation), false });
        }

        private object Field(string name)
        {
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            FieldInfo member = typeof(TopologyClientFlow).GetField(name, flags);
            return member != null
                ? member.GetValue(_flow)
                : typeof(TopologyClientFlow).GetProperty(name, flags).GetValue(_flow);
        }
    }
}
