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
        TimeSpan measuredDuration,
        TimeSpan warmupDuration,
        Action loadReady,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(botCount, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(botCount, 64);
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
        InputCommand[] commands = new InputCommand[botCount];
        byte[] sendBuffer = new byte[RealtimeProtocolCodec.MaxDatagramBytes];
        long inputFrames = 0;
        long snapshotFrames = 0;
        long snapshotBytes = 0;
        ulong newestSnapshotTick = 0;
        long started = Stopwatch.GetTimestamp();

        try
        {
            for (int index = 0; index < botCount; index++)
            {
                probes[index] = await gateway.ConnectLoopbackProbeAsync(startupTimeout.Token);
            }

            HashSet<uint> assignedEntities = new(botCount);
            for (int index = 0; index < botCount; index++)
            {
                receiveBuffers[index] = new byte[RealtimeProtocolCodec.MaxDatagramBytes];
                commands[index] = new InputCommand();
                await FantasyKcpLoopbackProbe.SendAsync(
                    probes[index],
                    MessageId.LoginRequest,
                    new LoginRequest { ProtocolMajor = 1, ClientBuild = "kcp-64-bot-load" },
                    sendBuffer,
                    startupTimeout.Token);
                LoginResponse login = await FantasyKcpLoopbackProbe.ReceiveAsync<LoginResponse>(
                    probes[index],
                    MessageId.LoginResponse,
                    startupTimeout.Token);
                await FantasyKcpLoopbackProbe.SendAsync(
                    probes[index],
                    MessageId.JoinRoomRequest,
                    new JoinRoomRequest { SessionId = login.SessionId, RequestedRoom = 1 },
                    sendBuffer,
                    startupTimeout.Token);
                JoinRoomResponse join = await FantasyKcpLoopbackProbe.ReceiveAsync<JoinRoomResponse>(
                    probes[index],
                    MessageId.JoinRoomResponse,
                    startupTimeout.Token);
                if (join.EntityId is 0 or > 64 || !assignedEntities.Add(join.EntityId) || join.TickRate != 60)
                {
                    throw new InvalidOperationException("The 64-bot KCP load probe received an invalid room assignment.");
                }
            }

            loadReady();
            long loadStartedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long loadStartedTimestamp = Stopwatch.GetTimestamp();
            long measuredStartedUnixMilliseconds = 0;
            using PeriodicTimer timer = new(TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 60));
            using CancellationTokenSource durationLimit =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            durationLimit.CancelAfter(warmupDuration + measuredDuration);
            uint sequence = 0;
            try
            {
                while (await timer.WaitForNextTickAsync(durationLimit.Token))
                {
                    if (measuredStartedUnixMilliseconds == 0 &&
                        Stopwatch.GetElapsedTime(loadStartedTimestamp) >= warmupDuration)
                    {
                        measuredStartedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    }

                    sequence++;
                    for (int index = 0; index < botCount; index++)
                    {
                        InputCommand command = commands[index];
                        command.RoomTick = newestSnapshotTick;
                        command.Sequence = sequence;
                        command.MoveXMilli = ((index + (int)sequence) & 1) == 0 ? 1000 : -1000;
                        command.MoveYMilli = ((index + (int)(sequence / 30)) & 1) == 0 ? 500 : -500;
                        await FantasyKcpLoopbackProbe.SendAsync(
                            probes[index],
                            MessageId.InputCommand,
                            command,
                            sendBuffer,
                            durationLimit.Token);
                        inputFrames++;
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
            }
            catch (OperationCanceledException) when (
                durationLimit.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
            }

            if (snapshotFrames == 0 || newestSnapshotTick == 0)
            {
                throw new InvalidOperationException("The 64-bot KCP load probe received no production snapshots.");
            }

            return new FantasyKcpLoadResult(
                botCount,
                SetupSeconds: Stopwatch.GetElapsedTime(started, loadStartedTimestamp).TotalSeconds,
                LoadElapsedSeconds: Stopwatch.GetElapsedTime(loadStartedTimestamp).TotalSeconds,
                WarmupSeconds: warmupDuration.TotalSeconds,
                MeasuredSeconds: measuredDuration.TotalSeconds,
                LoadStartedUnixMilliseconds: loadStartedUnixMilliseconds,
                MeasuredStartedUnixMilliseconds: measuredStartedUnixMilliseconds,
                CompletedUnixMilliseconds: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                inputFrames,
                snapshotFrames,
                snapshotBytes,
                newestSnapshotTick,
                gateway.ConnectionCount);
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
    double SetupSeconds,
    double LoadElapsedSeconds,
    double WarmupSeconds,
    double MeasuredSeconds,
    long LoadStartedUnixMilliseconds,
    long MeasuredStartedUnixMilliseconds,
    long CompletedUnixMilliseconds,
    long InputFrames,
    long SnapshotFrames,
    long SnapshotBytes,
    ulong NewestSnapshotTick,
    int PeakConnections);
