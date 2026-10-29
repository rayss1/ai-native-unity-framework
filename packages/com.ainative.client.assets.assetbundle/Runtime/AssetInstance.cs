using System;
using System.Threading.Tasks;
using UnityEngine;

namespace AiNative.Client.Assets.AssetBundle
{
    /// <summary>主线程拥有的实例租约；应先释放实例再关闭资源服务。</summary>
    public sealed class AssetInstance : IAsyncDisposable
    {
        private readonly LocalAssetBundleService _owner;
        private readonly AssetHandle<GameObject> _prefab;
        private Task _disposeTask;

        internal AssetInstance(
            GameObject value,
            LocalAssetBundleService owner,
            AssetHandle<GameObject> prefab
        )
        {
            Value = value;
            _owner = owner;
            _prefab = prefab;
        }

        /// <summary>租约拥有的实例；释放完成后为 Unity 已销毁对象。</summary>
        public GameObject Value { get; }

        /// <summary>主线程销毁实例并等待其真正销毁后释放 Prefab 句柄；可重复调用。</summary>
        public ValueTask DisposeAsync()
        {
            _owner.AssertOwnerThread();
            if (_disposeTask == null)
            {
                _disposeTask = DisposeCoreAsync();
            }
            return new ValueTask(_disposeTask);
        }

        private async Task DisposeCoreAsync()
        {
            if (Value != null)
            {
                if (Application.isPlaying)
                {
                    UnityEngine.Object.Destroy(Value);
                    while (Value != null)
                    {
                        await Task.Yield();
                    }
                }
                else
                {
                    UnityEngine.Object.DestroyImmediate(Value);
                }
            }
            await _owner.ReleaseAsync(_prefab);
            _owner.ForgetInstance(this);
        }
    }
}
