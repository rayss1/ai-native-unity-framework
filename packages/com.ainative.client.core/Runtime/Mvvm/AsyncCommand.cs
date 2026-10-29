#nullable enable
using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace AiNative.Client.Core.Mvvm
{
    /// <summary>
    /// 单所有者线程的异步命令。执行中拒绝重入，业务异常记录到 Error；
    /// 取消属于正常终止，关闭后不再触发界面通知。不使用线程池调度。
    /// </summary>
    public sealed class AsyncCommand : INotifyPropertyChanged, IDisposable
    {
        private readonly Func<CancellationToken, ValueTask> _execute;
        private readonly Func<bool>? _canExecute;
        private CancellationTokenSource? _execution;
        private bool _disposed;

        /// <summary>注入异步业务及可选可执行条件；业务必须合作响应取消。</summary>
        public AsyncCommand(
            Func<CancellationToken, ValueTask> execute,
            Func<bool>? canExecute = null
        )
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        /// <summary>当前命令是否仍在执行；关闭时立即清空，不等待业务完成。</summary>
        public bool IsExecuting { get; private set; }

        /// <summary>未关闭、未执行且业务条件允许时可执行。</summary>
        public bool CanExecute => !_disposed && !IsExecuting && (_canExecute?.Invoke() ?? true);

        /// <summary>最近一次非取消业务异常；下次执行开始时清空，不自动记录敏感参数。</summary>
        public Exception? Error { get; private set; }

        /// <summary>执行和错误状态同步通知；绑定作用域应在关闭时解绑。</summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// 执行业务并观察异常；不可执行时直接返回。调用者取消只影响本次执行。
        /// 关闭后迟到的业务结果被忽略，但返回值仍等待业务真正结束。
        /// </summary>
        public async ValueTask ExecuteAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!CanExecute)
            {
                return;
            }
            var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _execution = execution;
            IsExecuting = true;
            Error = null;
            try
            {
                Notify(nameof(IsExecuting));
                Notify(nameof(CanExecute));
                Notify(nameof(Error));
                execution.Token.ThrowIfCancellationRequested();
                await _execute(execution.Token);
            }
            catch (OperationCanceledException) when (execution.IsCancellationRequested)
            {
                // 生命周期取消不显示成业务错误。
            }
            catch (Exception failure)
            {
                if (!_disposed)
                {
                    Error = failure;
                    Notify(nameof(Error));
                }
            }
            finally
            {
                _execution = null;
                execution.Dispose();
                IsExecuting = false;
                Notify(nameof(IsExecuting));
                Notify(nameof(CanExecute));
            }
        }

        /// <summary>业务条件变化后，由 ViewModel 显式刷新按钮可执行状态。</summary>
        public void RefreshCanExecute()
        {
            Notify(nameof(CanExecute));
        }

        /// <summary>请求取消当前执行；取消回调失败由调用者观察，业务仍需完成清理。</summary>
        public void Cancel()
        {
            _execution?.Cancel();
        }

        /// <summary>幂等关闭并取消；先解除监听，保证任何迟到结果都不能刷新界面。</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            PropertyChanged = null;
            IsExecuting = false;
            Cancel();
        }

        private void Notify(string propertyName)
        {
            if (!_disposed)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }
    }
}
