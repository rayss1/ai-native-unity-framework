using System.Diagnostics.Metrics;

namespace AiNative.BattleHost;

public sealed class BattleMetrics : IDisposable
{
    public const string MeterName = "AiNative.BattleHost";
    private readonly Meter _meter = new(MeterName, "0.1.0");
    private readonly Histogram<double> _tickDurationMilliseconds;
    private readonly Counter<long> _droppedDiagnostics;

    public BattleMetrics()
    {
        _tickDurationMilliseconds = _meter.CreateHistogram<double>("battle.tick.duration", "ms");
        _droppedDiagnostics = _meter.CreateCounter<long>("battle.diagnostics.dropped");
    }

    public void RecordTick(double milliseconds) => _tickDurationMilliseconds.Record(milliseconds);

    public void RecordDroppedDiagnostic() => _droppedDiagnostics.Add(1);

    public void Dispose() => _meter.Dispose();
}
