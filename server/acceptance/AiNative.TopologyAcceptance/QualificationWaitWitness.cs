using System.Diagnostics;
using System.Text.Json;

// Optional diagnostic witness, independent of the driver's timer/ThreadPool continuation path.
// Construct only when diagnostics are enabled. This does not schedule or send any inputs.
public sealed class QualificationWaitWitness : IDisposable
{
    private const int SlowCapacity = 4096;
    private readonly object _lifecycle = new();
    private readonly Func<long> _clock;
    private readonly Action _sleep;
    private readonly QualificationTimingHistogram _wait = new();
    private readonly SlowCycle[] _slow = new SlowCycle[SlowCapacity];
    private Thread? _thread;
    private volatile bool _stop;
    private bool _started, _closed;
    private long _measurementStart, _measurementEnd, _omitted;
    private int _slowCount;
    private string? _failure;

    public QualificationWaitWitness() : this(Stopwatch.GetTimestamp, () => Thread.Sleep(1)) { }

    // Deterministic clock/sleep seam for tests; production always uses the constructor above.
    internal QualificationWaitWitness(Func<long> clock, Action sleep)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _sleep = sleep ?? throw new ArgumentNullException(nameof(sleep));
    }

    public void Start(long measurementStartTicks, long measurementEndTicks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(measurementStartTicks);
        if (measurementEndTicks <= measurementStartTicks)
            throw new ArgumentOutOfRangeException(nameof(measurementEndTicks));
        lock (_lifecycle)
        {
            if (_started || _closed) throw new InvalidOperationException("Witness cannot be restarted.");
            _measurementStart = measurementStartTicks;
            _measurementEnd = measurementEndTicks;
            _started = true;
            _thread = new Thread(Run) { IsBackground = true, Name = "Qualification wait witness" };
            try { _thread.Start(); }
            catch { _thread = null; _closed = true; throw; }
        }
    }

    public void Stop()
    {
        Thread? owned;
        lock (_lifecycle) { _stop = true; _closed = true; owned = _thread; }
        if (owned == Thread.CurrentThread) throw new InvalidOperationException("Witness cannot join itself.");
        owned?.Join();
    }

    public void Dispose() => Stop();

    public JsonElement Report()
    {
        lock (_lifecycle)
        {
            if (_thread?.IsAlive == true) throw new InvalidOperationException("Stop the witness before reporting.");
            _thread?.Join();
            return JsonSerializer.SerializeToElement(new
            {
                scope = "Diagnostic only; dedicated Thread.Sleep(1) witness; GC counts are correlation observations, not causal attribution",
                windowMethod = "record waits whose resume timestamp is in [measurementStartTicks, measurementEndTicks); a wait may begin before the window",
                measurementStartTicks = _measurementStart,
                measurementEndTicks = _measurementEnd,
                requestedSleepMilliseconds = 1,
                slowThresholdStopwatchTicks = Stopwatch.Frequency / 60,
                slowThresholdMethod = "strictly greater than one 60 Hz period (integer Stopwatch ticks)",
                started = _started,
                threadAlive = false,
                failure = _failure,
                wait = _wait.Report(),
                slowCycles = _slow.AsSpan(0, _slowCount).ToArray(),
                slowCyclesCapacity = SlowCapacity,
                slowCyclesOmitted = _omitted
            });
        }
    }

    private void Run()
    {
        try
        {
            while (!_stop)
            {
                long started = _clock();
                if (started >= _measurementEnd) break;
                _sleep();
                long ended = _clock();
                if (ended < started) throw new InvalidOperationException("Witness clock moved backwards.");
                if (ended >= _measurementEnd) break;
                if (ended < _measurementStart) continue;
                long elapsed = ended - started;
                _wait.Record(elapsed);
                if (elapsed <= Stopwatch.Frequency / 60) continue;
                if (_slowCount < SlowCapacity)
                    _slow[_slowCount++] = new(started, ended, Environment.CurrentManagedThreadId,
                        GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
                else if (_omitted < long.MaxValue) _omitted++;
            }
        }
        catch (Exception error)
        {
            // A diagnostic failure must be visible, and must not crash the qualification process.
            _failure = error.GetType().Name + ": " + error.Message;
        }
    }

    private readonly record struct SlowCycle(long StartedTicks, long EndedTicks, int ThreadId, int Gen0, int Gen1, int Gen2);
}
