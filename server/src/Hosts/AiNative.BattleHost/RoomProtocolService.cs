using AiNative.Protocol.V1;
using AiNative.Gameplay;
using AiNative.Realtime;
using AiNative.Server.Fantasy;
using AiNative.Server.Protocol;
using Google.Protobuf;

namespace AiNative.BattleHost;

internal sealed class RoomProtocolService(
    FantasyKcpGateway gateway,
    BattleRoomSet rooms,
    ArenaRoom arenaRoom,
    BattleGameModeSettings gameMode,
    BattleMetrics metrics,
    BattleReplayCapture replayCapture,
    ILogger<RoomProtocolService> logger) : IAsyncDisposable
{
    private const ulong ReconnectRetentionTicks = 60 * 30;
    private readonly byte[] _receiveBuffer = new byte[RealtimeProtocolCodec.MaxDatagramBytes];
    private readonly byte[] _sendBuffer = new byte[RealtimeProtocolCodec.MaxDatagramBytes];
    private readonly PreparedLegacySnapshot[] _snapshotFrames =
        Enumerable.Range(0, rooms.RoomCount).Select(_ => new PreparedLegacySnapshot()).ToArray();
    private readonly List<ConnectionState> _connections = new(rooms.Settings.TotalBotCapacity);
    private readonly Dictionary<ulong, LogicalSession> _sessions = new(rooms.Settings.TotalBotCapacity);
    private ulong _nextSessionId;
    private uint _arenaEventSequence;
    private readonly ArenaCombatEventRecord[] _arenaEvents = new ArenaCombatEventRecord[ArenaRoom.MaximumEvents];
    private long _observedArenaEventDrops;

    public int ConnectedCount => _connections.Count;

    public void PumpInbound(ulong roomTick)
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
                    roomTick,
                    _receiveBuffer.AsSpan(0, packet.WrittenBytes));
            }
        }
    }

    public void PublishSnapshots(ulong roomTick)
    {
        if (gameMode.IsArena) PublishArenaEvents();
        if (roomTick == 0 || roomTick % 3 != 0)
        {
            return;
        }

        try
        {
            foreach (ConnectionState connection in _connections)
            {
                if (connection.Session is { Joined: true } session)
                {
                    if (gameMode.IsArena)
                    {
                        Send(connection, MessageId.Snapshot, CreateArenaSnapshot(session, roomTick));
                        continue;
                    }
                    PreparedLegacySnapshot frame = _snapshotFrames[session.RoomIndex];
                    if ((!frame.IsPrepared && !frame.TryPrepare(rooms[session.RoomIndex].CreateSnapshot(roomTick))) ||
                        !frame.TryWriteRecipient(session.LastInputSequence, _sendBuffer, out int writtenBytes))
                    {
                        metrics.RecordDroppedDiagnostic();
                        continue;
                    }
                    SendEncoded(connection, frame.Channel, writtenBytes);
                }
            }
        }
        finally
        {
            foreach (PreparedLegacySnapshot frame in _snapshotFrames) frame.Reset();
        }
    }

    private void PublishArenaEvents()
    {
        long dropped = arenaRoom.DroppedEventCount;
        if (dropped > _observedArenaEventDrops) metrics.RecordDroppedDiagnostic();
        _observedArenaEventDrops = dropped;
        int count = arenaRoom.DrainEvents(_arenaEvents);
        for (int i = 0; i < count; i++)
        {
            ArenaCombatEventRecord e = _arenaEvents[i];
            var message = new ReliableEvent
            {
                RoomTick = e.Tick, Sequence = ++_arenaEventSequence, EventType = (uint)e.Kind,
                CombatEvent = new ArenaCombatEvent
                {
                    EventType = (ArenaCombatEventType)(int)e.Kind, EventTick = e.Tick,
                    SourceEntityId = e.SourceEntityId, TargetEntityId = e.TargetEntityId,
                    WeaponId = (AiNative.Protocol.V1.ArenaWeaponId)(int)e.Weapon, Damage = (uint)Math.Max(0, e.Damage),
                    PositionXMilli = e.PositionXMillimetres, PositionYMilli = e.PositionYMillimetres, PositionZMilli = e.PositionZMillimetres
                }
            };
            foreach (ConnectionState connection in _connections)
                if (connection.Session is { Joined: true }) Send(connection, MessageId.ReliableEvent, message);
        }
    }

    private void AcceptConnections()
    {
        while (gateway.TryAccept(out FantasyKcpConnection? accepted) && accepted is not null)
        {
            if (_connections.Count >= gameMode.GetConnectionCapacity(rooms.Settings))
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
                HandleInput(connection, command, roomTick, frame);
                return;
            case MessageId.InputBatch when decoded.Message is InputBatch batch:
                HandleInputBatch(connection, batch, roomTick, frame);
                return;
            case MessageId.ReconnectRequest when decoded.Message is ReconnectRequest request:
                HandleReconnect(connection, request, roomTick);
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

        uint requestedRoom = request.RequestedRoom == 0 ? 1U : request.RequestedRoom;
        if (requestedRoom > (uint)rooms.RoomCount)
        {
            metrics.RecordDroppedDiagnostic();
            return;
        }

        int roomIndex = checked((int)requestedRoom - 1);
        int entityIndex;
        if (gameMode.IsArena)
        {
            if (!arenaRoom.TryJoin(out uint arenaEntityId))
            {
                metrics.RecordDroppedDiagnostic();
                return;
            }

            entityIndex = checked((int)arenaEntityId - 1);
        }
        else if (!rooms.TryAssignEntity(roomIndex, out entityIndex))
        {
            metrics.RecordDroppedDiagnostic();
            return;
        }

        session.RoomIndex = roomIndex;
        session.EntityIndex = entityIndex;
        session.Joined = true;
        Send(connection, MessageId.JoinRoomResponse, new JoinRoomResponse
        {
            RoomId = requestedRoom,
            EntityId = checked((uint)entityIndex + 1),
            TickRate = 60,
        });
    }

    private void HandleInput(
        ConnectionState connection,
        InputCommand command,
        ulong roomTick,
        ReadOnlySpan<byte> frame)
    {
        LogicalSession? session = connection.Session;
        if (session is not { Joined: true } || command.Sequence <= session.LastInputSequence)
        {
            metrics.RecordDroppedDiagnostic();
            return;
        }

        if (gameMode.IsArena)
        {
            ArenaInput input = new(
                command.Sequence,
                command.RoomTick,
                command.MoveXMilli,
                command.MoveYMilli,
                command.LookYawMilli,
                command.LookPitchMilli,
                (ArenaButtons)command.Buttons,
                (AiNative.Gameplay.ArenaWeaponId)command.WeaponId);
            if (!arenaRoom.SubmitInput(checked((uint)session.EntityIndex + 1), input))
            {
                metrics.RecordDroppedDiagnostic();
                return;
            }

            session.LastInputSequence = command.Sequence;
            return;
        }

        session.LastInputSequence = command.Sequence;
        replayCapture.TryRecordInput(session.RoomIndex, roomTick, session.EntityIndex, frame);
        rooms[session.RoomIndex].ApplyInput(session.EntityIndex, command.MoveXMilli, command.MoveYMilli);
    }

    private void HandleInputBatch(
        ConnectionState connection,
        InputBatch batch,
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

        if (gameMode.IsArena)
        {
            foreach (InputCommand command in batch.Commands)
            {
                ArenaInput input = new(
                    command.Sequence,
                    command.RoomTick,
                    command.MoveXMilli,
                    command.MoveYMilli,
                    command.LookYawMilli,
                    command.LookPitchMilli,
                    (ArenaButtons)command.Buttons,
                    (AiNative.Gameplay.ArenaWeaponId)command.WeaponId);
                if (!arenaRoom.SubmitInput(checked((uint)session.EntityIndex + 1), input))
                {
                    metrics.RecordDroppedDiagnostic();
                    return;
                }
            }

            session.LastInputSequence = previousSequence;
            return;
        }

        replayCapture.TryRecordInput(session.RoomIndex, roomTick, session.EntityIndex, frame);
        foreach (InputCommand command in batch.Commands)
        {
            rooms[session.RoomIndex].ApplyInput(
                session.EntityIndex,
                command.MoveXMilli,
                command.MoveYMilli);
        }

        session.LastInputSequence = previousSequence;
    }

    private void HandleReconnect(
        ConnectionState connection,
        ReconnectRequest request,
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
        Snapshot resumeSnapshot = gameMode.IsArena
            ? CreateArenaSnapshot(session, roomTick)
            : rooms[session.RoomIndex].CreateSnapshot(roomTick);
        if (!gameMode.IsArena) resumeSnapshot.LastProcessedInputSequence = session.LastInputSequence;
        Send(connection, MessageId.ReconnectResponse, new ReconnectResponse
        {
            ConnectionEpoch = connection.Connection.ConnectionEpoch,
            ResumeTick = roomTick,
            Snapshot = resumeSnapshot,
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

        SendEncoded(connection, channel, writtenBytes);
    }

    private void SendEncoded(ConnectionState connection, TransportChannel channel, int writtenBytes)
    {
        SendResult result = connection.Connection.Transport
            .SendAsync(channel, _sendBuffer.AsMemory(0, writtenBytes))
            .GetAwaiter()
            .GetResult();

        if (result.Status != SendStatus.Accepted)
        {
            metrics.RecordDroppedDiagnostic();
        }
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
            if (gameMode.IsArena)
            {
                // Keep the logical slot reserved during reconnect retention.
            }
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
                if (gameMode.IsArena)
                {
                    arenaRoom.Leave(checked((uint)session.EntityIndex + 1));
                }
                else
                {
                    rooms.ReleaseEntity(session.RoomIndex, session.EntityIndex);
                }
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

        public int RoomIndex { get; set; } = -1;

        public uint LastInputSequence { get; set; }

        public bool Joined { get; set; }

        public ulong? DisconnectedAtTick { get; set; }
    }

    private Snapshot CreateArenaSnapshot(LogicalSession session, ulong roomTick)
    {
        Snapshot snapshot = new()
        {
            ProtocolMajor = 1,
            RoomTick = roomTick,
            BaselineTick = roomTick > 3 ? roomTick - 3 : 0,
            StateHash = arenaRoom.ComputeStateHash(),
            LastProcessedInputSequence = arenaRoom.TryGetPlayer((uint)session.EntityIndex + 1, out ArenaPlayerState acknowledged) ? acknowledged.LastProcessedInputSequence : 0,
            MatchPhase = (AiNative.Protocol.V1.ArenaMatchPhase)(int)arenaRoom.Phase,
            RemainingTicks = checked((uint)Math.Min(uint.MaxValue, arenaRoom.RemainingTicks)),
            LeaderEntityId = arenaRoom.LeaderEntityId,
        };

        for (uint entityId = 1; entityId <= ArenaRoom.MaxPlayers; entityId++)
        {
            if (!arenaRoom.TryGetPlayer(entityId, out ArenaPlayerState state))
            {
                continue;
            }

            snapshot.Players.Add(new PlayerState
            {
                EntityId = entityId,
                PositionXMilli = state.PositionXMillimetres,
                PositionYMilli = state.PositionYMillimetres,
                PositionZMilli = state.PositionZMillimetres,
                YawMillidegrees = state.YawMillidegrees,
                PitchMillidegrees = state.PitchMillidegrees,
                Health = checked((uint)Math.Max(0, state.Health)),
                VelocityXMilliPerSecond = state.VelocityXMillimetresPerSecond,
                VelocityYMilliPerSecond = state.VelocityYMillimetresPerSecond,
                VelocityZMilliPerSecond = state.VelocityZMillimetresPerSecond,
                WeaponId = (uint)state.Weapon,
                Armor = checked((uint)Math.Max(0, state.Armor)),
                Alive = state.Alive,
                Kills = state.Kills,
            });
        }

        Span<ArenaPickupState> pickups = stackalloc ArenaPickupState[8];
        int pickupCount = arenaRoom.CopyPickups(pickups);
        for (int index = 0; index < pickupCount; index++)
        {
            ArenaPickupState pickup = pickups[index];
            snapshot.Pickups.Add(new PickupState
            {
                PickupId = checked((uint)pickup.Id),
                PickupType = (AiNative.Protocol.V1.ArenaPickupType)(int)pickup.Type,
                PositionXMilli = pickup.PositionXMillimetres,
                PositionYMilli = pickup.PositionYMillimetres,
                PositionZMilli = pickup.PositionZMillimetres,
                Active = pickup.Active,
                RespawnTick = pickup.RespawnTick,
            });
        }

        return snapshot;
    }
}
