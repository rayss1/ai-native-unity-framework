using AiNative.Server.Fantasy;

namespace AiNative.BattleHost;

internal sealed class FantasyRuntimeService(
    FantasyKcpGateway gateway,
    RuntimeReadiness readiness,
    ILogger<FantasyRuntimeService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Task runtime = gateway.RunAsync(stoppingToken);

        try
        {
            await gateway.WaitUntilListeningAsync(stoppingToken);
            readiness.MarkNetworkReady();
            logger.LogInformation(
                "Fantasy KCP gateway ready on UDP port {Port}",
                gateway.ListeningPort);
            await runtime;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            await runtime;
        }
        finally
        {
            readiness.BeginDrain();
            gateway.BeginDrain();
            logger.LogInformation("Fantasy KCP gateway drained");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        readiness.BeginDrain();
        gateway.BeginDrain();
        await base.StopAsync(cancellationToken);
    }
}
