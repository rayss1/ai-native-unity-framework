using NUnit.Framework;

namespace AiNative.Topology.ToolTests;

[Platform("Win")]
public sealed class WindowsQualificationDriverWaitTests
{
    [Test]
    public void CancelledConstructionDoesNotCreateAWaiter()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => new WindowsQualificationDriverWait(stop.Token));
    }

    [Test]
    public void NativeOneShotTimerCanBeRearmedAndClosed()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var waiter = new WindowsQualificationDriverWait(stop.Token);
        try { for (int i = 0; i < 4; i++) waiter.Wait(); }
        finally { waiter.Dispose(); }
        waiter.Dispose();
        Assert.Throws<ObjectDisposedException>(() => waiter.Wait());
    }

    [Test]
    public async Task CancellationStopsNativeWaitingAndJoinsItsOwner()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread? owner = null;
        Task operation = QualificationDriverExecution.Run(wait =>
        {
            owner = Thread.CurrentThread;
            entered.SetResult();
            while (true) wait();
        }, () => new WindowsQualificationDriverWait(stop.Token));
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { stop.Cancel(); }
        try { await operation.WaitAsync(TimeSpan.FromSeconds(10)); Assert.Fail("Cancellation was lost."); }
        catch (OperationCanceledException error) { Assert.That(error.CancellationToken, Is.EqualTo(stop.Token)); }
        Assert.That(owner, Is.Not.Null);
        Assert.That(owner!.IsAlive, Is.False);
    }
}
