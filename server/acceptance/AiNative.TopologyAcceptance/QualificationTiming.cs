using System.Diagnostics;
using System.Numerics;
using System.Text.Json;

// Diagnostic only. One driver owns each instance; read reports after that driver stops.
// Successful Record calls allocate nothing. Reporting allocates and belongs off the driver path.
public sealed class QualificationTimingHistogram
{
    // Exact zero, then upper bounds 1, 2, 4, ... 2^62, and a long.MaxValue tail.
    private readonly long[] _buckets = new long[65];
    private long _count, _total, _maximum;
    private bool _totalSaturated, _sampleCountSaturated;

    public void Record(long elapsedStopwatchTicks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elapsedStopwatchTicks);
        // Freeze the first long.MaxValue samples. Keep every bucket and percentile consistent
        // with count, rather than wrapping a counter or silently changing the denominator.
        if (_count == long.MaxValue) { _sampleCountSaturated = true; return; }
        int bucket = elapsedStopwatchTicks <= 1 ? (int)elapsedStopwatchTicks
            : BitOperations.Log2((ulong)(elapsedStopwatchTicks - 1)) + 2;
        _buckets[bucket]++;
        _count++;
        _maximum = Math.Max(_maximum, elapsedStopwatchTicks);
        if (elapsedStopwatchTicks > long.MaxValue - _total)
        { _total = long.MaxValue; _totalSaturated = true; }
        else _total += elapsedStopwatchTicks;
    }

    public JsonElement Report()
    {
        long? p50 = PercentileUpperBound(50), p95 = PercentileUpperBound(95), p99 = PercentileUpperBound(99);
        return JsonSerializer.SerializeToElement(new
        {
            count = _count,
            totalStopwatchTicks = _total,
            maxStopwatchTicks = _maximum,
            totalSaturated = _totalSaturated,
            sampleCountSaturated = _sampleCountSaturated,
            stopwatchFrequency = Stopwatch.Frequency,
            maxMicroseconds = Microseconds(_maximum),
            p50UpperBoundStopwatchTicks = p50,
            p95UpperBoundStopwatchTicks = p95,
            p99UpperBoundStopwatchTicks = p99,
            p50UpperBoundMicroseconds = Microseconds(p50),
            p95UpperBoundMicroseconds = Microseconds(p95),
            p99UpperBoundMicroseconds = Microseconds(p99),
            bucketCount = _buckets.Length,
            tailBucketLowerExclusiveStopwatchTicks = 1L << 62,
            percentileMethod = "approximate nearest-rank histogram upper bound: exact zero, powers of two in Stopwatch ticks, long.MaxValue tail",
            overflowPolicy = "total saturates at long.MaxValue; after long.MaxValue recorded samples further samples are omitted and the histogram is frozen; flags disclose saturation"
        });
    }

    private long? PercentileUpperBound(int percentile)
    {
        if (_count == 0) return null;
        // ceil(count * percentile / 100), without multiplication overflow or floating rounding.
        long rank = _count / 100 * percentile + ((_count % 100 * percentile + 99) / 100);
        long cumulative = 0;
        for (int i = 0; i < _buckets.Length; i++)
        {
            cumulative += _buckets[i];
            if (cumulative >= rank) return i == 0 ? 0 : i == 64 ? long.MaxValue : 1L << (i - 1);
        }
        throw new InvalidOperationException("diagnostic-histogram-count-mismatch");
    }

    private static double? Microseconds(long? ticks) => ticks.HasValue
        ? ticks.Value * (1_000_000d / Stopwatch.Frequency) : null;
}

// Construct only when diagnostics are enabled. Integration owns measurement-window filtering.
public sealed class QualificationDriverTiming
{
    public QualificationTimingHistogram Receive { get; } = new();
    public QualificationTimingHistogram SendBatch { get; } = new();
    public QualificationTimingHistogram DelayResume { get; } = new();
    public QualificationTimingHistogram LoopWork { get; } = new();
    public QualificationTimingHistogram DeadlineLateness { get; } = new();

    public JsonElement Report() => JsonSerializer.SerializeToElement(new
    {
        receive = Receive.Report(),
        sendBatch = SendBatch.Report(),
        delayResume = DelayResume.Report(),
        loopWork = LoopWork.Report(),
        deadlineLateness = DeadlineLateness.Report()
    });
}
