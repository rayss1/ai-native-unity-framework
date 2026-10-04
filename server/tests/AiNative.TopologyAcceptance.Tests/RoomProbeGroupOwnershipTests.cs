using System.Reflection;
using NUnit.Framework;

namespace AiNative.Topology.ToolTests;

public class RoomProbeGroupOwnershipTests
{
    [Test]
    public async Task FailedRoomCreationStillAwaitsGroupCloseAndRepeatedTerminalDisposeSharesCompletion()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var group = new Group(async () => { entered.TrySetResult(true); await released.Task; });
        int clientClosures = 0;
        var owner = Resources(group, () => { Interlocked.Increment(ref clientClosures); return Task.FromException(new InvalidOperationException("join-failed")); });
        Task failedCreationCleanup = owner.DisposeAsync().AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task terminalCleanup = owner.DisposeAsync().AsTask();
        Assert.That(failedCreationCleanup.IsCompleted || terminalCleanup.IsCompleted, Is.False);
        Assert.That(group.Closures, Is.EqualTo(1));
        released.SetResult(true);
        var failure = Assert.ThrowsAsync<InvalidOperationException>(async () => await failedCreationCleanup);
        Assert.That(failure!.Message, Is.EqualTo("join-failed"));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await terminalCleanup);
        Assert.That(clientClosures, Is.EqualTo(1)); Assert.That(group.Closures, Is.EqualTo(1));
    }

    [Test]
    public async Task NormalCloseFinishesAllClientCleanupBeforeClosingTheGroup()
    {
        var finishClients = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var group = new Group(() => Task.CompletedTask);
        var owner = Resources(group, () => finishClients.Task);
        Task first = owner.DisposeAsync().AsTask(); Task second = owner.DisposeAsync().AsTask();
        Assert.That(group.Closures, Is.Zero); Assert.That(first.IsCompleted || second.IsCompleted, Is.False);
        finishClients.SetResult(true); await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
        await owner.DisposeAsync(); Assert.That(group.Closures, Is.EqualTo(1));
    }

    static IAsyncDisposable Resources(IAsyncDisposable group, Func<Task> closeClients)
    {
        var type = Assembly.Load("AiNative.TopologyAcceptance").GetType("QualificationRoomResources");
        Assert.That(type, Is.Not.Null, "Missing room generation probe-group ownership");
        return (IAsyncDisposable)Activator.CreateInstance(type!, [group, closeClients])!;
    }
    sealed class Group(Func<Task> close) : IAsyncDisposable
    {
        public int Closures;
        public async ValueTask DisposeAsync() { Interlocked.Increment(ref Closures); await close(); }
    }
}
