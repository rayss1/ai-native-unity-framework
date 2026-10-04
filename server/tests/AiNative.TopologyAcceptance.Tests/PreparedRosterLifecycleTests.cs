using System.Reflection;
using NUnit.Framework;

namespace AiNative.Topology.ToolTests;

public class PreparedRosterLifecycleTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task AdmissionStartsEveryMemberButKeepsRosterExclusionUntilAllRepliesSettle(bool synchronousRejection)
    {
        var type = Assembly.Load("AiNative.TopologyAcceptance").GetType("QualificationRosterAdmission");
        Assert.That(type, Is.Not.Null);
        var replies = Enumerable.Range(0, 8).Select(_ => Signal<bool>()).ToArray();
        var started = new List<int>(); using var gate = new SemaphoreSlim(1, 1); using var cancellation = new CancellationTokenSource();
        Func<int, Task> submit = index =>
        {
            started.Add(index);
            if (index == 0 && synchronousRejection) throw new InvalidOperationException("first-request-rejected");
            return replies[index].Task;
        };
        var admission = (Task)type!.GetMethod("EnqueueAsync")!.Invoke(null, [gate, 8, submit, cancellation.Token])!;
        Assert.That(started, Is.EquivalentTo(Enumerable.Range(0, 8)));
        Assert.That(gate.CurrentCount, Is.Zero);
        cancellation.Cancel();
        if (!synchronousRejection) replies[0].SetException(new InvalidOperationException("first-request-rejected"));
        for (int i = 1; i < 7; i++) replies[i].SetResult(true);
        Assert.That(admission.IsCompleted, Is.False, "An early rejection cannot release the roster gate while sibling mutations are unresolved.");
        Assert.That(gate.CurrentCount, Is.Zero);
        replies[7].SetResult(true);
        Assert.ThrowsAsync<InvalidOperationException>(async () => await admission);
        Assert.That(gate.CurrentCount, Is.EqualTo(1));
    }

    [Test]
    public void CancelledAdmissionDoesNotSubmitAnyMember()
    {
        var type = Assembly.Load("AiNative.TopologyAcceptance").GetType("QualificationRosterAdmission");
        Assert.That(type, Is.Not.Null);
        using var gate = new SemaphoreSlim(1, 1); int submitted = 0;
        Func<int, Task> submit = _ => { submitted++; return Task.CompletedTask; };
        var admission = (Task)type!.GetMethod("EnqueueAsync")!.Invoke(null, [gate, 8, submit, new CancellationToken(true)])!;
        Assert.CatchAsync<OperationCanceledException>(async () => await admission);
        Assert.That(submitted, Is.Zero); Assert.That(gate.CurrentCount, Is.EqualTo(1));
    }

    static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Test]
    public async Task PreparationRunsDuringCurrentRoomButHandoffWaitsForSettlementAndReleasedCapacity()
    {
        var prepared = Signal<bool>(); var settled = Signal<bool>(); var released = Signal<bool>();
        var members = Enumerable.Range(0, 8).Select(_ => new Member()).ToArray(); int created = 0;
        await using var roster = NewRoster(async (index, _) =>
        {
            if (Interlocked.Increment(ref created) == 8) prepared.TrySetResult(true);
            await Task.Yield(); return members[index];
        });
        await prepared.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var handoff = roster.Take(async token => { await settled.Task.WaitAsync(token); await released.Task.WaitAsync(token); });
        Assert.That(handoff.IsCompleted, Is.False);
        settled.SetResult(true); await Task.Yield(); Assert.That(handoff.IsCompleted, Is.False);
        released.SetResult(true);
        var result = await handoff.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(result, Is.EqualTo(members));
        await roster.DisposeAsync(); Assert.That(members.All(member => member.Disposals == 0), Is.True);
        foreach (var member in result) await member.DisposeAsync();
        Assert.ThrowsAsync<InvalidOperationException>(async () => await roster.Take(_ => Task.CompletedTask));
    }

    [Test]
    public async Task HandoffWaitsForInFlightKeepAliveBeforeGivingConnectionToQueue()
    {
        var entered = Signal<bool>(); var finish = Signal<bool>(); int inHeartbeat = 0;
        await using var roster = NewRoster((_, _) => Task.FromResult(new Member()), async (_, _) =>
        {
            Interlocked.Increment(ref inHeartbeat); entered.TrySetResult(true);
            try { await finish.Task; } finally { Interlocked.Decrement(ref inHeartbeat); }
        }, TimeSpan.Zero);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var handoff = roster.Take(_ => Task.CompletedTask);
        Assert.That(handoff.IsCompleted, Is.False);
        finish.SetResult(true);
        var members = await handoff.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(inHeartbeat, Is.Zero);
        foreach (var member in members) await member.DisposeAsync();
    }

    [Test]
    public async Task CancellationWaitsForLatePreparationAndDisposesEveryAcquiredMemberExactlyOnce()
    {
        using var stop = new CancellationTokenSource(); var entered = Signal<bool>(); var finish = Signal<bool>();
        var members = Enumerable.Range(0, 8).Select(_ => new Member()).ToArray(); int calls = 0;
        var roster = NewRoster(async (index, _) =>
        {
            if (Interlocked.Increment(ref calls) == 8) entered.TrySetResult(true);
            await finish.Task; return members[index];
        }, cancellation: stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); stop.Cancel();
        Task disposal = roster.DisposeAsync().AsTask();
        Assert.That(disposal.IsCompleted, Is.False);
        finish.SetResult(true); await disposal.WaitAsync(TimeSpan.FromSeconds(2));
        await roster.DisposeAsync();
        Assert.That(members.Select(member => member.Disposals), Is.All.EqualTo(1));
    }

    [Test]
    public async Task CancellationDuringHandoffAwaitsSharedCleanupAndNeverTransfersOwnership()
    {
        var cleanupStarted = Signal<bool>(); var cleanupFinish = Signal<bool>();
        var members = Enumerable.Range(0, 8).Select(_ => new Member(async () =>
        { cleanupStarted.TrySetResult(true); await cleanupFinish.Task; })).ToArray();
        var roster = NewRoster((index, _) => Task.FromResult(members[index]));
        var handoff = roster.Take(async token => await Task.Delay(Timeout.InfiniteTimeSpan, token));
        Task first = roster.DisposeAsync().AsTask();
        await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task second = roster.DisposeAsync().AsTask();
        Assert.That(first.IsCompleted || second.IsCompleted, Is.False);
        cleanupFinish.SetResult(true); await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.ThrowsAsync<OperationCanceledException>(async () => await handoff);
        Assert.That(members.Select(member => member.Disposals), Is.All.EqualTo(1));
    }

    [Test]
    public async Task FailedPreparationStillOwnsSuccessfulSiblingsAndCleanupFailureRejectsRun()
    {
        var member = new Member(() => Task.FromException(new InvalidOperationException("party-cleanup-outcome-unknown")));
        var roster = NewRoster((index, _) => index == 3
            ? Task.FromException<Member>(new InvalidOperationException("prepare-rejected")) : Task.FromResult(index == 0 ? member : new Member()));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await roster.Take(_ => Task.CompletedTask));
        var failure = Assert.ThrowsAsync<InvalidOperationException>(async () => await roster.DisposeAsync());
        Assert.That(failure!.Message, Is.EqualTo("party-cleanup-outcome-unknown"));
        Assert.That(member.Disposals, Is.EqualTo(1));
    }

    static Roster NewRoster(Func<int, CancellationToken, Task<Member>> prepare,
        Func<Member, CancellationToken, Task>? heartbeat = null, TimeSpan? interval = null, CancellationToken cancellation = default)
    {
        var type = Assembly.Load("AiNative.TopologyAcceptance").GetType("QualificationPreparedRoster`1");
        Assert.That(type, Is.Not.Null, "Missing bounded next-roster lifecycle behavior");
        return new Roster(Activator.CreateInstance(type!.MakeGenericType(typeof(Member)),
            [8, prepare, heartbeat ?? ((_, _) => Task.CompletedTask), interval ?? TimeSpan.FromSeconds(10), cancellation])!);
    }
    sealed class Roster(object value) : IAsyncDisposable
    {
        public Task<Member[]> Take(Func<CancellationToken, Task> barrier) => (Task<Member[]>)value.GetType()
            .GetMethod("TakeAsync")!.Invoke(value, [barrier])!;
        public ValueTask DisposeAsync() => ((IAsyncDisposable)value).DisposeAsync();
    }
    public sealed class Member(Func<Task>? cleanup = null) : IAsyncDisposable
    {
        public int Disposals;
        public async ValueTask DisposeAsync() { Interlocked.Increment(ref Disposals); if (cleanup is not null) await cleanup(); }
    }
}
