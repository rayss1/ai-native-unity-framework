using System.Buffers.Binary;
using AiNative.Gameplay;

namespace AiNative.BattleHost;

internal sealed record BattleHostCapacitySettings(int RoomCount)
{
    public const int BotsPerRoom = 64;
    public const int MaximumEvaluationRooms = 2;

    public int TotalBotCapacity => checked(RoomCount * BotsPerRoom);

    public static BattleHostCapacitySettings Create(IConfiguration configuration)
    {
        int roomCount = configuration.GetValue("AINATIVE_EVALUATION_ROOM_COUNT", 1);
        if (roomCount is < 1 or > MaximumEvaluationRooms)
        {
            throw new InvalidOperationException(
                $"AINATIVE_EVALUATION_ROOM_COUNT must be between 1 and {MaximumEvaluationRooms}.");
        }

        if (roomCount > 1 &&
            !configuration.GetValue("AINATIVE_ENABLE_EVALUATION_ENDPOINTS", false))
        {
            throw new InvalidOperationException(
                "Multi-room execution is evaluation-only until its capacity evidence is accepted.");
        }

        if (roomCount > 1 &&
            !string.IsNullOrWhiteSpace(configuration["AINATIVE_REPLAY_CAPTURE_PATH"]))
        {
            throw new InvalidOperationException(
                "The version 1 replay format is single-room; multi-room evaluation cannot enable replay capture.");
        }

        return new BattleHostCapacitySettings(roomCount);
    }
}

internal sealed class BattleRoomSet
{
    private static readonly XxHash64StateHasher StateHasher = new();
    private readonly SyntheticRoom[] _rooms;
    private readonly bool[][] _assignedEntities;

    public BattleRoomSet(BattleHostCapacitySettings settings)
    {
        Settings = settings;
        _rooms = Enumerable.Range(0, settings.RoomCount)
            .Select(_ => new SyntheticRoom(BattleHostCapacitySettings.BotsPerRoom))
            .ToArray();
        _assignedEntities = Enumerable.Range(0, settings.RoomCount)
            .Select(_ => new bool[BattleHostCapacitySettings.BotsPerRoom])
            .ToArray();
    }

    public BattleHostCapacitySettings Settings { get; }

    public int RoomCount => _rooms.Length;

    public SyntheticRoom this[int roomIndex] => _rooms[roomIndex];

    public void TickAll()
    {
        foreach (SyntheticRoom room in _rooms)
        {
            room.Tick();
        }
    }

    public bool TryAssignEntity(int roomIndex, out int entityIndex)
    {
        bool[] assigned = _assignedEntities[roomIndex];
        for (int index = 0; index < assigned.Length; index++)
        {
            if (assigned[index])
            {
                continue;
            }

            assigned[index] = true;
            entityIndex = index;
            return true;
        }

        entityIndex = -1;
        return false;
    }

    public void ReleaseEntity(int roomIndex, int entityIndex)
    {
        if ((uint)roomIndex < (uint)_assignedEntities.Length &&
            (uint)entityIndex < (uint)_assignedEntities[roomIndex].Length)
        {
            _assignedEntities[roomIndex][entityIndex] = false;
        }
    }

    public ulong ComputeCombinedStateHash()
    {
        if (_rooms.Length == 1)
        {
            return _rooms[0].ComputeStateHash();
        }

        Span<byte> canonical = stackalloc byte[4 + (BattleHostCapacitySettings.MaximumEvaluationRooms * 8)];
        BinaryPrimitives.WriteUInt32LittleEndian(canonical, checked((uint)_rooms.Length));
        int offset = 4;
        foreach (SyntheticRoom room in _rooms)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(canonical[offset..], room.ComputeStateHash());
            offset += 8;
        }

        return StateHasher.ComputeHash(canonical[..offset]);
    }
}
