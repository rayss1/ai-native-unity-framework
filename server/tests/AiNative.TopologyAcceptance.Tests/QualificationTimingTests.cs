using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using NUnit.Framework;

namespace AiNative.Topology.ToolTests;

public sealed class QualificationTimingTests
{
    static Type Feature(string name) => Assembly.Load("AiNative.TopologyAcceptance").GetType(name)
        ?? throw new AssertionException("Missing diagnostic type: " + name);
    static object Histogram() => Activator.CreateInstance(Feature("QualificationTimingHistogram"))!;
    static Action<long> Recorder(object histogram) => histogram.GetType().GetMethod("Record")!.CreateDelegate<Action<long>>(histogram);
    static JsonElement Report(object value) => (JsonElement)value.GetType().GetMethod("Report")!.Invoke(value, null)!;

    [Test]
    public void EmptyHistogramHasNoInventedPercentiles()
    {
        var report = Report(Histogram());
        Assert.That(report.GetProperty("count").GetInt64(), Is.Zero);
        Assert.That(report.GetProperty("totalStopwatchTicks").GetInt64(), Is.Zero);
        Assert.That(report.GetProperty("p99UpperBoundStopwatchTicks").ValueKind, Is.EqualTo(JsonValueKind.Null));
        Assert.That(report.GetProperty("stopwatchFrequency").GetInt64(), Is.EqualTo(Stopwatch.Frequency));
    }

    [Test]
    public void PercentilesUseNearestRankAndConservativeBucketUpperBounds()
    {
        var h = Histogram(); var record = Recorder(h);
        for (int i = 0; i < 50; i++) record(0);
        for (int i = 0; i < 45; i++) record(3);
        for (int i = 0; i < 4; i++) record(9);
        record(33);
        var r = Report(h);
        Assert.That(r.GetProperty("count").GetInt64(), Is.EqualTo(100));
        Assert.That(r.GetProperty("totalStopwatchTicks").GetInt64(), Is.EqualTo(204));
        Assert.That(r.GetProperty("maxStopwatchTicks").GetInt64(), Is.EqualTo(33));
        Assert.That(r.GetProperty("p50UpperBoundStopwatchTicks").GetInt64(), Is.Zero);
        Assert.That(r.GetProperty("p95UpperBoundStopwatchTicks").GetInt64(), Is.EqualTo(4));
        Assert.That(r.GetProperty("p99UpperBoundStopwatchTicks").GetInt64(), Is.EqualTo(16));
        Assert.That(r.GetProperty("p99UpperBoundMicroseconds").GetDouble(), Is.EqualTo(16 * 1_000_000d / Stopwatch.Frequency));
        Assert.That(r.GetProperty("percentileMethod").GetString(), Does.Contain("upper bound"));
    }

    [Test]
    public void ExactBoundariesAndTailBucketNeverUnderstateSamples()
    {
        foreach (long elapsed in new[] { 0L, 1L, 2L, 3L, 4L, 1L << 62, (1L << 62) + 1, long.MaxValue })
        {
            var h = Histogram(); Recorder(h)(elapsed); var r = Report(h);
            long expected = elapsed == 3 ? 4 : elapsed > (1L << 62) ? long.MaxValue : elapsed;
            Assert.That(r.GetProperty("p99UpperBoundStopwatchTicks").GetInt64(), Is.EqualTo(expected));
            Assert.That(r.GetProperty("maxStopwatchTicks").GetInt64(), Is.EqualTo(elapsed));
        }
    }

    [Test]
    public void NegativeSampleIsRejectedWithoutChangingCounters()
    {
        var h = Histogram(); var record = Recorder(h);
        Assert.Throws<ArgumentOutOfRangeException>(() => record(-1));
        Assert.That(Report(h).GetProperty("count").GetInt64(), Is.Zero);
    }

    [Test]
    public void TotalSaturatesWithoutLosingSampleCountOrMaximum()
    {
        var h = Histogram(); var record = Recorder(h); record(long.MaxValue); record(1);
        var r = Report(h);
        Assert.That(r.GetProperty("count").GetInt64(), Is.EqualTo(2));
        Assert.That(r.GetProperty("totalStopwatchTicks").GetInt64(), Is.EqualTo(long.MaxValue));
        Assert.That(r.GetProperty("totalSaturated").GetBoolean(), Is.True);
        Assert.That(r.GetProperty("maxStopwatchTicks").GetInt64(), Is.EqualTo(long.MaxValue));
    }

    [Test]
    public void SampleCapacityFreezesHistogramAndReportsTruncation()
    {
        var h = Histogram(); const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        h.GetType().GetField("_count", flags)!.SetValue(h, long.MaxValue - 1);
        var buckets = (long[])h.GetType().GetField("_buckets", flags)!.GetValue(h)!;
        buckets[0] = long.MaxValue - 1;
        var record = Recorder(h); record(0); record(100);
        var r = Report(h);
        Assert.That(r.GetProperty("count").GetInt64(), Is.EqualTo(long.MaxValue));
        Assert.That(r.GetProperty("sampleCountSaturated").GetBoolean(), Is.True);
        Assert.That(r.GetProperty("maxStopwatchTicks").GetInt64(), Is.Zero);
        Assert.That(r.GetProperty("p99UpperBoundStopwatchTicks").GetInt64(), Is.Zero);
    }

    [Test]
    public void RecordHasNoManagedAllocationAfterWarmup()
    {
        var h = Histogram(); var record = Recorder(h);
        for (int i = 0; i < 1000; i++) record(i);
        Assert.That(MeasureRecordingAllocation(record), Is.Zero);
    }

    // Isolate the measured loop from NUnit's boxed assertion argument and constraint creation.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static long MeasureRecordingAllocation(Action<long> record)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) record(i);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Test]
    public void DriverOwnsFiveIndependentSerializableHistograms()
    {
        var type = Feature("QualificationDriverTiming"); var driver = Activator.CreateInstance(type)!;
        string[] names = ["Receive", "SendBatch", "DelayResume", "LoopWork", "DeadlineLateness"];
        for (int i = 0; i < names.Length; i++) Recorder(type.GetProperty(names[i])!.GetValue(driver)!)(i + 1);
        var r = Report(driver);
        Assert.That(r.EnumerateObject().Count(), Is.EqualTo(5));
        for (int i = 0; i < names.Length; i++)
        {
            var entry = r.GetProperty(char.ToLowerInvariant(names[i][0]) + names[i][1..]);
            Assert.That(entry.GetProperty("count").GetInt64(), Is.EqualTo(1));
            Assert.That(entry.GetProperty("maxStopwatchTicks").GetInt64(), Is.EqualTo(i + 1));
        }
        Assert.DoesNotThrow(() => JsonSerializer.Serialize(driver));
    }
}
