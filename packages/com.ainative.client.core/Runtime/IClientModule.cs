#nullable enable
using System.Threading;
using System.Threading.Tasks;

namespace AiNative.Client.Core
{
    /// <summary>应用拥有的模块。同一所有者上下文调用；启动可能部分成功，因此失败后也必须允许停止。</summary>
    public interface IClientModule
    {
        /// <summary>在调用方同步上下文启动；取消时释放不能交给调用方的临时资源。</summary>
        ValueTask StartAsync(CancellationToken cancellationToken);

        /// <summary>停止并释放模块拥有的资源；必须支持部分启动和重复停止。
        /// 调用方须等待释放完成并观察失败；ClientApplication 以不可取消令牌保证清理不中断。</summary>
        ValueTask StopAsync(CancellationToken cancellationToken);
    }
}
