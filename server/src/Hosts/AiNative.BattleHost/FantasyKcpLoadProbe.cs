using System.Diagnostics;
using AiNative.Protocol.V1;
using AiNative.Realtime;
using AiNative.Server.Fantasy;
using AiNative.Server.Protocol;

namespace AiNative.BattleHost;

internal static class FantasyKcpLoadProbe
{
    public static async Task<FantasyKcpLoadResult> RunAsync(
        FantasyKcpGateway gateway,
        int botCount,
        int roomCount,
        TimeSpan measuredDuration,
        TimeSpan warmupDuration,
        Action loadReady,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(botCount, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            botCount,
            BattleHostCapacitySettings.BotsPerRoom * BattleHostCapacitySettings.MaximumEvaluationRooms);
        ArgumentOutOfRangeException.ThrowIfLessThan(roomCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            roomCount,
            BattleHostCapacitySettings.MaximumEvaluationRooms);
        if (botCount % roomCount != 0 ||
            botCount / roomCount is < 2 or > BattleHostCapacitySettings.BotsPerRoom)
        {
            throw new ArgumentException(
                "The KCP load probe requires 2-64 bots in each evenly populated room.",
                nameof(botCount));
        }

        int botsPerRoom = botCount / roomCount;
        ArgumentNullException.ThrowIfNull(loadReady);
        if (measuredDuration <= TimeSpan.Zero || measuredDuration > TimeSpan.FromMinutes(60) ||
            warmupDuration < TimeSpan.Zero || warmupDuration > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(measuredDuration));
        }

        using CancellationTokenSource startupTimeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startupTimeout.CancelAfter(TimeSpan.FromMinutes(2));
        FantasyKcpProbe[] probes = new FantasyKcpProbe[botCount];
        byte[][] receiveBuffers = new byte[botCount][];
        InputBatch[] inputBatches = new InputBatch[botCount];
        byte[] sendBuffer = new byte[RealtimeProtocolCodec.MaxDatagramBytes];
        long inputFrames = 0;
        long measuredInputFrames = 0;
        long inputBatchesSent = 0;
        long measuredInputBatchesSent = 0;
        long snapshotFrames = 0;
        long snapshotBytes = 0;
        ulong newestSnapshotTick = 0;
        long started = Stopwatch.GetTimestamp();

        try
        {
            HashSet<(uint RoomId, uint EntityId)> assignedEntities = new(botCount);
            const int connectionBatchSize = 8;
            uint setupSequence = 0;
            for (int batchStart = 0; batchStart < botCount; batchStart += connectionBatchSize)
            {
                int batchCount = Math.Min(connectionBatchSize, botCount - batchStart);
                Task<FantasyKcpProbe>[] connections = new Task<FantasyKcpProbe>[batchCount];
                for (int offset = 0; offset < batchCount; offset++)
                {
                    connections[offset] = gateway.ConnectLoopbackProbeAsync(startupTimeout.Token);
                }

                try
                {
                    await Task.WhenAll(connections);
                }
                finally
                {
                    for (int offset = 0; offset < batchCount; offset++)
                    {
                        if (connections[offset].IsCompletedSuccessfully)
                        {
                            probes[batchStart + offset] = connections[offset].Result;
                        }
                    }
                }

                for (int offset = 0; offset < batchCount; offset++)
                {
                    int index = batchStart + offset;
                    receiveBuffers[index] = new byte[RealtimeProtocolCodec.MaxDatagramBytes];
                    InputBatch inputBatch = new();
                    inputBatch.Commands.Add(new InputCommand());
                    inputBatch.Commands.Add(new InputCommand());
                    inputBatches[index] = inputBatch;
                    await FantasyKcpLoopbackProbe.SendAsync(
                        probes[index],
                        MessageId.LoginRequest,
                        new LoginRequest { ProtocolMajor = 1, ClientBuild = "kcp-multi-room-load" },
                        sendBuffer,
                        startupTimeout.Token);
                    LoginResponse login = await FantasyKcpLoopbackProbe.ReceiveAsync<LoginResponse>(
                        probes[index],
                        MessageId.LoginResponse,
                        startupTimeout.Token);
                    uint requestedRoom = checked((uint)(index / botsPerRoom) + 1);
                    await FantasyKcpLoopbackProbe.SendAsync(
                        probes[index],
                        MessageId.JoinRoomRequest,
                        new JoinRoomRequest
                        {
                            SessionId = login.SessionId,
                            RequestedRoom = requestedRoom,
                        },
                        sendBuffer,
                        startupTimeout.Token);
                    JoinRoomResponse join = await FantasyKcpLoopbackProbe.ReceiveAsync<JoinRoomResponse>(
                        probes[index],
                        MessageId.JoinRoomResponse,
                        startupTimeout.Token);
                    if (join.RoomId != requestedRoom ||
                        join.EntityId is 0 or > BattleHostCapacitySettings.BotsPerRoom ||
                        !assignedEntities.Add((join.RoomId, join.EntityId)) ||
                        join.TickRate != 60)
                    {
                        throw new InvalidOperationException(
                            "The KCP load probe received an invalid room assignment.");
                    }
                }

                setupSequence++;
                int connectedCount = batchStart + batchCount;
                for (int index = 0; index < connectedCount; index++)
                {
                    int roomIndex = index / botsPerRoom;
                    InputCommand command = inputBatches[index].Commands[0];
                    command.Sequence = setupSequence;
                    command.MoveXMilli = ((index + roomIndex) & 1) == 0 ? 1000 : -1000;
                    command.MoveYMilli = 0;
                    await FantasyKcpLoopbackProbe.SendAsync(
                        probes[index],
                        MessageId.InputCommand,
                        command,
                        sendBuffer,
                        startupTimeout.Token);
                }
            }

            loadReady();
            long loadStartedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long loadStartedTimestamp = Stopwatch.GetTimestamp();
            long measuredStartedUnixMilliseconds = 0;
            long measuredStartedTimestamp = 0;
            long warmupTicks = checked((long)Math.Ceiling(warmupDuration.TotalSeconds * 60));
            long measuredTicks = checked((long)Math.Ceiling(measuredDuration.TotalSeconds * 60));
            long totalTicks = checked(warmupTicks + measuredTicks);
            MonotonicFixedRatePacer pacer = new(60);
            uint sequence = setupSequence;
            for (long loadTick = 0; loadTick < totalTicks; loadTick++)
            {
                if (loadTick == warmupTicks)
                {
                    measuredStartedTimestamp = Stopwatch.GetTimestamp();
                    measuredStartedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                }

                await pacer.WaitForNextTickAsync(cancellationToken);
                sequence++;
                int commandSlot = (int)(loadTick & 1);
                for (int index = 0; index < botCount; index++)
                {
                    int roomIndex = index / botsPerRoom;
                    InputCommand command = inputBatches[index].Commands[commandSlot];
                    command.RoomTick = newestSnapshotTick;
                    command.Sequence = sequence;
                    command.MoveXMilli = ((index + roomIndex + (int)sequence) & 1) == 0 ? 1000 : -1000;
                    command.MoveYMilli = ((index + roomIndex + (int)(sequence / 30)) & 1) == 0 ? 500 : -500;
                    command.Buttons = (sequence + (uint)roomIndex) % 6 == 0 ? 1U : 0U;
                    inputFrames++;
                    if (loadTick >= warmupTicks)
                    {
                        measuredInputFrames++;
                    }
                }

                if (commandSlot == 1)
                {
                    for (int index = 0; index < botCount; index++)
                    {
                        await FantasyKcpLoopbackProbe.SendAsync(
                            probes[index],
                            MessageId.InputBatch,
                            inputBatches[index],
                            sendBuffer,
                            cancellationToken);
                        inputBatchesSent++;
                        if (loadTick >= warmupTicks)
                        {
                            measuredInputBatchesSent++;
                        }
                    }
                }

                for (int index = 0; index < botCount; index++)
                {
                    Span<byte> receiveBuffer = receiveBuffers[index];
                    while (probes[index].Transport.TryReceive(receiveBuffer, out ReceivedPacket packet))
                    {
                        if (!packet.IsComplete ||
                            RealtimeProtocolCodec.TryDecode(
                                receiveBuffer[..packet.WrittenBytes],
                                out DecodedProtocolMessage decoded) != ProtocolDecodeStatus.Accepted ||
                            decoded.MessageId != MessageId.Snapshot ||
                            decoded.Message is not Snapshot snapshot)
                        {
                            continue;
                        }

                        snapshotFrames++;
                        snapshotBytes += packet.WrittenBytes;
                        newestSnapshotTick = Math.Max(newestSnapshotTick, snapshot.RoomTick);
                    }
                }
            }

            if (snapshotFrames == 0 || newestSnapshotTick == 0)
            {
                throw new InvalidOperationException("The 64-bot KCP load probe received no production snapshots.");
            }

            long completedTimestamp = Stopwatch.GetTimestamp();
            double measuredInputElapsedSeconds =
                Stopwatch.GetElapsedTime(measuredStartedTimestamp, completedTimestamp).TotalSeconds;
            return new FantasyKcpLoadResult(
                botCount,
                roomCount,
                botsPerRoom,
                SetupSeconds: Stopwatch.GetElapsedTime(started, loadStartedTimestamp).TotalSeconds,
                LoadElapsedSeconds: Stopwatch.GetElapsedTime(loadStartedTimestamp, completedTimestamp).TotalSeconds,
                WarmupSeconds: warmupDuration.TotalSeconds,
                MeasuredSeconds: measuredDuration.TotalSeconds,
                LoadStartedUnixMilliseconds: loadStartedUnixMilliseconds,
                MeasuredStartedUnixMilliseconds: measuredStartedUnixMilliseconds,
                CompletedUnixMilliseconds: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                inputFrames,
                measuredInputFrames,
                measuredInputElapsedSeconds,
                measuredInputFrames / (double)botCount / measuredInputElapsedSeconds,
                inputBatchesSent,
                measuredInputBatchesSent,
                measuredInputBatchesSent / (double)botCount / measuredInputElapsedSeconds,
                snapshotFrames,
                snapshotBytes,
                newestSnapshotTick,
                gateway.ConnectionCount,
                gateway.OuterKcpMtu,
                Environment.GetEnvironmentVariable("AINATIVE_SOURCE_COMMIT") ?? "unrecorded",
                Environment.GetEnvironmentVariable("AINATIVE_FANTASY_COMMIT") ?? "unrecorded",
                Environment.GetEnvironmentVariable("AINATIVE_PROTOCOL_IDENTITY") ?? "unrecorded");
        }
        finally
        {
            foreach (FantasyKcpProbe? probe in probes)
            {
                if (probe is not null)
                {
                    await probe.DisposeAsync();
                }
            }
        }
    }
}

internal readonly record struct FantasyKcpLoadResult(
    int BotCount,
    int RoomCount,
    int BotsPerRoom,
    double SetupSeconds,
    double LoadElapsedSeconds,
    double WarmupSeconds,
    double MeasuredSeconds,
    long LoadStartedUnixMilliseconds,
    long MeasuredStartedUnixMilliseconds,
    long CompletedUnixMilliseconds,
    long InputFrames,
    long MeasuredInputFrames,
    double MeasuredInputElapsedSeconds,
    double MeasuredInputRateHz,
    long InputBatchesSent,
    long MeasuredInputBatchesSent,
    double MeasuredInputBatchRateHz,
    long SnapshotFrames,
    long SnapshotBytes,
    ulong NewestSnapshotTick,
    int PeakConnections,
    int OuterKcpMtu,
    string SourceCommit,
    string FantasyCommit,
    string ProtocolIdentity);
