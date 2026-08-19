using System.Diagnostics;

namespace AiNative.BattleHost;

internal sealed class AcceptanceRoomService(
    RuntimeReadiness readiness,
    BattleMetrics metrics,
    ILogger<AcceptanceRoomService> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 60);
    private readonly SyntheticRoom _room = new(64);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TickInterval);
        readiness.MarkReady();
        logger.LogInformation("Acceptance room ready with {BotCount} bots at 60 Hz", _room.BotCount);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                long started = Stopwatch.GetTimestamp();
                _room.Tick();
                readiness.AdvanceTick();
                metrics.RecordTick(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            readiness.BeginDrain();
            logger.LogInformation("Acceptance room drained at tick {Tick}", readiness.Tick);
        }
    }
}

internal sealed class SyntheticRoom
{
    private readonly int[] _positionX;
    private readonly int[] _positionZ;
    private readonly int[] _health;
    private ulong _state = 0x5eed;

    public SyntheticRoom(int botCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(botCount);
        BotCount = botCount;
        _positionX = new int[botCount];
        _positionZ = new int[botCount];
        _health = Enumerable.Repeat(100, botCount).ToArray();
    }

    public int BotCount { get; }

    public void Tick()
    {
        for (int index = 0; index < BotCount; index++)
        {
            _state = unchecked((_state * 6364136223846793005UL) + 1442695040888963407UL);
            _positionX[index] += (int)((_state >> 32) % 7) - 3;
            _state = unchecked((_state * 6364136223846793005UL) + 1442695040888963407UL);
            _positionZ[index] += (int)((_state >> 32) % 7) - 3;
            if ((_state & 15) == 0)
            {
                int target = (index + 1 + (int)((_state >> 40) % (uint)(BotCount - 1))) % BotCount;
                _health[target] = _health[target] > 0 ? _health[target] - 1 : 100;
            }
        }
    }
}
