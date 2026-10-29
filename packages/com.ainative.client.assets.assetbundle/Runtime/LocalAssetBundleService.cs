using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityBundle = UnityEngine.AssetBundle;
using UnityObject = UnityEngine.Object;

namespace AiNative.Client.Assets.AssetBundle
{
    /// <summary>
    /// 主线程拥有的随包资源服务。仅读取固定本地目录；所有句柄与实例必须在服务关闭前释放。
    /// 调用方取消不取消共享原生加载，完成后的无人持有结果自动清理。
    /// </summary>
    public sealed class LocalAssetBundleService : IAssetService, IAsyncDisposable
    {
        // Unity 不允许相同原生 Bundle 被重复加载。这里只共享已验证内容的原生租约，
        // 应用服务、资源句柄与取消状态仍按实例隔离；最后一个租约释放时移除条目。
        private static readonly Dictionary<string, NativeBundle> NativeBundles =
            new Dictionary<string, NativeBundle>();
        private readonly string _root;
        private readonly int _threadId;
        private readonly Dictionary<string, BundleState> _bundles =
            new Dictionary<string, BundleState>();
        private readonly Dictionary<string, AssetRecord> _catalog =
            new Dictionary<string, AssetRecord>();
        private readonly Dictionary<string, AssetState> _assets =
            new Dictionary<string, AssetState>();
        private readonly HashSet<AssetHandle> _handles = new HashSet<AssetHandle>();
        private readonly List<AssetInstance> _instances = new List<AssetInstance>();
        private readonly List<AssetHandle> _preloads = new List<AssetHandle>();
        private bool _disposed;
        private Task _disposeTask;

        private sealed class BundleState
        {
            public BundleRecord Record;
            public int References;
            public UniTaskCompletionSource<UnityBundle> Loading;
            public UnityBundle Value;
            public NativeBundle Native;
        }

        private sealed class NativeBundle
        {
            public string Hash;
            public string Name;
            public string DependencyIdentity;
            public int References;
            public UniTaskCompletionSource<UnityBundle> Loading;
            public UnityBundle Value;
        }

        // 已完成的 Task 在某些 Mono 路径仍会保留结果和异步状态机。让它们只持有
        // 可清空容器，原生加载结束后显式切断大型验证字节的引用，而不依赖编译器清理字段。
        private sealed class BundleBytes : IDisposable
        {
            public byte[] Value;

            public BundleBytes(byte[] value)
            {
                Value = value;
            }

            public void Dispose()
            {
                Value = null;
            }
        }

        private static BundleBytes ReadBundleBytes(string path)
        {
            return new BundleBytes(File.ReadAllBytes(path));
        }

        private sealed class AssetState
        {
            public AssetRecord Record;
            public List<BundleState> Dependencies;
            public int References;
            public bool IsLoading = true;
            public UniTaskCompletionSource<UnityObject> Loading;
            public UnityObject Value;
        }

        /// <summary>rootDirectory 包含构建工具生成的 catalog.json；expectedPlatform 使用 BuildTarget 名称。</summary>
        public LocalAssetBundleService(string rootDirectory, string expectedPlatform)
        {
            _threadId = Thread.CurrentThread.ManagedThreadId;
            _root = Path.GetFullPath(
                rootDirectory ?? throw new ArgumentNullException(nameof(rootDirectory))
            );
            BundleCatalog catalog;
            try
            {
                catalog = JsonUtility.FromJson<BundleCatalog>(
                    File.ReadAllText(Path.Combine(_root, "catalog.json"))
                );
            }
            catch (Exception exception)
            {
                throw new AssetLoadException(
                    AssetLoadError.Unavailable,
                    "content-catalog-unavailable",
                    exception
                );
            }
            if (
                catalog == null
                || catalog.Version != 1
                || catalog.Platform != expectedPlatform
                || catalog.Bundles == null
                || catalog.Assets == null
            )
            {
                throw new AssetLoadException(
                    AssetLoadError.IncompatibleManifest,
                    "content-catalog-incompatible"
                );
            }
            foreach (BundleRecord record in catalog.Bundles)
            {
                if (
                    record == null
                    || !IsSafeFileName(record.Name)
                    || record.Size <= 0
                    || !IsHash(record.Sha256)
                    || record.Dependencies == null
                    || _bundles.ContainsKey(record.Name)
                )
                {
                    throw InvalidCatalog();
                }
                _bundles.Add(record.Name, new BundleState { Record = record });
            }
            foreach (BundleState bundle in _bundles.Values)
            {
                CollectDependencies(
                    bundle,
                    new HashSet<string>(),
                    new HashSet<string>(),
                    new List<BundleState>()
                );
            }
            foreach (AssetRecord record in catalog.Assets)
            {
                if (
                    record == null
                    || string.IsNullOrWhiteSpace(record.Id)
                    || string.IsNullOrWhiteSpace(record.AssetPath)
                    || string.IsNullOrWhiteSpace(record.Group)
                    || !_bundles.ContainsKey(record.Bundle ?? "")
                    || _catalog.ContainsKey(record.Id)
                )
                {
                    throw InvalidCatalog();
                }
                _catalog.Add(record.Id, record);
            }
        }

        /// <summary>当前 Player 的构建目标名称；Editor 应传 EditorUserBuildSettings.activeBuildTarget。</summary>
        public static string CurrentPlatform
        {
            get
            {
                switch (Application.platform)
                {
                    case RuntimePlatform.OSXPlayer:
                    case RuntimePlatform.OSXEditor:
                        return "StandaloneOSX";
                    case RuntimePlatform.WindowsPlayer:
                    case RuntimePlatform.WindowsEditor:
                        return "StandaloneWindows64";
                    case RuntimePlatform.LinuxPlayer:
                    case RuntimePlatform.LinuxEditor:
                        return "StandaloneLinux64";
                    case RuntimePlatform.Android:
                        return "Android";
                    case RuntimePlatform.IPhonePlayer:
                        return "iOS";
                    default:
                        throw new PlatformNotSupportedException("content-platform-unsupported");
                }
            }
        }

        /// <summary>本服务当前持有的句柄数，包含预加载；只在拥有线程读取。</summary>
        public int ActiveHandleCount => _handles.Count;

        /// <summary>本服务仍引用的已加载 Bundle 数；底层原生对象可能与其他服务共享。</summary>
        public int LoadedBundleCount
        {
            get
            {
                int count = 0;
                foreach (BundleState bundle in _bundles.Values)
                {
                    if (bundle.Value != null)
                    {
                        count++;
                    }
                }
                return count;
            }
        }

        /// <summary>获取独立句柄；取消仅撤销本调用兴趣，Unity 加载结束后清理无人持有结果。</summary>
        public ValueTask<AssetHandle<T>> LoadAsync<T>(
            AssetId id,
            CancellationToken cancellationToken = default
        )
        {
            return LoadCoreAsync<T>(id, cancellationToken).AsValueTask();
        }

        private async UniTask<AssetHandle<T>> LoadCoreAsync<T>(
            AssetId id,
            CancellationToken cancellationToken = default
        )
        {
            CheckAvailable();
            cancellationToken.ThrowIfCancellationRequested();
            if (!_catalog.TryGetValue(id.Value ?? "", out AssetRecord record))
            {
                throw new AssetLoadException(AssetLoadError.UnknownId, "content-id-unknown: " + id);
            }
            if (!_assets.TryGetValue(id.Value, out AssetState asset))
            {
                var dependencies = new List<BundleState>();
                CollectDependencies(
                    _bundles[record.Bundle],
                    new HashSet<string>(),
                    new HashSet<string>(),
                    dependencies
                );
                asset = new AssetState
                {
                    Record = record,
                    Dependencies = dependencies,
                    References = 1,
                };
                foreach (BundleState bundle in dependencies)
                {
                    bundle.References++;
                }
                _assets.Add(record.Id, asset);
                asset.Loading = Share(LoadEntryAsync(asset));
            }
            else
            {
                asset.References++;
            }
            bool transferred = false;
            try
            {
                UnityObject loaded = await AwaitInterestAsync(
                    asset.Loading.Task,
                    cancellationToken
                );
                CheckAvailable();
                cancellationToken.ThrowIfCancellationRequested();
                if (!(loaded is T value))
                {
                    throw new AssetLoadException(
                        AssetLoadError.Unavailable,
                        "content-type-mismatch: " + id
                    );
                }
                AssetHandle<T> handle = CreateHandle(id, value, asset);
                transferred = true;
                return handle;
            }
            finally
            {
                if (!transferred)
                {
                    ReleaseEntry(asset);
                }
            }
        }

        /// <summary>仅命中已加载且类型匹配的资源；成功时取得新的所有权。</summary>
        public bool TryGetLoaded<T>(AssetId id, out AssetHandle<T> handle)
        {
            CheckAvailable();
            if (
                _assets.TryGetValue(id.Value ?? "", out AssetState asset)
                && !asset.IsLoading
                && asset.Value is T value
            )
            {
                asset.References++;
                handle = CreateHandle(id, value, asset);
                return true;
            }
            handle = null;
            return false;
        }

        /// <summary>成功预加载由本服务作用域持有；失败或取消释放本次已取得的资源。</summary>
        public ValueTask PreloadAsync(
            AssetGroupId group,
            CancellationToken cancellationToken = default
        )
        {
            return PreloadCoreAsync(group, cancellationToken).AsValueTask();
        }

        private async UniTask PreloadCoreAsync(
            AssetGroupId group,
            CancellationToken cancellationToken = default
        )
        {
            CheckAvailable();
            var acquired = new List<AssetHandle>();
            bool found = false;
            try
            {
                foreach (AssetRecord record in _catalog.Values)
                {
                    if (record.Group == group.Value)
                    {
                        found = true;
                        acquired.Add(
                            await LoadCoreAsync<UnityObject>(
                                new AssetId(record.Id),
                                cancellationToken
                            )
                        );
                    }
                }
                if (!found)
                {
                    throw new AssetLoadException(AssetLoadError.UnknownId, "content-group-unknown");
                }
                CheckAvailable();
                _preloads.AddRange(acquired);
                acquired.Clear();
            }
            finally
            {
                foreach (AssetHandle handle in acquired)
                {
                    handle.Release(this);
                }
            }
        }

        /// <summary>主线程释放本服务句柄；重复调用无副作用。</summary>
        public ValueTask ReleaseAsync(AssetHandle handle)
        {
            CheckThread();
            if (handle == null)
            {
                throw new ArgumentNullException(nameof(handle));
            }
            handle.Release(this);
            return default;
        }

        /// <summary>实例持有源 Prefab；DisposeAsync 销毁实例后才释放资源。</summary>
        public ValueTask<AssetInstance> InstantiateAsync(
            AssetId id,
            Transform parent = null,
            CancellationToken cancellationToken = default
        )
        {
            return InstantiateCoreAsync(id, parent, cancellationToken).AsValueTask();
        }

        private async UniTask<AssetInstance> InstantiateCoreAsync(
            AssetId id,
            Transform parent = null,
            CancellationToken cancellationToken = default
        )
        {
            AssetHandle<GameObject> handle = await LoadCoreAsync<GameObject>(id, cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var instance = new AssetInstance(
                    UnityObject.Instantiate(handle.Value, parent),
                    this,
                    handle
                );
                _instances.Add(instance);
                return instance;
            }
            catch
            {
                handle.Release(this);
                throw;
            }
        }

        internal void AssertOwnerThread() => CheckThread();

        internal void ForgetInstance(AssetInstance instance) => _instances.Remove(instance);

        private AssetHandle<T> CreateHandle<T>(AssetId id, T value, AssetState asset)
        {
            AssetHandle<T> handle = null;
            handle = new AssetHandle<T>(
                id,
                value,
                this,
                () =>
                {
                    CheckThread();
                    _handles.Remove(handle);
                    ReleaseEntry(asset);
                }
            );
            _handles.Add(handle);
            return handle;
        }

        private async UniTask<UnityObject> LoadEntryAsync(AssetState asset)
        {
            try
            {
                foreach (BundleState bundle in asset.Dependencies)
                {
                    if (bundle.Loading == null)
                    {
                        bundle.Loading = Share(LoadBundleAsync(bundle));
                    }
                    await bundle.Loading.Task;
                }
                AssetBundleRequest request = _bundles[asset.Record.Bundle]
                    .Value.LoadAssetAsync(asset.Record.AssetPath);
                await AwaitOperationAsync(request);
                if (request.asset == null)
                {
                    throw new AssetLoadException(
                        AssetLoadError.Unavailable,
                        "content-asset-missing: " + asset.Record.Id
                    );
                }
                asset.Value = request.asset;
                return asset.Value;
            }
            catch (AssetLoadException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new AssetLoadException(
                    AssetLoadError.InternalFault,
                    "content-asset-load-fault: " + asset.Record.Id,
                    exception
                );
            }
            finally
            {
                asset.IsLoading = false;
                CollectEntry(asset);
            }
        }

        private async UniTask<UnityBundle> LoadBundleAsync(BundleState state)
        {
            string path = Path.Combine(_root, state.Record.Name);
            BundleBytes bytes = null;
            try
            {
                // 校验与加载使用同一份字节，避免文件在校验与原生读取之间改变。
                // 真正的文件 I/O 放在线程池执行；保留 Task.Run，await 后回到调用方 Unity 上下文。
                bytes = await Task.Run(() => ReadBundleBytes(path));
                string hash;
                using (SHA256 sha = SHA256.Create())
                {
                    hash = BitConverter
                        .ToString(sha.ComputeHash(bytes.Value))
                        .Replace("-", "")
                        .ToLowerInvariant();
                }
                if (
                    bytes.Value.LongLength != state.Record.Size
                    || !string.Equals(hash, state.Record.Sha256, StringComparison.OrdinalIgnoreCase)
                )
                {
                    throw new AssetLoadException(
                        AssetLoadError.IntegrityFailure,
                        "content-bundle-integrity: " + state.Record.Name
                    );
                }
                // 即使已有同哈希缓存也先验证此来源，不能让缓存掩盖损坏的安装目录。
                string dependencyIdentity = DependencyIdentity(state);
                bool ownsNativeLoad = false;
                if (!NativeBundles.TryGetValue(state.Record.Name, out NativeBundle native))
                {
                    native = new NativeBundle
                    {
                        Hash = hash,
                        Name = state.Record.Name,
                        DependencyIdentity = dependencyIdentity,
                    };
                    NativeBundles.Add(state.Record.Name, native);
                    ownsNativeLoad = true;
                    native.Loading = Share(LoadNativeBundleAsync(native, bytes));
                }
                else if (native.Hash != hash || native.DependencyIdentity != dependencyIdentity)
                {
                    // Unity 的原生 Bundle 名是进程级身份；不能把另一内容版本绑定到旧依赖。
                    throw new AssetLoadException(
                        AssetLoadError.IncompatibleManifest,
                        "content-version-already-in-use: " + state.Record.Name
                    );
                }
                if (!ownsNativeLoad)
                {
                    // 共享加载不使用此来源的验证副本；保持每个来源都先校验的约束。
                    bytes.Dispose();
                }
                native.References++;
                state.Native = native;
                state.Value = await native.Loading.Task;
                return state.Value;
            }
            catch (AssetLoadException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new AssetLoadException(
                    AssetLoadError.Unavailable,
                    "content-bundle-unavailable: " + state.Record.Name,
                    exception
                );
            }
            finally
            {
                bytes?.Dispose();
            }
        }

        private string DependencyIdentity(BundleState bundle)
        {
            var closure = new List<BundleState>();
            CollectDependencies(bundle, new HashSet<string>(), new HashSet<string>(), closure);
            closure.Sort(
                (left, right) => StringComparer.Ordinal.Compare(left.Record.Name, right.Record.Name)
            );
            var identity = new System.Text.StringBuilder();
            foreach (BundleState dependency in closure)
            {
                identity
                    .Append(dependency.Record.Name.Length)
                    .Append(':')
                    .Append(dependency.Record.Name);
                identity
                    .Append(':')
                    .Append(dependency.Record.Sha256.ToLowerInvariant())
                    .Append(';');
            }
            return identity.ToString();
        }

        private static async UniTask<UnityBundle> LoadNativeBundleAsync(
            NativeBundle native,
            BundleBytes bytes
        )
        {
            try
            {
                AssetBundleCreateRequest request = UnityBundle.LoadFromMemoryAsync(bytes.Value);
                await AwaitOperationAsync(request);
                native.Value = request.assetBundle;
                if (native.Value == null)
                {
                    throw new AssetLoadException(
                        AssetLoadError.Unavailable,
                        "content-native-bundle-load"
                    );
                }
                return native.Value;
            }
            finally
            {
                // Unity 已完成读取（或入口抛错）；只保留原生 Bundle，不保留其输入字节。
                bytes.Dispose();
            }
        }

        private static void ReleaseNativeBundle(BundleState bundle)
        {
            NativeBundle native = bundle.Native;
            bundle.Native = null;
            if (native == null)
            {
                return;
            }
            native.References--;
            if (native.References == 0)
            {
                NativeBundles.Remove(native.Name);
                if (native.Value != null)
                {
                    native.Value.Unload(true);
                }
            }
        }

        private static UniTask AwaitOperationAsync(AsyncOperation operation)
        {
            if (operation.isDone)
            {
                return UniTask.CompletedTask;
            }
            var completion = new UniTaskCompletionSource();
            Action<AsyncOperation> completed = null;
            completed = finished =>
            {
                finished.completed -= completed;
                completion.TrySetResult();
            };
            operation.completed += completed;
            // 注册前后若原生操作刚好完成，也确保回调被移除；TrySetResult 支持重复通知。
            if (operation.isDone)
            {
                completed(operation);
            }
            return completion.Task;
        }

        // 只把完成源存入合并加载缓存；普通 UniTask 可能来自池化状态机，不能被重复 await。
        private static UniTaskCompletionSource<T> Share<T>(UniTask<T> operation)
        {
            var completion = new UniTaskCompletionSource<T>();
            CompleteSharedAsync(operation, completion).Forget();
            return completion;
        }

        private static async UniTask CompleteSharedAsync<T>(
            UniTask<T> operation,
            UniTaskCompletionSource<T> completion
        )
        {
            try
            {
                completion.TrySetResult(await operation);
            }
            catch (OperationCanceledException exception)
            {
                completion.TrySetCanceled(exception.CancellationToken);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }

        private static async UniTask<T> AwaitInterestAsync<T>(
            UniTask<T> shared,
            CancellationToken token
        )
        {
            try
            {
                // 包装器只取消本次等待；它仍观察共享操作的迟到结果及失败。
                return await shared.AttachExternalCancellation(token);
            }
            finally
            {
                // 取消可能来自工作线程，引用计数及 Unity 对象清理必须返回主线程。
                await UniTask.SwitchToMainThread();
            }
        }

        private static async UniTask ObserveAsync(UniTask operation)
        {
            try
            {
                await operation;
            }
            catch
            {
                // 加载失败由仍感兴趣的调用方观察；关闭仅等待全部已启动加载完成。
            }
        }

        private void ReleaseEntry(AssetState asset)
        {
            asset.References--;
            CollectEntry(asset);
        }

        private void CollectEntry(AssetState asset)
        {
            if (asset.References != 0 || asset.IsLoading)
            {
                return;
            }
            _assets.Remove(asset.Record.Id);
            for (int index = asset.Dependencies.Count - 1; index >= 0; index--)
            {
                BundleState bundle = asset.Dependencies[index];
                bundle.References--;
                if (bundle.References == 0)
                {
                    ReleaseNativeBundle(bundle);
                    bundle.Value = null;
                    bundle.Loading = null;
                }
            }
        }

        private void CollectDependencies(
            BundleState bundle,
            HashSet<string> visiting,
            HashSet<string> complete,
            List<BundleState> result
        )
        {
            if (complete.Contains(bundle.Record.Name))
            {
                return;
            }
            if (!visiting.Add(bundle.Record.Name))
            {
                throw InvalidCatalog();
            }
            foreach (string dependency in bundle.Record.Dependencies)
            {
                if (!_bundles.TryGetValue(dependency ?? "", out BundleState child))
                {
                    throw InvalidCatalog();
                }
                CollectDependencies(child, visiting, complete, result);
            }
            visiting.Remove(bundle.Record.Name);
            complete.Add(bundle.Record.Name);
            result.Add(bundle);
        }

        /// <summary>主线程关闭服务，先销毁实例、等待加载收尾再释放所有句柄；重复调用共享结束任务。</summary>
        public ValueTask DisposeAsync()
        {
            CheckThread();
            if (_disposeTask == null)
            {
                _disposed = true;
                // 公共 ValueTask 边界只转换一次，重复释放等待同一个 Task。
                var completion = new UniTaskCompletionSource();
                _disposeTask = completion.Task.AsTask();
                CompleteDisposalAsync(completion).Forget();
            }
            return new ValueTask(_disposeTask);
        }

        private async UniTask CompleteDisposalAsync(UniTaskCompletionSource completion)
        {
            try
            {
                await DisposeCoreAsync();
                completion.TrySetResult();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }

        private async UniTask DisposeCoreAsync()
        {
            foreach (AssetInstance instance in _instances.ToArray())
            {
                await instance.DisposeAsync();
            }
            var pending = new List<UniTask>();
            foreach (AssetState asset in _assets.Values)
            {
                pending.Add(ObserveAsync(asset.Loading.Task));
            }
            await UniTask.WhenAll(pending);
            foreach (AssetHandle handle in new List<AssetHandle>(_handles))
            {
                handle.Release(this);
            }
            _preloads.Clear();
        }

        private void CheckAvailable()
        {
            CheckThread();
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(LocalAssetBundleService));
            }
        }

        private void CheckThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != _threadId)
            {
                throw new InvalidOperationException("content-owner-thread-required");
            }
        }

        private static bool IsSafeFileName(string name) =>
            !string.IsNullOrWhiteSpace(name)
            && name != "."
            && name != ".."
            && name.IndexOfAny(new[] { '/', '\\', ':' }) < 0;

        private static bool IsHash(string value)
        {
            if (value == null || value.Length != 64)
            {
                return false;
            }
            foreach (char character in value)
            {
                if (!Uri.IsHexDigit(character))
                {
                    return false;
                }
            }
            return true;
        }

        private static AssetLoadException InvalidCatalog() =>
            new AssetLoadException(AssetLoadError.IncompatibleManifest, "content-catalog-invalid");
    }
}
