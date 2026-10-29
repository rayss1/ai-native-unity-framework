using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AiNative.Client.Assets.AssetBundle.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace AiNative.Client.Assets.AssetBundle.Tests
{
    public sealed class LocalAssetBundleTests
    {
        private string _assetDirectory;
        private string _output;
        private string _platform;
        private LocalAssetBundleService _service;

        [OneTimeSetUp]
        public void BuildRealContent()
        {
            _assetDirectory = "Assets/AiNativeBundleTests_" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(_assetDirectory);
            File.WriteAllText(_assetDirectory + "/first.txt", "first-resource");
            File.WriteAllText(_assetDirectory + "/second.txt", "second-resource");
            AssetDatabase.Refresh();
            var texture = new Texture2D(2, 2);
            AssetDatabase.CreateAsset(texture, _assetDirectory + "/shared.asset");
            Shader shader = Shader.Find("Unlit/Texture");
            Assert.That(shader, Is.Not.Null);
            var firstMaterial = new Material(shader) { mainTexture = texture };
            var secondMaterial = new Material(shader) { mainTexture = texture };
            AssetDatabase.CreateAsset(firstMaterial, _assetDirectory + "/first.mat");
            AssetDatabase.CreateAsset(secondMaterial, _assetDirectory + "/second.mat");
            var prefab = new GameObject("BundleOwnedInstance");
            try
            {
                PrefabUtility.SaveAsPrefabAsset(prefab, _assetDirectory + "/instance.prefab");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(prefab);
            }
            AssetDatabase.SaveAssets();
            _output = Path.Combine(
                Path.GetTempPath(),
                "ainative-bundles-" + Guid.NewGuid().ToString("N")
            );
            _platform = EditorUserBuildSettings.activeBuildTarget.ToString();
            LocalAssetBundleBuilder.Build(
                _output,
                EditorUserBuildSettings.activeBuildTarget,
                new BundleAssetDefinition(
                    "instance",
                    _assetDirectory + "/instance.prefab",
                    "instances",
                    "instances"
                ),
                new BundleAssetDefinition("first", _assetDirectory + "/first.txt", "texts"),
                new BundleAssetDefinition("second", _assetDirectory + "/second.txt", "texts"),
                new BundleAssetDefinition(
                    "texture",
                    _assetDirectory + "/shared.asset",
                    "shared",
                    "materials"
                ),
                new BundleAssetDefinition(
                    "material-a",
                    _assetDirectory + "/first.mat",
                    "material-a",
                    "materials"
                ),
                new BundleAssetDefinition(
                    "material-b",
                    _assetDirectory + "/second.mat",
                    "material-b",
                    "materials"
                )
            );
        }

        [OneTimeTearDown]
        public void RemoveOwnedArtifacts()
        {
            if (!string.IsNullOrEmpty(_assetDirectory))
            {
                AssetDatabase.DeleteAsset(_assetDirectory);
            }
            if (Directory.Exists(_output))
            {
                Directory.Delete(_output, true);
            }
        }

        [UnityTearDown]
        public IEnumerator StopService()
        {
            if (_service != null)
            {
                Task task = _service.DisposeAsync().AsTask();
                while (!task.IsCompleted)
                {
                    yield return null;
                }
                task.GetAwaiter().GetResult();
                _service = null;
            }
        }

        [UnityTest]
        public IEnumerator ConcurrentOwnersShareBundleAndReleaseIndependently()
        {
            return Run(async () =>
            {
                _service = new LocalAssetBundleService(_output, _platform);
                Task<AssetHandle<TextAsset>> first = _service
                    .LoadAsync<TextAsset>(new AssetId("first"))
                    .AsTask();
                Task<AssetHandle<TextAsset>> second = _service
                    .LoadAsync<TextAsset>(new AssetId("second"))
                    .AsTask();
                AssetHandle<TextAsset> a = await first;
                AssetHandle<TextAsset> b = await second;
                Assert.That(a.Value.text, Is.EqualTo("first-resource"));
                Assert.That(_service.LoadedBundleCount, Is.EqualTo(1));
                Assert.That(
                    _service.TryGetLoaded(new AssetId("first"), out AssetHandle<TextAsset> copy),
                    Is.True
                );
                await _service.ReleaseAsync(a);
                await _service.ReleaseAsync(a);
                Assert.That(copy.Value.text, Is.EqualTo("first-resource"));
                await _service.ReleaseAsync(copy);
                Assert.That(b.Value.text, Is.EqualTo("second-resource"));
                await _service.ReleaseAsync(b);
                Assert.That(_service.ActiveHandleCount, Is.Zero);
                Assert.That(_service.LoadedBundleCount, Is.Zero);
            });
        }

        [UnityTest]
        public IEnumerator TwoServicesShareNativeContentWithIndependentLifetimes()
        {
            return Run(async () =>
            {
                _service = new LocalAssetBundleService(_output, _platform);
                var other = new LocalAssetBundleService(_output, _platform);
                try
                {
                    Task<AssetHandle<Material>> firstTask = _service
                        .LoadAsync<Material>(new AssetId("material-a"))
                        .AsTask();
                    Task<AssetHandle<Material>> secondTask = other
                        .LoadAsync<Material>(new AssetId("material-a"))
                        .AsTask();
                    AssetHandle<Material> first = await firstTask;
                    AssetHandle<Material> second = await secondTask;
                    Assert.That(first.Value, Is.SameAs(second.Value));
                    Assert.That(_service.ActiveHandleCount, Is.EqualTo(1));
                    Assert.That(other.ActiveHandleCount, Is.EqualTo(1));
                    await _service.DisposeAsync();
                    Assert.That(first.IsReleased, Is.True);
                    Assert.That(second.Value.mainTexture, Is.Not.Null);
                    Assert.That(other.LoadedBundleCount, Is.EqualTo(2));
                    await other.ReleaseAsync(second);
                    Assert.That(other.LoadedBundleCount, Is.Zero);
                    Assert.That(other.ActiveHandleCount, Is.Zero);
                }
                finally
                {
                    await other.DisposeAsync();
                }
            });
        }

        [UnityTest]
        public IEnumerator ConcurrentCatalogCannotRebindLoadedBundleDependencies()
        {
            return Run(async () =>
            {
                string alternate = Path.Combine(
                    Path.GetTempPath(),
                    "ainative-alternate-" + Guid.NewGuid().ToString("N")
                );
                Directory.CreateDirectory(alternate);
                LocalAssetBundleService other = null;
                try
                {
                    BundleCatalog catalog = JsonUtility.FromJson<BundleCatalog>(
                        File.ReadAllText(Path.Combine(_output, "catalog.json"))
                    );
                    foreach (BundleRecord bundle in catalog.Bundles)
                    {
                        File.Copy(
                            Path.Combine(_output, bundle.Name),
                            Path.Combine(alternate, bundle.Name)
                        );
                        if (bundle.Name == "material-a")
                        {
                            bundle.Dependencies = new[] { "shared", "texts" };
                        }
                    }
                    File.WriteAllText(
                        Path.Combine(alternate, "catalog.json"),
                        JsonUtility.ToJson(catalog)
                    );
                    _service = new LocalAssetBundleService(_output, _platform);
                    AssetHandle<Material> original = await _service.LoadAsync<Material>(
                        new AssetId("material-a")
                    );
                    other = new LocalAssetBundleService(alternate, _platform);
                    try
                    {
                        await other.LoadAsync<Material>(new AssetId("material-a"));
                        Assert.Fail(
                            "Native content must not be shared across different dependency identities."
                        );
                    }
                    catch (AssetLoadException exception)
                    {
                        Assert.That(
                            exception.Code,
                            Is.EqualTo(AssetLoadError.IncompatibleManifest)
                        );
                    }
                    Assert.That(original.Value.mainTexture, Is.Not.Null);
                    Assert.That(other.ActiveHandleCount, Is.Zero);
                    Assert.That(other.LoadedBundleCount, Is.Zero);
                }
                finally
                {
                    if (other != null)
                    {
                        await other.DisposeAsync();
                    }
                    Directory.Delete(alternate, true);
                }
            });
        }

        [UnityTest]
        public IEnumerator SharedDependencyOutlivesOneOfItsConsumers()
        {
            return Run(async () =>
            {
                _service = new LocalAssetBundleService(_output, _platform);
                AssetHandle<Material> first = await _service.LoadAsync<Material>(
                    new AssetId("material-a")
                );
                AssetHandle<Material> second = await _service.LoadAsync<Material>(
                    new AssetId("material-b")
                );
                Assert.That(first.Value.mainTexture, Is.SameAs(second.Value.mainTexture));
                Assert.That(_service.LoadedBundleCount, Is.EqualTo(3));
                await _service.ReleaseAsync(first);
                Assert.That(_service.LoadedBundleCount, Is.EqualTo(2));
                Assert.That(second.Value.mainTexture, Is.Not.Null);
                await _service.ReleaseAsync(second);
                Assert.That(_service.LoadedBundleCount, Is.Zero);
            });
        }

        [UnityTest]
        public IEnumerator CancelledInterestDoesNotCancelSharedLoad()
        {
            return Run(async () =>
            {
                _service = new LocalAssetBundleService(_output, _platform);
                using (var cancellation = new CancellationTokenSource())
                {
                    Task<AssetHandle<TextAsset>> abandoned = _service
                        .LoadAsync<TextAsset>(new AssetId("first"), cancellation.Token)
                        .AsTask();
                    Task<AssetHandle<TextAsset>> interested = _service
                        .LoadAsync<TextAsset>(new AssetId("first"))
                        .AsTask();
                    cancellation.Cancel();
                    try
                    {
                        await abandoned;
                        Assert.Fail("Cancelled acquisition must not return a handle.");
                    }
                    catch (OperationCanceledException) { }
                    AssetHandle<TextAsset> handle = await interested;
                    Assert.That(handle.Value.text, Is.EqualTo("first-resource"));
                    await _service.ReleaseAsync(handle);
                    Assert.That(_service.ActiveHandleCount, Is.Zero);
                    Assert.That(_service.LoadedBundleCount, Is.Zero);
                }
            });
        }

        [UnityTest]
        public IEnumerator PreloadAndDisposeReleaseAllTokens()
        {
            return Run(async () =>
            {
                _service = new LocalAssetBundleService(_output, _platform);
                await _service.PreloadAsync(new AssetGroupId("default"));
                Assert.That(_service.ActiveHandleCount, Is.EqualTo(2));
                await _service.DisposeAsync();
                await _service.DisposeAsync();
                Assert.That(_service.ActiveHandleCount, Is.Zero);
                Assert.That(_service.LoadedBundleCount, Is.Zero);
            });
        }

        [Test]
        public void WrongPlatformIsRejectedBeforeBundleLoad()
        {
            AssetLoadException error = Assert.Throws<AssetLoadException>(() =>
                new LocalAssetBundleService(_output, "not-a-platform")
            );
            Assert.That(error.Code, Is.EqualTo(AssetLoadError.IncompatibleManifest));
        }

        [UnityTest]
        public IEnumerator CorruptedBundleCannotBecomeVisible()
        {
            return Run(async () =>
            {
                string copied = Path.Combine(
                    Path.GetTempPath(),
                    "ainative-corrupt-" + Guid.NewGuid().ToString("N")
                );
                Directory.CreateDirectory(copied);
                try
                {
                    File.Copy(
                        Path.Combine(_output, "catalog.json"),
                        Path.Combine(copied, "catalog.json")
                    );
                    File.WriteAllText(Path.Combine(copied, "texts"), "corrupt");
                    _service = new LocalAssetBundleService(copied, _platform);
                    try
                    {
                        await _service.LoadAsync<TextAsset>(new AssetId("first"));
                        Assert.Fail("Corrupt content must fail.");
                    }
                    catch (AssetLoadException exception)
                    {
                        Assert.That(exception.Code, Is.EqualTo(AssetLoadError.IntegrityFailure));
                    }
                    Assert.That(_service.ActiveHandleCount, Is.Zero);
                    Assert.That(_service.LoadedBundleCount, Is.Zero);
                    await _service.DisposeAsync();
                }
                finally
                {
                    Directory.Delete(copied, true);
                }
            });
        }

        [UnityTest]
        public IEnumerator RepeatedInstanceScopesReturnToBaseline()
        {
            return Run(async () =>
            {
                _service = new LocalAssetBundleService(_output, _platform);
                for (int iteration = 0; iteration < 20; iteration++)
                {
                    AssetInstance instance = await _service.InstantiateAsync(
                        new AssetId("instance")
                    );
                    Assert.That(instance.Value, Is.Not.Null);
                    Assert.That(_service.ActiveHandleCount, Is.EqualTo(1));
                    await instance.DisposeAsync();
                    await instance.DisposeAsync();
                    Assert.That(instance.Value == null, Is.True);
                    Assert.That(_service.ActiveHandleCount, Is.Zero);
                    Assert.That(_service.LoadedBundleCount, Is.Zero);
                }
            });
        }

        [Test]
        public void CatalogRejectsCyclesBeforeLoading()
        {
            string directory = Path.Combine(
                Path.GetTempPath(),
                "ainative-cycle-" + Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(directory);
            try
            {
                BundleCatalog catalog = JsonUtility.FromJson<BundleCatalog>(
                    File.ReadAllText(Path.Combine(_output, "catalog.json"))
                );
                catalog.Bundles[0].Dependencies = new[] { catalog.Bundles[0].Name };
                File.WriteAllText(
                    Path.Combine(directory, "catalog.json"),
                    JsonUtility.ToJson(catalog)
                );
                AssetLoadException error = Assert.Throws<AssetLoadException>(() =>
                    new LocalAssetBundleService(directory, _platform)
                );
                Assert.That(error.Code, Is.EqualTo(AssetLoadError.IncompatibleManifest));
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [UnityTest]
        public IEnumerator UnknownIdAndWrongTypeNeverLeakOwnership()
        {
            return Run(async () =>
            {
                _service = new LocalAssetBundleService(_output, _platform);
                try
                {
                    await _service.LoadAsync<TextAsset>(new AssetId("missing"));
                    Assert.Fail("Unknown ID must fail.");
                }
                catch (AssetLoadException exception)
                {
                    Assert.That(exception.Code, Is.EqualTo(AssetLoadError.UnknownId));
                }
                try
                {
                    await _service.LoadAsync<Material>(new AssetId("first"));
                    Assert.Fail("Wrong type must fail.");
                }
                catch (AssetLoadException exception)
                {
                    Assert.That(exception.Code, Is.EqualTo(AssetLoadError.Unavailable));
                }
                Assert.That(_service.ActiveHandleCount, Is.Zero);
                Assert.That(_service.LoadedBundleCount, Is.Zero);
            });
        }

        [UnityTest]
        public IEnumerator PackagedInstallerPublishesVerifiedDirectoryAndReusesIt()
        {
            return Run(async () =>
            {
                string name = "AiNativeInstallTests_" + Guid.NewGuid().ToString("N");
                string source = Path.Combine(Application.streamingAssetsPath, name);
                string destination = Path.Combine(Application.persistentDataPath, name);
                Directory.CreateDirectory(source);
                try
                {
                    BundleCatalog catalog = JsonUtility.FromJson<BundleCatalog>(
                        File.ReadAllText(Path.Combine(_output, "catalog.json"))
                    );
                    File.Copy(
                        Path.Combine(_output, "catalog.json"),
                        Path.Combine(source, "catalog.json")
                    );
                    foreach (BundleRecord bundle in catalog.Bundles)
                    {
                        File.Copy(
                            Path.Combine(_output, bundle.Name),
                            Path.Combine(source, bundle.Name)
                        );
                    }
                    string installed = await LocalContentInstaller.PrepareAsync(
                        source,
                        destination,
                        _platform
                    );
                    Assert.That(File.Exists(Path.Combine(installed, "catalog.json")), Is.True);
                    Assert.That(
                        await LocalContentInstaller.PrepareAsync(source, destination, _platform),
                        Is.EqualTo(installed)
                    );
                    File.WriteAllText(Path.Combine(installed, "texts"), "corrupt");
                    try
                    {
                        await LocalContentInstaller.PrepareAsync(source, destination, _platform);
                        Assert.Fail("Existing corrupt install must not be reused.");
                    }
                    catch (AssetLoadException exception)
                    {
                        Assert.That(exception.Code, Is.EqualTo(AssetLoadError.IntegrityFailure));
                    }
                }
                finally
                {
                    if (Directory.Exists(source))
                    {
                        Directory.Delete(source, true);
                    }
                    if (File.Exists(source + ".meta"))
                    {
                        File.Delete(source + ".meta");
                    }
                    if (Directory.Exists(destination))
                    {
                        Directory.Delete(destination, true);
                    }
                }
            });
        }

        [Test]
        public void ManagedBuildRefusesUnknownOutputWithoutRemovingIt()
        {
            string directory = Path.Combine(
                Path.GetTempPath(),
                "ainative-unknown-" + Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(directory);
            string unknown = Path.Combine(directory, "user-file.txt");
            File.WriteAllText(unknown, "preserve");
            try
            {
                Assert.Throws<InvalidOperationException>(() =>
                    LocalAssetBundleBuilder.BuildManaged(
                        directory,
                        EditorUserBuildSettings.activeBuildTarget,
                        new BundleAssetDefinition("first", _assetDirectory + "/first.txt", "texts")
                    )
                );
                Assert.That(File.ReadAllText(unknown), Is.EqualTo("preserve"));
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [UnityTest]
        public IEnumerator CompletedNativeTaskDoesNotRetainVerifiedInputBytes()
        {
            return Run(async () =>
            {
                object buffer = typeof(LocalAssetBundleService)
                    .GetMethod("ReadBundleBytes", BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, new object[] { Path.Combine(_output, "texts") });
                WeakReference input = ObserveInputBytes(buffer);
                Type nativeType = typeof(LocalAssetBundleService).GetNestedType(
                    "NativeBundle",
                    BindingFlags.NonPublic
                );
                object native = Activator.CreateInstance(nativeType, true);
                var loading =
                    (Task<UnityEngine.AssetBundle>)
                        typeof(LocalAssetBundleService)
                            .GetMethod(
                                "LoadNativeBundleAsync",
                                BindingFlags.Static | BindingFlags.NonPublic
                            )
                            .Invoke(null, new object[] { native, buffer });
                UnityEngine.AssetBundle bundle = null;
                try
                {
                    bundle = await loading;
                    Assert.That(
                        bundle.LoadAsset<TextAsset>(_assetDirectory + "/first.txt").text,
                        Is.EqualTo("first-resource")
                    );
                    Assert.That(
                        buffer.GetType().GetField("Value").GetValue(buffer),
                        Is.Null,
                        "原生加载结束必须主动断开验证字节，即使完成 Task 仍被持有。"
                    );
                    // 让原生完成回调退栈；弱引用在独立无内联方法创建，避免测试局部变量保留输入。
                    await Task.Yield();
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    Assert.That(
                        input.IsAlive,
                        Is.False,
                        "Bundle/已完成 Task/容器仍存活时，输入字节也应可回收。"
                    );
                    GC.KeepAlive(loading);
                    GC.KeepAlive(buffer);
                    GC.KeepAlive(native);
                }
                finally
                {
                    if (bundle != null)
                    {
                        bundle.Unload(true);
                    }
                    ((IDisposable)buffer).Dispose();
                }
            });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference ObserveInputBytes(object buffer)
        {
            return new WeakReference(buffer.GetType().GetField("Value").GetValue(buffer));
        }

        private static IEnumerator Run(Func<Task> action)
        {
            Task task = action();
            while (!task.IsCompleted)
            {
                yield return null;
            }
            task.GetAwaiter().GetResult();
        }
    }
}
