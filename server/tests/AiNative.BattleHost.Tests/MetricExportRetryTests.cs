using AiNative.BattleHost;
using NUnit.Framework;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace AiNative.BattleHost.Tests;

public sealed class MetricExportRetryTests
{
    [Test]
    public void BackoffStillObservesMetricCardinalityAndTags()
    {
        ManualClock clock = new();
        TelemetryExportHealth health = new(true);
        using var meter = new System.Diagnostics.Metrics.Meter(BattleMetrics.MeterName);
        using MeterProvider provider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(BattleMetrics.MeterName)
            .AddReader(new PeriodicExportingMetricReader(
                new TrackingMetricExporter(new RecoverableExporter(), health, clock),
                exportIntervalMilliseconds: 60_000,
                exportTimeoutMilliseconds: 100))
            .Build();
        var counter = meter.CreateCounter<long>("test.cardinality");
        counter.Add(1);
        Assert.That(provider.ForceFlush(1000), Is.False);
        counter.Add(1, new KeyValuePair<string, object?>("unexpected", "tag"));
        Assert.That(provider.ForceFlush(1000), Is.False);
        var snapshot = health.Snapshot();
        Assert.That(snapshot.MetricExportAttempts, Is.EqualTo(1));
        Assert.That(snapshot.MetricExportBackoffs, Is.EqualTo(1));
        Assert.That(snapshot.ProjectMetricTagViolations, Is.GreaterThan(0));
        Assert.That(snapshot.ProjectMetricSeries, Is.EqualTo(2));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void OutageRetriesAreBoundedAndSuccessfulRecoveryResetsDelay(bool throws)
    {
        ManualClock clock = new();
        TelemetryExportHealth health = new(true);
        RecoverableExporter inner = new() { Throws = throws };
        using TrackingMetricExporter exporter = new(inner, health, clock);
        Batch<Metric> batch = new(Array.Empty<Metric>(), 0);

        Assert.That(exporter.Export(in batch), Is.EqualTo(ExportResult.Failure));
        foreach (int delaySeconds in new[] { 1, 2, 4, 8, 16, 30, 30 })
        {
            int attempts = inner.Attempts;
            clock.AdvanceMilliseconds(delaySeconds * 1000 - 1);
            Assert.That(exporter.Export(in batch), Is.EqualTo(ExportResult.Failure));
            Assert.That(inner.Attempts, Is.EqualTo(attempts), "Backoff must not call the exporter.");
            clock.AdvanceMilliseconds(1);
            Assert.That(exporter.Export(in batch), Is.EqualTo(ExportResult.Failure));
            Assert.That(inner.Attempts, Is.EqualTo(attempts + 1));
        }

        TelemetryExportSnapshot failed = health.Snapshot();
        Assert.That(failed.MetricExportAttempts, Is.EqualTo(8));
        Assert.That(failed.MetricExportFailures, Is.EqualTo(8));
        Assert.That(failed.MetricExportBackoffs, Is.EqualTo(7));

        inner.Healthy = true;
        clock.AdvanceMilliseconds(30_000);
        Assert.That(exporter.Export(in batch), Is.EqualTo(ExportResult.Success));
        Assert.That(exporter.Export(in batch), Is.EqualTo(ExportResult.Success));
        inner.Healthy = false;
        Assert.That(exporter.Export(in batch), Is.EqualTo(ExportResult.Failure));
        clock.AdvanceMilliseconds(1000);
        Assert.That(exporter.Export(in batch), Is.EqualTo(ExportResult.Failure));
        Assert.That(inner.Attempts, Is.EqualTo(12), "A recovered exporter must restart at the one-second delay.");
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _timestamp;
        public void AdvanceMilliseconds(long milliseconds) => _timestamp += milliseconds;
    }

    private sealed class RecoverableExporter : BaseExporter<Metric>
    {
        public int Attempts { get; private set; }
        public bool Throws { get; init; }
        public bool Healthy { get; set; }

        public override ExportResult Export(in Batch<Metric> batch)
        {
            Attempts++;
            if (Healthy) return ExportResult.Success;
            if (Throws) throw new InvalidOperationException("Simulated collector outage");
            return ExportResult.Failure;
        }
    }
}
