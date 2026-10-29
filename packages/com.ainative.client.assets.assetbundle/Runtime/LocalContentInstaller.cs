using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace AiNative.Client.Assets.AssetBundle
{
    /// <summary>把应用自己的 StreamingAssets 内容提取为不可变本地目录；不是远端更新入口。</summary>
    public static class LocalContentInstaller
    {
        /// <summary>
        /// 主线程调用。streamingRoot 必须位于本应用 StreamingAssets，destinationRoot 必须位于
        /// persistentDataPath。仅在全部校验成功后发布目录，取消或失败保留旧目录。
        /// </summary>
        public static ValueTask<string> PrepareAsync(
            string streamingRoot,
            string destinationRoot,
            string platform,
            CancellationToken cancellationToken = default
        )
        {
            return PrepareCoreAsync(streamingRoot, destinationRoot, platform, cancellationToken)
                .AsValueTask();
        }

        private static async UniTask<string> PrepareCoreAsync(
            string streamingRoot,
            string destinationRoot,
            string platform,
            CancellationToken cancellationToken
        )
        {
            string source = ValidateSource(streamingRoot);
            string destination = Path.GetFullPath(destinationRoot);
            string privateRoot =
                Path.GetFullPath(Application.persistentDataPath)
                    .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!destination.StartsWith(privateRoot, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "content-install-requires-private-directory",
                    nameof(destinationRoot)
                );
            }
            cancellationToken.ThrowIfCancellationRequested();
            byte[] catalogBytes = await ReadBundledAsync(
                source + "/catalog.json",
                cancellationToken
            );
            string identity = Hash(catalogBytes);
            string complete = Path.Combine(destination, identity);
            Directory.CreateDirectory(destination);
            string temporary = Path.Combine(
                destination,
                ".install-" + Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(temporary);
            try
            {
                File.WriteAllBytes(Path.Combine(temporary, "catalog.json"), catalogBytes);
                // 复用服务的目录结构检查，在任何 Bundle 路径用于读取之前拒绝越界和依赖环。
                var validation = new LocalAssetBundleService(temporary, platform);
                await validation.DisposeAsync();
                BundleCatalog catalog = JsonUtility.FromJson<BundleCatalog>(
                    System.Text.Encoding.UTF8.GetString(catalogBytes)
                );
                if (Directory.Exists(complete))
                {
                    ValidateInstalled(complete, catalog, identity);
                    return complete;
                }
                foreach (BundleRecord bundle in catalog.Bundles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    byte[] bytes = await ReadBundledAsync(
                        source + "/" + bundle.Name,
                        cancellationToken
                    );
                    Verify(bundle, bytes);
                    File.WriteAllBytes(Path.Combine(temporary, bundle.Name), bytes);
                }
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    Directory.Move(temporary, complete);
                }
                catch (IOException) when (Directory.Exists(complete))
                {
                    // 同一应用中的另一实例可能先完成相同目录，只接受完全相同且有效的产物。
                    ValidateInstalled(complete, catalog, identity);
                }
                return complete;
            }
            finally
            {
                if (Directory.Exists(temporary))
                {
                    Directory.Delete(temporary, true);
                }
            }
        }

        private static string ValidateSource(string source)
        {
            string normalized = (source ?? "").Replace('\\', '/').TrimEnd('/');
            string packagedRoot = Application.streamingAssetsPath.Replace('\\', '/').TrimEnd('/');
            if (
                normalized != packagedRoot
                && !normalized.StartsWith(packagedRoot + "/", StringComparison.Ordinal)
            )
            {
                throw new ArgumentException("content-source-must-be-packaged", nameof(source));
            }
            foreach (string segment in normalized.Substring(packagedRoot.Length).Split('/'))
            {
                if (
                    segment == ".."
                    || segment == "."
                    || segment.Contains("%")
                    || segment.Contains("?")
                    || segment.Contains("#")
                )
                {
                    throw new ArgumentException("content-source-path-invalid", nameof(source));
                }
            }
            return normalized;
        }

        private static async UniTask<byte[]> ReadBundledAsync(string path, CancellationToken token)
        {
            string uri = path.Contains("://") ? path : new Uri(Path.GetFullPath(path)).AbsoluteUri;
            using (UnityWebRequest request = UnityWebRequest.Get(uri))
            {
                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    if (token.IsCancellationRequested)
                    {
                        request.Abort();
                        token.ThrowIfCancellationRequested();
                    }
                    await UniTask.Yield(PlayerLoopTiming.Update);
                }
                token.ThrowIfCancellationRequested();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    throw new AssetLoadException(
                        AssetLoadError.Unavailable,
                        "packaged-content-unavailable"
                    );
                }
                return request.downloadHandler.data;
            }
        }

        private static void ValidateInstalled(
            string directory,
            BundleCatalog catalog,
            string identity
        )
        {
            if (Hash(File.ReadAllBytes(Path.Combine(directory, "catalog.json"))) != identity)
            {
                throw new AssetLoadException(
                    AssetLoadError.IntegrityFailure,
                    "installed-catalog-mismatch"
                );
            }
            foreach (BundleRecord bundle in catalog.Bundles)
            {
                Verify(bundle, File.ReadAllBytes(Path.Combine(directory, bundle.Name)));
            }
        }

        private static void Verify(BundleRecord bundle, byte[] bytes)
        {
            if (
                bytes.LongLength != bundle.Size
                || !string.Equals(Hash(bytes), bundle.Sha256, StringComparison.OrdinalIgnoreCase)
            )
            {
                throw new AssetLoadException(
                    AssetLoadError.IntegrityFailure,
                    "packaged-content-integrity: " + bundle.Name
                );
            }
        }

        private static string Hash(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return BitConverter
                    .ToString(sha.ComputeHash(bytes))
                    .Replace("-", "")
                    .ToLowerInvariant();
            }
        }
    }
}
