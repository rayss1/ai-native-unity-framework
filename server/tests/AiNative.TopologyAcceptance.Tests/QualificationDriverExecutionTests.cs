using NUnit.Framework;

namespace AiNative.Topology.ToolTests;

public sealed class QualificationDriverExecutionTests
{
    static readonly TimeSpan SafetyTimeout = TimeSpan.FromSeconds(10);

    [TestCase(ThreadPriority.Highest)]
    [TestCase(ThreadPriority.Lowest)]
    public void Unsupported_priority_cannot_start_an_owner(ThreadPriority priority)
    {
        int factoryCalls = 0, bodyCalls = 0;
        Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await QualificationDriverExecution.Run(
            _ => { bodyCalls++; return Task.CompletedTask; },
            () => { factoryCalls++; return new RecordingWait(); }, priority));
        Assert.That(factoryCalls, Is.Zero);
        Assert.That(bodyCalls, Is.Zero);
    }

    [Test]
    [Platform("Win")]
    public async Task Explicit_input_priority_applies_before_factory_and_survives_incomplete_await()
    {
        var entered = Signal();
        var release = Signal();
        Thread? owner = null;
        ThreadPriority? factoryPriority = null, initialPriority = null, resumedPriority = null;
        var waiter = new RecordingWait();
        var running = QualificationDriverExecution.Run(async wait =>
        {
            initialPriority = Thread.CurrentThread.Priority;
            entered.TrySetResult();
            await release.Task;
            resumedPriority = Thread.CurrentThread.Priority;
            wait();
        }, () =>
        {
            owner = Thread.CurrentThread;
            factoryPriority = owner.Priority;
            return waiter;
        }, ThreadPriority.AboveNormal);
        try
        {
            await entered.Task.WaitAsync(SafetyTimeout);
            Assert.That(running.IsCompleted, Is.False);
        }
        finally { release.TrySetResult(); await running.WaitAsync(SafetyTimeout); }
        Assert.Multiple(() =>
        {
            Assert.That(factoryPriority, Is.EqualTo(ThreadPriority.AboveNormal));
            Assert.That(initialPriority, Is.EqualTo(ThreadPriority.AboveNormal));
            Assert.That(resumedPriority, Is.EqualTo(ThreadPriority.AboveNormal));
            Assert.That(owner!.IsAlive, Is.False);
            Assert.That(waiter.DisposeCalls, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Incomplete_await_returns_body_and_wait_to_its_normal_owned_thread()
    {
        var suspended = Signal();
        var completion = Signal();
        Thread? owner = null, beforeAwait = null, afterAwait = null, completionThread = null;
        SynchronizationContext? beforeContext = null, afterContext = null;
        var waiter = new RecordingWait();
        var running = QualificationDriverExecution.Run(async wait =>
        {
            beforeAwait = Thread.CurrentThread;
            beforeContext = SynchronizationContext.Current;
            wait();
            suspended.SetResult();
            await completion.Task;
            afterAwait = Thread.CurrentThread;
            afterContext = SynchronizationContext.Current;
            wait();
        }, () => { owner = CaptureOwner(); return waiter; });
        try
        {
            await suspended.Task.WaitAsync(SafetyTimeout);
            Assert.That(running.IsCompleted, Is.False);
            var completer = new Thread(() => { completionThread = Thread.CurrentThread; completion.TrySetResult(); }) { IsBackground = true };
            completer.Start();
            Assert.That(completer.Join(SafetyTimeout), Is.True);
            await running.WaitAsync(SafetyTimeout);
            Assert.That(beforeAwait, Is.SameAs(owner));
            Assert.That(afterAwait, Is.SameAs(owner));
            Assert.That(completionThread, Is.Not.SameAs(owner));
            Assert.That(beforeContext, Is.Not.Null);
            Assert.That(afterContext, Is.SameAs(beforeContext));
            Assert.That(waiter.WaitThreads, Is.EqualTo(new[] { owner, owner }));
            AssertReleased(owner, waiter);
        }
        finally { completion.TrySetResult(); await running.WaitAsync(SafetyTimeout); }
    }

    [Test]
    public async Task Synchronously_completed_body_still_disposes_and_joins_before_returning()
    {
        Thread? owner = null, bodyThread = null;
        var waiter = new RecordingWait();
        await QualificationDriverExecution.Run(wait =>
        {
            bodyThread = Thread.CurrentThread;
            wait();
            return Task.CompletedTask;
        }, () => { owner = CaptureOwner(); return waiter; }).WaitAsync(SafetyTimeout);
        Assert.That(bodyThread, Is.SameAs(owner));
        Assert.That(waiter.WaitThreads, Is.EqualTo(new[] { owner }));
        AssertReleased(owner, waiter);
    }

    [Test]
    public async Task Cancellation_while_waiting_preserves_token_and_joins_before_completion()
    {
        using var cancelled = new CancellationTokenSource();
        var waiting = Signal();
        Thread? owner = null;
        var waiter = new RecordingWait(() =>
        {
            waiting.SetResult();
            cancelled.Token.WaitHandle.WaitOne();
            cancelled.Token.ThrowIfCancellationRequested();
        });
        var running = QualificationDriverExecution.Run(wait => { wait(); return Task.CompletedTask; },
            () => { owner = CaptureOwner(); return waiter; });
        try
        {
            await waiting.Task.WaitAsync(SafetyTimeout);
            Assert.That(running.IsCompleted, Is.False);
        }
        finally { cancelled.Cancel(); }
        var error = Assert.CatchAsync<OperationCanceledException>(async () => await running.WaitAsync(SafetyTimeout));
        Assert.That(error!.CancellationToken, Is.EqualTo(cancelled.Token));
        Assert.That(running.IsCanceled, Is.True);
        AssertReleased(owner, waiter);
    }

    [Test]
    public void Factory_failure_does_not_run_body_and_does_not_leave_worker_alive()
    {
        Thread? owner = null;
        int bodyCalls = 0;
        var expected = new InvalidOperationException("factory failure");
        var running = QualificationDriverExecution.Run(_ => { Interlocked.Increment(ref bodyCalls); return Task.CompletedTask; },
            () => { owner = CaptureOwner(); throw expected; });
        var error = Assert.ThrowsAsync<InvalidOperationException>(async () => await running.WaitAsync(SafetyTimeout));
        Assert.That(error, Is.SameAs(expected));
        Assert.That(bodyCalls, Is.Zero);
        AssertOwnedThreadStopped(owner);
    }

    [Test]
    public void Wait_failure_is_preserved_after_exactly_once_disposal_and_join()
    {
        Thread? owner = null;
        var expected = new InvalidOperationException("wait failure");
        var waiter = new RecordingWait(() => throw expected);
        var running = QualificationDriverExecution.Run(wait => { wait(); return Task.CompletedTask; },
            () => { owner = CaptureOwner(); return waiter; });
        var error = Assert.ThrowsAsync<InvalidOperationException>(async () => await running.WaitAsync(SafetyTimeout));
        Assert.That(error, Is.SameAs(expected));
        AssertReleased(owner, waiter);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Body_failure_before_or_after_incomplete_await_is_preserved_and_releases_worker(bool asynchronously)
    {
        Thread? owner = null;
        var expected = new InvalidOperationException("body failure");
        var waiter = new RecordingWait();
        var release = Signal();
        var suspended = Signal();
        Task Body(Action wait)
        {
            if (!asynchronously) throw expected;
            return FailAfterAwait();
        }
        async Task FailAfterAwait()
        {
            suspended.SetResult();
            await release.Task;
            throw expected;
        }
        var running = QualificationDriverExecution.Run(Body, () => { owner = CaptureOwner(); return waiter; });
        if (asynchronously)
        {
            try { Assert.That(suspended.Task.Wait(SafetyTimeout), Is.True); }
            finally { release.TrySetResult(); }
        }
        var error = Assert.ThrowsAsync<InvalidOperationException>(async () => await running.WaitAsync(SafetyTimeout));
        Assert.That(error, Is.SameAs(expected));
        AssertReleased(owner, waiter);
    }

    [Test]
    public async Task Blocked_room_does_not_prevent_an_independent_room_from_finishing()
    {
        using var releaseFirst = new ManualResetEventSlim();
        var firstWaiting = Signal();
        Thread? firstOwner = null, secondOwner = null;
        var firstWaiter = new RecordingWait(() => { firstWaiting.SetResult(); releaseFirst.Wait(); });
        var secondWaiter = new RecordingWait();
        var first = QualificationDriverExecution.Run(wait => { wait(); return Task.CompletedTask; },
            () => { firstOwner = CaptureOwner(); return firstWaiter; });
        try
        {
            await firstWaiting.Task.WaitAsync(SafetyTimeout);
            await QualificationDriverExecution.Run(wait => { wait(); return Task.CompletedTask; },
                () => { secondOwner = CaptureOwner(); return secondWaiter; }).WaitAsync(SafetyTimeout);
            Assert.That(first.IsCompleted, Is.False);
            Assert.That(firstOwner, Is.Not.SameAs(secondOwner));
            AssertReleased(secondOwner, secondWaiter);
        }
        finally { releaseFirst.Set(); await first.WaitAsync(SafetyTimeout); }
        AssertReleased(firstOwner, firstWaiter);
    }

    static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    static Thread CaptureOwner()
    {
        var owner = Thread.CurrentThread;
        Assert.That(owner.IsThreadPoolThread, Is.False);
        Assert.That(owner.IsBackground, Is.True);
        Assert.That(owner.Priority, Is.EqualTo(ThreadPriority.Normal));
        return owner;
    }

    static void AssertOwnedThreadStopped(Thread? owner)
    {
        Assert.That(owner, Is.Not.Null);
        Assert.That(owner!.IsAlive, Is.False, "Run task must not complete until its owned thread has exited");
    }

    static void AssertReleased(Thread? owner, RecordingWait waiter)
    {
        AssertOwnedThreadStopped(owner);
        Assert.That(waiter.DisposeCalls, Is.EqualTo(1));
        Assert.That(waiter.DisposeThread, Is.SameAs(owner));
    }

    sealed class RecordingWait(Action? onWait = null) : IQualificationDriverWait
    {
        public readonly List<Thread> WaitThreads = [];
        public int DisposeCalls;
        public Thread? DisposeThread;
        public void Wait() { WaitThreads.Add(Thread.CurrentThread); onWait?.Invoke(); }
        public void Dispose() { Interlocked.Increment(ref DisposeCalls); DisposeThread = Thread.CurrentThread; }
    }
}
