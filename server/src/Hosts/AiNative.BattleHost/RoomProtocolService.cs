using AiNative.Protocol.V1;
using AiNative.Realtime;
using AiNative.Server.Fantasy;
using AiNative.Server.Protocol;
using Google.Protobuf;

namespace AiNative.BattleHost;

internal sealed class RoomProtocolService(
    FantasyKcpGateway gateway,
    BattleMetrics metrics,
    BattleReplayCapture replayCapture,
    ILogger<RoomProtocolService> logger) : IAsyncDisposable
{
    private const int MaxConnections = 64;
    private const ulong ReconnectRetentionTicks = 60 * 30;
    private readonly byte[] _receiveBuffer = new byte[RealtimeProtocolCodec.MaxDatagramBytes];
    private readonly byte[] _sendBuffer = new byte[RealtimeProtocolCodec.MaxDatagramBytes];
    private readonly List<ConnectionState> _connections = new(MaxConnections);
    private readonly Dictionary<ulong, LogicalSession> _sessions = new(MaxConnections);
    private readonly bool[] _assignedEntities = new bool[MaxConnections];
    private ulong _nextSessionId;

    public int ConnectedCount => _connections.Count;

    public void PumpInbound(SyntheticRoom room, ulong roomTick)
    {
        AcceptConnections();
        ExpireDisconnectedSessions(roomTick);

        for (int index = _connections.Count - 1; index >= 0; index--)
        {
            ConnectionState connection = _connections[index];
            if (connection.Connection.Transport.State is TransportState.Closed or TransportState.Faulted)
            {
                RemoveConnection(index, roomTick);
                continue;
            }

            while (connection.Connection.Transport.TryReceive(_receiveBuffer, out ReceivedPacket packet))
            {
                if (!packet.IsComplete)
                {
                    metrics.RecordDroppedDiagnostic();
                    continue;
                }

                ProtocolDecodeStatus status = RealtimeProtocolCodec.TryDecode(
                    _receiveBuffer.AsSpan(0, packet.WrittenBytes),
                    out DecodedProtocolMessage decoded);

                if (status != ProtocolDecodeStatus.Accepted || decoded.Channel.Id != packet.Channel.Id)
                {
                    metrics.RecordDroppedDiagnostic();
                    continue;
                }

                Handle(
                    connection,
                    decoded,
                    room,
                    roomTick,
                    _receiveBuffer.AsSpan(0, packet.WrittenBytes));
            }
        }
    }

    public void PublishSnapshot(SyntheticRoom room, ulong roomTick)
    {
        if (roomTick == 0 || roomTick % 3 != 0)
        {
            return;
        }

        Snapshot snapshot = room.CreateSnapshot(roomTick);
        foreach (ConnectionState connection in _connections)
        {
            if (connection.Session is { Joined: true })
            {
                Send(connection, MessageId.Snapshot, snapshot);
            }
        }
    }

    private void AcceptConnections()
    {
        while (gateway.TryAccept(out FantasyKcpConnection? accepted) && accepted is not null)
        {
            if (_connections.Count >= MaxConnections)
            {
                accepted.DisposeAsync().AsTask().GetAwaiter().GetResult();
                metrics.RecordDroppedDiagnostic();
                continue;
            }

            _connections.Add(new ConnectionState(accepted));
            metrics.RecordConnectionAccepted();
            logger.LogInformation(
                "Accepted Fantasy KCP connection {ConnectionId} epoch {ConnectionEpoch}",
                accepted.ConnectionId,
                accepted.ConnectionEpoch);
        }
    }

    private void Handle(
        ConnectionState connection,
        DecodedProtocolMessage decoded,
        SyntheticRoom room,
        ulong roomTick,
        ReadOnlySpan<byte> frame)
    {
        switch (decoded.MessageId)
        {
            case MessageId.LoginRequest when decoded.Message is LoginRequest request:
                HandleLogin(connection, request);
                return;
            case MessageId.JoinRoomRequest when decoded.Message is JoinRoomRequest request:
                HandleJoin(connection, request);
                return;
            case MessageId.InputCommand when decoded.Message is InputCommand command:
                HandleInput(connection, command, room, roomTick, frame);
                return;
            case MessageId.InputBatch when decoded.Message is InputBatch batch:
                HandleInputBatch(connection, batch, room, roomTick, frame);
                return;
            case MessageId.ReconnectRequest when decoded.Message is ReconnectRequest request:
                HandleReconnect(connection, request, room, roomTick);
                return;
            default:
                metrics.RecordDroppedDiagnostic();
                return;
        }
    }

    private void HandleLogin(ConnectionState connection, LoginRequest request)
    {
        if (request.ProtocolMajor != 1 || connection.Session is not null)
        {
            metrics.RecordDroppedDiagnostic();
            return;
        }

        ulong sessionId = ++_nextSessionId;
        LogicalSession session = new(sessionId, connection);
        connection.Session = session;
        _sessions.Add(sessionId, session);

        Send(connection, MessageId.LoginResponse, new LoginResponse
        {
            SessionId = sessionId,
            ConnectionEpoch = connection.Connection.ConnectionEpoch,
        });
    }

    private void HandleJoin(ConnectionState connection, JoinRoomRequest request)
    {
        LogicalSession? session = connection.Session;
        if (session is null || session.SessionId != request.SessionId || session.Joined)
        {
            metrics.RecordDroppedDiagnostic();
            return;
        }

        int entityIndex = FindAvailableEntity();
        if (entityIndex < 0)
        {
            metrics.RecordDroppedDiagnostic();
            return;
        }

        _assignedEntities[entityIndex] = true;
        session.EntityIndex = entityIndex;
        session.Joined = true;
        Send(connection, MessageId.JoinRoomResponse, new JoinRoomResponse
        {
            RoomId = request.RequestedRoom == 0 ? 1U : request.RequestedRoom,
            EntityId = checked((uint)entityIndex + 1),
            TickRate = 60,
        });
    }

    private void HandleInput(
        ConnectionState connection,
        InputCommand command,
        SyntheticRoom room,
        ulong roomTick,
        ReadOnlySpan<byte> frame)
    {
        LogicalSession? session = connection.Session;
        if (session is not { Joined: true } || command.Sequence <= session.LastInputSequence)
        {
            metrics.RecordDroppedDiagnostic();
            return;
        }

        session.LastInputSequence = command.Sequence;
        replayCapture.TryRecordInput(roomTick, session.EntityIndex, frame);
        room.ApplyInput(session.EntityIndex, command.MoveXMilli, command.MoveYMilli);
    }

    private void HandleInputBatch(
        ConnectionState connection,
        InputBatch batch,
        SyntheticRoom room,
        ulong roomTick,
        ReadOnlySpan<byte> frame)
    {
        LogicalSession? session = connection.Session;
        if (session is not { Joined: true } || batch.Commands.Count is < 1 or > 2)
        {
            metrics.RecordDroppedDiagnostic();
            return;
        }

        uint previousSequence = session.LastInputSequence;
        foreach (InputCommand command in batch.Commands)
        {
            if (command.Sequence <= previousSequence)
            {
                metrics.RecordDroppedDiagnostic();
                return;
            }

            previousSequence = command.Sequence;
        }

        replayCapture.TryRecordInput(roomTick, session.EntityIndex, frame);
        foreach (InputCommand command in batch.Commands)
        {
            room.ApplyInput(session.EntityIndex, command.MoveXMilli, command.MoveYMilli);
        }

        session.LastInputSequence = previousSequence;
    }

    private void HandleReconnect(
        ConnectionState connection,
        ReconnectRequest request,
        SyntheticRoom room,
        ulong roomTick)
    {
        if (connection.Session is not null ||
            !_sessions.TryGetValue(request.SessionId, out LogicalSession? session) ||
            request.ConnectionEpoch != session.Connection.Connection.ConnectionEpoch)
        {
            metrics.RecordDroppedDiagnostic();
            return;
        }

        session.Connection.Session = null;
        session.Connection = connection;
        session.DisconnectedAtTick = null;
        connection.Session = session;
        Send(connection, MessageId.ReconnectResponse, new ReconnectResponse
        {
            ConnectionEpoch = connection.Connection.ConnectionEpoch,
            ResumeTick = roomTick,
            Snapshot = room.CreateSnapshot(roomTick),
        });
    }

    private void Send(ConnectionState connection, MessageId messageId, IMessage message)
    {
        if (!RealtimeProtocolCodec.TryEncode(
                messageId,
                message,
                _sendBuffer,
                out TransportChannel channel,
                out int writtenBytes))
        {
            metrics.RecordDroppedDiagnostic();
            return;
        }

        SendResult result = connection.Connection.Transport
            .SendAsync(channel, _sendBuffer.AsMemory(0, writtenBytes))
            .GetAwaiter()
            .GetResult();

        if (result.Status != SendStatus.Accepted)
        {
            metrics.RecordDroppedDiagnostic();
        }
    }

    private int FindAvailableEntity()
    {
        for (int index = 0; index < _assignedEntities.Length; index++)
        {
            if (!_assignedEntities[index])
            {
                return index;
            }
        }

        return -1;
    }

    private void RemoveConnection(int index, ulong roomTick)
    {
        ConnectionState connection = _connections[index];
        _connections.RemoveAt(index);
        metrics.RecordConnectionRemoved();
        if (connection.Session is { Joined: true } session && ReferenceEquals(session.Connection, connection))
        {
            session.DisconnectedAtTick = roomTick;
            connection.Session = null;
        }

        else if (connection.Session is { } unjoined)
        {
            _sessions.Remove(unjoined.SessionId);
        }

        gateway.Release(connection.Connection);
    }

    private void ExpireDisconnectedSessions(ulong roomTick)
    {
        foreach ((ulong sessionId, LogicalSession session) in _sessions.ToArray())
        {
            if (session.DisconnectedAtTick is not { } disconnectedAt ||
                roomTick - disconnectedAt <= ReconnectRetentionTicks)
            {
                continue;
            }

            if (session.EntityIndex >= 0)
            {
                _assignedEntities[session.EntityIndex] = false;
            }

            _sessions.Remove(sessionId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        int connectionCount = _connections.Count;
        foreach (ConnectionState connection in _connections)
        {
            await connection.Connection.DisposeAsync();
        }

        _connections.Clear();
        if (connectionCount > 0)
        {
            metrics.RecordConnectionRemoved(connectionCount);
        }

        _sessions.Clear();
    }

    private sealed class ConnectionState(FantasyKcpConnection connection)
    {
        public FantasyKcpConnection Connection { get; } = connection;

        public LogicalSession? Session { get; set; }
    }

    private sealed class LogicalSession(ulong sessionId, ConnectionState connection)
    {
        public ulong SessionId { get; } = sessionId;

        public ConnectionState Connection { get; set; } = connection;

        public int EntityIndex { get; set; } = -1;

        public uint LastInputSequence { get; set; }

        public bool Joined { get; set; }

        public ulong? DisconnectedAtTick { get; set; }
    }
}
