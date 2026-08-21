using AiNative.BattleHost;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<RuntimeReadiness>();
builder.Services.AddSingleton<BattleMetrics>();
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
}

await app.RunAsync();

public partial class Program
{
}
