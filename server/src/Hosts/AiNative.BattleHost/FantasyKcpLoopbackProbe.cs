using AiNative.Protocol.V1;
using AiNative.Realtime;
using AiNative.Server.Fantasy;
using AiNative.Server.Protocol;

namespace AiNative.BattleHost;

internal static class FantasyKcpLoopbackProbe
{
    public static async Task<FantasyKcpProbeResult> RunAsync(
        FantasyKcpGateway gateway,
        CancellationToken cancellationToken
    )
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        timeout.CancelAfter(TimeSpan.FromSeconds(12));

        S2C_LoginResponse login;
        S2C_JoinRoomResponse join;
        S2C_SnapshotPush snapshot;

        await using (FantasyKcpProbe probe = await gateway.ConnectLoopbackProbeAsync(timeout.Token))
        {
            await SendAsync(
                probe,
                MessageId.C2SLoginRequest,
                new C2S_LoginRequest { ProtocolMajor = 1, ClientBuild = "kcp-loopback-probe" },
                timeout.Token
            );
            login = await ReceiveAsync<S2C_LoginResponse>(
                probe,
                MessageId.S2CLoginResponse,
                timeout.Token
            );

            await SendAsync(
                probe,
                MessageId.C2SJoinRoomRequest,
                new C2S_JoinRoomRequest { SessionId = login.SessionId, RequestedRoom = 1 },
                timeout.Token
            );
            join = await ReceiveAsync<S2C_JoinRoomResponse>(
                probe,
                MessageId.S2CJoinRoomResponse,
                timeout.Token
            );

            S2C_SnapshotPush baseline = await ReceiveAsync<S2C_SnapshotPush>(
                probe,
                MessageId.S2CSnapshotPush,
                timeout.Token
            );
            PlayerState baselinePlayer = baseline.Players.First(player =>
                player.EntityId == join.EntityId
            );

            await SendAsync(
                probe,
                MessageId.C2SInputCommand,
                new C2S_InputCommand
                {
                    RoomTick = 0,
                    Sequence = 1,
                    MoveXMilli = 1000,
                    MoveYMilli = -500,
                },
                timeout.Token
            );
            do
            {
                snapshot = await ReceiveAsync<S2C_SnapshotPush>(
                    probe,
                    MessageId.S2CSnapshotPush,
                    timeout.Token
                );
            } while (
                snapshot.Players.First(player => player.EntityId == join.EntityId).PositionXMilli
                < baselinePlayer.PositionXMilli + 25
            );

            if (snapshot.LastProcessedInputSequence != 1)
            {
                throw new InvalidOperationException(
                    "Fantasy KCP snapshot did not acknowledge the processed prediction input."
                );
            }
        }

        await using FantasyKcpProbe reconnectProbe = await gateway.ConnectLoopbackProbeAsync(
            timeout.Token
        );
        await SendAsync(
            reconnectProbe,
            MessageId.C2SReconnectRequest,
            new C2S_ReconnectRequest
            {
                SessionId = login.SessionId,
                ConnectionEpoch = login.ConnectionEpoch,
                LastReceivedTick = snapshot.RoomTick,
            },
            timeout.Token
        );
        S2C_ReconnectResponse reconnect = await ReceiveAsync<S2C_ReconnectResponse>(
            reconnectProbe,
            MessageId.S2CReconnectResponse,
            timeout.Token
        );

        if (
            reconnect.ConnectionEpoch == login.ConnectionEpoch
            || reconnect.Snapshot is null
            || reconnect.ResumeTick < snapshot.RoomTick
            || reconnect.Snapshot.LastProcessedInputSequence != 1
        )
        {
            throw new InvalidOperationException(
                "Fantasy KCP reconnect returned an invalid epoch or snapshot."
            );
        }

        return new FantasyKcpProbeResult(
            login.SessionId,
            login.ConnectionEpoch,
            reconnect.ConnectionEpoch,
            join.RoomId,
            join.EntityId,
            snapshot.RoomTick,
            reconnect.ResumeTick,
            snapshot.LastProcessedInputSequence
        );
    }

    internal static Task SendAsync(
        FantasyKcpProbe probe,
        MessageId messageId,
        Google.Protobuf.IMessage message,
        CancellationToken cancellationToken
    )
    {
        byte[] sendBuffer = new byte[RealtimeProtocolCodec.MaxDatagramBytes];
        return SendAsync(probe, messageId, message, sendBuffer, cancellationToken);
    }

    internal static async Task SendAsync(
        FantasyKcpProbe probe,
        MessageId messageId,
        Google.Protobuf.IMessage message,
        byte[] sendBuffer,
        CancellationToken cancellationToken
    )
    {
        if (
            !RealtimeProtocolCodec.TryEncode(
                messageId,
                message,
                sendBuffer,
                out TransportChannel channel,
                out int writtenBytes
            )
        )
        {
            throw new InvalidOperationException(
                $"Could not encode the KCP loopback {messageId} message."
            );
        }

        SendResult send = await probe.Transport.SendAsync(
            channel,
            sendBuffer.AsMemory(0, writtenBytes),
            cancellationToken
        );
        if (send.Status != SendStatus.Accepted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException(
                $"KCP loopback {messageId} send failed with {send.Status}."
            );
        }
    }

    internal static async Task<TMessage> ReceiveAsync<TMessage>(
        FantasyKcpProbe probe,
        MessageId expectedMessageId,
        CancellationToken cancellationToken
    )
        where TMessage : class, Google.Protobuf.IMessage
    {
        byte[] receiveBuffer = new byte[RealtimeProtocolCodec.MaxDatagramBytes];
        while (!cancellationToken.IsCancellationRequested)
        {
            if (probe.Transport.TryReceive(receiveBuffer, out ReceivedPacket packet))
            {
                ProtocolDecodeStatus status = RealtimeProtocolCodec.TryDecode(
                    receiveBuffer.AsSpan(0, packet.WrittenBytes),
                    out DecodedProtocolMessage decoded
                );
                if (
                    packet.IsComplete
                    && status == ProtocolDecodeStatus.Accepted
                    && decoded.MessageId == expectedMessageId
                    && decoded.Message is TMessage response
                )
                {
                    return response;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
        }

        throw new TimeoutException($"Fantasy KCP loopback did not return {expectedMessageId}.");
    }
}

internal readonly record struct FantasyKcpProbeResult(
    ulong SessionId,
    uint InitialConnectionEpoch,
    uint ReconnectedEpoch,
    uint RoomId,
    uint EntityId,
    ulong SnapshotTick,
    ulong ResumeTick,
    uint LastProcessedInputSequence
);
