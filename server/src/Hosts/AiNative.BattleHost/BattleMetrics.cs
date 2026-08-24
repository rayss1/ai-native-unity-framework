using System.Diagnostics.Metrics;
using System.Diagnostics;
using System.Text.Json;

namespace AiNative.BattleHost;

public sealed class BattleMetrics : IDisposable
{
    public const string MeterName = "AiNative.BattleHost";
    private readonly Meter _meter = new(MeterName, "0.1.0");
    private readonly Histogram<double> _tickDurationMilliseconds;
    private readonly Counter<long> _droppedDiagnostics;
    private readonly Counter<long> _droppedReplayRecords;
    private readonly string? _acceptanceReportPath;
    private readonly double[]? _tickSamples;
    private readonly double[]? _gameplaySamples;
    private readonly long[]? _gameplayAllocations;
    private readonly int _warmupTicks;
    private readonly long _startedTimestamp = Stopwatch.GetTimestamp();
    private int _acceptanceReportWritten;
    private int _sampleCount;
    private int _observedTickCount;

    public BattleMetrics(IConfiguration? configuration = null)
    {
        _tickDurationMilliseconds = _meter.CreateHistogram<double>("battle.tick.duration", "ms");
        _droppedDiagnostics = _meter.CreateCounter<long>("battle.diagnostics.dropped");
        _droppedReplayRecords = _meter.CreateCounter<long>("battle.replay.records.dropped");
        _acceptanceReportPath = configuration?["AINATIVE_ACCEPTANCE_REPORT_PATH"];
        if (!string.IsNullOrWhiteSpace(_acceptanceReportPath))
        {
            int capacity = configuration!.GetValue("AINATIVE_ACCEPTANCE_MAX_TICKS", 300_000);
            _warmupTicks = configuration!.GetValue("AINATIVE_ACCEPTANCE_WARMUP_TICKS", 600);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
            ArgumentOutOfRangeException.ThrowIfNegative(_warmupTicks);
            _tickSamples = new double[capacity];
            _gameplaySamples = new double[capacity];
            _gameplayAllocations = new long[capacity];
        }
    }

    public void RecordTick(double milliseconds)
    {
        _tickDurationMilliseconds.Record(milliseconds);
        int observedTick = _observedTickCount++;
        if (observedTick < _warmupTicks)
        {
            return;
        }

        if (_tickSamples is not null && _sampleCount < _tickSamples.Length)
        {
            _tickSamples[_sampleCount] = milliseconds;
            _sampleCount++;
        }
    }

    public void RecordGameplayTick(double milliseconds, long allocatedBytes)
    {
        if (_gameplaySamples is null ||
            _gameplayAllocations is null ||
            _observedTickCount < _warmupTicks ||
            _sampleCount >= _gameplaySamples.Length)
        {
            return;
        }

        int index = _sampleCount;
        _gameplaySamples[index] = milliseconds;
        _gameplayAllocations[index] = allocatedBytes;
    }

    public void RecordDroppedDiagnostic() => _droppedDiagnostics.Add(1);

    public void RecordReplayDropped() => _droppedReplayRecords.Add(1);

    public void WriteAcceptanceReport(ulong finalTick, ulong finalStateHash)
    {
        if (_acceptanceReportPath is null ||
            _tickSamples is null ||
            _gameplaySamples is null ||
            _gameplayAllocations is null ||
            Interlocked.Exchange(ref _acceptanceReportWritten, 1) != 0)
        {
            return;
        }

        int count = _sampleCount;
        double[] ticks = _tickSamples.AsSpan(0, count).ToArray();
        double[] gameplay = _gameplaySamples.AsSpan(0, count).ToArray();
        Array.Sort(ticks);
        Array.Sort(gameplay);
        long allocatedBytes = _gameplayAllocations.AsSpan(0, count).ToArray().Sum();
        int slowTicks = ticks.Count(value => value > 16.67);
        double tickP99 = Percentile(ticks, 0.99);
        double tickP999 = Percentile(ticks, 0.999);
        double gameplayP99 = Percentile(gameplay, 0.99);
        RuntimeSoakReport report = new(
            EvidenceClass: "release-equivalent-host-core",
            WarmupTicks: _warmupTicks,
            ObservedTickCount: _observedTickCount,
            SampleCount: count,
            FinalTick: finalTick,
            FinalStateHash: finalStateHash.ToString("x16", System.Globalization.CultureInfo.InvariantCulture),
            ElapsedSeconds: Stopwatch.GetElapsedTime(_startedTimestamp).TotalSeconds,
            TickP99Milliseconds: tickP99,
            TickP999Milliseconds: tickP999,
            SlowTickPercentage: count == 0 ? 100 : slowTicks * 100d / count,
            GameplayP99Milliseconds: gameplayP99,
            GameplayAllocatedBytes: allocatedBytes,
            Runtime: Environment.Version.ToString(),
            OperatingSystem: Environment.OSVersion.ToString(),
            ProcessorCount: Environment.ProcessorCount,
            SourceCommit: Environment.GetEnvironmentVariable("AINATIVE_SOURCE_COMMIT") ?? "unrecorded",
            FantasyCommit: Environment.GetEnvironmentVariable("AINATIVE_FANTASY_COMMIT") ?? "unrecorded",
            ProtocolIdentity: Environment.GetEnvironmentVariable("AINATIVE_PROTOCOL_IDENTITY") ?? "unrecorded");
        string fullPath = Path.GetFullPath(_acceptanceReportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        }) + Environment.NewLine);
    }

    public void Dispose() => _meter.Dispose();

    private static double Percentile(double[] samples, double percentile) =>
        samples.Length == 0
            ? 0
            : samples[Math.Clamp((int)Math.Ceiling(samples.Length * percentile) - 1, 0, samples.Length - 1)];
}

internal sealed record RuntimeSoakReport(
    string EvidenceClass,
    int WarmupTicks,
    int ObservedTickCount,
    int SampleCount,
    ulong FinalTick,
    string FinalStateHash,
    double ElapsedSeconds,
    double TickP99Milliseconds,
    double TickP999Milliseconds,
    double SlowTickPercentage,
    double GameplayP99Milliseconds,
    long GameplayAllocatedBytes,
    string Runtime,
    string OperatingSystem,
    int ProcessorCount,
    string SourceCommit,
    string FantasyCommit,
    string ProtocolIdentity);
