using System.Collections;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using AiNative.Server.Control;
using NUnit.Framework;

namespace AiNative.Server.Fantasy.Tests;

// No Entry, services or sockets. The actual vendor context, Entity, Session.Call,
// pending FTask callbacks and Session.Dispose run here; only the outbound channel
// is a black hole. Vendor types are accessed by reflection to preserve ARC008.
[NonParallelizable]
public sealed class FantasyBackendProbeLifecycleTests
{
    [Test]
    public async Task DisposeWaitsForOwnerActionAndActuallyClosesVendorSession()
    {
        await using var fixture = new VendorFixture();
        Task closing = fixture.Probe.DisposeAsync().AsTask();
        Task repeated = fixture.Probe.DisposeAsync().AsTask();
        Assert.That(closing.IsCompleted, Is.False,
            "Posting a Scene action is not completion of Session.Dispose.");
        Assert.That(fixture.SessionIsDisposed, Is.False);
        Assert.That(repeated, Is.SameAs(closing), "Concurrent disposal must share its completion.");
        await fixture.PumpUntilCompleted(closing);
        await closing;
        fixture.AssertActuallyClosed();
    }

    [Test]
    public async Task CloseStartedRejectsNewCallsBeforeSceneHasPumped()
    {
        await using var fixture = new VendorFixture();
        Task closing = fixture.Probe.DisposeAsync().AsTask();
        Task<ServiceReply> call = fixture.Call();
        Assert.That(call.IsCompleted, Is.True, "Closing must reject calls immediately, before the Scene action runs.");
        Assert.That((await call).Error, Is.EqualTo("unavailable"));
        Assert.That(fixture.PendingCallbacks, Is.Zero);
        Assert.That(fixture.Channel.SendCount, Is.Zero);
        await fixture.PumpUntilCompleted(closing);
        await closing;
        fixture.AssertActuallyClosed();
    }

    [Test]
    public async Task PreCancelledCallDoesNotPostOrRegisterAnRpc()
    {
        await using var fixture = new VendorFixture();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Task<ServiceReply> call = fixture.Call(cancelled.Token);
        Assert.CatchAsync<OperationCanceledException>(async () => await call);
        Assert.That(fixture.OwnerActions, Is.Zero, "A pre-cancelled call must not enqueue an accepted mutation.");
        fixture.Pump();
        Assert.That(fixture.Channel.SendCount, Is.Zero);
        Assert.That(fixture.PendingCallbacks, Is.Zero);
        Assert.That(fixture.AvailableCallSlots, Is.EqualTo(8));
    }

    [Test]
    public async Task CancelledRpcWaitsForOwnerCloseAndSettlesActualVendorCallbacks()
    {
        await using var fixture = new VendorFixture();
        using var cancelled = new CancellationTokenSource();
        Task<ServiceReply> call = fixture.Call(cancelled.Token);
        fixture.Pump();
        fixture.AssertActualRpcPending();
        cancelled.Cancel();
        await WaitUntil(() => call.IsCompleted || fixture.OwnerActions != 0, TimeSpan.FromSeconds(3));
        Assert.That(call.IsCompleted, Is.False,
            "Cancellation must retain Session/RPC ownership until the owning Scene has closed it.");
        await fixture.PumpUntilCompleted(call);
        Assert.CatchAsync<OperationCanceledException>(async () => await call);
        fixture.AssertActuallyClosed();
        Task<ServiceReply> later = fixture.Call();
        Assert.That(later.IsCompleted, Is.True);
        Assert.That((await later).Error, Is.EqualTo("unavailable"));
        Assert.That(fixture.Channel.SendCount, Is.EqualTo(1));
    }

    [Test]
    public async Task OneCancelledCallClosesAllEightActualInflightRpcCallbacks()
    {
        await using var fixture = new VendorFixture();
        using var cancelled = new CancellationTokenSource();
        Task<ServiceReply>[] calls = [fixture.Call(cancelled.Token), .. Enumerable.Range(0, 7).Select(_ => fixture.Call())];
        fixture.Pump();
        Assert.That(fixture.Channel.SendCount, Is.EqualTo(8));
        Assert.That(fixture.PendingCallbacks, Is.EqualTo(8), "All eight callbacks must come from actual vendor Session.Call.");
        Assert.That(fixture.AvailableCallSlots, Is.Zero);

        cancelled.Cancel();
        await WaitUntil(() => calls[0].IsCompleted || fixture.OwnerActions != 0, TimeSpan.FromSeconds(3));
        Assert.That(calls[0].IsCompleted, Is.False, "The cancelled waiter must await closure of all owned RPCs.");
        Assert.That(fixture.PendingCallbacks, Is.EqualTo(8), "The owner context has not executed Session.Dispose yet.");
        Task closing = fixture.Probe.DisposeAsync().AsTask();
        Assert.That(closing.IsCompleted, Is.False);
        await fixture.PumpUntilCompleted(closing);
        await closing;
        fixture.AssertActuallyClosed();

        await fixture.PumpUntilCompleted(calls[0]);
        Assert.CatchAsync<OperationCanceledException>(async () => await calls[0]);
        foreach (Task<ServiceReply> other in calls.Skip(1))
        {
            await fixture.PumpUntilCompleted(other);
            ServiceException? failure = Assert.ThrowsAsync<ServiceException>(async () => await other);
            Assert.That(failure!.Message, Is.EqualTo("unavailable"));
        }
        fixture.Pump();
        Assert.That(calls.Skip(1).All(call => call.IsFaulted), Is.True,
            "Every sibling RPC must stay terminally faulted; none may complete successfully after close.");
        Assert.That(fixture.PendingCallbacks, Is.Zero, "No vendor callback remains eligible for a late response.");
        Assert.That(fixture.Channel.SendCount, Is.EqualTo(8));
        Assert.That(fixture.AvailableCallSlots, Is.EqualTo(8));
    }

    [Test]
    public async Task QueuedCallBeforeOwnerPumpIsRejectedByCloseWithoutSending()
    {
        await using var fixture = new VendorFixture();
        Task<ServiceReply> call = fixture.Call();
        Assert.That(fixture.OwnerActions, Is.EqualTo(1));
        Assert.That(fixture.PendingCallbacks, Is.Zero);
        Assert.That(fixture.AvailableCallSlots, Is.EqualTo(7));
        Task closing = fixture.Probe.DisposeAsync().AsTask();
        Assert.That(closing.IsCompleted, Is.False, "Disposal must retain the queued call until its finally settles.");
        await fixture.PumpUntilCompleted(closing);
        await closing;
        await fixture.PumpUntilCompleted(call);
        ServiceException? failure = Assert.ThrowsAsync<ServiceException>(async () => await call);
        Assert.That(failure!.Message, Is.EqualTo("unavailable"));
        Assert.That(fixture.Channel.SendCount, Is.Zero, "A queued call must recheck closing before actual Session.Call/Send.");
        Assert.That(fixture.PendingCallbacks, Is.Zero);
        fixture.AssertActuallyClosed();
    }

    [Test]
    public async Task TimedOutRpcWaitsForOwnerCloseAndSettlesActualVendorCallbacks()
    {
        await using var fixture = new VendorFixture();
        Task<ServiceReply> call = fixture.Call();
        fixture.Pump();
        fixture.AssertActualRpcPending();
        // Exercise the production ten-second timeout, without a fake clock or Task.
        await WaitUntil(() => call.IsCompleted || fixture.OwnerActions != 0, TimeSpan.FromSeconds(13));
        Assert.That(call.IsCompleted, Is.False,
            "A timeout must await actual Session disposal, not leave its vendor FTask pending.");
        await fixture.PumpUntilCompleted(call);
        Assert.ThrowsAsync<TimeoutException>(async () => await call);
        fixture.AssertActuallyClosed();
    }

    [Test]
    public async Task DisposalOnOwnerContextWithoutPendingRpcCanCompleteInline()
    {
        await using var fixture = new VendorFixture();
        Task? closing = null;
        fixture.Post(() => closing = fixture.Probe.DisposeAsync().AsTask());
        fixture.Pump();
        Assert.That(closing, Is.Not.Null);
        Assert.That(closing!.IsCompletedSuccessfully, Is.True,
            "The connection abandonment path may close inline on its owning Scene.");
        await closing;
        fixture.AssertActuallyClosed();
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed >= timeout) throw new TimeoutException("Vendor lifecycle fixture did not reach the expected state.");
            await Task.Delay(5).ConfigureAwait(false);
        }
    }

    public class BlackholeChannel : DispatchProxy
    {
        public object Session { get; set; } = null!;
        public int SendCount { get; private set; }
        public int DisposeCount { get; private set; }
        public void MarkDisposed() => DisposeCount++;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case "Send":
                    Assert.That(args![0], Is.Not.EqualTo(0u), "A real Session.Call must allocate an RPC ID.");
                    Assert.That(args[3], Is.Not.Null);
                    SendCount++;
                    return null; // No reply: Session.Call's real vendor callback stays pending.
                case "get_Session": return Session;
                case "get_IsDisposed": return DisposeCount != 0;
                case "Dispose": MarkDisposed(); return null;
                default: throw new NotSupportedException(targetMethod?.Name);
            }
        }
    }

    private sealed class VendorFixture : IAsyncDisposable
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static long _nextRuntimeId = 10_000_000;
        private readonly object _scene, _session, _context, _entityPool;
        private readonly List<Task<ServiceReply>> _calls = [];
        private readonly long _sessionRuntimeId;
        public FantasyBackendProbe Probe { get; }
        public BlackholeChannel Channel { get; }

        public VendorFixture()
        {
            Assembly vendor = Assembly.Load("Fantasy-Net");
            Type Vendor(string name) => vendor.GetType(name, throwOnError: true)!;
            Type entity = Vendor("Fantasy.Entitas.Entity");
            Type sceneType = Vendor("Fantasy.Scene");
            Type sessionType = Vendor("Fantasy.Network.Session");
            _scene = Activator.CreateInstance(sceneType)!;
            _session = Activator.CreateInstance(sessionType)!;
            _context = Activator.CreateInstance(Vendor("Fantasy.ThreadSynchronizationContext"))!;
            _entityPool = Activator.CreateInstance(Vendor("Fantasy.Entitas.EntityPool"), nonPublic: true)!;
            object component = Activator.CreateInstance(Vendor("Fantasy.Entitas.EntityComponent"))!;

            // No assemblies/events are started. Install a valid frozen destroy
            // registry with a non-Session sentinel: Entity.Dispose takes the real
            // "no registered destroy system" path instead of dereferencing null.
            FieldInfo destroy = component.GetType().GetField("_destroySystems", Members)!;
            Type callbackType = typeof(Action<>).MakeGenericType(entity);
            Delegate noOp = Expression.Lambda(callbackType, Expression.Empty(), Expression.Parameter(entity)).Compile();
            Array callbacks = Array.CreateInstance(callbackType, 1);
            callbacks.SetValue(noOp, 0);
            ConstructorInfo frozenConstructor = destroy.FieldType.GetConstructor(
                [typeof(RuntimeTypeHandle[]), callbackType.MakeArrayType()])
                ?? throw new MissingMethodException("The actual vendor destroy registry constructor differs from the inspected pinned source.");
            destroy.SetValue(component, frozenConstructor.Invoke(
                [new RuntimeTypeHandle[] { typeof(VendorFixture).TypeHandle }, callbacks]));

            Set(_scene, "Scene", _scene);
            Set(_scene, "Parent", _scene);
            Set(_scene, "Type", sceneType);
            Set(_scene, "RuntimeId", Interlocked.Increment(ref _nextRuntimeId));
            Set(_scene, "ThreadSynchronizationContext", _context);
            Set(_scene, "EntityComponent", component);
            Set(_scene, "EntityPool", _entityPool);
            _sessionRuntimeId = Interlocked.Increment(ref _nextRuntimeId);
            Set(_session, "Scene", _scene);
            Set(_session, "Type", sessionType);
            Set(_session, "Id", _sessionRuntimeId);
            Set(_session, "RuntimeId", _sessionRuntimeId);
            sceneType.GetMethod("AddEntity", Members)!.Invoke(_scene, [_session]);

            object channel = DispatchProxy.Create(Vendor("Fantasy.Network.Interface.INetworkChannel"), typeof(BlackholeChannel));
            Channel = (BlackholeChannel)channel;
            Channel.Session = _session;
            Set(_session, "Channel", channel);
            sessionType.GetField("OnDispose", Members)!.SetValue(_session, (Action)Channel.MarkDisposed);
            Probe = (FantasyBackendProbe)Activator.CreateInstance(typeof(FantasyBackendProbe), Members,
                binder: null, args: [_session], culture: null)!;
        }

        private static void Set(object target, string name, object value)
        {
            for (Type? type = target.GetType(); type is not null; type = type.BaseType)
            {
                if (type.GetProperty(name, Members | BindingFlags.DeclaredOnly) is { } property)
                { property.SetValue(target, value); return; }
                if (type.GetField(name, Members | BindingFlags.DeclaredOnly) is { } field)
                { field.SetValue(target, value); return; }
            }
            throw new MissingMemberException(target.GetType().FullName, name);
        }
        private static object Get(object target, string name)
        {
            for (Type? type = target.GetType(); type is not null; type = type.BaseType)
            {
                if (type.GetProperty(name, Members | BindingFlags.DeclaredOnly) is { } property)
                    return property.GetValue(target)!;
                if (type.GetField(name, Members | BindingFlags.DeclaredOnly) is { } field)
                    return field.GetValue(target)!;
            }
            throw new MissingMemberException(target.GetType().FullName, name);
        }
        public bool SessionIsDisposed => (bool)Get(_session, "IsDisposed");
        public int PendingCallbacks => ((IDictionary)_session.GetType().GetField("RequestCallback", Members)!.GetValue(_session)!).Count;
        public int AvailableCallSlots => ((SemaphoreSlim)typeof(FantasyBackendProbe).GetField("_calls", Members)!.GetValue(Probe)!).CurrentCount;
        public int OwnerActions => (int)Get(_context.GetType().GetField("_queue", Members)!.GetValue(_context)!, "Count");
        public Task<ServiceReply> Call(CancellationToken cancellation = default)
        {
            Task<ServiceReply> call = Probe.CallAsync("lifecycle-regression", ReadOnlyMemory<byte>.Empty, ct: cancellation).AsTask();
            _calls.Add(call);
            return call;
        }
        public void Post(Action action) => _context.GetType().GetMethod("Post", [typeof(Action)])!.Invoke(_context, [action]);
        public void Pump()
        {
            SynchronizationContext? prior = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext((SynchronizationContext)_context);
                _context.GetType().GetMethod("Update")!.Invoke(_context, null);
            }
            finally { SynchronizationContext.SetSynchronizationContext(prior); }
        }
        public async Task PumpUntilCompleted(Task task)
        {
            await WaitUntil(() => { Pump(); return task.IsCompleted; }, TimeSpan.FromSeconds(3));
        }
        public void AssertActualRpcPending()
        {
            Assert.That(Channel.SendCount, Is.EqualTo(1));
            Assert.That(PendingCallbacks, Is.EqualTo(1), "The actual vendor Session.Call must register its own FTask callback.");
            Assert.That(AvailableCallSlots, Is.EqualTo(7));
        }
        public void AssertActuallyClosed()
        {
            Assert.That(SessionIsDisposed, Is.True, "Real Entity.RuntimeId must be cleared by Session.Dispose.");
            Assert.That(PendingCallbacks, Is.Zero);
            Assert.That(AvailableCallSlots, Is.EqualTo(8), "The vendor failure callback must resume RPC finally and release its slot.");
            Assert.That(Channel.DisposeCount, Is.EqualTo(1), "Actual Session.OnDispose must run once.");
            MethodInfo getEntity = _scene.GetType().GetMethods(Members).Single(method =>
                method.Name == "GetEntity" && !method.IsGenericMethod && method.GetParameters().Length == 1);
            Assert.That(getEntity.Invoke(_scene, [_sessionRuntimeId]), Is.Null);
        }
        public async ValueTask DisposeAsync()
        {
            // Runs even after a RED assertion. Drain the real owner queue, force
            // final Session closure if needed, and observe all caller faults.
            Task closing = Probe.DisposeAsync().AsTask();
            try
            {
                Pump();
                if (!SessionIsDisposed)
                {
                    Post(() => _session.GetType().GetMethod("Dispose")!.Invoke(_session, null));
                    Pump();
                }
                await PumpUntilCompleted(closing);
                await closing;
                foreach (Task<ServiceReply> call in _calls)
                {
                    await PumpUntilCompleted(call);
                    try { await call; }
                    catch (Exception error) when (error is OperationCanceledException or TimeoutException or ServiceException) { }
                }
                Pump();
            }
            finally
            {
                foreach (Task call in _calls) if (call.IsFaulted) _ = call.Exception;
                _entityPool.GetType().GetMethod("Dispose")!.Invoke(_entityPool, null);
                Set(_scene, "RuntimeId", 0L);
            }
        }
    }
}
