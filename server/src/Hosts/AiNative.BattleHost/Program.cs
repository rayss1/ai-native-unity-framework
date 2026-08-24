using AiNative.BattleHost;
using AiNative.Server.Fantasy;
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
builder.Services.AddSingleton(new RuntimeReadiness(networkRequired: fantasyEnabled));
builder.Services.AddSingleton<BattleMetrics>();
builder.Services.AddSingleton<BattleReplayCapture>();
builder.Services.AddSingleton<FantasyKcpGateway>();
builder.Services.AddSingleton<RoomProtocolService>();
if (fantasyEnabled)
{
    builder.Services.AddHostedService<FantasyRuntimeService>();
}
builder.Services.AddHostedService<AcceptanceRoomService>();

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(
        serviceName: "ainative-battle-host",
        serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0"))
    .WithMetrics(metrics =>
    {
        metrics.AddMeter(BattleMetrics.MeterName)
            .AddRuntimeInstrumentation();
        if (Uri.TryCreate(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"], UriKind.Absolute, out Uri? endpoint))
        {
            metrics.AddOtlpExporter(options => options.Endpoint = endpoint);
        }
    })
    .WithTracing(tracing =>
    {
        tracing.AddAspNetCoreInstrumentation();
        if (Uri.TryCreate(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"], UriKind.Absolute, out Uri? endpoint))
        {
            tracing.AddOtlpExporter(options => options.Endpoint = endpoint);
        }
    });

WebApplication app = builder.Build();
RuntimeReadiness readiness = app.Services.GetRequiredService<RuntimeReadiness>();
app.Lifetime.ApplicationStopping.Register(readiness.BeginDrain);

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", () => readiness.IsReady
    ? Results.Ok(new { status = "ready", tick = readiness.Tick })
    : Results.Json(new { status = "draining", tick = readiness.Tick }, statusCode: StatusCodes.Status503ServiceUnavailable));

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
            int? durationSeconds,
            int? warmupSeconds,
            CancellationToken cancellationToken) =>
        {
            int requestedWarmupSeconds = warmupSeconds ?? 1;
            FantasyKcpLoadResult result = await FantasyKcpLoadProbe.RunAsync(
                gateway,
                botCount ?? 64,
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
