using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using AiNative.BattleHost;
using AiNative.Gameplay;
using AiNative.Protocol.V1;
using AiNative.Realtime;
using AiNative.RuntimeAcceptance;
using AiNative.Server.Protocol;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using NUnit.Framework;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace AiNative.BattleHost.Tests;

public sealed class HealthAndBudgetTests
{
    [Test]
    public void FixedRatePacerPreservesFractionalSixtyHertzDeadlines()
    {
        long[] deadlines = Enumerable.Range(1, 60)
            .Select(tick => MonotonicFixedRatePacer.GetDeadlineOffset(tick, 60, 1000))
            .ToArray();
        long[] intervals = deadlines
            .Zip(new long[] { 0 }.Concat(deadlines[..^1]), (deadline, previous) => deadline - previous)
            .ToArray();

        Assert.That(deadlines[^1], Is.EqualTo(1000));
        Assert.That(intervals.Count(interval => interval == 16), Is.EqualTo(20));
        Assert.That(intervals.Count(interval => interval == 17), Is.EqualTo(40));
        Assert.That(intervals.Sum(), Is.EqualTo(1000));
    }

    [Test]
    public void ReadinessRequiresBothRoomAndNetworkWhenFantasyIsEnabled()
    {
        RuntimeReadiness readiness = new(networkRequired: true);

        readiness.MarkRoomReady();
        Assert.That(readiness.IsReady, Is.False);

        readiness.MarkNetworkReady();
        Assert.That(readiness.IsReady, Is.True);

        readiness.BeginDrain();
        Assert.That(readiness.IsReady, Is.False);
    }

    [Test]
    public async Task DrainMakesReadinessUnavailableWithoutFailingLiveness()
    {
        await using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("AINATIVE_ENABLE_EVALUATION_ENDPOINTS", "true");
                builder.UseSetting("AINATIVE_FANTASY_ENABLED", "false");
            });
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage live = await client.GetAsync("/health/live");
        HttpResponseMessage ready = await client.GetAsync("/health/ready");
        HttpResponseMessage drain = await client.PostAsync("/admin/drain", content: null);
        HttpResponseMessage draining = await client.GetAsync("/health/ready");

        Assert.That(live.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(ready.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(drain.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        Assert.That(draining.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
    }

    [Test]
    public async Task TelemetryHealthDoesNotAffectRuntimeReadiness()
    {
        await using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseSetting("AINATIVE_FANTASY_ENABLED", "false"));
        using HttpClient client = factory.CreateClient();

        using JsonDocument telemetry = JsonDocument.Parse(
            await client.GetStringAsync("/health/telemetry"));
        JsonElement root = telemetry.RootElement;

        Assert.That(root.GetProperty("status").GetString(), Is.EqualTo("healthy"));
        Assert.That(root.GetProperty("exporterConfigured").GetBoolean(), Is.False);
        Assert.That(root.GetProperty("metricExportFailures").GetInt64(), Is.Zero);
        Assert.That(root.GetProperty("traceExportFailures").GetInt64(), Is.Zero);
        Assert.That((await client.GetAsync("/health/ready")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public void TelemetryIdentityIsBoundedAndHashesTheApplicationConfiguration()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"ainative-telemetry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        byte[] fantasyConfig = "telemetry-config"u8.ToArray();
        File.WriteAllBytes(Path.Combine(directory, "Fantasy.config"), fantasyConfig);

        try
        {
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AINATIVE_DEPLOYMENT_ENVIRONMENT"] = "acceptance",
                    ["AINATIVE_SERVICE_INSTANCE_ID"] = "runner-1",
                    ["AINATIVE_SOURCE_COMMIT"] = new string('1', 40),
                    ["AINATIVE_FANTASY_COMMIT"] = new string('2', 40),
                    ["AINATIVE_PROTOCOL_IDENTITY"] = new string('3', 64),
                })
                .Build();

            BattleTelemetrySettings settings = BattleTelemetrySettings.Create(configuration, directory);
            Dictionary<string, object> attributes = settings.Identity.ResourceAttributes.ToDictionary();

            Assert.That(settings.Endpoint, Is.Null);
            Assert.That(attributes["deployment.environment.name"], Is.EqualTo("acceptance"));
            Assert.That(settings.Identity.ServiceInstanceId, Is.EqualTo("runner-1"));
            Assert.That(attributes["ainative.source.commit"], Is.EqualTo(new string('1', 40)));
            Assert.That(
                attributes["ainative.configuration.identity"],
                Is.EqualTo(Convert.ToHexString(SHA256.HashData(fantasyConfig)).ToLowerInvariant()));
            Assert.That(attributes.Keys, Has.Count.EqualTo(7));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestCase("OTEL_EXPORTER_OTLP_ENDPOINT", "not-a-uri")]
    [TestCase("AINATIVE_DEPLOYMENT_ENVIRONMENT", "contains spaces")]
    [TestCase("AINATIVE_OTEL_TRACE_QUEUE_SIZE", "64")]
    [TestCase("AINATIVE_OTEL_TRACE_EXPORT_BATCH_SIZE", "4096")]
    [TestCase("AINATIVE_SOURCE_COMMIT", "not-a-commit")]
    public void InvalidTelemetryConfigurationFailsAtStartup(string key, string value)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();

        Assert.That(
            () => BattleTelemetrySettings.Create(configuration, Path.GetTempPath()),
            Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public void FailedMetricExporterPreservesBoundedTaglessProjectSeries()
    {
        TelemetryExportHealth health = new(exporterConfigured: true);
        using PeriodicExportingMetricReader reader = new(
            new TrackingMetricExporter(new FailureMetricExporter(), health),
            exportIntervalMilliseconds: 10,
            exportTimeoutMilliseconds: 100);
        using MeterProvider provider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(BattleMetrics.MeterName)
            .AddReader(reader)
            .Build();
        using BattleMetrics metrics = new(telemetryHealth: health);

        for (int index = 0; index < 64; index++)
        {
            metrics.RecordConnectionAccepted();
            metrics.RecordTick(index / 100d);
            metrics.RecordDroppedDiagnostic();
            metrics.RecordReplayDropped();
        }

        metrics.RecordConnectionRemoved(64);
        Assert.That(
            SpinWait.SpinUntil(
                () => health.Snapshot().MetricExportAttempts > 0,
                TimeSpan.FromSeconds(2)),
            Is.True);

        TelemetryExportSnapshot snapshot = health.Snapshot();
        Assert.That(snapshot.MetricExportFailures, Is.GreaterThan(0));
        Assert.That(snapshot.ProjectMetricSeries, Is.InRange(1, TelemetryExportHealth.MaximumProjectMetricSeries));
        Assert.That(snapshot.ProjectMetricTagViolations, Is.Zero);
        Assert.That(snapshot.ProjectMetricSeriesOverflow, Is.Zero);
    }

    [Test]
    public void BoundedTraceProcessorDropsAndCountsWithoutBlockingProducer()
    {
        TelemetryExportHealth health = new(exporterConfigured: true);
        BlockingActivityExporter blocking = new();
        using BoundedActivityExportProcessor processor = new(
            new TrackingActivityExporter(blocking, health),
            health,
            queueSize: 2,
            exportDelayMilliseconds: 1,
            exporterTimeoutMilliseconds: 1_000,
            exportBatchSize: 1);
        Activity[] activities = Enumerable.Range(0, 128)
            .Select(index => new Activity($"trace-{index}").Start())
            .ToArray();
        foreach (Activity activity in activities)
        {
            activity.Stop();
        }

        Stopwatch elapsed = Stopwatch.StartNew();
        foreach (Activity activity in activities)
        {
            processor.OnEnd(activity);
        }

        elapsed.Stop();
        Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromMilliseconds(250)));
        Assert.That(health.Snapshot().TraceRecordsDropped, Is.GreaterThan(0));

        blocking.Release();
        Assert.That(processor.ForceFlush(1_000), Is.True);
        foreach (Activity activity in activities)
        {
            activity.Dispose();
        }
    }

    [Test]
    public void SyntheticRoomTickHasZeroSteadyStateManagedAllocation()
    {
        SyntheticRoom room = new(64);
        for (int index = 0; index < 600; index++)
        {
            room.Tick();
        }

        const int measuredTicks = 3600;
        long[] durations = new long[measuredTicks];
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < measuredTicks; index++)
        {
            long started = Stopwatch.GetTimestamp();
            room.Tick();
            durations[index] = Stopwatch.GetTimestamp() - started;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Array.Sort(durations);

        double p99 = ToMilliseconds(durations[PercentileIndex(measuredTicks, 0.99)]);
        double p999 = ToMilliseconds(durations[PercentileIndex(measuredTicks, 0.999)]);
        int slowTicks = durations.Count(duration => ToMilliseconds(duration) > 16.67);

        TestContext.WriteLine(
            $"runtime={Environment.Version}; os={Environment.OSVersion}; processors={Environment.ProcessorCount}; " +
            $"warmup=600; ticks={measuredTicks}; p99_ms={p99:F4}; p999_ms={p999:F4}; slow_ticks={slowTicks}; allocated={allocated}");

        Assert.That(allocated, Is.Zero);
        Assert.That(p99, Is.LessThanOrEqualTo(16.67));
        Assert.That(p999, Is.LessThanOrEqualTo(20.0));
        Assert.That(slowTicks, Is.LessThanOrEqualTo((int)Math.Floor(measuredTicks * 0.001)));
    }

    [Test]
    public void RecordedInputStreamReplaysToTheExactCanonicalStateHash()
    {
        const int ticks = 3600;
        RecordedInput[] recording = new RecordedInput[ticks];
        Pcg32Random inputRandom = new(seed: 0xA11CE5EEDUL, sequence: 0x13UL);
        SyntheticRoom recorded = new(64);

        for (int tick = 0; tick < ticks; tick++)
        {
            RecordedInput input = new(
                (int)(inputRandom.NextUInt32() % 64),
                (int)(inputRandom.NextUInt32() % 2001) - 1000,
                (int)(inputRandom.NextUInt32() % 2001) - 1000);
            recording[tick] = input;
            recorded.ApplyInput(input.EntityIndex, input.MoveXMilli, input.MoveYMilli);
            recorded.Tick();
        }

        ulong recordedHash = recorded.ComputeStateHash();
        SyntheticRoom replayed = new(64);
        foreach (RecordedInput input in recording)
        {
            replayed.ApplyInput(input.EntityIndex, input.MoveXMilli, input.MoveYMilli);
            replayed.Tick();
        }

        ulong replayedHash = replayed.ComputeStateHash();
        Assert.That(replayedHash, Is.EqualTo(recordedHash));
        Assert.That(replayed.CreateSnapshot(ticks).StateHash, Is.EqualTo(recordedHash));

        replayed.ApplyInput(0, 1000, 0);
        Assert.That(replayed.ComputeStateHash(), Is.Not.EqualTo(recordedHash));
    }

    [Test]
    public async Task ProductionInputFramesCaptureAndReplayWithExactIdentityAndHash()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"ainative-replay-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "battle.anrp");
        Directory.CreateDirectory(directory);

        try
        {
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AINATIVE_REPLAY_CAPTURE_PATH"] = path,
                    ["AINATIVE_REPLAY_CAPTURE_CAPACITY"] = "4096",
                    ["AINATIVE_SOURCE_COMMIT"] = "test-source",
                    ["AINATIVE_FANTASY_COMMIT"] = "test-fantasy",
                    ["AINATIVE_PROTOCOL_IDENTITY"] = "test-protocol",
                    ["AINATIVE_CONFIGURATION_IDENTITY"] = "test-config",
                })
                .Build();
            using BattleMetrics metrics = new();
            await using BattleReplayCapture capture = new(configuration, metrics);
            SyntheticRoom recorded = new(64);
            Pcg32Random random = new(seed: 0xC0FFEEUL, sequence: 0x51UL);
            byte[] frame = new byte[RealtimeProtocolCodec.MaxDatagramBytes];
            const int ticks = 3600;

            for (int tick = 0; tick < ticks; tick++)
            {
                int entityIndex = (int)(random.NextUInt32() % 64);
                InputCommand command = new()
                {
                    RoomTick = checked((ulong)tick),
                    Sequence = checked((uint)tick + 1),
                    MoveXMilli = (int)(random.NextUInt32() % 2001) - 1000,
                    MoveYMilli = (int)(random.NextUInt32() % 2001) - 1000,
                };
                Assert.That(RealtimeProtocolCodec.TryEncode(
                    MessageId.InputCommand,
                    command,
                    frame,
                    out TransportChannel channel,
                    out int writtenBytes), Is.True);
                Assert.That(channel.Id, Is.EqualTo(2));
                Assert.That(capture.TryRecordInput(
                    checked((ulong)tick),
                    entityIndex,
                    frame.AsSpan(0, writtenBytes)), Is.True);
                recorded.ApplyInput(entityIndex, command.MoveXMilli, command.MoveYMilli);
                recorded.Tick();
            }

            const int batchedEntityIndex = 3;
            InputBatch batch = new();
            batch.Commands.Add(new InputCommand
            {
                RoomTick = checked((ulong)ticks),
                Sequence = checked((uint)ticks + 1),
                MoveXMilli = 1000,
                MoveYMilli = -500,
            });
            batch.Commands.Add(new InputCommand
            {
                RoomTick = checked((ulong)ticks),
                Sequence = checked((uint)ticks + 2),
                MoveXMilli = -250,
                MoveYMilli = 750,
                Buttons = 1,
            });
            Assert.That(RealtimeProtocolCodec.TryEncode(
                MessageId.InputBatch,
                batch,
                frame,
                out TransportChannel batchChannel,
                out int batchBytes), Is.True);
            Assert.That(batchChannel.Id, Is.EqualTo(2));
            Assert.That(capture.TryRecordInput(
                checked((ulong)ticks),
                batchedEntityIndex,
                frame.AsSpan(0, batchBytes)), Is.True);
            foreach (InputCommand batchedCommand in batch.Commands)
            {
                recorded.ApplyInput(
                    batchedEntityIndex,
                    batchedCommand.MoveXMilli,
                    batchedCommand.MoveYMilli);
            }

            await capture.CompleteAsync(ticks, recorded.ComputeStateHash());
            ReplayVerificationResult verified = BattleReplayVerifier.Verify(path);

            Assert.That(verified.SourceCommit, Is.EqualTo("test-source"));
            Assert.That(verified.FantasyCommit, Is.EqualTo("test-fantasy"));
            Assert.That(verified.ProtocolIdentity, Is.EqualTo("test-protocol"));
            Assert.That(verified.ConfigurationIdentity, Is.EqualTo("test-config"));
            Assert.That(verified.InputCount, Is.EqualTo(ticks + batch.Commands.Count));
            Assert.That(verified.FinalTick, Is.EqualTo((ulong)ticks));
            Assert.That(verified.StateHash, Is.EqualTo(recorded.ComputeStateHash()));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void PcapAnalyzerAccountsPerClientWireWindowsAndDatagramSizes()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"ainative-pcap-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "loopback.pcap");
        Directory.CreateDirectory(directory);
        const uint firstSecond = 1_700_000_000;

        try
        {
            using (FileStream stream = File.Create(path))
            using (BinaryWriter writer = new(stream))
            {
                writer.Write(0xa1b2c3d4U);
                writer.Write((ushort)2);
                writer.Write((ushort)4);
                writer.Write(0);
                writer.Write(0U);
                writer.Write(65_535U);
                writer.Write(1U);
                for (int second = 0; second < 60; second++)
                {
                    for (int client = 0; client < 64; client++)
                    {
                        ushort clientPort = checked((ushort)(30_000 + client));
                        WriteUdpPacket(writer, firstSecond + checked((uint)second), 22000, clientPort);
                        WriteUdpPacket(writer, firstSecond + checked((uint)second), clientPort, 22000);
                    }
                }
            }

            PcapReport report = PcapAnalyzer.Analyze(path, 22000, firstSecond * 1000L, 60);

            Assert.That(report.QualifiedSocketImpairment, Is.True);
            Assert.That(report.ClientCount, Is.EqualTo(64));
            Assert.That(report.PacketCount, Is.EqualTo(64L * 60 * 2));
            Assert.That(report.DownstreamP95Kbps, Is.EqualTo(1.024).Within(0.0001));
            Assert.That(report.UpstreamP95Kbps, Is.EqualTo(1.024).Within(0.0001));
            Assert.That(report.DatagramPayloadP95Bytes, Is.EqualTo(100));
            Assert.That(report.GatesPassed, Is.True);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static int PercentileIndex(int count, double percentile) =>
        Math.Clamp((int)Math.Ceiling(count * percentile) - 1, 0, count - 1);

    private static double ToMilliseconds(long timestampDelta) =>
        timestampDelta * 1000d / Stopwatch.Frequency;

    private readonly record struct RecordedInput(int EntityIndex, int MoveXMilli, int MoveYMilli);

    private sealed class FailureMetricExporter : BaseExporter<Metric>
    {
        public override ExportResult Export(in Batch<Metric> batch) => ExportResult.Failure;
    }

    private sealed class BlockingActivityExporter : BaseExporter<Activity>
    {
        private readonly ManualResetEventSlim _released = new(initialState: false);

        public override ExportResult Export(in Batch<Activity> batch)
        {
            _released.Wait(TimeSpan.FromSeconds(2));
            return ExportResult.Success;
        }

        public void Release() => _released.Set();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _released.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private static void WriteUdpPacket(BinaryWriter writer, uint seconds, ushort sourcePort, ushort destinationPort)
    {
        const int payloadBytes = 100;
        const int ipBytes = 20 + 8 + payloadBytes;
        const int frameBytes = 14 + ipBytes;
        byte[] frame = new byte[frameBytes];
        frame[12] = 0x08;
        frame[13] = 0x00;
        Span<byte> ip = frame.AsSpan(14);
        ip[0] = 0x45;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(ip[2..], ipBytes);
        ip[8] = 64;
        ip[9] = 17;
        ip[12] = 127;
        ip[15] = 1;
        ip[16] = 127;
        ip[19] = 1;
        Span<byte> udp = ip[20..];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(udp, sourcePort);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(udp[2..], destinationPort);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(udp[4..], 8 + payloadBytes);
        writer.Write(seconds);
        writer.Write(0U);
        writer.Write(frameBytes);
        writer.Write(frameBytes);
        writer.Write(frame);
    }
}
