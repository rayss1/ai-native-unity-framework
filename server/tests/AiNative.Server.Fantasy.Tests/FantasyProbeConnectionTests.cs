using AiNative.Server.Fantasy;
using NUnit.Framework;

namespace AiNative.Server.Fantasy.Tests;

public sealed class FantasyProbeConnectionTests
{
    [Test]
    public async Task TimedOutConnectionWaitRejectsLateSuccessAndRetainsNetworkCleanupOwnership()
    {
        var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action? connected = null; int created = 0, closed = 0;
        FantasyProbeConnection.Begin<object, object>(callback => { connected = callback; return new(); },
            session => { created++; return session; }, completion);
        var wait = WaitOwned(completion, TimeSpan.FromMilliseconds(10), CancellationToken.None,
            () => { closed++; return ValueTask.CompletedTask; }, _ => ValueTask.CompletedTask);
        Assert.ThrowsAsync<TimeoutException>(async () => await wait);
        connected!();
        Assert.That(created, Is.Zero, "A connection abandoned by its caller must not register a late unowned probe.");
        Assert.That(closed, Is.EqualTo(1));
        Assert.That(completion.Task.IsCompleted, Is.True);
        _ = completion.Task.Exception;
    }

    [Test]
    public async Task CancelledConnectionWaitRejectsLateSuccessAndAwaitsItsCleanup()
    {
        using var cancel = new CancellationTokenSource();
        var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleaning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Action? connected = null; int created = 0;
        FantasyProbeConnection.Begin<object, object>(callback => { connected = callback; return new(); },
            session => { created++; return session; }, completion);
        var wait = WaitOwned(completion, TimeSpan.FromSeconds(3), cancel.Token,
            async () => { cleaning.SetResult(); await release.Task; }, _ => ValueTask.CompletedTask);
        cancel.Cancel();
        var observed = await Task.WhenAny(cleaning.Task, wait);
        try
        {
            Assert.That(observed, Is.SameAs(cleaning.Task), "Cancellation must retain ownership until network cleanup actually completes.");
            Assert.That(wait.IsCompleted, Is.False);
            connected!(); Assert.That(created, Is.Zero);
        }
        finally { release.TrySetResult(); try { await wait; } catch (OperationCanceledException) { } }
        _ = completion.Task.Exception;
    }

    [Test]
    public async Task SuccessfulConnectionTransfersOwnershipWithoutAbandoningItsNetwork()
    {
        object owner = new(); int closed = 0;
        var completion = new TaskCompletionSource<object>(); completion.SetResult(owner);
        object result = await WaitOwned(completion, TimeSpan.FromSeconds(1), CancellationToken.None,
            () => { closed++; return ValueTask.CompletedTask; }, _ => { closed++; return ValueTask.CompletedTask; });
        Assert.That(result, Is.SameAs(owner)); Assert.That(closed, Is.Zero);
    }

    private static Task<T> WaitOwned<T>(TaskCompletionSource<T> completion, TimeSpan timeout,
        CancellationToken cancellation, Func<ValueTask> abandon, Func<T, ValueTask> dispose)
    {
        // The old adapter boundary uses WaitAsync directly. Run the same behavior
        // until the owned-wait boundary exists, then exercise it without a vendor import.
        var method = typeof(FantasyProbeConnection).GetMethod("WaitOwnedAsync",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        return method is null ? completion.Task.WaitAsync(timeout, cancellation) :
            (Task<T>)method.MakeGenericMethod(typeof(T)).Invoke(null, [completion, timeout, cancellation, abandon, dispose])!;
    }

    [Test]
    public void AbandonmentDuringFactoryDisposesTheNewProbeInsteadOfLosingItsOwner()
    {
        object probe = new(); int disposed = 0;
        var completion = new TaskCompletionSource<object>();
        Func<Action, object> connect = callback => { callback(); return new(); };
        Func<object, object> create = _ => { completion.TrySetException(new TimeoutException()); return probe; };
        var method = typeof(FantasyProbeConnection).GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            .Single(candidate => candidate.Name == "Begin");
        if (method.GetParameters().Length == 3) FantasyProbeConnection.Begin(connect, create, completion);
        else method.MakeGenericMethod(typeof(object), typeof(object)).Invoke(null,
            [connect, create, completion, (Action<object>)(_ => disposed++)]);
        Assert.That(disposed, Is.EqualTo(1)); _ = completion.Task.Exception;
    }

    [Test]
    public void InlineSuccessUsesReturnedSessionRatherThanUnassignedCapture()
    {
        object owner = new();
        var completed = new TaskCompletionSource<object>();
        int created = 0;
        FantasyProbeConnection.Begin<object, object>(callback => { callback(); return owner; }, session =>
        {
            Assert.That(session, Is.SameAs(owner)); created++; return session;
        }, completed);
        Assert.That(completed.Task.IsCompletedSuccessfully, Is.True);
        Assert.That(completed.Task.Result, Is.SameAs(owner));
        Assert.That(created, Is.EqualTo(1));
    }

    [Test]
    public void DeferredSuccessCompletesOnceWithOriginalSession()
    {
        object owner = new(); Action? later = null;
        var completed = new TaskCompletionSource<object>(); int created = 0;
        FantasyProbeConnection.Begin<object, object>(callback => { later = callback; return owner; }, session =>
        { created++; return session; }, completed);
        Assert.That(completed.Task.IsCompleted, Is.False);
        later!(); later();
        Assert.That(completed.Task.Result, Is.SameAs(owner));
        Assert.That(created, Is.EqualTo(1));
    }

    [Test]
    public void FailureBeforeConnectReturnsCannotCreateAProbe()
    {
        var completed = new TaskCompletionSource<object>(); int created = 0;
        FantasyProbeConnection.Begin<object, object>(callback =>
        { completed.TrySetException(new InvalidOperationException("connect-failed")); callback(); return new(); }, session =>
        { created++; return session; }, completed);
        Assert.That(completed.Task.IsFaulted, Is.True);
        Assert.That(created, Is.Zero);
    }

    [Test]
    public void DeferredFactoryFailureCompletesTheWaiterWithAnError()
    {
        var completed = new TaskCompletionSource<object>(); Action? later = null;
        FantasyProbeConnection.Begin<object, object>(callback => { later = callback; return new(); }, session =>
            throw new InvalidOperationException("factory-failed"), completed);
        Assert.DoesNotThrow(() => later!());
        Assert.That(completed.Task.IsFaulted, Is.True);
    }
}
