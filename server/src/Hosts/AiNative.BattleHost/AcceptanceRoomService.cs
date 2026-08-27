using System.Buffers.Binary;
using System.Diagnostics;
using AiNative.Gameplay;

namespace AiNative.BattleHost;

internal sealed class AcceptanceRoomService(
    RuntimeReadiness readiness,
    BattleMetrics metrics,
    RoomProtocolService protocol,
    BattleRoomSet rooms,
    BattleReplayCapture replayCapture,
    ILogger<AcceptanceRoomService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        MonotonicFixedRatePacer pacer = new(60);
        readiness.MarkRoomReady();
        logger.LogInformation(
            "Acceptance room set ready with {RoomCount} room(s) and {BotCapacity} total bots at 60 Hz",
            rooms.RoomCount,
            rooms.Settings.TotalBotCapacity);

        try
        {
            while (true)
            {
                await pacer.WaitForNextTickAsync(stoppingToken);
                long started = Stopwatch.GetTimestamp();
                protocol.PumpInbound(checked((ulong)readiness.Tick));
                long gameplayStarted = Stopwatch.GetTimestamp();
                long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                rooms.TickAll();
                long gameplayAllocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
                metrics.RecordGameplayTick(
                    Stopwatch.GetElapsedTime(gameplayStarted).TotalMilliseconds,
                    gameplayAllocated);
                readiness.AdvanceTick();
                protocol.PublishSnapshots(checked((ulong)readiness.Tick));
                metrics.RecordTick(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            readiness.BeginDrain();
            await replayCapture.CompleteAsync(
                checked((ulong)readiness.Tick),
                rooms.ComputeCombinedStateHash());
            metrics.WriteAcceptanceReport(
                checked((ulong)readiness.Tick),
                rooms.ComputeCombinedStateHash());
            logger.LogInformation(
                "Acceptance room set drained at tick {Tick} with {RoomCount} room(s)",
                readiness.Tick,
                rooms.RoomCount);
        }
    }
}

internal sealed class SyntheticRoom
{
    internal const ulong InitialRandomState = 0x5eed;
    private const int MaxBots = 64;
    private const int CanonicalHeaderBytes = 16;
    private const int CanonicalBotBytes = 12;
    private static readonly XxHash64StateHasher StateHasher = new();
    private readonly int[] _positionX;
    private readonly int[] _positionZ;
    private readonly int[] _health;
    private ulong _state = InitialRandomState;

    public SyntheticRoom(int botCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(botCount, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(botCount, MaxBots);
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

        _positionX[entityIndex] = KinematicMovement.ApplyAxis(
            _positionX[entityIndex],
            moveXMilli);
        _positionZ[entityIndex] = KinematicMovement.ApplyAxis(
            _positionZ[entityIndex],
            moveYMilli);
    }

    public AiNative.Protocol.V1.Snapshot CreateSnapshot(ulong roomTick)
    {
        AiNative.Protocol.V1.Snapshot snapshot = new()
        {
            ProtocolMajor = 1,
            RoomTick = roomTick,
            BaselineTick = roomTick > 3 ? roomTick - 3 : 0,
            StateHash = ComputeStateHash(),
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

    public ulong ComputeStateHash()
    {
        Span<byte> canonical = stackalloc byte[CanonicalHeaderBytes + (MaxBots * CanonicalBotBytes)];
        BinaryPrimitives.WriteUInt32LittleEndian(canonical, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(canonical[4..], checked((uint)BotCount));
        BinaryPrimitives.WriteUInt64LittleEndian(canonical[8..], _state);

        int offset = CanonicalHeaderBytes;
        for (int index = 0; index < BotCount; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(canonical[offset..], _positionX[index]);
            BinaryPrimitives.WriteInt32LittleEndian(canonical[(offset + 4)..], _positionZ[index]);
            BinaryPrimitives.WriteInt32LittleEndian(canonical[(offset + 8)..], _health[index]);
            offset += CanonicalBotBytes;
        }

        return StateHasher.ComputeHash(canonical[..offset]);
    }
}
