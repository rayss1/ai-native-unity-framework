using System;
using AiNative.Client.Core;
using UnityEngine;

namespace AiNative.Client.Runtime
{
    /// <summary>开发诊断接入；调用方不得传入口令、证书私钥或完整认证消息。</summary>
    public sealed class UnityClientDiagnostics : IClientDiagnostics
    {
        /// <inheritdoc/>
        /// <summary>将已脱敏事件直接输出至 Unity 日志；不缓存或接管消息资源。</summary>
        public void Record(string eventName, string message)
        {
            Debug.Log("[Client] " + eventName + ": " + message);
        }

        /// <inheritdoc/>
        /// <summary>将已观察异常及操作名输出至 Unity 日志；调用方负责取消、恢复和资源释放。</summary>
        public void ReportException(string operation, Exception exception)
        {
            Debug.LogError("[Client] " + operation);
            Debug.LogException(exception);
        }
    }
}
