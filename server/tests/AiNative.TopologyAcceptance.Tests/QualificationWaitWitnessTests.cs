using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using NUnit.Framework;

namespace AiNative.Topology.ToolTests;

public sealed class QualificationWaitWitnessTests
{
    static Type Feature() => Assembly.Load("AiNative.TopologyAcceptance").GetType("QualificationWaitWitness")
        ?? throw new AssertionException("Missing diagnostic type: QualificationWaitWitness");
    static object Create(Func<long> clock, Action sleep) => Activator.CreateInstance(Feature(),
        BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { clock, sleep }, null)!;
    static void Start(object value, long start, long end) => value.GetType().GetMethod("Start")!
        .CreateDelegate<Action<long, long>>(value)(start, end);
    static void Stop(object value) => value.GetType().GetMethod("Stop")!.CreateDelegate<Action>(value)();
    static JsonElement Report(object value) => value.GetType().GetMethod("Report")!.CreateDelegate<Func<JsonElement>>(value)();

    [Test]
    public void NeverStartedStopAndDisposeAreIdempotentAndPreventRestart()
    {
        var value = Create(() => 0, () => { });
        Stop(value); Stop(value); ((IDisposable)value).Dispose();
        Assert.That(Report(value).GetProperty("wait").GetProperty("count").GetInt64(), Is.Zero);
        Assert.Throws<InvalidOperationException>(() => Start(value, 0, 100));
    }

    [Test]
    public void InvalidWindowsAreRejectedBeforeStartingThread()
    {
        using var value = (IDisposable)Create(() => 0, () => { });
        Assert.Throws<ArgumentOutOfRangeException>(() => Start(value, -1, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => Start(value, 100, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => Start(value, 101, 100));
    }

    [Test]
    public void OwnedThreadFiltersByResumeWindowAndStopsAtEnd()
    {
        long now = 0; int threadId = 0; bool poolThread = true;
        using var endReached = new ManualResetEventSlim();
        using var value = (IDisposable)Create(() => now, () =>
        {
            threadId = Environment.CurrentManagedThreadId; poolThread = Thread.CurrentThread.IsThreadPoolThread;
            now += 10; if (now == 100) endReached.Set();
        });
        Start(value, 25, 100);
        Assert.That(endReached.Wait(TimeSpan.FromSeconds(10)), Is.True, "owned thread did not reach deterministic window end");
        Stop(value); Stop(value);
        var r = Report(value);
        Assert.That(r.GetProperty("wait").GetProperty("count").GetInt64(), Is.EqualTo(7));
        Assert.That(r.GetProperty("wait").GetProperty("totalStopwatchTicks").GetInt64(), Is.EqualTo(70));
        Assert.That(threadId, Is.Not.EqualTo(Environment.CurrentManagedThreadId));
        Assert.That(poolThread, Is.False);
        Assert.That(r.GetProperty("threadAlive").GetBoolean(), Is.False);
        Assert.Throws<InvalidOperationException>(() => Start(value, 100, 200));
    }

    [Test]
    public void SlowSamplesAreBoundedAndRetainCorrelationFields()
    {
        long now = 0, step = Stopwatch.Frequency / 60 + 1, end = step * 4101;
        using var endReached = new ManualResetEventSlim();
        using var value = (IDisposable)Create(() => now, () => { now += step; if (now == end) endReached.Set(); });
        Start(value, 0, end);
        Assert.That(endReached.Wait(TimeSpan.FromSeconds(10)), Is.True);
        Stop(value); var r = Report(value);
        Assert.That(r.GetProperty("wait").GetProperty("count").GetInt64(), Is.EqualTo(4100));
        Assert.That(r.GetProperty("slowCycles").GetArrayLength(), Is.EqualTo(4096));
        Assert.That(r.GetProperty("slowCyclesOmitted").GetInt64(), Is.EqualTo(4));
        var first = r.GetProperty("slowCycles")[0];
        Assert.That(first.GetProperty("StartedTicks").GetInt64(), Is.Zero);
        Assert.That(first.GetProperty("EndedTicks").GetInt64(), Is.EqualTo(step));
        Assert.That(first.GetProperty("ThreadId").GetInt32(), Is.GreaterThan(0));
        foreach (var name in new[] { "Gen0", "Gen1", "Gen2" })
            Assert.That(first.GetProperty(name).GetInt32(), Is.GreaterThanOrEqualTo(0));
    }

    [Test]
    public void ReportRejectsRunningThreadAndDisposeJoinsIt()
    {
        using var sleeping = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var stopEntered = new ManualResetEventSlim();
        using var stopped = new ManualResetEventSlim();
        var value = (IDisposable)Create(() => 0, () => { sleeping.Set(); release.Wait(); });
        Start(value, 0, 100);
        Assert.That(sleeping.Wait(TimeSpan.FromSeconds(10)), Is.True);
        Assert.Throws<InvalidOperationException>(() => Report(value));
        var stopper = new Thread(() => { stopEntered.Set(); value.Dispose(); stopped.Set(); });
        stopper.Start();
        try
        {
            Assert.That(stopEntered.Wait(TimeSpan.FromSeconds(10)), Is.True);
            Assert.That(stopped.IsSet, Is.False, "Dispose must not return while owned thread is blocked");
        }
        finally { release.Set(); stopper.Join(); value.Dispose(); }
        Assert.That(stopped.IsSet, Is.True);
        Assert.That(Report(value).GetProperty("threadAlive").GetBoolean(), Is.False);
    }
}
