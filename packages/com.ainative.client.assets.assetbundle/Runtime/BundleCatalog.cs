using System;

namespace AiNative.Client.Assets.AssetBundle
{
    /// <summary>随应用交付的目录格式；不接受在线目录替换。</summary>
    [Serializable]
    public sealed class BundleCatalog
    {
        /// <summary>目录格式版本，当前仅接受 1。</summary>
        public int Version = 1;

        /// <summary>Unity BuildTarget 名称，必须与运行目标一致。</summary>
        public string Platform;

        /// <summary>全部 Bundle 的完整性与依赖记录。</summary>
        public BundleRecord[] Bundles = Array.Empty<BundleRecord>();

        /// <summary>逻辑资源到 Bundle 内路径的映射。</summary>
        public AssetRecord[] Assets = Array.Empty<AssetRecord>();
    }

    /// <summary>构建输出的依赖与完整性记录，字段是持久化格式。</summary>
    [Serializable]
    public sealed class BundleRecord
    {
        /// <summary>目录内的 Bundle 文件名，不允许越界路径。</summary>
        public string Name;

        /// <summary>构建产物长度，单位为字节。</summary>
        public long Size;

        /// <summary>产物完整字节的 SHA-256 十六进制摘要。</summary>
        public string Sha256;

        /// <summary>直接依赖 Bundle 名称；整个目录必须无环。</summary>
        public string[] Dependencies = Array.Empty<string>();
    }

    /// <summary>逻辑 ID 到 Bundle 内部路径的构建映射。</summary>
    [Serializable]
    public sealed class AssetRecord
    {
        /// <summary>大小写敏感的稳定逻辑 ID。</summary>
        public string Id;

        /// <summary>所属 Bundle 文件名。</summary>
        public string Bundle;

        /// <summary>Bundle 内的源资产路径。</summary>
        public string AssetPath;

        /// <summary>预加载分组名称。</summary>
        public string Group;
    }
}
