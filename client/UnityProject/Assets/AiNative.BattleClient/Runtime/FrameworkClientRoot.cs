using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AiNative.Client.Assets;
using AiNative.Client.Assets.AssetBundle;
using AiNative.Client.Composition;
using AiNative.Client.Core;
using AiNative.Client.Runtime;
using AiNative.Client.UI;
using UnityEngine;
using UnityEngine.InputSystem.UI;
using VContainer;

namespace AiNative.Client.Application
{
    /// <summary>应用组合入口；每个本地玩家独立拥有资源、UI 和容器，不使用静态服务定位器。</summary>
    [DisallowMultipleComponent]
    public sealed class FrameworkClientRoot : MonoBehaviour
    {
        private sealed class ResourceModule : IClientModule
        {
            private readonly FrameworkClientRoot _owner;

            public ResourceModule(FrameworkClientRoot owner)
            {
                _owner = owner;
            }

            public async ValueTask StartAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string directory = await LocalContentInstaller.PrepareAsync(
                    Path.Combine(
                        global::UnityEngine.Application.streamingAssetsPath,
                        "AiNativeContent"
                    ),
                    Path.Combine(
                        global::UnityEngine.Application.persistentDataPath,
                        "AiNativeContent"
                    ),
                    LocalAssetBundleService.CurrentPlatform,
                    cancellationToken
                );
                _owner._assets = new LocalAssetBundleService(
                    directory,
                    LocalAssetBundleService.CurrentPlatform
                );
            }

            public async ValueTask StopAsync(CancellationToken cancellationToken)
            {
                if (_owner._assets != null)
                {
                    await _owner._assets.DisposeAsync();
                    _owner._assets = null;
                }
            }
        }

        [SerializeField]
        private FrameworkClientSettings _settings;
        private ClientSettingsSnapshot _configuration;
        private readonly UnityClientDiagnostics _diagnostics = new UnityClientDiagnostics();
        private ClientApplication _application;
        private ClientServiceScope _scope;
        private LocalAssetBundleService _assets;
        private UiService _ui;
        private GameObject _ownedEventSystem;
        private ClientSceneService _scenes;
        private CancellationTokenSource _lifetime = new CancellationTokenSource();
        private Task _startTask;
        private Task _stopTask;
        private Task _retryTask;
        private Func<int, ValueTask> _quit;
        private string _failure;
        private TopologyClientFlow _flow;
        private BattleClientCompositionRoot _battleRoot;

        private void Awake()
        {
            _configuration =
                _settings != null ? _settings.Capture() : new ClientSettingsSnapshot(60, true);
            global::UnityEngine.Application.targetFrameRate = _configuration.TargetFrameRate;
            global::UnityEngine.Application.runInBackground = _configuration.RunInBackground;
            _flow = GetComponent<TopologyClientFlow>();
            _battleRoot = GetComponent<BattleClientCompositionRoot>();
        }

        /// <summary>只供现有场景入口过渡装配；返回同一 GameObject 拥有的根。</summary>
        public static FrameworkClientRoot GetOrCreate(GameObject owner)
        {
            var root = owner.GetComponent<FrameworkClientRoot>();
            if (root == null)
            {
                root = owner.AddComponent<FrameworkClientRoot>();
            }
            root._flow = owner.GetComponent<TopologyClientFlow>();
            root._battleRoot = owner.GetComponent<BattleClientCompositionRoot>();
            return root;
        }

        /// <summary>等待应用资源启动；调用方取消不取消其他消费者的启动。</summary>
        public async ValueTask<IAssetService> AssetsAsync(CancellationToken cancellationToken)
        {
            if (_stopTask != null)
            {
                throw new ObjectDisposedException(nameof(FrameworkClientRoot));
            }
            if (_startTask == null)
            {
                _startTask = StartCoreAsync();
            }
            await _startTask;
            cancellationToken.ThrowIfCancellationRequested();
            _lifetime.Token.ThrowIfCancellationRequested();
            return _assets;
        }

        /// <summary>组合根提供当前应用拥有的界面管理器；不跨本地玩家共享。</summary>
        public async ValueTask<UiService> UiAsync(CancellationToken cancellationToken)
        {
            await AssetsAsync(cancellationToken);
            return _ui;
        }

        private async Task StartCoreAsync()
        {
            try
            {
                _ownedEventSystem = new GameObject("Application UI EventSystem");
                _ownedEventSystem.SetActive(false);
                _ownedEventSystem.transform.SetParent(transform, false);
                var eventSystem = _ownedEventSystem.AddComponent<MultiplayerEventSystem>();
                eventSystem.playerRoot = gameObject;
                _ownedEventSystem.AddComponent<InputSystemUIInputModule>();
                _ownedEventSystem.SetActive(true);
                _ui = new UiService(eventSystem);
                _scenes = new ClientSceneService(id => id.Value);
                _scope = new ClientServiceScope(builder =>
                {
                    builder.RegisterInstance<IUIService>(_ui);
                    builder.RegisterInstance<IClientDiagnostics>(_diagnostics);
                    builder.RegisterInstance<ISceneService>(_scenes);
                });
                _application = new ClientApplication(
                    new IClientModule[] { new ResourceModule(this) }
                );
                await _application.StartAsync(_lifetime.Token);
                _diagnostics.Record("CLIENT_FRAMEWORK_STARTED", "Local content ready");
            }
            catch (Exception exception)
            {
                _failure = exception.Message;
                Debug.LogException(exception);
                throw;
            }
        }

        /// <summary>先释放 UI，再关闭资源模块和容器；正常流程退出应显式等待。</summary>
        public ValueTask StopAsync()
        {
            if (_stopTask == null)
            {
                var completion = new TaskCompletionSource<bool>();
                _stopTask = completion.Task;
                _ = StopObservedAsync(completion);
            }
            return new ValueTask(_stopTask);
        }

        private async Task StopObservedAsync(TaskCompletionSource<bool> completion)
        {
            var errors = new List<Exception>();
            try
            {
                _lifetime.Cancel();
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }
            if (_startTask != null)
            {
                try
                {
                    await _startTask;
                }
                catch (Exception)
                { /* 启动异常已记录。 */
                }
            }
            if (!ReferenceEquals(_flow, null))
            {
                try
                {
                    await _flow.StopAsync();
                }
                catch (Exception exception)
                {
                    errors.Add(exception);
                }
            }
            if (!ReferenceEquals(_battleRoot, null))
            {
                try
                {
                    await _battleRoot.StopAsync();
                }
                catch (Exception exception)
                {
                    errors.Add(exception);
                }
            }
            try
            {
                await ReleaseServicesAsync();
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }
            if (errors.Count > 0)
            {
                completion.TrySetException(new AggregateException(errors));
            }
            else
            {
                completion.TrySetResult(true);
            }
        }

        private async ValueTask ReleaseServicesAsync()
        {
            var errors = new List<Exception>();
            if (_ui != null)
            {
                try
                {
                    await _ui.DisposeAsync();
                }
                catch (Exception exception)
                {
                    errors.Add(exception);
                }
            }
            if (_ownedEventSystem != null)
            {
                GameObject eventSystem = _ownedEventSystem;
                _ownedEventSystem = null;
                eventSystem.SetActive(false);
                Destroy(eventSystem);
                // 重试在同一帧创建新根之前，先完成旧系统的 Unity 销毁。
                await AiNative.Client.Async.UnityAsync.NextFrameAsync(CancellationToken.None);
            }
            if (_scenes != null)
            {
                try
                {
                    await _scenes.UnloadAsync();
                }
                catch (Exception exception)
                {
                    errors.Add(exception);
                }
            }
            if (_application != null)
            {
                try
                {
                    await _application.StopAsync(CancellationToken.None);
                }
                catch (Exception exception)
                {
                    errors.Add(exception);
                }
            }
            try
            {
                _scope?.Dispose();
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }
            if (errors.Count > 0)
            {
                throw new AggregateException(errors);
            }
        }

        /// <summary>仅重试失败的启动；不会重置已经建立的对局或登录状态。</summary>
        public ValueTask RetryStartupAsync()
        {
            if (_retryTask == null || _retryTask.IsCompleted)
            {
                _retryTask = RetryStartupCoreAsync();
            }
            return new ValueTask(_retryTask);
        }

        private async Task RetryStartupCoreAsync()
        {
            if (_stopTask != null)
            {
                return;
            }
            if (string.IsNullOrEmpty(_failure))
            {
                if (_flow != null)
                {
                    await _flow.RetryUiAsync();
                }
                return;
            }
            await ReleaseServicesAsync();
            _failure = null;
            _startTask = null;
            await AssetsAsync(_lifetime.Token);
            if (_flow != null)
            {
                await _flow.RetryUiAsync();
            }
            if (_battleRoot != null && _battleRoot.isActiveAndEnabled)
            {
                _battleRoot.enabled = false;
                _battleRoot.enabled = true;
            }
        }

        private async void RetryObserved()
        {
            try
            {
                await RetryStartupAsync();
            }
            catch (Exception exception)
            {
                _failure = exception.Message;
                Debug.LogException(exception);
            }
        }

        /// <summary>由持久启动场景注入退出流程，保持退出码并等待内容清理；限主线程装配。</summary>
        public void ConfigureQuit(Func<int, ValueTask> quit)
        {
            _quit = quit ?? throw new ArgumentNullException(nameof(quit));
        }

        /// <summary>请求退出并观察异常；场景独立启动时也先等待当前应用停止。</summary>
        public void RequestQuit(int exitCode)
        {
            _ = QuitObservedAsync(exitCode);
        }

        private async Task QuitObservedAsync(int exitCode)
        {
            try
            {
                if (_quit != null)
                {
                    await _quit(exitCode);
                    return;
                }
                await StopAsync();
                global::UnityEngine.Application.Quit(exitCode);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                global::UnityEngine.Application.Quit(1);
            }
        }

        private async void OnDestroy()
        {
            try
            {
                await StopAsync();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
        }

        private void OnGUI()
        {
            if (Debug.isDebugBuild && _assets != null)
            {
                GUI.Label(
                    new Rect(Mathf.Max(16, Screen.width - 360), Screen.height - 70, 350, 60),
                    "客户端资源：句柄 "
                        + _assets.ActiveHandleCount
                        + " / Bundle "
                        + _assets.LoadedBundleCount
                        + "\n界面 "
                        + (_ui?.OpenCount ?? 0)
                        + " / 启动 "
                        + (_application?.IsRunning == true)
                );
            }
            if (
                !string.IsNullOrEmpty(_failure)
                || (_flow != null && _flow.Error == "ui-load-failed")
            )
            {
                GUI.Label(
                    new Rect(16, 140, 1000, 60),
                    "客户端资源启动失败：" + (_failure ?? "界面资源不可用")
                );
                if (GUI.Button(new Rect(16, 205, 140, 32), "重试资源启动"))
                {
                    RetryObserved();
                }
            }
        }
    }
}
