using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AiNative.Client.Runtime;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AiNative.Client.Application
{
    /// <summary>
    /// 持久启动场景拥有的产品场景生命周期。仅负责加载与有序关闭，不驱动网络或预测 Tick。
    /// 正常退出先等待内容根关闭，再卸载 Additive 场景；强制进程终止不保证异步清理完成。
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-1000)]
    public sealed class FrameworkBootstrap : MonoBehaviour
    {
        /// <summary>现有产品场景路径；直接打开该场景的开发入口保持有效。</summary>
        public const string ContentScenePath =
            "Assets/AiNative.BattleClient/Scenes/BattleClient.unity";

        // 按内容路径预留所属启动器，不把任意应用根当作全局单例。
        private static readonly Dictionary<string, FrameworkBootstrap> ContentOwners =
            new Dictionary<string, FrameworkBootstrap>(StringComparer.OrdinalIgnoreCase);

        internal static bool IsExternallyManaged(Scene scene)
        {
            return ContentOwners.TryGetValue(scene.path, out FrameworkBootstrap owner)
                && owner != null
                && !owner._destroyed;
        }

        private void ReserveContent()
        {
            if (
                ContentOwners.TryGetValue(ContentScenePath, out FrameworkBootstrap owner)
                && owner != null
                && owner != this
            )
            {
                throw new InvalidOperationException("bootstrap-content-already-owned");
            }
            ContentOwners[ContentScenePath] = this;
        }

        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private ClientSceneService _scenes;
        private Scene _bootstrapScene;
        private UniTaskCompletionSource _startupTask;
        private UniTaskCompletionSource _stopTask;
        private UniTaskCompletionSource _quitTask;
        private bool _quitAllowed;
        private bool _destroyed;
        private string _failure;

        /// <summary>启动失败原因；仅用于诊断和恢复界面，不作为业务错误码。</summary>
        public string Failure => _failure;

        private void Awake()
        {
            _bootstrapScene = gameObject.scene;
            _scenes = new ClientSceneService(id =>
            {
                if (id.Value != "battle")
                {
                    throw new ArgumentException("bootstrap-scene-not-registered");
                }
                return ContentScenePath;
            });
            global::UnityEngine.Application.wantsToQuit += HandleWantsToQuit;
        }

        private void Start()
        {
            if (_stopTask == null)
            {
                ObserveStartupAsync().Forget();
            }
        }

        /// <summary>主线程加载一次产品场景并等待其资源启动；重复调用等待同一任务。</summary>
        public ValueTask StartAsync()
        {
            if (_stopTask != null)
            {
                throw new ObjectDisposedException(nameof(FrameworkBootstrap));
            }
            if (_startupTask == null)
            {
                var completion = new UniTaskCompletionSource();
                _startupTask = completion;
                StartObservedAsync(completion).Forget();
            }
            return _startupTask.Task.AsValueTask();
        }

        /// <summary>主线程重试失败启动，保留已加载内容场景；关闭中不允许重试，重复调用等待当前启动。</summary>
        public ValueTask RetryStartupAsync()
        {
            if (_stopTask != null)
            {
                throw new ObjectDisposedException(nameof(FrameworkBootstrap));
            }
            if (_startupTask == null || !(_startupTask.Task.Status == UniTaskStatus.Faulted))
            {
                return StartAsync();
            }
            var completion = new UniTaskCompletionSource();
            _startupTask = completion;
            _failure = null;
            StartObservedAsync(completion, true).Forget();
            return _startupTask.Task.AsValueTask();
        }

        private async UniTask StartObservedAsync(
            UniTaskCompletionSource completion,
            bool retry = false
        )
        {
            try
            {
                // 不把原生加载取消直接交给场景服务：必须先发现迟到的内容根并等待其 Stop，
                // 然后才允许卸载，避免资源启动仍在继续时销毁它们的 MonoBehaviour。
                ReserveContent();
                Scene content = SceneManager.GetSceneByPath(ContentScenePath);
                if (!content.IsValid() || !content.isLoaded)
                {
                    await _scenes.SwitchAsync(new SceneId("battle"), CancellationToken.None);
                }
                List<FrameworkClientRoot> roots = CollectContentRoots();
                if (roots.Count == 0)
                {
                    throw new InvalidOperationException("bootstrap-content-root-missing");
                }
                foreach (FrameworkClientRoot root in roots)
                {
                    root.ConfigureQuit(RequestQuitAsync);
                }
                _lifetime.Token.ThrowIfCancellationRequested();
                foreach (FrameworkClientRoot root in roots)
                {
                    if (root.isActiveAndEnabled)
                    {
                        if (retry)
                        {
                            await root.StopAsync();
                        }
                        await root.StartAsync(_lifetime.Token);
                    }
                }
                completion.TrySetResult();
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                completion.TrySetCanceled();
            }
            catch (Exception exception)
            {
                _failure = exception.Message;
                completion.TrySetException(exception);
            }
        }

        private async UniTask ObserveStartupAsync()
        {
            try
            {
                await StartAsync();
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                // 关闭期间取消属于正常生命周期，不作为启动错误打印。
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
        }

        /// <summary>先等待正在加载的场景，再逐个停止内容根，最后卸载；重复调用共享清理任务。</summary>
        public ValueTask StopAsync()
        {
            if (_stopTask == null)
            {
                var completion = new UniTaskCompletionSource();
                _stopTask = completion;
                StopObservedAsync(completion).Forget();
            }
            return _stopTask.Task.AsValueTask();
        }

        private async UniTask StopObservedAsync(UniTaskCompletionSource completion)
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
            if (_startupTask != null)
            {
                try
                {
                    await _startupTask.Task;
                }
                catch (OperationCanceledException) { }
                catch (Exception exception)
                {
                    errors.Add(exception);
                }
            }
            foreach (FrameworkClientRoot root in CollectContentRoots())
            {
                try
                {
                    await root.StopAsync();
                }
                catch (Exception exception)
                {
                    errors.Add(exception);
                }
            }
            try
            {
                if (_bootstrapScene.IsValid() && _bootstrapScene.isLoaded)
                {
                    SceneManager.SetActiveScene(_bootstrapScene);
                }
                if (_scenes != null)
                {
                    await _scenes.UnloadAsync(CancellationToken.None);
                }
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }
            if (
                ContentOwners.TryGetValue(ContentScenePath, out FrameworkBootstrap owner)
                && owner == this
            )
            {
                ContentOwners.Remove(ContentScenePath);
            }
            _lifetime.Dispose();
            if (errors.Count == 0)
            {
                completion.TrySetResult();
            }
            else
            {
                completion.TrySetException(new AggregateException(errors));
            }
        }

        /// <summary>正常产品退出入口，保留调用方退出码并观察清理异常；应由内容根显式注入使用。</summary>
        public ValueTask RequestQuitAsync(int exitCode = 0)
        {
            if (_quitTask == null)
            {
                var completion = new UniTaskCompletionSource();
                _quitTask = completion;
                QuitObservedAsync(exitCode, completion).Forget();
            }
            return _quitTask.Task.AsValueTask();
        }

        private async UniTask QuitObservedAsync(int exitCode, UniTaskCompletionSource completion)
        {
            try
            {
                await StopAsync();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                if (exitCode == 0)
                {
                    exitCode = 1;
                }
            }
            _quitAllowed = true;
            completion.TrySetResult();
            if (!_destroyed)
            {
                global::UnityEngine.Application.Quit(exitCode);
            }
        }

        private bool HandleWantsToQuit()
        {
            if (_quitAllowed)
            {
                return true;
            }
            // 操作系统窗口关闭没有业务退出码；显式 smoke 退出使用注入的 RequestQuitAsync。
            RequestQuitAsync().AsUniTask().Forget(Debug.LogException);
            return false;
        }

        private List<FrameworkClientRoot> CollectContentRoots()
        {
            var result = new List<FrameworkClientRoot>();
            if (
                !ContentOwners.TryGetValue(ContentScenePath, out FrameworkBootstrap owner)
                || owner != this
            )
            {
                return result;
            }
            Scene content = SceneManager.GetSceneByPath(ContentScenePath);
            if (!content.IsValid() || !content.isLoaded)
            {
                return result;
            }
            foreach (GameObject root in content.GetRootGameObjects())
            {
                result.AddRange(root.GetComponentsInChildren<FrameworkClientRoot>(true));
            }
            return result;
        }

        private void OnDestroy()
        {
            _destroyed = true;
            global::UnityEngine.Application.wantsToQuit -= HandleWantsToQuit;
            ObserveFallbackStopAsync().Forget();
        }

        private async UniTask ObserveFallbackStopAsync()
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
            if (!string.IsNullOrEmpty(_failure))
            {
                GUI.Label(new Rect(16, 12, 1100, 60), "客户端场景启动失败：" + _failure);
                if (
                    _stopTask == null
                    && _startupTask != null
                    && (_startupTask.Task.Status == UniTaskStatus.Faulted)
                    && GUI.Button(new Rect(16, 78, 160, 36), "重试场景启动")
                )
                {
                    ObserveRetryAsync().Forget();
                }
            }
        }

        private async UniTask ObserveRetryAsync()
        {
            try
            {
                await RetryStartupAsync();
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                // 用户退出时结束重试，不显示新的恢复错误。
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
        }
    }
}
