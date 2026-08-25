using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;

namespace AiNative.BattleHost;

internal sealed record BattleTelemetryIdentity(
    string DeploymentEnvironment,
    string ServiceInstanceId,
    string SourceCommit,
    string FantasyCommit,
    string ProtocolIdentity,
    string ConfigurationIdentity)
{
    public IEnumerable<KeyValuePair<string, object>> ResourceAttributes
    {
        get
        {
            yield return new("deployment.environment.name", DeploymentEnvironment);
            yield return new("process.pid", Environment.ProcessId);
            yield return new("ainative.source.commit", SourceCommit);
            yield return new("ainative.fantasy.commit", FantasyCommit);
            yield return new("ainative.protocol.identity", ProtocolIdentity);
            yield return new("ainative.configuration.identity", ConfigurationIdentity);
            yield return new("ainative.room.capacity", 64);
        }
    }
}

internal sealed record BattleTelemetrySettings(
    BattleTelemetryIdentity Identity,
    Uri? Endpoint,
    int ExportTimeoutMilliseconds,
    int MetricExportIntervalMilliseconds,
    int TraceQueueSize,
    int TraceExportDelayMilliseconds,
    int TraceExportBatchSize)
{
    public static BattleTelemetrySettings Create(IConfiguration configuration, string contentRootPath)
    {
        string? endpointValue = configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        Uri? endpoint = null;
        if (!string.IsNullOrWhiteSpace(endpointValue) &&
            (!Uri.TryCreate(endpointValue, UriKind.Absolute, out endpoint) ||
             endpoint.Scheme is not ("http" or "https")))
        {
            throw new InvalidOperationException("OTEL_EXPORTER_OTLP_ENDPOINT must be an absolute HTTP(S) URI.");
        }

        string deployment = BoundedToken(
            configuration["AINATIVE_DEPLOYMENT_ENVIRONMENT"] ?? "development",
            "AINATIVE_DEPLOYMENT_ENVIRONMENT",
            32);
        string instance = BoundedToken(
            configuration["AINATIVE_SERVICE_INSTANCE_ID"] ?? Environment.MachineName,
            "AINATIVE_SERVICE_INSTANCE_ID",
            128);
        string configurationIdentity = ResolveConfigurationIdentity(configuration, contentRootPath);
        BattleTelemetryIdentity identity = new(
            deployment,
            instance,
            FixedIdentity(configuration, "AINATIVE_SOURCE_COMMIT", 40),
            FixedIdentity(configuration, "AINATIVE_FANTASY_COMMIT", 40),
            FixedIdentity(configuration, "AINATIVE_PROTOCOL_IDENTITY", 64),
            configurationIdentity);

        int exportTimeout = BoundedInteger(
            configuration,
            "AINATIVE_OTEL_EXPORT_TIMEOUT_MILLISECONDS",
            defaultValue: 10_000,
            minimum: 100,
            maximum: 30_000);
        int metricInterval = BoundedInteger(
            configuration,
            "AINATIVE_OTEL_METRIC_EXPORT_INTERVAL_MILLISECONDS",
            defaultValue: 60_000,
            minimum: 1_000,
            maximum: 60_000);
        int traceQueue = BoundedInteger(
            configuration,
            "AINATIVE_OTEL_TRACE_QUEUE_SIZE",
            defaultValue: 2_048,
            minimum: 128,
            maximum: 8_192);
        int traceDelay = BoundedInteger(
            configuration,
            "AINATIVE_OTEL_TRACE_EXPORT_DELAY_MILLISECONDS",
            defaultValue: 5_000,
            minimum: 100,
            maximum: 60_000);
        int traceBatch = BoundedInteger(
            configuration,
            "AINATIVE_OTEL_TRACE_EXPORT_BATCH_SIZE",
            defaultValue: 512,
            minimum: 1,
            maximum: traceQueue);

        return new BattleTelemetrySettings(
            identity,
            endpoint,
            exportTimeout,
            metricInterval,
            traceQueue,
            traceDelay,
            traceBatch);
    }

    public OtlpExporterOptions CreateExporterOptions() => new()
    {
        Endpoint = Endpoint!,
        Protocol = OtlpExportProtocol.Grpc,
        TimeoutMilliseconds = ExportTimeoutMilliseconds,
    };

    private static int BoundedInteger(
        IConfiguration configuration,
        string key,
        int defaultValue,
        int minimum,
        int maximum)
    {
        int value = configuration.GetValue(key, defaultValue);
        if (value < minimum || value > maximum)
        {
            throw new InvalidOperationException($"{key} must be between {minimum} and {maximum}.");
        }

        return value;
    }

    private static string BoundedToken(string value, string key, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > maximumLength ||
            value.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':')))
        {
            throw new InvalidOperationException(
                $"{key} must contain 1-{maximumLength} ASCII letters, digits, '.', '_', '-', or ':'.");
        }

        return value;
    }

    private static string ResolveConfigurationIdentity(
        IConfiguration configuration,
        string contentRootPath)
    {
        string? configured = configuration["AINATIVE_CONFIGURATION_IDENTITY"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (configured.Length != 64 || configured.Any(character => !char.IsAsciiHexDigit(character)))
            {
                throw new InvalidOperationException(
                    "AINATIVE_CONFIGURATION_IDENTITY must be a 64-character SHA-256 identity.");
            }

            return configured.ToLowerInvariant();
        }

        string configPath = Path.Combine(contentRootPath, "Fantasy.config");
        if (!File.Exists(configPath))
        {
            return "unrecorded";
        }

        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(configPath))).ToLowerInvariant();
    }

    private static string FixedIdentity(
        IConfiguration configuration,
        string key,
        int length)
    {
        string? value = configuration[key];
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unrecorded";
        }

        if (value.Length != length || value.Any(character => !char.IsAsciiHexDigit(character)))
        {
            throw new InvalidOperationException($"{key} must be a {length}-character hexadecimal identity.");
        }

        return value.ToLowerInvariant();
    }
}

internal readonly record struct TelemetryExportSnapshot(
    bool ExporterConfigured,
    long MetricExportAttempts,
    long MetricExportFailures,
    long TraceExportAttempts,
    long TraceExportFailures,
    long TraceRecordsDropped,
    int ProjectMetricSeries,
    long ProjectMetricTagViolations,
    long ProjectMetricSeriesOverflow);

internal sealed class TelemetryExportHealth(bool exporterConfigured)
{
    internal const int MaximumProjectMetricSeries = 16;
    private readonly Lock _seriesLock = new();
    private readonly HashSet<string> _projectMetricSeries = new(StringComparer.Ordinal);
    private long _metricExportAttempts;
    private long _metricExportFailures;
    private long _traceExportAttempts;
    private long _traceExportFailures;
    private long _projectMetricTagViolations;
    private long _projectMetricSeriesOverflow;
    private long _traceRecordsDropped;

    public TelemetryExportSnapshot Snapshot()
    {
        int seriesCount;
        lock (_seriesLock)
        {
            seriesCount = _projectMetricSeries.Count;
        }

        return new TelemetryExportSnapshot(
            exporterConfigured,
            Interlocked.Read(ref _metricExportAttempts),
            Interlocked.Read(ref _metricExportFailures),
            Interlocked.Read(ref _traceExportAttempts),
            Interlocked.Read(ref _traceExportFailures),
            Interlocked.Read(ref _traceRecordsDropped),
            seriesCount,
            Interlocked.Read(ref _projectMetricTagViolations),
            Interlocked.Read(ref _projectMetricSeriesOverflow));
    }

    public void RecordMetricExport(ExportResult result)
    {
        Interlocked.Increment(ref _metricExportAttempts);
        if (result == ExportResult.Failure)
        {
            Interlocked.Increment(ref _metricExportFailures);
        }
    }

    public void RecordTraceExport(ExportResult result)
    {
        Interlocked.Increment(ref _traceExportAttempts);
        if (result == ExportResult.Failure)
        {
            Interlocked.Increment(ref _traceExportFailures);
        }
    }

    public void RecordTraceDropped(int count = 1) =>
        Interlocked.Add(ref _traceRecordsDropped, count);

    public void ObserveMetricBatch(ref Batch<Metric> batch)
    {
        foreach (Metric metric in batch)
        {
            if (!string.Equals(metric.MeterName, BattleMetrics.MeterName, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (ref readonly MetricPoint point in metric.GetMetricPoints())
            {
                StringBuilder signature = new(metric.Name);
                bool hasTags = false;
                foreach (KeyValuePair<string, object?> tag in point.Tags)
                {
                    hasTags = true;
                    signature.Append('|').Append(tag.Key).Append('=').Append(tag.Value);
                }

                if (hasTags)
                {
                    Interlocked.Increment(ref _projectMetricTagViolations);
                }

                lock (_seriesLock)
                {
                    if (_projectMetricSeries.Contains(signature.ToString()))
                    {
                        continue;
                    }

                    if (_projectMetricSeries.Count >= MaximumProjectMetricSeries)
                    {
                        Interlocked.Increment(ref _projectMetricSeriesOverflow);
                        continue;
                    }

                    _projectMetricSeries.Add(signature.ToString());
                }
            }
        }
    }
}

internal sealed class TrackingMetricExporter(
    BaseExporter<Metric> inner,
    TelemetryExportHealth health) : BaseExporter<Metric>
{
    public override ExportResult Export(in Batch<Metric> batch)
    {
        Batch<Metric> observed = batch;
        health.ObserveMetricBatch(ref observed);
        ExportResult result;
        try
        {
            result = inner.Export(in batch);
        }
        catch
        {
            result = ExportResult.Failure;
        }

        health.RecordMetricExport(result);
        return result;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}

internal sealed class TrackingActivityExporter(
    BaseExporter<Activity> inner,
    TelemetryExportHealth health) : BaseExporter<Activity>
{
    public override ExportResult Export(in Batch<Activity> batch)
    {
        ExportResult result;
        try
        {
            result = inner.Export(in batch);
        }
        catch
        {
            result = ExportResult.Failure;
        }

        health.RecordTraceExport(result);
        return result;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}

internal sealed class BoundedActivityExportProcessor : BaseProcessor<Activity>
{
    private readonly BaseExporter<Activity> _exporter;
    private readonly TelemetryExportHealth _health;
    private readonly Channel<Activity> _queue;
    private readonly CancellationTokenSource _abort = new();
    private readonly Task _worker;
    private readonly int _exportDelayMilliseconds;
    private readonly int _exportBatchSize;
    private readonly int _shutdownTimeoutMilliseconds;
    private long _pendingItems;
    private int _resourcesDisposed;

    public BoundedActivityExportProcessor(
        BaseExporter<Activity> exporter,
        TelemetryExportHealth health,
        int queueSize,
        int exportDelayMilliseconds,
        int exporterTimeoutMilliseconds,
        int exportBatchSize)
    {
        _exporter = exporter;
        _health = health;
        _exportDelayMilliseconds = exportDelayMilliseconds;
        _exportBatchSize = exportBatchSize;
        _shutdownTimeoutMilliseconds = exporterTimeoutMilliseconds;
        _queue = Channel.CreateBounded<Activity>(new BoundedChannelOptions(queueSize)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        _worker = Task.Run(ExportAsync);
    }

    public override void OnEnd(Activity data)
    {
        Interlocked.Increment(ref _pendingItems);
        if (!_queue.Writer.TryWrite(data))
        {
            Interlocked.Decrement(ref _pendingItems);
            _health.RecordTraceDropped();
        }
    }

    protected override bool OnForceFlush(int timeoutMilliseconds) =>
        WaitForDrain(timeoutMilliseconds);

    protected override bool OnShutdown(int timeoutMilliseconds)
    {
        _queue.Writer.TryComplete();
        bool completed = WaitForTask(_worker, timeoutMilliseconds);
        if (!completed)
        {
            _abort.Cancel();
        }

        return completed && _exporter.Shutdown(timeoutMilliseconds);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Shutdown(_shutdownTimeoutMilliseconds);
            _abort.Cancel();
            if (_worker.IsCompleted)
            {
                DisposeResources();
            }
            else
            {
                _ = _worker.ContinueWith(
                    _ => DisposeResources(),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        base.Dispose(disposing);
    }

    private async Task ExportAsync()
    {
        Activity[] batchItems = new Activity[_exportBatchSize];
        ChannelReader<Activity> reader = _queue.Reader;
        try
        {
            while (await reader.WaitToReadAsync(_abort.Token))
            {
                int count = 0;
                bool exportAttempted = false;
                try
                {
                    while (count < batchItems.Length && reader.TryRead(out Activity? activity))
                    {
                        batchItems[count++] = activity;
                    }

                    if (count < batchItems.Length && !reader.Completion.IsCompleted)
                    {
                        await Task.Delay(_exportDelayMilliseconds, _abort.Token);
                        while (count < batchItems.Length && reader.TryRead(out Activity? activity))
                        {
                            batchItems[count++] = activity;
                        }
                    }

                    if (count == 0)
                    {
                        continue;
                    }

                    exportAttempted = true;
                    Batch<Activity> batch = new(batchItems, count);
                    _exporter.Export(in batch);
                }
                finally
                {
                    if (!exportAttempted && count > 0)
                    {
                        _health.RecordTraceDropped(count);
                    }

                    Array.Clear(batchItems, 0, count);
                    Interlocked.Add(ref _pendingItems, -count);
                }
            }
        }
        catch (OperationCanceledException) when (_abort.IsCancellationRequested)
        {
        }
        finally
        {
            int abandoned = 0;
            while (reader.TryRead(out _))
            {
                abandoned++;
            }

            if (abandoned > 0)
            {
                _health.RecordTraceDropped(abandoned);
                Interlocked.Add(ref _pendingItems, -abandoned);
            }
        }
    }

    private bool WaitForDrain(int timeoutMilliseconds)
    {
        long deadline = timeoutMilliseconds == Timeout.Infinite
            ? long.MaxValue
            : Environment.TickCount64 + timeoutMilliseconds;
        while (Interlocked.Read(ref _pendingItems) > 0)
        {
            if (Environment.TickCount64 >= deadline)
            {
                return false;
            }

            Thread.Sleep(10);
        }

        return true;
    }

    private static bool WaitForTask(Task task, int timeoutMilliseconds)
    {
        try
        {
            return task.Wait(timeoutMilliseconds);
        }
        catch (AggregateException exception) when (
            exception.InnerExceptions.All(inner => inner is OperationCanceledException))
        {
            return false;
        }
    }

    private void DisposeResources()
    {
        if (Interlocked.Exchange(ref _resourcesDisposed, 1) != 0)
        {
            return;
        }

        _exporter.Dispose();
        _abort.Dispose();
    }
}
