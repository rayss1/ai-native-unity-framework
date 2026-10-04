namespace AiNative.Server.Fantasy;

// The vendor completes connections either inline or later on the owning Scene thread.
internal static class FantasyProbeConnection
{
    internal static void Begin<TSession, TProbe>(Func<Action, TSession> connect, Func<TSession, TProbe> create,
        TaskCompletionSource<TProbe> completion, Action<TProbe>? discard = null) where TSession : class
    {
        TSession? session = null;
        bool succeeded = false;
        void Finish()
        {
            if (!succeeded || session is null || completion.Task.IsCompleted) return;
            try
            {
                TProbe probe = create(session);
                if (!completion.TrySetResult(probe)) discard?.Invoke(probe);
            }
            catch (Exception error) { completion.TrySetException(error); }
        }
        session = connect(() => { succeeded = true; Finish(); });
        // Inline success waits until Connect returns its session. Deferred callbacks use the same path.
        Finish();
    }

    internal static async Task<T> WaitOwnedAsync<T>(TaskCompletionSource<T> completion, TimeSpan timeout,
        CancellationToken cancellation, Func<ValueTask> abandon, Func<T, ValueTask> dispose)
    {
        try { return await completion.Task.WaitAsync(timeout, cancellation); }
        catch (Exception error)
        {
            // WaitAsync alone leaves the original completion live. Reject subsequent
            // callbacks before closing the network on its actual owning Scene.
            completion.TrySetException(error);
            if (completion.Task.IsFaulted) _ = completion.Task.Exception;
            await abandon();
            // Success may have raced with cancellation of the outer waiter.
            if (completion.Task.IsCompletedSuccessfully) await dispose(completion.Task.Result);
            throw;
        }
    }

    internal static async ValueTask OnSceneAsync(global::Fantasy.Scene scene, Action action)
    {
        if (scene.IsDisposed) return;
        if (ReferenceEquals(SynchronizationContext.Current, scene.ThreadSynchronizationContext))
        { action(); return; }
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scene.ThreadSynchronizationContext.Post(() =>
        {
            try { if (!scene.IsDisposed) action(); completed.TrySetResult(); }
            catch (Exception error) { completed.TrySetException(error); }
        });
        // A failed cleanup is reported, never silently treated as completed ownership.
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
