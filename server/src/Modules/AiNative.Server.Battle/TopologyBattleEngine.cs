using System.Collections.Concurrent;
using System.Security.Cryptography;

using AiNative.Gameplay;
using AiNative.Protocol.V1;
using AiNative.Protocol.Backend.V1;
using AiNative.Realtime;
using AiNative.Server.Backend;
using AiNative.Server.Fantasy;
using AiNative.Server.Protocol;
using AiNative.Server.Rooms;
using Google.Protobuf;
using AiNative.BattleHost;
namespace AiNative.Server.Battle;

internal sealed class TopologyBattleEngine(FantasyKcpGateway gateway, EntryTicketVerifier tickets, DurableResultOutbox outbox, int matchLength = ArenaRoom.MatchLengthTicks, string? replayDirectory = null, ArenaReplayIdentity? replayIdentity = null, int replayCapacity = 4096, int maxReplayFiles = ArenaReplayStorage.DefaultMaximumFiles, long maxReplayBytes = ArenaReplayStorage.DefaultMaximumBytes, TimeProvider? clock = null) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, RuntimeRoom> _rooms = new(StringComparer.Ordinal);
    private readonly List<Connection> _connections = new();
    private readonly byte[] _receive = new byte[1200], _send = new byte[1200];
    private readonly SemaphoreSlim _flush = new(1, 1);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ArenaReplayStorage? _replayStorage = replayDirectory == null ? null : new(replayDirectory, maxReplayFiles, maxReplayBytes);
    private readonly string? _replayDirectory = replayDirectory;
    private readonly ArenaReplayIdentity? _replayIdentity = replayIdentity;
    private readonly int _replayCapacity = replayCapacity;
    private readonly DurableResultOutbox _outbox = outbox;
    private readonly ConcurrentDictionary<string, ReplayFile> _replays = new(StringComparer.Ordinal);
    private readonly object _replayGate = new();
    private int _replayFailure;
    private long _incompleteReplays;
    public long IncompleteReplayCount => Interlocked.Read(ref _incompleteReplays);
    public bool ReplayHealthy => Volatile.Read(ref _replayFailure) == 0 && _replayStorage?.Healthy != false;
    public bool ReplayCanAdmit => ReplayHealthy && (_replayStorage?.CanReserve(ArenaReplayStorage.ReservationBytes(matchLength)) ?? true);
    public int ReplayFiles => _replayStorage?.Files ?? 0;
    public long ReplayBytes => _replayStorage?.Bytes ?? 0;
    private sealed class ReplayFile(ArenaReplayCapture capture)
    {
        public readonly ArenaReplayCapture Capture = capture;
        public FileStream? Stream; public ArenaReplayWriter? Writer; public string? PendingPath, FinalPath;
    }
    private long _drops, _eventDrops;
    public BattleWorkerPool Pool { get; set; } = null!;
    public long DroppedPackets => Interlocked.Read(ref _drops);
    public long DroppedEvents => Interlocked.Read(ref _eventDrops);
    public IWorkerRoom CreateRoom(RoomAllocation allocation)
    {
        if (!ReplayHealthy) throw new InvalidOperationException("replay-persistence-unavailable");
        long replayBudget = ArenaReplayStorage.ReservationBytes(matchLength);
        if (_replayStorage != null && !_replayStorage.TryReserve(allocation.AllocationId, replayBudget))
            throw new InvalidOperationException("replay-storage-quota");
        try
        {
            var room = new RuntimeRoom(this, allocation.Clone(), matchLength);
            if (!_rooms.TryAdd(allocation.RoomId, room)) throw new InvalidOperationException("duplicate-room");
            if (room.Replay != null) _replays.TryAdd(allocation.AllocationId, new ReplayFile(room.Replay));
            return room;
        }
        catch { _replayStorage?.ReleaseUncreated(allocation.AllocationId); throw; }
    }

    public ValueTask PumpAsync(CancellationToken ct = default)
    {
        while (gateway.TryAccept(out var accepted)) _connections.Add(new Connection(accepted!, _clock.GetTimestamp()));
        for (int i = _connections.Count - 1; i >= 0; i--)
        {
            var c = _connections[i];
            if (c.Revoked || (!c.Joined && _clock.GetElapsedTime(c.AcceptedTimestamp) >= TimeSpan.FromSeconds(10)) || c.Room?.Disposed == true || c.Wire.Transport.State is TransportState.Closed or TransportState.Faulted)
            { gateway.Release(c.Wire); _connections.RemoveAt(i); continue; }
            for (int n = 0; n < 16 && !c.Revoked && c.Wire.Transport.TryReceive(_receive, out var packet); n++)
            {
                if (packet.ConnectionEpoch != c.Wire.ConnectionEpoch || !packet.IsComplete || RealtimeProtocolCodec.TryDecode(_receive.AsSpan(0, packet.WrittenBytes), out var decoded) != ProtocolDecodeStatus.Accepted || !decoded.Channel.Equals(packet.Channel)) { Drop(); continue; }
                Handle(c, decoded.Message);
            }
        }
        foreach (var room in _rooms.Values) Publish(room);
        DrainReplays();
        return ValueTask.CompletedTask;
    }
    private void DrainReplays(int budget = 8192)
    {
        if (_replayDirectory is null) return;
        if (!ReplayHealthy) Pool.BeginDrain();
        lock (_replayGate)
        {
            foreach (var pair in _replays)
            {
                ReplayFile file = pair.Value;
                try
                {
                    if (file.Writer == null)
                    {
                        Directory.CreateDirectory(_replayDirectory);
                        string name = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(file.Capture.Allocation + ":" + file.Capture.Room + ":" + file.Capture.Boot))).ToLowerInvariant();
                        file.FinalPath = Path.Combine(_replayDirectory, name + ".anar");
                        file.PendingPath = file.FinalPath + ".pending";
                        file.Stream = new FileStream(file.PendingPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536);
                        file.Writer = new ArenaReplayWriter(file.Stream, file.Capture, ArenaReplayStorage.ReservationBytes(matchLength));
                    }
                    file.Writer.Drain(budget);
                    if (!file.Writer.IsFinalized) continue;
                    if (file.Capture.Status != ArenaReplayStatus.Complete) Interlocked.Increment(ref _incompleteReplays);
                    long actualBytes = file.Stream!.Length;
                    file.Writer.Dispose(); file.Stream.Dispose();
                    File.Move(file.PendingPath!, file.Capture.Status == ArenaReplayStatus.Complete ? file.FinalPath! : file.FinalPath! + ".incomplete");
                    _replayStorage!.Complete(pair.Key, actualBytes);
                    _replays.TryRemove(pair.Key, out _);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    file.Capture.PersistenceFailed(); Interlocked.Increment(ref _incompleteReplays); Interlocked.Exchange(ref _replayFailure, 1); Pool.BeginDrain();
                    try { file.Writer?.Dispose(); file.Stream?.Dispose(); } catch (IOException) { }
                    _replays.TryRemove(pair.Key, out _);
                }
            }
        }
    }
    private void Drop() => Interlocked.Increment(ref _drops);
    private void Handle(Connection c, IMessage message)
    {
        if (message is LoginRequest login)
        {
            if (c.Room != null || login.ProtocolMajor != 1 || !_rooms.TryGetValue(login.GlobalRoomId, out var room) || !_outbox.CanAdmitRoom(room.Allocation.MatchId) || room.Disposed || room.Finished || !tickets.TryVerify(login.EntryTicket, room.Allocation.RoomId, room.Allocation.NodeId, room.Allocation.BootEpoch, out var player, out _) || Array.IndexOf(room.Roster, player) < 0 || !Pool.Inventory().Rooms.Any(r => r.RoomId == login.GlobalRoomId && r.State == "Ready")) { c.Revoked = true; Drop(); return; }
            c.Room = room; c.Player = Array.IndexOf(room.Roster, player);
            Send(c, MessageId.LoginResponse, new LoginResponse { SessionId = room.Sessions[c.Player], ConnectionEpoch = c.Wire.ConnectionEpoch, GlobalRoomId = room.Allocation.RoomId, BootEpoch = room.Allocation.BootEpoch, RoomTick = (ulong)Interlocked.Read(ref room.Clock) });
            return;
        }
        if (c.Room is not { } r || c.Revoked || r.Finished) { Drop(); return; }
        if (message is JoinRoomRequest join && join.SessionId == r.Sessions[c.Player]) Enqueue(r, new Command(c, default, 1));
        else if (message is ReconnectRequest reconnect && reconnect.SessionId == r.Sessions[c.Player]) Enqueue(r, new Command(c, default, 2));
        else if (message is InputCommand input) Input(c, input);
        else if (message is InputBatch batch && batch.Commands.Count is > 0 and <= 2) foreach (var input2 in batch.Commands) Input(c, input2);
        else Drop();
    }
    private void Input(Connection c, InputCommand input)
    {
        if (input.Sequence == 0 || input.WeaponId > 3 || (input.Buttons & ~31u) != 0 || input.MoveXMilli is < -1000 or > 1000 || input.MoveYMilli is < -1000 or > 1000 || input.LookYawMilli is < -360000 or > 360000 || input.LookPitchMilli is < -180000 or > 180000 || !c.Joined || c.Revoked) { Drop(); return; }
        try { Enqueue(c.Room!, new Command(c, new ArenaInput(input.Sequence, input.RoomTick, input.MoveXMilli, input.MoveYMilli, input.LookYawMilli, input.LookPitchMilli, (ArenaButtons)input.Buttons, (AiNative.Gameplay.ArenaWeaponId)input.WeaponId), 0)); }
        catch (ArgumentException) { Drop(); }
    }
    private void Enqueue(RuntimeRoom room, Command command) { if (!room.Commands.TryWrite(command)) Drop(); }
    private void Send(Connection c, MessageId id, IMessage message)
    {
        if (c.Revoked) return;
        if (!RealtimeProtocolCodec.TryEncode(id, message, _send, out var channel, out int bytes)) { Drop(); return; }
        if (c.Wire.Transport.SendAsync(channel, _send.AsMemory(0, bytes)).GetAwaiter().GetResult().Status != SendStatus.Accepted) Drop();
    }
    private void Publish(RuntimeRoom room)
    {
        Frame? selected = ClaimLatest(room.Frames);
        if (selected != null)
        {
            try
            {
                for (int p = 0; p < room.Roster.Length; p++)
                {
                    var c = Volatile.Read(ref room.Owners[p]); if (c == null || c.Revoked || !c.Joined) continue;
                    var snapshot = SnapshotOf(selected, room.Entities[p]);
                    int response = Interlocked.Exchange(ref c.Response, 0);
                    if (response == 1) Send(c, MessageId.JoinRoomResponse, new JoinRoomResponse { RoomId = Pool.GetLocalRoomId(room.Allocation.RoomId), EntityId = room.Entities[p], TickRate = 60 });
                    if (response == 2) Send(c, MessageId.ReconnectResponse, new ReconnectResponse { ConnectionEpoch = c.Wire.ConnectionEpoch, ResumeTick = selected.Tick, Snapshot = snapshot });
                    Send(c, MessageId.Snapshot, snapshot);
                }
                if (selected.Phase == AiNative.Gameplay.ArenaMatchPhase.Finished) Volatile.Write(ref room.FinalSent, 1);
            }
            finally { Volatile.Write(ref selected.State, 0); }
        }
        while (room.Events.TryRead(out var e))
        {
            var message = new ReliableEvent { RoomTick = e.Tick, Sequence = ++room.EventSequence, EventType = (uint)e.Kind, CombatEvent = new ArenaCombatEvent { EventType = (ArenaCombatEventType)(int)e.Kind, EventTick = e.Tick, SourceEntityId = e.SourceEntityId, TargetEntityId = e.TargetEntityId, WeaponId = (AiNative.Protocol.V1.ArenaWeaponId)(int)e.Weapon, Damage = (uint)Math.Max(0, e.Damage), PositionXMilli = e.PositionXMillimetres, PositionYMilli = e.PositionYMillimetres, PositionZMilli = e.PositionZMillimetres } };
            foreach (var c in room.Owners) if (c is { Joined: true }) Send(c, MessageId.ReliableEvent, message);
        }
    }
    private static Snapshot SnapshotOf(Frame f, uint entity)
    {
        var s = new Snapshot { ProtocolMajor = 1, RoomTick = f.Tick, StateHash = f.Hash, MatchPhase = (AiNative.Protocol.V1.ArenaMatchPhase)(int)f.Phase, RemainingTicks = (uint)f.Remaining, LeaderEntityId = f.Leader };
        for (int i = 0; i < 8; i++) if (f.Present[i])
        {
            var p = f.Players[i]; if ((uint)i + 1 == entity) s.LastProcessedInputSequence = p.LastProcessedInputSequence;
            s.Players.Add(new PlayerState { EntityId = (uint)i + 1, PositionXMilli = p.PositionXMillimetres, PositionYMilli = p.PositionYMillimetres, PositionZMilli = p.PositionZMillimetres, VelocityXMilliPerSecond = p.VelocityXMillimetresPerSecond, VelocityYMilliPerSecond = p.VelocityYMillimetresPerSecond, VelocityZMilliPerSecond = p.VelocityZMillimetresPerSecond, YawMillidegrees = p.YawMillidegrees, PitchMillidegrees = p.PitchMillidegrees, Health = (uint)Math.Max(0, p.Health), Armor = (uint)Math.Max(0, p.Armor), Alive = p.Alive, Kills = p.Kills, WeaponId = (uint)p.Weapon });
        }
        for (int i = 0; i < f.PickupCount; i++) { var p = f.Pickups[i]; s.Pickups.Add(new PickupState { PickupId = (uint)p.Id, PickupType = (AiNative.Protocol.V1.ArenaPickupType)(int)p.Type, PositionXMilli = p.PositionXMillimetres, PositionYMilli = p.PositionYMillimetres, PositionZMilli = p.PositionZMillimetres, Active = p.Active, RespawnTick = p.RespawnTick }); }
        return s;
    }
    public async ValueTask FlushResultsAsync(CancellationToken ct = default)
    {
        await _flush.WaitAsync(ct);
        try
        {
            foreach (var r in _rooms.Values)
            {
                if (r.TimedOut) { await Pool.ReleaseAsync(r.Allocation.RoomId, r.Allocation.AllocationId, r.Allocation.BootEpoch, ct); continue; }
                if (!r.Finished || Volatile.Read(ref r.FinalSent) == 0) continue;
                r.Result ??= r.BuildResult();
                if (await _outbox.StoreAsync(r.Result, ct)) await Pool.ReleaseAsync(r.Allocation.RoomId, r.Allocation.AllocationId, r.Allocation.BootEpoch, ct);
            }
        }
        finally { _flush.Release(); }
    }
    public ValueTask DisposeAsync() { foreach (var c in _connections) gateway.Release(c.Wire); _connections.Clear(); foreach (var replay in _replays.Values) replay.Capture.Abort(); DrainReplays(int.MaxValue); return ValueTask.CompletedTask; }
    private sealed class Connection(FantasyKcpConnection wire, long acceptedTimestamp) { public FantasyKcpConnection Wire = wire; public long AcceptedTimestamp = acceptedTimestamp; public RuntimeRoom? Room; public int Player; public volatile bool Revoked, Joined; public int Response; }
    private readonly record struct Command(Connection Connection, ArenaInput Input, byte Kind);
    internal sealed class SpscRing<T>(int capacity) where T : struct
    {
        private readonly T[] _items = new T[capacity + 1];
        private int _read, _write;
        public bool TryWrite(T item) { int write = _write; int next = (write + 1) % _items.Length; if (next == Volatile.Read(ref _read)) return false; _items[write] = item; Volatile.Write(ref _write, next); return true; }
        public bool TryRead(out T item) { int read = _read; if (read == Volatile.Read(ref _write)) { item = default; return false; } item = _items[read]; _items[read] = default; Volatile.Write(ref _read, (read + 1) % _items.Length); return true; }
    }
    // A published slot is immutable until the reader releases it. The worker never waits for a reader.
    internal static Frame? ClaimLatest(Frame[] frames)
    {
        Frame? selected = null;
        foreach (var f in frames) if (Interlocked.CompareExchange(ref f.State, 3, 2) == 2) { if (selected != null && selected.Tick > f.Tick) Volatile.Write(ref f.State, 0); else { if (selected != null) Volatile.Write(ref selected.State, 0); selected = f; } }
        return selected;
    }
    internal static Frame? ClaimWritable(Frame[] frames)
    {
        foreach (var f in frames) if (Interlocked.CompareExchange(ref f.State, 1, 0) == 0) return f;
        foreach (var f in frames) if (Interlocked.CompareExchange(ref f.State, 1, 2) == 2) return f;
        return null;
    }
    internal sealed class Frame
    {
        public int State; public readonly ArenaPlayerState[] Players = new ArenaPlayerState[8]; public readonly bool[] Present = new bool[8]; public readonly ArenaPickupState[] Pickups = new ArenaPickupState[8]; public int PickupCount, Remaining; public ulong Tick, Hash; public uint Leader; public AiNative.Gameplay.ArenaMatchPhase Phase;
    }
    private sealed class RuntimeRoom : IWorkerRoom
    {
        private readonly TopologyBattleEngine _engine; private readonly ArenaRoom _game; private readonly ArenaCombatEventRecord[] _events = new ArenaCombatEventRecord[256];
        public readonly RoomAllocation Allocation; public readonly string[] Roster; public readonly ulong[] Sessions; public readonly uint[] Entities; public readonly Connection?[] Owners;
        public readonly Frame[] Frames = { new(), new(), new() };
        public readonly SpscRing<Command> Commands = new(256);
        public readonly SpscRing<ArenaCombatEventRecord> Events = new(256);
        public volatile bool Disposed, Finished, TimedOut; public long Clock; public int FinalSent; public uint EventSequence; public MatchResult? Result;
        private readonly ArenaPlayerState[] _final = new ArenaPlayerState[8]; private uint _leader;
        private long _observedEventDrops;
        public readonly ArenaReplayCapture? Replay;
        public RuntimeRoom(TopologyBattleEngine engine, RoomAllocation allocation, int length)
        {
            _engine = engine; Allocation = allocation; Roster = allocation.PlayerIds.ToArray(); if (Roster.Length is < 1 or > 8 || Roster.Distinct().Count() != Roster.Length) throw new ArgumentException("invalid-roster"); _game = new ArenaRoom(length); Sessions = new ulong[Roster.Length]; Entities = new uint[Roster.Length]; Owners = new Connection?[Roster.Length]; for (int i = 0; i < Sessions.Length; i++) Sessions[i] = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)) | 1;
            if (engine._replayDirectory != null) Replay = new ArenaReplayCapture(engine._replayIdentity ?? throw new InvalidOperationException("Replay identity required"), allocation.RoomId, allocation.AllocationId, allocation.MatchId, allocation.NodeId, allocation.BootEpoch, length, engine._replayCapacity);

        }
        public void Tick()
        {
            if (Disposed || Finished || TimedOut) return;
            bool changed = false;
            for (int n = 0; n < 128 && Commands.TryRead(out var cmd); n++)
            {
                var c = cmd.Connection; int p = c.Player; if (c.Revoked) continue;
                if (cmd.Kind != 0)
                {
                    if (Entities[p] == 0)
                    {
                        if (!_game.TryJoin(out Entities[p])) continue;
                        Replay?.RecordJoin(_game.Tick, Entities[p]);
                    }
                    var old = Owners[p]; if (old != null && old != c) old.Revoked = true;
                    if (old != c) { _game.ClearPendingInputs(Entities[p]); Replay?.RecordClear(_game.Tick, Entities[p]); }
                    Volatile.Write(ref Owners[p], c); c.Joined = true; Volatile.Write(ref c.Response, cmd.Kind); changed = true;
                }
                else if (ReferenceEquals(Owners[p], c))
                {
                    if (_game.SubmitInput(Entities[p], cmd.Input)) Replay?.RecordInput(_game.Tick, Entities[p], cmd.Input);
                    else _engine.Drop();
                }
            }
            _game.TickOnce(); Interlocked.Exchange(ref Clock, (long)_game.Tick);
            Replay?.RecordTick(_game.Tick, _game.ComputeStateHash());
            long gameDrops = _game.DroppedEventCount; Interlocked.Add(ref _engine._eventDrops, gameDrops - _observedEventDrops); _observedEventDrops = gameDrops;
            int count = _game.DrainEvents(_events); for (int i = 0; i < count; i++) if (!Events.TryWrite(_events[i])) Interlocked.Increment(ref _engine._eventDrops);
            bool finished = _game.Phase == AiNative.Gameplay.ArenaMatchPhase.Finished;
            if (_game.Phase == AiNative.Gameplay.ArenaMatchPhase.Waiting && _game.Tick >= 2700) { TimedOut = true; Replay?.Complete(_game.Tick, _game.ComputeStateHash()); return; }
            if (changed || _game.Tick % 3 == 0 || finished) WriteFrame();
            if (finished) { for (int i = 0; i < Entities.Length; i++) _game.TryGetPlayer(Entities[i], out _final[i]); _leader = _game.LeaderEntityId; Replay?.Complete(_game.Tick, _game.ComputeStateHash()); Finished = true; }
        }
        private void WriteFrame()
        {
            Frame? frame = ClaimWritable(Frames);
            if (frame == null) return;
            frame.Tick = _game.Tick; frame.Hash = _game.ComputeStateHash(); frame.Remaining = _game.RemainingTicks; frame.Phase = _game.Phase; frame.Leader = _game.LeaderEntityId;
            for (int i = 0; i < 8; i++) frame.Present[i] = _game.TryGetPlayer((uint)i + 1, out frame.Players[i]); frame.PickupCount = _game.CopyPickups(frame.Pickups); Volatile.Write(ref frame.State, 2);
        }
        public MatchResult BuildResult() { var result = new MatchResult { MatchId = Allocation.MatchId, RoomId = Allocation.RoomId, NodeId = Allocation.NodeId, BootEpoch = Allocation.BootEpoch, Completion = "Finished" }; for (int i = 0; i < Roster.Length; i++) result.Players.Add(new PlayerResult { PlayerId = Roster[i], Won = Entities[i] != 0 && Entities[i] == _leader, Kills = (int)_final[i].Kills }); return result; }
        public void Dispose() { Disposed = true; Replay?.Abort(); ((ICollection<KeyValuePair<string, RuntimeRoom>>)_engine._rooms).Remove(new(Allocation.RoomId, this)); }
    }
}
