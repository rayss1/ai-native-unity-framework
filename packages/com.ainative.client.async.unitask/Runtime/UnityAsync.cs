using System;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace AiNative.Client.Async
{
    /// <summary>将 Unity 操作接入 ValueTask；原生操作不被取消，调用方负责清理迟到结果。</summary>
    public static class UnityAsync
    {
        /// <summary>等待原生操作完成，在 Unity 主线程继续；不暴露第三方 awaitable。</summary>
        public static async ValueTask CompleteAsync(AsyncOperation operation)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }
            await operation.ToUniTask();
        }

        /// <summary>下一帧恢复；取消只结束当前等待。</summary>
        public static async ValueTask NextFrameAsync(CancellationToken cancellationToken)
        {
            await UniTask.NextFrame(cancellationToken: cancellationToken);
        }
    }
}
