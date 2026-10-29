using System;
using System.Threading;
using System.Threading.Tasks;

namespace AiNative.Client.Assets
{
    /// <summary>稳定逻辑资源标识，不包含路径或传输细节。</summary>
    public readonly struct AssetId : IEquatable<AssetId>
    {
        /// <summary>创建非空逻辑 ID；空白值属于调用方参数错误。</summary>
        public AssetId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("Asset ID cannot be empty.", nameof(value));
            }
            Value = value;
        }

        /// <summary>大小写敏感的逻辑名称；默认结构体不代表有效资源。</summary>
        public string Value { get; }

        /// <summary>按序数比较逻辑名称。</summary>
        public bool Equals(AssetId other) =>
            string.Equals(Value, other.Value, StringComparison.Ordinal);

        /// <inheritdoc />
        public override bool Equals(object obj) => obj is AssetId other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() =>
            Value == null ? 0 : StringComparer.Ordinal.GetHashCode(Value);

        /// <summary>返回逻辑名称；默认值显示为空字符串。</summary>
        public override string ToString() => Value ?? string.Empty;
    }

    /// <summary>构建时定义的预加载分组。</summary>
    public readonly struct AssetGroupId
    {
        /// <summary>创建非空预加载分组名称。</summary>
        public AssetGroupId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("Asset group cannot be empty.", nameof(value));
            }
            Value = value;
        }

        /// <summary>大小写敏感的逻辑名称；默认结构体不代表有效资源。</summary>
        public string Value { get; }
    }

    /// <summary>稳定失败分类；不向业务暴露 Bundle 或文件实现类型。</summary>
    public enum AssetLoadError
    {
        /// <summary>目录中没有资源或分组 ID。</summary>
        UnknownId,

        /// <summary>目录格式、平台或并存版本不兼容。</summary>
        IncompatibleManifest,

        /// <summary>资源不可获取或与请求类型不匹配。</summary>
        Unavailable,

        /// <summary>资源长度或哈希校验失败。</summary>
        IntegrityFailure,

        /// <summary>资源配额不足，供限制容量的实现使用。</summary>
        Quota,

        /// <summary>取消分类；异步 .NET 调用仍以 OperationCanceledException 传播取消。</summary>
        Cancelled,

        /// <summary>实现内部异常，不能安全继续加载。</summary>
        InternalFault,
    }

    /// <summary>资源加载错误，Code 用于稳定的界面错误映射。</summary>
    public sealed class AssetLoadException : Exception
    {
        /// <summary>包装稳定分类与诊断原因；内部异常不应用作业务分支条件。</summary>
        public AssetLoadException(AssetLoadError code, string message, Exception inner = null)
            : base(message, inner)
        {
            Code = code;
        }

        /// <summary>供业务映射的稳定失败分类。</summary>
        public AssetLoadError Code { get; }
    }

    /// <summary>每次获取独立的所有权令牌；释放后不可继续使用资源。</summary>
    public abstract class AssetHandle
    {
        private readonly object _owner;
        private readonly Action _release;
        private bool _released;

        /// <summary>服务绑定所属实例与仅执行一次的释放回调。</summary>
        protected AssetHandle(AssetId id, object owner, Action release)
        {
            Id = id;
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            _release = release ?? throw new ArgumentNullException(nameof(release));
        }

        /// <summary>本次获取的逻辑资源 ID。</summary>
        public AssetId Id { get; }

        /// <summary>所有权是否已经释放；释放后不能继续读取 Value。</summary>
        public bool IsReleased => _released;

        /// <summary>由创建服务在拥有线程调用；重复释放无副作用，其他服务不得释放。</summary>
        public void Release(object owner)
        {
            if (!ReferenceEquals(_owner, owner))
            {
                throw new ArgumentException(
                    "Handle belongs to a different service.",
                    nameof(owner)
                );
            }
            if (_released)
            {
                return;
            }
            _released = true;
            _release();
        }
    }

    /// <summary>类型化资源所有权；构造由资源服务完成。</summary>
    public sealed class AssetHandle<T> : AssetHandle
    {
        private readonly T _value;

        /// <summary>由服务创建独立所有权；调用方不应自行伪造服务句柄。</summary>
        public AssetHandle(AssetId id, T value, object owner, Action release)
            : base(id, owner, release)
        {
            _value = value;
        }

        /// <summary>有效期内的资源对象；释放后抛出 ObjectDisposedException。</summary>
        public T Value
        {
            get
            {
                if (IsReleased)
                {
                    throw new ObjectDisposedException(nameof(AssetHandle<T>));
                }
                return _value;
            }
        }
    }

    /// <summary>资源端口。Unity 实现由主线程调用，成功句柄必须显式释放。</summary>
    public interface IAssetService
    {
        /// <summary>异步取得独立所有权；取消抛出 OperationCanceledException，加载失败使用 AssetLoadException。</summary>
        ValueTask<AssetHandle<T>> LoadAsync<T>(
            AssetId id,
            CancellationToken cancellationToken = default
        );

        /// <summary>仅命中已加载且类型匹配的资源；成功返回新的独立句柄。</summary>
        bool TryGetLoaded<T>(AssetId id, out AssetHandle<T> handle);

        /// <summary>预加载分组，由实现的资源作用域持有；取消不泄漏本次已加载所有权。</summary>
        ValueTask PreloadAsync(AssetGroupId group, CancellationToken cancellationToken = default);

        /// <summary>在服务拥有线程释放本服务的句柄；重复释放无副作用。</summary>
        ValueTask ReleaseAsync(AssetHandle handle);
    }
}
