using System.Diagnostics;

namespace AiNative.BattleHost;

internal sealed class MonotonicFixedRatePacer
{
    private readonly int _ticksPerSecond;
    private readonly long _timestampFrequency;
    private readonly long _startedTimestamp;
    private long _nextTick = 1;

    public MonotonicFixedRatePacer(int ticksPerSecond)
        : this(ticksPerSecond, Stopwatch.Frequency, Stopwatch.GetTimestamp())
    {
    }

    internal MonotonicFixedRatePacer(
        int ticksPerSecond,
        long timestampFrequency,
        long startedTimestamp)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ticksPerSecond);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timestampFrequency);
        _ticksPerSecond = ticksPerSecond;
        _timestampFrequency = timestampFrequency;
        _startedTimestamp = startedTimestamp;
    }

    public async ValueTask WaitForNextTickAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long now = Stopwatch.GetTimestamp();
            long deadline = checked(
                _startedTimestamp + GetDeadlineOffset(_nextTick, _ticksPerSecond, _timestampFrequency));
            long remaining = deadline - now;
            if (remaining <= 0)
            {
                long elapsed = Math.Max(0, now - _startedTimestamp);
                long latestDueTick = checked(elapsed * _ticksPerSecond / _timestampFrequency);
                _nextTick = Math.Max(_nextTick, latestDueTick) + 1;
                return;
            }

            long delayMilliseconds = Math.Max(
                1,
                checked((remaining * 1000 + _timestampFrequency - 1) / _timestampFrequency));
            await Task.Delay(
                TimeSpan.FromMilliseconds(Math.Min(delayMilliseconds, int.MaxValue)),
                cancellationToken);
        }
    }

    internal static long GetDeadlineOffset(
        long tick,
        int ticksPerSecond,
        long timestampFrequency)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tick);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ticksPerSecond);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timestampFrequency);
        return checked((tick * timestampFrequency + ticksPerSecond - 1) / ticksPerSecond);
    }
}
