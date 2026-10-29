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
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.InputSystem.UI;
using VContainer;

namespace AiNative.Client.Application
{
    /// <summary>显式应用组合根；所有模块由 Core 依赖表统一启动及逆序释放。</summary>
    [DisallowMultipleComponent]
    public sealed class FrameworkClientRoot : MonoBehaviour
    {
        private sealed class Module : IClientModule
        {
            private readonly Func<CancellationToken, UniTask> _start;
            private readonly Func<UniTask> _stop;

            public Module(Func<CancellationToken, UniTask> start, Func<UniTask> stop)
            {
                _start = start;
                _stop = stop;
            }

            public ValueTask StartAsync(CancellationToken token) => _start(token).AsValueTask();

            public ValueTask StopAsync(CancellationToken token) => _stop().AsValueTask();
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
        private ClientFrameEvents _frames;
        private bool _ownsFrames;
        private CancellationTokenSource _lifetime;
        private UniTaskCompletionSource _start;
        private UniTaskCompletionSource _stop;
        private UniTaskCompletionSource _retry;
        private Func<int, ValueTask> _quit;
        private string _failure;
        private TopologyClientFlow _flow;
        private BattleClientCompositionRoot _battleRoot;
        private TopologyAccountLobbyService _accountLobby;
        private TopologyMatchController _matchController;
        private bool _destroyed;

        /// <summary>全部模块就绪后为真；停止受理后立即为假。</summary>
        public bool IsRunning => _stop == null && _application?.IsRunning == true;

        private void Awake()
        {
            _configuration =
                _settings != null ? _settings.Capture() : new ClientSettingsSnapshot(60, true);
        }

        /// <summary>兼容装配入口，只创建根及业务对象，绝不启动资源或网络。业务代码使用注入引用。</summary>
        public static FrameworkClientRoot GetOrCreate(GameObject owner)
        {
            var root = owner.GetComponent<FrameworkClientRoot>();
            if (root == null)
            {
                root = owner.AddComponent<FrameworkClientRoot>();
            }
            root.PrepareServices();
            return root;
        }

        private void PrepareServices()
        {
            _flow = GetComponent<TopologyClientFlow>();
            _battleRoot = GetComponent<BattleClientCompositionRoot>();
            if (_accountLobby == null)
            {
                _accountLobby = new TopologyAccountLobbyService();
                _matchController = new TopologyMatchController();
            }
            if (_flow != null)
            {
                _flow.ConfigureServices(this, _accountLobby, _matchController);
            }
        }

        /// <summary>显式启动；首次令牌控制此次启动，重复调用共享结果。停止完成后创建全新运行作用域。</summary>
        public ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_destroyed)
            {
                throw new ObjectDisposedException(nameof(FrameworkClientRoot));
            }
            if (_stop != null && !_stop.Task.Status.IsCompleted())
            {
                throw new InvalidOperationException("client-stopping");
            }
            if (_start != null && _stop == null)
            {
                return _start.Task.AsValueTask();
            }
            _stop = null;
            _failure = null;
            _lifetime?.Dispose();
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _application = null;
            _start = new UniTaskCompletionSource();
            // 配置和装配也属于同一次启动；同步校验失败必须由共享结果观察。
            ObserveStartAsync(_start).Forget(Debug.LogException);
            return _start.Task.AsValueTask();
        }

        private ClientApplication CreateApplication()
        {
            return new ClientApplication(
                new[]
                {
                    new ClientModuleRegistration(
                        "foundation",
                        new Module(StartFoundationAsync, StopFoundationAsync)
                    ),
                    new ClientModuleRegistration(
                        "resources",
                        new Module(StartResourcesAsync, StopResourcesAsync),
                        "foundation"
                    ),
                    new ClientModuleRegistration(
                        "ui",
                        new Module(StartUiAsync, StopUiAsync),
                        "resources"
                    ),
                    new ClientModuleRegistration(
                        "account",
                        new Module(StartAccountAsync, StopAccountAsync),
                        "ui"
                    ),
                    new ClientModuleRegistration(
                        "match",
                        new Module(StartMatchAsync, StopMatchAsync),
                        "account"
                    ),
                    new ClientModuleRegistration(
                        "presentation",
                        new Module(StartPresentationAsync, StopPresentationAsync),
                        "match"
                    ),
                    new ClientModuleRegistration(
                        "flow",
                        new Module(StartFlowAsync, StopFlowAsync),
                        "presentation"
                    ),
                }
            );
        }

        private async UniTask ObserveStartAsync(UniTaskCompletionSource completion)
        {
            try
            {
                _battleRoot = GetComponent<BattleClientCompositionRoot>();
                _battleRoot?.ConfigureLaunch();
                PrepareServices();
                _application = CreateApplication();
                await _application.StartAsync(_lifetime.Token);
                _diagnostics.Record("CLIENT_FRAMEWORK_STARTED", "All modules ready");
                completion.TrySetResult();
            }
            catch (OperationCanceledException)
            {
                completion.TrySetCanceled();
            }
            catch (Exception exception)
            {
                _failure = exception.Message;
                completion.TrySetException(exception);
            }
        }

        /// <summary>只等待显式启动，不触发启动；调用方取消不影响其他消费者。</summary>
        public ValueTask<IAssetService> AssetsAsync(CancellationToken token) =>
            GetAssetsAsync(token).AsValueTask();

        private async UniTask<IAssetService> GetAssetsAsync(CancellationToken token)
        {
            if (_stop != null)
            {
                throw new ObjectDisposedException(nameof(FrameworkClientRoot));
            }
            if (_start == null)
            {
                throw new InvalidOperationException("client-not-started");
            }
            await _start.Task.AttachExternalCancellation(token);
            token.ThrowIfCancellationRequested();
            if (_stop != null)
            {
                throw new ObjectDisposedException(nameof(FrameworkClientRoot));
            }
            return _assets;
        }

        /// <summary>获取已显式启动的 UI 服务；不会隐式启动应用。</summary>
        public ValueTask<UiService> UiAsync(CancellationToken token) =>
            GetUiAsync(token).AsValueTask();

        private async UniTask<UiService> GetUiAsync(CancellationToken token)
        {
            await GetAssetsAsync(token);
            return _ui;
        }

        private UniTask StartFoundationAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            global::UnityEngine.Application.targetFrameRate = _configuration.TargetFrameRate;
            global::UnityEngine.Application.runInBackground = _configuration.RunInBackground;
            _scenes = new ClientSceneService(id => id.Value);
            _frames = GetComponent<ClientFrameEvents>();
            _ownsFrames = _frames == null;
            if (_ownsFrames)
            {
                _frames = gameObject.AddComponent<ClientFrameEvents>();
            }
            _scope = new ClientServiceScope(builder =>
            {
                builder.RegisterInstance<IClientDiagnostics>(_diagnostics);
                builder.RegisterInstance<ISceneService>(_scenes);
                builder.RegisterInstance(_configuration);
            });
            return UniTask.CompletedTask;
        }

        private async UniTask StopFoundationAsync()
        {
            try
            {
                if (_scenes != null)
                {
                    await _scenes.UnloadAsync();
                }
            }
            finally
            {
                _scope?.Dispose();
                _scope = null;
                _scenes = null;
                if (_ownsFrames && _frames != null)
                {
                    Destroy(_frames);
                    await UniTask.NextFrame();
                }
                _frames = null;
                _accountLobby = null;
                _matchController = null;
            }
        }

        private async UniTask StartResourcesAsync(CancellationToken token)
        {
            string directory = await LocalContentInstaller.PrepareAsync(
                Path.Combine(
                    global::UnityEngine.Application.streamingAssetsPath,
                    "AiNativeContent"
                ),
                Path.Combine(global::UnityEngine.Application.persistentDataPath, "AiNativeContent"),
                LocalAssetBundleService.CurrentPlatform,
                token
            );
            _assets = new LocalAssetBundleService(
                directory,
                LocalAssetBundleService.CurrentPlatform
            );
        }

        private async UniTask StopResourcesAsync()
        {
            if (_assets != null)
            {
                await _assets.DisposeAsync();
                _assets = null;
            }
        }

        private UniTask StartUiAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            _ownedEventSystem = new GameObject("Application UI EventSystem");
            _ownedEventSystem.SetActive(false);
            _ownedEventSystem.transform.SetParent(transform, false);
            var events = _ownedEventSystem.AddComponent<MultiplayerEventSystem>();
            events.playerRoot = gameObject;
            _ownedEventSystem.AddComponent<InputSystemUIInputModule>();
            _ownedEventSystem.SetActive(true);
            _ui = new UiService(events);
            return UniTask.CompletedTask;
        }

        private async UniTask StopUiAsync()
        {
            try
            {
                if (_ui != null)
                {
                    await _ui.DisposeAsync();
                }
            }
            finally
            {
                _ui = null;
                if (_ownedEventSystem != null)
                {
                    _ownedEventSystem.SetActive(false);
                    Destroy(_ownedEventSystem);
                    _ownedEventSystem = null;
                    await UniTask.NextFrame();
                }
            }
        }

        private UniTask StartAccountAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return UniTask.CompletedTask;
        }

        private UniTask StopAccountAsync()
        {
            _accountLobby?.Dispose();
            _accountLobby = null;
            return UniTask.CompletedTask;
        }

        private UniTask StartMatchAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return UniTask.CompletedTask;
        }

        private async UniTask StopMatchAsync()
        {
            var errors = new List<Exception>();
            try
            {
                if (_matchController != null)
                {
                    await _matchController.StopAsync();
                }
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
            try
            {
                if (_battleRoot != null)
                {
                    await _battleRoot.StopSessionAsync();
                }
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
            _matchController = null;
            if (errors.Count > 0)
            {
                throw new AggregateException(errors);
            }
        }

        private async UniTask StartPresentationAsync(CancellationToken token)
        {
            if (_battleRoot != null && _battleRoot.isActiveAndEnabled)
            {
                await _battleRoot.StartPresentationAsync(this, _assets, token);
            }
        }

        private async UniTask StopPresentationAsync()
        {
            if (_battleRoot != null)
            {
                await _battleRoot.ReleaseVisualsAsync();
            }
        }

        private async UniTask StartFlowAsync(CancellationToken token)
        {
            if (_flow != null)
            {
                _flow.ConfigureUi(_assets, _ui);
                await _flow.StartFlowAsync(token);
            }
            else
            {
                _battleRoot?.StartSession();
            }
        }

        private async UniTask StopFlowAsync()
        {
            if (_flow != null)
            {
                await _flow.StopAsync();
            }
        }

        /// <summary>取消所属工作并等待 Core 逆序停止；重复调用共享结果，停止失败仍尝试剩余模块。</summary>
        public ValueTask StopAsync()
        {
            if (_stop == null)
            {
                _stop = new UniTaskCompletionSource();
                StopObservedAsync(_stop).Forget(Debug.LogException);
            }
            return _stop.Task.AsValueTask();
        }

        private async UniTask StopObservedAsync(UniTaskCompletionSource completion)
        {
            var errors = new List<Exception>();
            try
            {
                _lifetime?.Cancel();
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
            try
            {
                if (_application != null)
                {
                    await _application.StopAsync();
                }
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
            if (_application == null)
            {
                try
                {
                    if (_flow != null)
                    {
                        await _flow.StopAsync();
                    }
                }
                catch (Exception error)
                {
                    errors.Add(error);
                }
                try
                {
                    if (_matchController != null)
                    {
                        await _matchController.StopAsync();
                    }
                }
                catch (Exception error)
                {
                    errors.Add(error);
                }
                try
                {
                    _accountLobby?.Dispose();
                }
                catch (Exception error)
                {
                    errors.Add(error);
                }
                _accountLobby = null;
                _matchController = null;
            }
            // 所有模块停止后再解除父令牌注册，避免模块清理期间访问已释放的令牌源。
            try
            {
                _lifetime?.Dispose();
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
            finally
            {
                _lifetime = null;
            }
            if (errors.Count == 0)
            {
                completion.TrySetResult();
            }
            else
            {
                completion.TrySetException(new AggregateException(errors));
            }
        }

        /// <summary>失败启动通过停止旧运行作用域后重新启动恢复；运行中的应用不重置账号。</summary>
        public ValueTask RetryStartupAsync()
        {
            if (_retry == null || _retry.Task.Status.IsCompleted())
            {
                _retry = new UniTaskCompletionSource();
                RetryCoreAsync(_retry).Forget(Debug.LogException);
            }
            return _retry.Task.AsValueTask();
        }

        private async UniTask RetryCoreAsync(UniTaskCompletionSource completion)
        {
            try
            {
                if (!IsRunning)
                {
                    await StopAsync();
                    await StartAsync();
                }
                else if (_flow != null)
                {
                    await _flow.RetryUiAsync();
                }
                completion.TrySetResult();
            }
            catch (Exception error)
            {
                completion.TrySetException(error);
            }
        }

        private void RetryObserved() => RetryStartupAsync().AsUniTask().Forget(Debug.LogException);

        /// <summary>注入外层退出拥有者；停止内容根之后才允许卸载内容场景。</summary>
        public void ConfigureQuit(Func<int, ValueTask> quit) =>
            _quit = quit ?? throw new ArgumentNullException(nameof(quit));

        /// <summary>显式退出，观察异常并保留退出码。</summary>
        public void RequestQuit(int exitCode) =>
            QuitObservedAsync(exitCode).Forget(Debug.LogException);

        private async UniTask QuitObservedAsync(int exitCode)
        {
            if (_quit != null)
            {
                await _quit(exitCode);
                return;
            }
            try
            {
                await StopAsync();
            }
            catch (Exception error)
            {
                Debug.LogException(error);
                exitCode = 1;
            }
            global::UnityEngine.Application.Quit(exitCode);
        }

        private void OnDestroy()
        {
            _destroyed = true;
            try
            {
                _lifetime?.Cancel();
            }
            catch (Exception error)
            {
                Debug.LogException(error);
            }
            StopAsync().AsUniTask().Forget(Debug.LogException);
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
