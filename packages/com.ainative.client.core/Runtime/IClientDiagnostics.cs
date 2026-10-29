#nullable enable
using System;

namespace AiNative.Client.Core
{
    /// <summary>客户端诊断端口。实现决定输出位置；调用者不得传入凭据或敏感参数。</summary>
    public interface IClientDiagnostics
    {
        /// <summary>记录稳定事件名称与已脱敏说明，不用于固定 Tick 热路径。</summary>
        void Record(string eventName, string message);

        /// <summary>记录已观察的异常；实现不得使诊断导出成为业务操作的前置条件。</summary>
        void ReportException(string operation, Exception exception);
    }
}
