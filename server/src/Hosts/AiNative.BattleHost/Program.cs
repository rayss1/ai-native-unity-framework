using AiNative.BattleHost;
using AiNative.Server.Fantasy;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

if (args is ["--verify-replay", string replayPath])
{
    ReplayVerificationResult verified = BattleReplayVerifier.Verify(replayPath);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(verified));
    return;
}

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
bool fantasyEnabled = builder.Configuration.GetValue("AINATIVE_FANTASY_ENABLED", true);
int outerKcpMtu = builder.Configuration.GetValue("AINATIVE_FANTASY_OUTER_KCP_MTU", 1150);
BattleHostCapacitySettings capacitySettings = BattleHostCapacitySettings.Create(builder.Configuration);
BattleTelemetrySettings telemetrySettings = BattleTelemetrySettings.Create(
    builder.Configuration,
    builder.Environment.ContentRootPath,
    capacitySettings);
TelemetryExportHealth telemetryHealth = new(telemetrySettings.Endpoint is not null);
builder.Services.AddSingleton(new RuntimeReadiness(networkRequired: fantasyEnabled));
builder.Services.AddSingleton(capacitySettings);
builder.Services.AddSingleton<BattleRoomSet>();
builder.Services.AddSingleton(telemetrySettings);
builder.Services.AddSingleton(telemetryHealth);
builder.Services.AddSingleton<BattleMetrics>();
builder.Services.AddSingleton<BattleReplayCapture>();
builder.Services.AddSingleton(new FantasyKcpGateway(
    maxConnections: capacitySettings.TotalBotCapacity,
    outerKcpMtu: outerKcpMtu));
builder.Services.AddSingleton<RoomProtocolService>();
if (fantasyEnabled)
{
    builder.Services.AddHostedService<FantasyRuntimeService>();
}
builder.Services.AddHostedService<AcceptanceRoomService>();

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(
        serviceName: "ainative-battle-host",
        serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0",
        serviceInstanceId: telemetrySettings.Identity.ServiceInstanceId)
        .AddAttributes(telemetrySettings.Identity.ResourceAttributes))
    .WithMetrics(metrics =>
    {
        metrics.AddMeter(BattleMetrics.MeterName)
            .AddRuntimeInstrumentation();
        if (telemetrySettings.Endpoint is not null)
        {
            metrics.AddReader(_ => new PeriodicExportingMetricReader(
                new TrackingMetricExporter(
                    new OtlpMetricExporter(telemetrySettings.CreateExporterOptions()),
                    telemetryHealth),
                telemetrySettings.MetricExportIntervalMilliseconds,
                telemetrySettings.ExportTimeoutMilliseconds));
        }
    })
    .WithTracing(tracing =>
    {
        tracing.AddAspNetCoreInstrumentation();
        if (telemetrySettings.Endpoint is not null)
        {
            tracing.AddProcessor(_ =>
            {
                return new BoundedActivityExportProcessor(
                    new TrackingActivityExporter(
                        new OtlpTraceExporter(telemetrySettings.CreateExporterOptions()),
                        telemetryHealth),
                    telemetryHealth,
                    telemetrySettings.TraceQueueSize,
                    telemetrySettings.TraceExportDelayMilliseconds,
                    telemetrySettings.ExportTimeoutMilliseconds,
                    telemetrySettings.TraceExportBatchSize);
            });
        }
    });

WebApplication app = builder.Build();
RuntimeReadiness readiness = app.Services.GetRequiredService<RuntimeReadiness>();
app.Lifetime.ApplicationStopping.Register(readiness.BeginDrain);

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", () => readiness.IsReady
    ? Results.Ok(new { status = "ready", tick = readiness.Tick, capacitySettings.RoomCount })
    : Results.Json(
        new { status = "draining", tick = readiness.Tick, capacitySettings.RoomCount },
        statusCode: StatusCodes.Status503ServiceUnavailable));
app.MapGet("/health/telemetry", () =>
{
    TelemetryExportSnapshot snapshot = telemetryHealth.Snapshot();
    return Results.Ok(new
    {
        status = snapshot.MetricExportFailures + snapshot.TraceExportFailures == 0
            ? "healthy"
            : "degraded",
        snapshot.ExporterConfigured,
        snapshot.MetricExportAttempts,
        snapshot.MetricExportFailures,
        snapshot.TraceExportAttempts,
        snapshot.TraceExportFailures,
        snapshot.TraceRecordsDropped,
        snapshot.ProjectMetricSeries,
        snapshot.ProjectMetricTagViolations,
        snapshot.ProjectMetricSeriesOverflow,
    });
});

if (builder.Configuration.GetValue("AINATIVE_ENABLE_EVALUATION_ENDPOINTS", false))
{
    app.MapPost("/admin/drain", () =>
    {
        readiness.BeginDrain();
        return Results.Accepted();
    });

    if (fantasyEnabled)
    {
        app.MapPost("/admin/kcp-loopback", async (
            FantasyKcpGateway gateway,
            CancellationToken cancellationToken) =>
        {
            FantasyKcpProbeResult result =
                await FantasyKcpLoopbackProbe.RunAsync(gateway, cancellationToken);
            return Results.Ok(new
            {
                status = "ok",
                result.SessionId,
                result.InitialConnectionEpoch,
                result.ReconnectedEpoch,
                result.RoomId,
                result.EntityId,
                result.SnapshotTick,
                result.ResumeTick,
            });
        });

        app.MapPost("/admin/kcp-load", async (
            FantasyKcpGateway gateway,
            BattleMetrics metrics,
            int? botCount,
            int? roomCount,
            int? durationSeconds,
            int? warmupSeconds,
            CancellationToken cancellationToken) =>
        {
            int requestedWarmupSeconds = warmupSeconds ?? 1;
            FantasyKcpLoadResult result = await FantasyKcpLoadProbe.RunAsync(
                gateway,
                botCount ?? capacitySettings.TotalBotCapacity,
                roomCount ?? capacitySettings.RoomCount,
                TimeSpan.FromSeconds(durationSeconds ?? 10),
                TimeSpan.FromSeconds(requestedWarmupSeconds),
                () => metrics.RequestAcceptanceMeasurement(checked(requestedWarmupSeconds * 60)),
                cancellationToken);
            return Results.Ok(result);
        });
    }
}

await app.RunAsync();

public partial class Program
{
}
