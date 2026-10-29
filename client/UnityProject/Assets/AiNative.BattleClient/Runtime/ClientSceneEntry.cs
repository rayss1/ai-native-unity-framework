using System;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace AiNative.Client.Application
{
    /// <summary>场景启动策略；外部入口由所属 Bootstrap 显式启动根。</summary>
    public enum ClientSceneEntryMode
    {
        /// <summary>仅根据本内容场景的启动器预留决定是否外部启动。</summary>
        Automatic,

        /// <summary>直接打开场景时，由本入口启动根。</summary>
        Direct,

        /// <summary>由外部拥有者启动，组件不自行启动。</summary>
        External,
    }

    /// <summary>
    /// 主线程场景入口；只借用同对象的应用根，不拥有网络或资源。
    /// 直接启动时拥有进程退出协调：先等待根停止再允许退出；外部模式由 Bootstrap 协调。
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(FrameworkClientRoot))]
    public sealed class ClientSceneEntry : MonoBehaviour
    {
        [SerializeField]
        private ClientSceneEntryMode _mode = ClientSceneEntryMode.Automatic;

        private FrameworkClientRoot _root;
        private bool _ownsDirectStartup;
        private bool _quitAllowed;
        private bool _destroyed;
        private UniTaskCompletionSource _quitCompletion;

        private void Start()
        {
            if (
                _mode == ClientSceneEntryMode.External
                || (
                    _mode == ClientSceneEntryMode.Automatic
                    && FrameworkBootstrap.IsExternallyManaged(gameObject.scene)
                )
            )
            {
                return;
            }
            ConfigureDirect(GetComponent<FrameworkClientRoot>());
            _root.StartAsync().AsUniTask().Forget(Debug.LogException);
        }

        /// <summary>主线程借用直接启动的根并接管退出协调；重复配置同一根幂等，不触发启动。</summary>
        internal void ConfigureDirect(FrameworkClientRoot root)
        {
            if (root == null)
            {
                throw new ArgumentNullException(nameof(root));
            }
            if (_destroyed)
            {
                throw new ObjectDisposedException(nameof(ClientSceneEntry));
            }
            if (_ownsDirectStartup)
            {
                if (_root != root)
                {
                    throw new InvalidOperationException("scene-entry-root-already-configured");
                }
                return;
            }
            _root = root;
            _ownsDirectStartup = true;
            // 域重载关闭时仍只保留本实例的一份订阅；其他应用入口的订阅不受影响。
            global::UnityEngine.Application.wantsToQuit -= HandleWantsToQuit;
            global::UnityEngine.Application.wantsToQuit += HandleWantsToQuit;
            _root.ConfigureQuit(RequestQuitAsync);
        }

        /// <summary>
        /// 主线程请求正常退出；首次请求拥有退出码，重复调用共享清理结果。
        /// 停止失败记录异常并以 1 退出；对象销毁后仅完成清理，不再请求进程退出。
        /// </summary>
        internal ValueTask RequestQuitAsync(int exitCode = 0)
        {
            if (!_ownsDirectStartup)
            {
                throw new InvalidOperationException("scene-entry-does-not-own-quit");
            }
            if (_quitCompletion == null)
            {
                var completion = new UniTaskCompletionSource();
                _quitCompletion = completion;
                QuitObservedAsync(exitCode, completion).Forget(Debug.LogException);
            }
            return _quitCompletion.Task.AsValueTask();
        }

        private async UniTask QuitObservedAsync(int exitCode, UniTaskCompletionSource completion)
        {
            try
            {
                await _root.StopAsync();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                exitCode = 1;
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
            if (_quitAllowed || _destroyed || !_ownsDirectStartup)
            {
                return true;
            }
            RequestQuitAsync().AsUniTask().Forget(Debug.LogException);
            return false;
        }

        private void OnDestroy()
        {
            _destroyed = true;
            global::UnityEngine.Application.wantsToQuit -= HandleWantsToQuit;
        }
    }
}
