using System.Diagnostics;
using System.Threading.Channels;
using System.Collections.Concurrent;
using AiNative.Protocol.Backend.V1;

namespace AiNative.Server.Rooms;

public interface IWorkerRoom : IDisposable { void Tick(); }
public sealed record WorkerPerformance(int WorkerId, long TickCount, long OverBudgetTicks, long AllocatedBytes,
    long LastTickMicros, long FirstSampleTick, long[] RecentTickMicros, long[] SampleTickIds);

/// <summary>One dedicated owner thread per group. Admission and installation never move a live room.</summary>
public sealed class BattleWorkerPool : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly Worker[] _workers;
    private readonly Dictionary<string, Slot> _rooms = new(StringComparer.Ordinal);
    private readonly Func<RoomAllocation, IWorkerRoom> _factory;
    private readonly string _nodeId, _epoch;
    private bool _draining;
    private int _started;
    private string _coordinatorEpoch = "";
    private readonly HashSet<string> _retiredCoordinatorEpochs = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<Worker> _stoppedWorkers = new();
    private readonly ConcurrentQueue<string> _releasedMatches = new();
    private DurableResultOutbox? _resultOutbox;
    private bool _hasReserved;
    public BattleWorkerPool(string nodeId, string epoch, int workerCount, int roomsPerWorker, int mailboxCapacity,
        Func<RoomAllocation, IWorkerRoom> factory)
    {
        if (string.IsNullOrWhiteSpace(nodeId) || string.IsNullOrWhiteSpace(epoch)) throw new ArgumentException("node-identity-required");
        ArgumentOutOfRangeException.ThrowIfLessThan(workerCount, 1); ArgumentOutOfRangeException.ThrowIfGreaterThan(workerCount, 64);
        ArgumentOutOfRangeException.ThrowIfLessThan(roomsPerWorker, 1); ArgumentOutOfRangeException.ThrowIfGreaterThan(roomsPerWorker, 256);
        ArgumentOutOfRangeException.ThrowIfLessThan(mailboxCapacity, 1); ArgumentOutOfRangeException.ThrowIfGreaterThan(mailboxCapacity, 4096);
        _nodeId = nodeId; _epoch = epoch; _factory = factory;
        _workers = Enumerable.Range(0, workerCount).Select(i => new Worker(i, roomsPerWorker, mailboxCapacity, stopped => _stoppedWorkers.Enqueue(stopped))).ToArray();
    }
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("already-started");
        foreach (Worker worker in _workers) worker.Start();
    }
    public void BeginDrain() { lock (_sync) _draining = true; }
    public string BeginDrain(string coordinatorEpoch)
    {
        lock (_sync) { if (coordinatorEpoch.Length == 0 || coordinatorEpoch != _coordinatorEpoch) return "stale-coordinator"; _draining = true; return ""; }
    }
    public void ConfigureResultOutbox(DurableResultOutbox outbox)
    {
        ArgumentNullException.ThrowIfNull(outbox);
        lock (_sync) { if (_resultOutbox is not null || _hasReserved) throw new InvalidOperationException("outbox-must-attach-once-before-reserve"); _resultOutbox = outbox; }
    }
    public bool IsCoordinatorCurrent(string coordinatorEpoch) { lock (_sync) return coordinatorEpoch.Length != 0 && coordinatorEpoch == _coordinatorEpoch; }
    public int GetWorkerId(string roomId) { lock (_sync) return _rooms[roomId].Worker.Id; }
    public uint GetLocalRoomId(string roomId) { lock (_sync) { Slot slot = _rooms[roomId]; return (uint)(slot.Worker.Id * slot.Worker.Capacity + slot.Index + 1); } }
    public BattleNodeReport Fence(string coordinatorEpoch, string address, bool outboxHealthy)
    {
        if (string.IsNullOrWhiteSpace(coordinatorEpoch)) throw new ArgumentException("coordinator-epoch-required");
        lock (_sync)
        {
            if (_retiredCoordinatorEpochs.Contains(coordinatorEpoch)) throw new InvalidOperationException("stale-coordinator");
            if (_coordinatorEpoch.Length != 0 && _coordinatorEpoch != coordinatorEpoch && _retiredCoordinatorEpochs.Count >= 4096) throw new InvalidOperationException("fence-history-capacity-exceeded");
            if (_coordinatorEpoch.Length != 0 && _coordinatorEpoch != coordinatorEpoch) _retiredCoordinatorEpochs.Add(_coordinatorEpoch);
            _coordinatorEpoch = coordinatorEpoch;
            foreach (Slot slot in _rooms.Values)
            {
                if (slot.Allocation.CoordinatorEpoch == coordinatorEpoch) continue;
                // Install closures captured under an older owner cannot resurrect a missing room.
                slot.Installation?.TrySetResult("stale-coordinator");
                slot.Installation = null;
                slot.Allocation.CoordinatorEpoch = coordinatorEpoch;
            }
            return Inventory(address, outboxHealthy);
        }
    }
    public bool TryPost(int workerId, Action action) => (uint)workerId < _workers.Length && _workers[workerId].TryPost(action);
    // Diagnostic export allocates on its caller, never on a Worker. Sample IDs expose gaps/overwrites.
    public WorkerPerformance[] Performance() => _workers.Select(x => x.Performance()).ToArray();
    public string Reserve(RoomAllocation allocation)
    {
        lock (_sync)
        {
            ReclaimStoppedReservations();
            if (allocation.BootEpoch != _epoch || allocation.NodeId != _nodeId) return "stale-epoch";
            if (allocation.CoordinatorEpoch != _coordinatorEpoch) return "stale-coordinator";
            if (_rooms.TryGetValue(allocation.RoomId, out Slot? previous))
                return previous.Allocation.AllocationId == allocation.AllocationId && previous.Allocation.MatchId == allocation.MatchId &&
                    previous.Allocation.PlayerIds.SequenceEqual(allocation.PlayerIds) ? "" : "allocation-conflict";
            if (_draining) return "draining";
            if (_rooms.Values.Any(x => x.Allocation.MatchId == allocation.MatchId)) return "allocation-conflict";
            Worker? worker = _workers.Where(x => !x.Faulted && x.Reservations < x.Capacity && x.MailboxAvailable > 0)
                .OrderBy(x => x.Reservations).ThenBy(x => x.LastTickMicros).ThenBy(x => x.Id).FirstOrDefault();
            if (worker is null) return "capacity-unavailable";
            if (_resultOutbox is not null && !_resultOutbox.TryReserve(allocation)) return "outbox-capacity-unavailable";
            int slot = Enumerable.Range(0, worker.Capacity).First(i => !_rooms.Values.Any(x => x.Worker == worker && x.Index == i));
            worker.Reservations++;
            RoomAllocation reserved = allocation.Clone(); reserved.State = "Reserved";
            _rooms.Add(allocation.RoomId, new(reserved, worker, slot));
            _hasReserved = true;
            return "";
        }
    }
    public async ValueTask<string> CreateAsync(string roomId, string allocationId, string epoch, CancellationToken cancellationToken = default, string coordinatorEpoch = "")
    {
        Task<string> completion;
        Slot? toInitialize = null;
        TaskCompletionSource<string>? createdInstallation = null;
        lock (_sync)
        {
            if (epoch != _epoch) return "stale-epoch";
            if (coordinatorEpoch != _coordinatorEpoch) return "stale-coordinator";
            if (!_rooms.TryGetValue(roomId, out Slot? slot) || slot.Allocation.AllocationId != allocationId) return "reservation-not-found";
            if (cancellationToken.IsCancellationRequested)
            {
                if (slot.Allocation.State != "Ready" && slot.Installation is null) { _rooms.Remove(roomId); slot.Worker.Reservations--; _resultOutbox?.ReleaseReservation(slot.Allocation.MatchId); }
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (slot.ReleaseCompletion is not null) return "released";
            if (slot.Allocation.State == "Ready") return "";
            if (slot.Worker.Faulted) return "worker-unavailable";
            if (slot.Installation is not null) completion = slot.Installation.Task;
            else
            {
                TaskCompletionSource<string> installed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                slot.Installation = installed;
                createdInstallation = installed;
                toInitialize = slot;
                completion = installed.Task;
            }
        }
        if (toInitialize is not null)
        {
            Slot slot = toInitialize;
            string installationEpoch = coordinatorEpoch;
            TaskCompletionSource<string> installation = createdInstallation!;
            try
            {
                // Initialize off the owner thread and outside the pool lock.
                IWorkerRoom room = _factory(slot.Allocation.Clone());
                lock (_sync)
                {
                    if (slot.ReleaseCompletion is not null || !_rooms.TryGetValue(roomId, out Slot? current) || !ReferenceEquals(slot, current) || installationEpoch != _coordinatorEpoch)
                    { room.Dispose(); installation.TrySetResult("released"); }
                    else if (!slot.Worker.TryPost(() =>
                    {
                        lock (_sync)
                        {
                            if (slot.ReleaseCompletion is not null || installationEpoch != _coordinatorEpoch || !_rooms.TryGetValue(roomId, out Slot? current) || !ReferenceEquals(current, slot) || !ReferenceEquals(slot.Installation, installation))
                            { room.Dispose(); installation.TrySetResult("stale-coordinator"); return; }
                            slot.Worker.Install(slot.Index, room);
                            slot.Allocation.State = "Ready";
                            installation.TrySetResult("");
                        }
                    }))
                    { room.Dispose(); installation.TrySetResult("mailbox-full"); slot.Installation = null; }
                }
            }
            catch (Exception error) { installation.TrySetException(error); CancelUninstalled(slot, installation); }
        }
        try { return await completion.WaitAsync(cancellationToken); }
        catch (OperationCanceledException)
        {
            if (toInitialize is not null) CancelUninstalled(toInitialize, createdInstallation!);
            throw;
        }
    }
    public async ValueTask<string> ReleaseAsync(string roomId, string allocationId, string epoch, CancellationToken cancellationToken = default, string? coordinatorEpoch = null)
    {
        Task<string> completion;
        lock (_sync)
        {
            if (epoch != _epoch) return "stale-epoch";
            if (coordinatorEpoch is not null && (coordinatorEpoch.Length == 0 || coordinatorEpoch != _coordinatorEpoch)) return "stale-coordinator";
            if (!_rooms.TryGetValue(roomId, out Slot? slot)) return "";
            if (slot.Allocation.AllocationId != allocationId) return "allocation-conflict";
            if (slot.Worker.Terminated)
            {
                _rooms.Remove(roomId); slot.Worker.Reservations--; _resultOutbox?.ReleaseReservation(slot.Allocation.MatchId); return "";
            }
            if (slot.ReleaseCompletion is not null) completion = slot.ReleaseCompletion.Task;
            else
            {
            TaskCompletionSource<string> removed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!slot.Worker.TryPost(() =>
            {
                lock (_sync)
                {
                    if (coordinatorEpoch is not null && coordinatorEpoch != _coordinatorEpoch) { slot.ReleaseCompletion = null; removed.TrySetResult("stale-coordinator"); return; }
                    if (_rooms.TryGetValue(roomId, out Slot? current) && ReferenceEquals(slot, current))
                    { slot.Worker.Remove(slot.Index); _rooms.Remove(roomId); slot.Worker.Reservations--; _releasedMatches.Enqueue(slot.Allocation.MatchId); }
                }
                removed.TrySetResult("");
            })) return "mailbox-full";
            slot.ReleaseCompletion = removed;
            completion = removed.Task;
            }
        }
        string result = await completion.WaitAsync(cancellationToken);
        lock (_sync) ReclaimStoppedReservations();
        return result;
    }
    private void CancelUninstalled(Slot slot, TaskCompletionSource<string> installation)
    {
        lock (_sync)
        {
            if (slot.Allocation.State == "Ready" || !ReferenceEquals(slot.Installation, installation) || !_rooms.TryGetValue(slot.Allocation.RoomId, out Slot? current) || !ReferenceEquals(current, slot)) return;
            _rooms.Remove(slot.Allocation.RoomId); slot.Worker.Reservations--; slot.Installation = null;
            installation.TrySetResult("released"); _resultOutbox?.ReleaseReservation(slot.Allocation.MatchId);
        }
    }
    // Called by control/inspection callers, never by the fixed Tick owner. The worker only enqueues notices.
    private void ReclaimStoppedReservations()
    {
        while (_releasedMatches.TryDequeue(out string? match)) _resultOutbox?.ReleaseReservation(match);
        while (_stoppedWorkers.TryDequeue(out Worker? worker))
            foreach (Slot slot in _rooms.Values.Where(s => s.Worker == worker)) _resultOutbox?.ReleaseReservation(slot.Allocation.MatchId);
    }
    public BattleNodeReport Inventory(string address = "", bool outboxHealthy = true)
    {
        lock (_sync)
        {
            ReclaimStoppedReservations();
            BattleNodeReport report = new() { NodeId = _nodeId, BootEpoch = _epoch, Address = address, Draining = _draining, OutboxHealthy = outboxHealthy };
            foreach (Worker worker in _workers)
                report.Workers.Add(new WorkerCapacity { WorkerId = worker.Id, AvailableRooms = worker.Faulted ? 0 : worker.Capacity - worker.Reservations,
                    MailboxAvailable = worker.MailboxAvailable, LastTickMicros = worker.LastTickMicros });
            report.Rooms.Add(_rooms.Values.Select(x =>
            { RoomAllocation copy = x.Allocation.Clone(); if (x.Worker.Faulted) copy.State = "Lost"; return copy; }));
            return report;
        }
    }
    public async ValueTask DisposeAsync()
    {
        BeginDrain();
        foreach (Worker worker in _workers) worker.Stop();
        foreach (Worker worker in _workers) await Task.Run(worker.Join);
        lock (_sync) { ReclaimStoppedReservations(); foreach (Slot slot in _rooms.Values) _resultOutbox?.ReleaseReservation(slot.Allocation.MatchId); }
    }
    private sealed class Slot(RoomAllocation allocation, Worker worker, int index)
    {
        public RoomAllocation Allocation { get; } = allocation;
        public Worker Worker { get; } = worker;
        public int Index { get; } = index;
        public TaskCompletionSource<string>? Installation;
        public TaskCompletionSource<string>? ReleaseCompletion;
    }
    private sealed class Worker
    {
        private readonly Channel<Action> _mailbox;
        private readonly IWorkerRoom?[] _slots;
        private readonly int _mailboxCapacity;
        private readonly Thread _thread;
        private int _stopping, _faulted, _terminated;
        private long _lastTickMicros;
        private readonly long[] _tickSamples = new long[4096], _sampleIds = new long[4096];
        private long _ticks, _overBudget, _allocated;
        private readonly Action<Worker> _stopped;
        public Worker(int id, int capacity, int mailboxCapacity, Action<Worker> stopped)
        {
            _stopped = stopped;
            Id = id; Capacity = capacity; _mailboxCapacity = mailboxCapacity;
            _slots = new IWorkerRoom[capacity];
            _mailbox = Channel.CreateBounded<Action>(new BoundedChannelOptions(mailboxCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
            _thread = new Thread(Run) { IsBackground = true, Name = $"BattleWorker-{id}" };
        }
        public int Id { get; }
        public int Capacity { get; }
        public int Reservations { get; set; }
        public bool Faulted => Volatile.Read(ref _faulted) != 0;
        public bool Terminated => Volatile.Read(ref _terminated) != 0;
        public int MailboxAvailable => _mailboxCapacity - _mailbox.Reader.Count;
        public long LastTickMicros => Interlocked.Read(ref _lastTickMicros);
        public WorkerPerformance Performance()
        {
            long end = Interlocked.Read(ref _ticks), first = Math.Max(1, end - 511);
            List<long> samples = new();
            List<long> ids = new();
            for (long id = first; id <= end; id++)
            {
                int slot = (int)(id % _tickSamples.Length);
                long before = Volatile.Read(ref _sampleIds[slot]), value = Interlocked.Read(ref _tickSamples[slot]);
                if (before == id && Volatile.Read(ref _sampleIds[slot]) == id) { samples.Add(value); ids.Add(id); }
            }
            return new(Id, end, Interlocked.Read(ref _overBudget), Interlocked.Read(ref _allocated), LastTickMicros, first, samples.ToArray(), ids.ToArray());
        }
        public void Start() => _thread.Start();
        public bool TryPost(Action action) => !Faulted && Volatile.Read(ref _stopping) == 0 && _mailbox.Writer.TryWrite(action);
        public void Install(int index, IWorkerRoom room) => _slots[index] = room;
        public void Remove(int index) { _slots[index]?.Dispose(); _slots[index] = null; }
        public void Stop() => Volatile.Write(ref _stopping, 1);
        public void Join()
        {
            if ((_thread.ThreadState & System.Threading.ThreadState.Unstarted) != 0) return;
            if (!_thread.Join(TimeSpan.FromSeconds(5))) throw new TimeoutException("battle-worker-stop-timeout");
        }
        private void Run()
        {
            long period = Stopwatch.Frequency / 60;
            long deadline = Stopwatch.GetTimestamp();
            try
            {
                while (Volatile.Read(ref _stopping) == 0)
                {
                    long started = Stopwatch.GetTimestamp();
                    long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                    for (int n = 0; n < _mailboxCapacity && _mailbox.Reader.TryRead(out Action? command); n++) command();
                    for (int i = 0; i < _slots.Length; i++) _slots[i]?.Tick();
                    long elapsed = Stopwatch.GetTimestamp() - started;
                    long micros = elapsed * 1_000_000 / Stopwatch.Frequency;
                    Interlocked.Exchange(ref _lastTickMicros, micros);
                    Interlocked.Add(ref _allocated, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
                    if (elapsed > period) Interlocked.Increment(ref _overBudget);
                    long tickId = Interlocked.Read(ref _ticks) + 1;
                    int sampleSlot = (int)(tickId % _tickSamples.Length);
                    Volatile.Write(ref _sampleIds[sampleSlot], 0);
                    Interlocked.Exchange(ref _tickSamples[sampleSlot], micros);
                    Volatile.Write(ref _sampleIds[sampleSlot], tickId);
                    Interlocked.Exchange(ref _ticks, tickId);
                    deadline += period;
                    long now = Stopwatch.GetTimestamp();
                    if (now > deadline + period) deadline = now; // No unbounded catch-up or variable simulation delta.
                    int wait = (int)Math.Max(0, (deadline - now) * 1000 / Stopwatch.Frequency);
                    if (wait > 0) Thread.Sleep(wait);
                }
            }
            catch { Volatile.Write(ref _faulted, 1); }
            finally
            {
                for (int i = 0; i < _slots.Length; i++) { try { Remove(i); } catch { Volatile.Write(ref _faulted, 1); } }
                Volatile.Write(ref _terminated, 1); _stopped(this);
            }
        }
    }
}
