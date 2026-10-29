using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;

namespace AiNative.Client.Assets.AssetBundle.Editor
{
    /// <summary>应用显式声明的资源项；Bundle 与 Group 均为稳定构建分组。</summary>
    public sealed class BundleAssetDefinition
    {
        /// <summary>声明一项显式资源；构建时验证 ID、路径、分组与 Bundle 名。</summary>
        public BundleAssetDefinition(
            string id,
            string assetPath,
            string bundle,
            string group = "default"
        )
        {
            Id = id;
            AssetPath = assetPath;
            Bundle = bundle;
            Group = group;
        }

        /// <summary>稳定逻辑名称。</summary>
        public string Id { get; }

        /// <summary>Assets 下的源资源路径。</summary>
        public string AssetPath { get; }

        /// <summary>全小写且不包含目录分隔符的 Bundle 名。</summary>
        public string Bundle { get; }

        /// <summary>预加载分组名称。</summary>
        public string Group { get; }
    }

    /// <summary>显式构建随包内容；不修改资产 importer 的 Bundle 标签。</summary>
    public static class LocalAssetBundleBuilder
    {
        /// <summary>输出目录必须为空，避免覆盖未知产物；校验全部成功后最后写入 catalog.json。</summary>
        public static BundleCatalog Build(
            string outputDirectory,
            BuildTarget target,
            params BundleAssetDefinition[] assets
        )
        {
            if (assets == null || assets.Length == 0)
            {
                throw new ArgumentException("content-build-empty", nameof(assets));
            }
            string output = Path.GetFullPath(outputDirectory);
            if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            {
                throw new InvalidOperationException("content-output-must-be-empty");
            }
            var identifiers = new HashSet<string>(StringComparer.Ordinal);
            var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (BundleAssetDefinition asset in assets)
            {
                if (
                    asset == null
                    || string.IsNullOrWhiteSpace(asset.Id)
                    || !identifiers.Add(asset.Id)
                    || string.IsNullOrWhiteSpace(asset.Group)
                    || !SafeBundleName(asset.Bundle)
                )
                {
                    throw new ArgumentException("content-definition-invalid");
                }
                if (
                    string.IsNullOrWhiteSpace(asset.AssetPath)
                    || !asset.AssetPath.StartsWith("Assets/", StringComparison.Ordinal)
                    || AssetDatabase.LoadMainAssetAtPath(asset.AssetPath) == null
                    || AssetDatabase.IsValidFolder(asset.AssetPath)
                    || asset.AssetPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                    || !paths.TryAdd(asset.AssetPath, asset.Bundle)
                )
                {
                    throw new ArgumentException(
                        "content-asset-invalid-or-duplicate: " + asset.AssetPath
                    );
                }
            }
            ValidateImplicitDependencies(assets, paths);
            AssetBundleBuild[] builds = assets
                .GroupBy(asset => asset.Bundle)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => new AssetBundleBuild
                {
                    assetBundleName = group.Key,
                    assetNames = group
                        .Select(asset => asset.AssetPath)
                        .OrderBy(path => path, StringComparer.Ordinal)
                        .ToArray(),
                })
                .ToArray();
            Directory.CreateDirectory(output);
            AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(
                output,
                builds,
                BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.StrictMode,
                target
            );
            if (manifest == null)
            {
                throw new InvalidOperationException("content-build-failed");
            }
            var catalog = new BundleCatalog { Platform = target.ToString() };
            var records = new List<BundleRecord>();
            foreach (
                string name in manifest
                    .GetAllAssetBundles()
                    .OrderBy(name => name, StringComparer.Ordinal)
            )
            {
                string path = Path.Combine(output, name);
                string hash;
                using (var stream = File.OpenRead(path))
                using (SHA256 sha = SHA256.Create())
                {
                    hash = BitConverter
                        .ToString(sha.ComputeHash(stream))
                        .Replace("-", "")
                        .ToLowerInvariant();
                }
                records.Add(
                    new BundleRecord
                    {
                        Name = name,
                        Size = new FileInfo(path).Length,
                        Sha256 = hash,
                        Dependencies = manifest
                            .GetDirectDependencies(name)
                            .OrderBy(dependency => dependency, StringComparer.Ordinal)
                            .ToArray(),
                    }
                );
            }
            catalog.Bundles = records.ToArray();
            catalog.Assets = assets
                .OrderBy(asset => asset.Id, StringComparer.Ordinal)
                .Select(asset => new AssetRecord
                {
                    Id = asset.Id,
                    Bundle = asset.Bundle,
                    AssetPath = asset.AssetPath,
                    Group = asset.Group,
                })
                .ToArray();
            File.WriteAllText(
                Path.Combine(output, "catalog.json"),
                JsonUtility.ToJson(catalog, true)
            );
            return catalog;
        }

        [Serializable]
        private sealed class ManagedOutput
        {
            public string[] Files;
        }

        /// <summary>可重复构建仅替换由此入口创建的目录；发现额外文件时拒绝覆盖。</summary>
        public static BundleCatalog BuildManaged(
            string outputDirectory,
            BuildTarget target,
            params BundleAssetDefinition[] assets
        )
        {
            string output = Path.GetFullPath(outputDirectory);
            string parent = Path.GetDirectoryName(output);
            Directory.CreateDirectory(parent);
            VerifyManagedOutput(output);
            string stagingRoot = Path.Combine(
                Path.GetDirectoryName(Application.dataPath),
                "Library",
                "AiNativeContentBuild"
            );
            Directory.CreateDirectory(stagingRoot);
            string temporary = Path.Combine(stagingRoot, "build-" + Guid.NewGuid().ToString("N"));
            string backup = Path.Combine(
                parent,
                ".ainative-backup-" + Guid.NewGuid().ToString("N")
            );
            try
            {
                BundleCatalog catalog = Build(temporary, target, assets);
                var ownership = new ManagedOutput
                {
                    Files = Directory
                        .GetFiles(temporary)
                        .Select(Path.GetFileName)
                        .OrderBy(name => name, StringComparer.Ordinal)
                        .ToArray(),
                };
                File.WriteAllText(
                    Path.Combine(temporary, ".ainative-content-output.json"),
                    JsonUtility.ToJson(ownership, true)
                );
                VerifyManagedOutput(output);
                bool movedPrevious = Directory.Exists(output);
                if (movedPrevious)
                {
                    Directory.Move(output, backup);
                }
                try
                {
                    Directory.Move(temporary, output);
                }
                catch
                {
                    if (movedPrevious)
                    {
                        Directory.Move(backup, output);
                    }
                    throw;
                }
                if (movedPrevious)
                {
                    Directory.Delete(backup, true);
                }
                return catalog;
            }
            finally
            {
                if (Directory.Exists(temporary))
                {
                    Directory.Delete(temporary, true);
                }
            }
        }

        private static void VerifyManagedOutput(string directory)
        {
            if (!Directory.Exists(directory))
            {
                return;
            }
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                return;
            }
            string marker = Path.Combine(directory, ".ainative-content-output.json");
            if (!File.Exists(marker) || Directory.EnumerateDirectories(directory).Any())
            {
                throw new InvalidOperationException("content-output-not-owned");
            }
            ManagedOutput ownership = JsonUtility.FromJson<ManagedOutput>(File.ReadAllText(marker));
            if (ownership == null || ownership.Files == null)
            {
                throw new InvalidOperationException("content-output-invalid-ownership");
            }
            var expected = new HashSet<string>(ownership.Files, StringComparer.Ordinal);
            expected.Add(".ainative-content-output.json");
            // Unity 会为输出目录中的文件生成 .meta；它们也是本次构建产物的伴随文件。
            foreach (string name in expected.ToArray())
            {
                expected.Add(name + ".meta");
            }
            foreach (string path in Directory.GetFiles(directory))
            {
                if (!expected.Contains(Path.GetFileName(path)))
                {
                    throw new InvalidOperationException(
                        "content-output-has-unknown-file: " + Path.GetFileName(path)
                    );
                }
            }
        }

        private static bool SafeBundleName(string name)
        {
            if (
                string.IsNullOrWhiteSpace(name)
                || name != name.ToLowerInvariant()
                || name == "."
                || name == ".."
            )
            {
                return false;
            }
            return name.All(character =>
                (character >= 'a' && character <= 'z')
                || (character >= '0' && character <= '9')
                || character == '-'
                || character == '_'
                || character == '.'
            );
        }

        private static void ValidateImplicitDependencies(
            BundleAssetDefinition[] assets,
            Dictionary<string, string> explicitAssets
        )
        {
            var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (BundleAssetDefinition asset in assets)
            {
                foreach (string dependency in AssetDatabase.GetDependencies(asset.AssetPath, true))
                {
                    if (
                        !dependency.StartsWith("Assets/", StringComparison.Ordinal)
                        || explicitAssets.ContainsKey(dependency)
                        || AssetDatabase.GetMainAssetTypeAtPath(dependency) == typeof(MonoScript)
                    )
                    {
                        continue;
                    }
                    if (owners.TryGetValue(dependency, out string owner) && owner != asset.Bundle)
                    {
                        throw new InvalidOperationException(
                            "content-shared-dependency-requires-explicit-bundle: " + dependency
                        );
                    }
                    owners[dependency] = asset.Bundle;
                }
            }
        }
    }
}
