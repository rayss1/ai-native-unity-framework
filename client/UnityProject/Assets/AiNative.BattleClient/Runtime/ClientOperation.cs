using System;
using Cysharp.Threading.Tasks;

namespace AiNative.Client.Application
{
    /// <summary>应用内部多等待者操作；使用非池化完成源，禁止重复消费普通 UniTask。</summary>
    internal sealed class ClientOperation
    {
        private readonly UniTaskCompletionSource _completion = new UniTaskCompletionSource();
        public UniTask Task => _completion.Task;
        public bool IsCompleted => _completion.Task.Status.IsCompleted();

        public ClientOperation(Func<UniTask> execute)
        {
            ObserveAsync(execute).Forget(UnityEngine.Debug.LogException);
        }

        private async UniTask ObserveAsync(Func<UniTask> execute)
        {
            try
            {
                await execute();
                _completion.TrySetResult();
            }
            catch (OperationCanceledException)
            {
                _completion.TrySetCanceled();
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
        }
    }
}
