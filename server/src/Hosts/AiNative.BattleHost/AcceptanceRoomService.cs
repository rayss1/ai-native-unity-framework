using System.Diagnostics;

namespace AiNative.BattleHost;

internal sealed class AcceptanceRoomService(
    RuntimeReadiness readiness,
    BattleMetrics metrics,
    RoomProtocolService protocol,
    ILogger<AcceptanceRoomService> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 60);
    private readonly SyntheticRoom _room = new(64);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TickInterval);
        readiness.MarkRoomReady();
        logger.LogInformation("Acceptance room ready with {BotCount} bots at 60 Hz", _room.BotCount);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                long started = Stopwatch.GetTimestamp();
                protocol.PumpInbound(_room, checked((ulong)readiness.Tick));
                _room.Tick();
                readiness.AdvanceTick();
                protocol.PublishSnapshot(_room, checked((ulong)readiness.Tick));
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

    public void ApplyInput(int entityIndex, int moveXMilli, int moveYMilli)
    {
        if ((uint)entityIndex >= (uint)BotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(entityIndex));
        }

        _positionX[entityIndex] += Math.Clamp(moveXMilli, -1000, 1000) / 20;
        _positionZ[entityIndex] += Math.Clamp(moveYMilli, -1000, 1000) / 20;
    }

    public AiNative.Protocol.V1.Snapshot CreateSnapshot(ulong roomTick)
    {
        AiNative.Protocol.V1.Snapshot snapshot = new()
        {
            ProtocolMajor = 1,
            RoomTick = roomTick,
            BaselineTick = roomTick > 3 ? roomTick - 3 : 0,
        };

        for (int index = 0; index < BotCount; index++)
        {
            snapshot.Players.Add(new AiNative.Protocol.V1.PlayerState
            {
                EntityId = checked((uint)index + 1),
                PositionXMilli = _positionX[index],
                PositionYMilli = 0,
                PositionZMilli = _positionZ[index],
                YawMillidegrees = 0,
                Health = checked((uint)_health[index]),
            });
        }

        return snapshot;
    }
}
