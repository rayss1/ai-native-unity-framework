using AiNative.Protocol.V1;
using AiNative.Realtime;
using AiNative.Server.Fantasy;
using AiNative.Server.Protocol;

namespace AiNative.BattleHost;

internal static class FantasyKcpLoopbackProbe
{
    public static async Task<FantasyKcpProbeResult> RunAsync(
        FantasyKcpGateway gateway,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));

        LoginResponse login;
        JoinRoomResponse join;
        Snapshot snapshot;

        await using (FantasyKcpProbe probe = await gateway.ConnectLoopbackProbeAsync(timeout.Token))
        {
            await SendAsync(
                probe,
                MessageId.LoginRequest,
                new LoginRequest { ProtocolMajor = 1, ClientBuild = "kcp-loopback-probe" },
                timeout.Token);
            login = await ReceiveAsync<LoginResponse>(
                probe,
                MessageId.LoginResponse,
                timeout.Token);

            await SendAsync(
                probe,
                MessageId.JoinRoomRequest,
                new JoinRoomRequest { SessionId = login.SessionId, RequestedRoom = 1 },
                timeout.Token);
            join = await ReceiveAsync<JoinRoomResponse>(
                probe,
                MessageId.JoinRoomResponse,
                timeout.Token);

            await SendAsync(
                probe,
                MessageId.InputCommand,
                new InputCommand
                {
                    RoomTick = 0,
                    Sequence = 1,
                    MoveXMilli = 1000,
                    MoveYMilli = -500,
                },
                timeout.Token);
            snapshot = await ReceiveAsync<Snapshot>(
                probe,
                MessageId.Snapshot,
                timeout.Token);
        }

        await using FantasyKcpProbe reconnectProbe = await gateway.ConnectLoopbackProbeAsync(timeout.Token);
        await SendAsync(
            reconnectProbe,
            MessageId.ReconnectRequest,
            new ReconnectRequest
            {
                SessionId = login.SessionId,
                ConnectionEpoch = login.ConnectionEpoch,
                LastReceivedTick = snapshot.RoomTick,
            },
            timeout.Token);
        ReconnectResponse reconnect = await ReceiveAsync<ReconnectResponse>(
            reconnectProbe,
            MessageId.ReconnectResponse,
            timeout.Token);

        if (reconnect.ConnectionEpoch == login.ConnectionEpoch ||
            reconnect.Snapshot is null ||
            reconnect.ResumeTick < snapshot.RoomTick)
        {
            throw new InvalidOperationException("Fantasy KCP reconnect returned an invalid epoch or snapshot.");
        }

        return new FantasyKcpProbeResult(
            login.SessionId,
            login.ConnectionEpoch,
            reconnect.ConnectionEpoch,
            join.RoomId,
            join.EntityId,
            snapshot.RoomTick,
            reconnect.ResumeTick);
    }

    internal static Task SendAsync(
        FantasyKcpProbe probe,
        MessageId messageId,
        Google.Protobuf.IMessage message,
        CancellationToken cancellationToken)
    {
        byte[] sendBuffer = new byte[RealtimeProtocolCodec.MaxDatagramBytes];
        return SendAsync(probe, messageId, message, sendBuffer, cancellationToken);
    }

    internal static async Task SendAsync(
        FantasyKcpProbe probe,
        MessageId messageId,
        Google.Protobuf.IMessage message,
        byte[] sendBuffer,
        CancellationToken cancellationToken)
    {
        if (!RealtimeProtocolCodec.TryEncode(
                messageId,
                message,
                sendBuffer,
                out TransportChannel channel,
                out int writtenBytes))
        {
            throw new InvalidOperationException($"Could not encode the KCP loopback {messageId} message.");
        }

        SendResult send = await probe.Transport.SendAsync(
            channel,
            sendBuffer.AsMemory(0, writtenBytes),
            cancellationToken);
        if (send.Status != SendStatus.Accepted)
        {
            throw new InvalidOperationException($"KCP loopback {messageId} send failed with {send.Status}.");
        }
    }

    internal static async Task<TMessage> ReceiveAsync<TMessage>(
        FantasyKcpProbe probe,
        MessageId expectedMessageId,
        CancellationToken cancellationToken)
        where TMessage : class, Google.Protobuf.IMessage
    {
        byte[] receiveBuffer = new byte[RealtimeProtocolCodec.MaxDatagramBytes];
        while (!cancellationToken.IsCancellationRequested)
        {
            if (probe.Transport.TryReceive(receiveBuffer, out ReceivedPacket packet))
            {
                ProtocolDecodeStatus status = RealtimeProtocolCodec.TryDecode(
                    receiveBuffer.AsSpan(0, packet.WrittenBytes),
                    out DecodedProtocolMessage decoded);
                if (packet.IsComplete &&
                    status == ProtocolDecodeStatus.Accepted &&
                    decoded.MessageId == expectedMessageId &&
                    decoded.Message is TMessage response)
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
    ulong ResumeTick);
