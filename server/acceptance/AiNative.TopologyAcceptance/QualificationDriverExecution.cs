using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("AiNative.TopologyAcceptance.Tests")]

internal interface IQualificationDriverWait : IDisposable
{
    void Wait();
}

// Acceptance-only owner: asynchronous transport completion must resume on the same driver thread.
internal static class QualificationDriverExecution
{
    internal static string SelectWaitMode(string? configured, bool isWindows) => configured switch
    {
        null or "" => isWindows ? "windows-high-resolution" : "task-delay",
        "task-delay" => "task-delay",
        "windows-high-resolution" => "windows-high-resolution",
        _ => throw new InvalidOperationException("Unknown qualification driver wait mode.")
    };

    internal static ThreadPriority SelectThreadPriority(string? configured, bool isWindows, string waitMode)
        => configured switch
        {
            null or "" or "normal" => ThreadPriority.Normal,
            "above-normal" when isWindows && waitMode == "windows-high-resolution" => ThreadPriority.AboveNormal,
            _ => throw new InvalidOperationException("Unsupported qualification input-thread priority experiment.")
        };

    internal static async Task Run(Func<Action, Task> body, Func<IQualificationDriverWait> createWait,
        ThreadPriority priority = ThreadPriority.Normal)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(createWait);
        if (priority is not (ThreadPriority.Normal or ThreadPriority.AboveNormal))
            throw new ArgumentOutOfRangeException(nameof(priority));
        var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using (var waiter = createWait())
                using (var context = new DriverContext())
                {
                    SynchronizationContext? previous = SynchronizationContext.Current;
                    SynchronizationContext.SetSynchronizationContext(context);
                    try
                    {
                        Task operation = body(waiter.Wait);
                        context.Run(operation);
                        operation.GetAwaiter().GetResult();
                    }
                    finally { SynchronizationContext.SetSynchronizationContext(previous); }
                }
                terminal.TrySetResult();
            }
            catch (OperationCanceledException error) { terminal.TrySetCanceled(error.CancellationToken); }
            catch (Exception error) { terminal.TrySetException(error); }
        }) { IsBackground = true, Name = "Qualification input driver", Priority = priority };
        thread.Start();
        try { await terminal.Task.ConfigureAwait(false); }
        finally { thread.Join(); }
    }

    private sealed class DriverContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> callbacks = new();

        public override void Post(SendOrPostCallback callback, object? state) => callbacks.Add((callback, state));

        internal void Run(Task operation)
        {
            if (operation.IsCompleted) return;
            // Terminal notification only; input-loop continuations enter the owned queue through Post.
            _ = operation.ContinueWith(_ => callbacks.CompleteAdding(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            foreach (var item in callbacks.GetConsumingEnumerable()) item.Callback(item.State);
        }

        public void Dispose() => callbacks.Dispose();
    }
}
