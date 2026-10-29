using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AiNative.Client.Async;
using Cysharp.Threading.Tasks;
using UnityEngine.SceneManagement;

namespace AiNative.Client.Runtime
{
    /// <summary>逻辑场景标识；由应用映射到构建中显式包含的场景路径。</summary>
    public readonly struct SceneId
    {
        /// <summary>应用定义的逻辑名称；不含资源加载或场景所有权。</summary>
        public string Value { get; }

        /// <summary>创建非空标识；空白名称抛出 ArgumentException。</summary>
        public SceneId(string value)
        {
            Value = string.IsNullOrWhiteSpace(value)
                ? throw new ArgumentException("scene-id-required", nameof(value))
                : value;
        }
    }

    /// <summary>主线程调用的串行内容场景切换；启动场景始终由应用保留。</summary>
    public interface ISceneService
    {
        /// <summary>
        /// 主线程串行切换内容场景；成功加载后才移除旧场景。
        /// 取消原生加载须等待其完成并清理迟到结果；加载失败保留原场景，错误由调用方观察。
        /// 同一路径已加载或被其他实例预留时拒绝；不同路径可由不同实例独立加载。
        /// </summary>
        ValueTask SwitchAsync(SceneId id, CancellationToken cancellationToken = default);

        /// <summary>
        /// 主线程卸载本服务拥有的内容场景；空场景重复调用无副作用，不释放应用启动场景。
        /// 令牌可取消排队等待，原生卸载受理后等待完成；调用方必须观察失败并完成退出收尾。
        /// 尝试清理本服务的全部场景；失败保留所有权供后续重试，多个失败聚合报告。
        /// </summary>
        ValueTask UnloadAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>加载成功后才移除旧场景；取消产生的迟到场景由服务卸载。</summary>
    public sealed class ClientSceneService : ISceneService
    {
        // 仅记录主线程原生场景的排他所有权；不保存或解析服务，不跨应用传递实例。
        // 预留覆盖加载中的路径，避免两个服务在首个 await 前都通过 isLoaded 检查。
        private static readonly HashSet<string> ReservedPaths = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase
        );
        private readonly Dictionary<string, Scene> _ownedScenes = new Dictionary<string, Scene>(
            StringComparer.OrdinalIgnoreCase
        );
        private readonly Func<SceneId, string> _resolvePath;
        private readonly SemaphoreSlim _transition = new SemaphoreSlim(1, 1);
        private Scene _current;

        /// <summary>
        /// 注入应用拥有的逻辑 ID 到构建场景路径映射；不复制或拥有该映射。
        /// 调用方须在销毁应用前显式等待 UnloadAsync；不同实例不能并发拥有同一路径。
        /// </summary>
        public ClientSceneService(Func<SceneId, string> resolvePath)
        {
            _resolvePath = resolvePath ?? throw new ArgumentNullException(nameof(resolvePath));
        }

        /// <inheritdoc />
        public ValueTask SwitchAsync(SceneId id, CancellationToken cancellationToken = default) =>
            SwitchCoreAsync(id, cancellationToken).AsValueTask();

        private async UniTask SwitchCoreAsync(SceneId id, CancellationToken cancellationToken)
        {
            // 直接等待同步原语的 Task，保留空闲锁立即进入的时序；AsUniTask 会额外调度续体。
            await _transition.WaitAsync(cancellationToken);
            try
            {
                string path = _resolvePath(id);
                if (string.IsNullOrEmpty(path))
                {
                    throw new ArgumentException("scene-not-registered");
                }
                path = path.Replace('\\', '/');
                // LoadSceneAsync 也接受短名称，但 GetSceneByPath 不接受；仅使用构建清单的
                // 完整规范路径，确保成功加载后始终能找到并释放同一个原生场景。
                int buildIndex = SceneUtility.GetBuildIndexByScenePath(path);
                if (buildIndex < 0)
                {
                    throw new ArgumentException("scene-path-not-in-build");
                }
                string canonicalPath = SceneUtility.GetScenePathByBuildIndex(buildIndex);
                if (!string.Equals(path, canonicalPath, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("scene-full-path-required");
                }
                path = canonicalPath;
                if (_current.IsValid() && _current.isLoaded && _current.path == path)
                {
                    return;
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (SceneManager.GetSceneByPath(path).isLoaded || !ReservedPaths.Add(path))
                {
                    throw new InvalidOperationException("scene-already-owned");
                }
                _ownedScenes.Add(path, default);
                Scene loaded = await LoadOwnedAsync(path);
                if (cancellationToken.IsCancellationRequested)
                {
                    await UnloadOwnedAsync(path);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                Scene previous = _current;
                _current = loaded;
                SceneManager.SetActiveScene(loaded);
                if (previous.IsValid() && previous.isLoaded)
                {
                    await UnloadOwnedAsync(previous.path);
                }
            }
            finally
            {
                _transition.Release();
            }
        }

        /// <inheritdoc />
        public ValueTask UnloadAsync(CancellationToken cancellationToken = default) =>
            UnloadCoreAsync(cancellationToken).AsValueTask();

        private async UniTask UnloadCoreAsync(CancellationToken cancellationToken)
        {
            // 直接等待同步原语的 Task，保留空闲锁立即进入的时序；AsUniTask 会额外调度续体。
            await _transition.WaitAsync(cancellationToken);
            try
            {
                var paths = new List<string>(_ownedScenes.Keys);
                var failures = new List<Exception>();
                foreach (string path in paths)
                {
                    try
                    {
                        await UnloadOwnedAsync(path);
                    }
                    catch (Exception failure)
                    {
                        failures.Add(failure);
                    }
                }
                if (!_current.IsValid() || !_current.isLoaded)
                {
                    _current = default;
                }
                if (failures.Count > 0)
                {
                    throw new AggregateException(failures);
                }
            }
            finally
            {
                _transition.Release();
            }
        }

        private async UniTask<Scene> LoadOwnedAsync(string path)
        {
            try
            {
                await UnityAsync.CompleteAsync(
                    SceneManager.LoadSceneAsync(path, LoadSceneMode.Additive)
                );
                Scene loaded = SceneManager.GetSceneByPath(path);
                if (!loaded.IsValid() || !loaded.isLoaded)
                {
                    throw new InvalidOperationException("scene-load-incomplete");
                }
                _ownedScenes[path] = loaded;
                return loaded;
            }
            catch (Exception loadFailure)
            {
                try
                {
                    await UnloadOwnedAsync(path);
                }
                catch (Exception cleanupFailure)
                {
                    throw new AggregateException(loadFailure, cleanupFailure);
                }
                throw;
            }
        }

        private async UniTask UnloadOwnedAsync(string path)
        {
            Scene scene = _ownedScenes[path];
            if (!scene.IsValid())
            {
                scene = SceneManager.GetSceneByPath(path);
            }
            if (scene.IsValid() && scene.isLoaded)
            {
                await UnityAsync.CompleteAsync(SceneManager.UnloadSceneAsync(scene));
                if (scene.IsValid() && scene.isLoaded)
                {
                    throw new InvalidOperationException("scene-unload-incomplete");
                }
            }
            // 只有原生卸载成功才释放预留；失败保留在本服务，以便 UnloadAsync 重试。
            _ownedScenes.Remove(path);
            ReservedPaths.Remove(path);
        }
    }
}
