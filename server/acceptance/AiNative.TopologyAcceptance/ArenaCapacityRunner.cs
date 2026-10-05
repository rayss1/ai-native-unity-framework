using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiNative.Protocol.Backend.V1;
using AiNative.Protocol.V1;
using AiNative.Realtime;
using AiNative.Server.Backend;
using AiNative.Server.Battle;
using AiNative.Server.Control;
using AiNative.Server.Fantasy;
using AiNative.Server.Hosting;
using AiNative.Server.Protocol;
using Google.Protobuf;

internal static class QualificationRosterAdmission
{
    public static async Task EnqueueAsync(SemaphoreSlim gate, int count, Func<int, Task> submit, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            // Once admitted, every mutation must settle before another roster can enter.
            // The async wrapper also captures synchronous delegate failures without abandoning siblings.
            await Task.WhenAll(Enumerable.Range(0, count).Select(Submit));
        }
        finally { gate.Release(); }
        async Task Submit(int index) => await submit(index);
    }
}

internal static class QualificationFailureDiagnostics
{
    public static string? Find(Exception? error, string prefix)
    {
        if (error is null) return null;
        if (error.Message.StartsWith(prefix, StringComparison.Ordinal)) return error.Message;
        if (error is AggregateException aggregate)
        {
            foreach (var child in aggregate.InnerExceptions)
                if (Find(child, prefix) is { } found) return found;
            return null;
        }
        return Find(error.InnerException, prefix);
    }
}

// One room-generation group stays owned through every client's cleanup and the actual asynchronous Scene close.
internal sealed class QualificationRoomResources : IAsyncDisposable
{
    readonly IAsyncDisposable group;
    readonly Func<Task> closeClients;
    readonly object ownership = new();
    Task? disposal;
    public QualificationRoomResources(IAsyncDisposable group, Func<Task> closeClients)
    { this.group = group; this.closeClients = closeClients; }
    public ValueTask DisposeAsync()
    { lock (ownership) return new(disposal ??= CloseAsync()); }
    async Task CloseAsync()
    { try { await closeClients(); } finally { await group.DisposeAsync(); } }
}

// Tool-owned next-generation roster. Every acquired member has one owner until the checked handoff succeeds.
internal sealed class QualificationPreparedRoster<T> : IAsyncDisposable where T : class, IAsyncDisposable
{
    readonly T?[] members;
    readonly CancellationTokenSource stop, keepAliveStop = new();
    readonly SemaphoreSlim handoff = new(1, 1);
    readonly object disposalGate = new();
    readonly Task prepared, heartbeat;
    readonly TaskCompletionSource<bool> failed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Task? disposal;
    int takeStarted;
    bool transferred;
    public Task Failure => failed.Task;
    public QualificationPreparedRoster(int count, Func<int, CancellationToken, Task<T>> prepare,
        Func<T, CancellationToken, Task> keepAlive, TimeSpan interval, CancellationToken cancellation)
    {
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
        members = new T?[count]; stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        prepared = Prepare(); heartbeat = KeepAlive();
        async Task Prepare()
        {
            try { await Task.WhenAll(Enumerable.Range(0, count).Select(One)); }
            catch (Exception failure) { failed.TrySetException(failure); throw; }
            async Task One(int index)
            {
                try { members[index] = await prepare(index, stop.Token); }
                catch { stop.Cancel(); throw; }
            }
        }
        async Task KeepAlive()
        {
            try
            {
                await prepared;
                while (true)
                {
                    await Task.Delay(interval, keepAliveStop.Token);
                    keepAliveStop.Token.ThrowIfCancellationRequested();
                    // Stop prevents another batch, but ownership waits for every bounded in-flight call.
                    await Task.WhenAll(members.Select(member => keepAlive(member!, CancellationToken.None)));
                }
            }
            catch (OperationCanceledException) when (keepAliveStop.IsCancellationRequested) { }
            catch (Exception failure) { failed.TrySetException(failure); throw; }
        }
    }
    public async Task<T[]> TakeAsync(Func<CancellationToken, Task> settlementAndRelease)
    {
        if (Interlocked.Exchange(ref takeStarted, 1) != 0) throw new InvalidOperationException("qualification-roster-already-taken");
        if (failed.Task.IsFaulted) await failed.Task;
        await handoff.WaitAsync(stop.Token);
        try
        {
            await settlementAndRelease(stop.Token);
            await prepared;
            keepAliveStop.Cancel(); await heartbeat;
            stop.Token.ThrowIfCancellationRequested();
            transferred = true;
            return members.Select(member => member!).ToArray();
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { throw new OperationCanceledException(stop.Token); }
        finally { handoff.Release(); }
    }
    public ValueTask DisposeAsync()
    { lock (disposalGate) return new(disposal ??= DisposeCore()); }
    async Task DisposeCore()
    {
        stop.Cancel(); keepAliveStop.Cancel();
        try { await prepared; } catch (Exception) { /* All siblings have settled; clean every acquired member. */ }
        try { await heartbeat; } catch (Exception) { /* Preparation/heartbeat failure is surfaced through Take/Failure. */ }
        await handoff.WaitAsync();
        try
        {
            if (!transferred) await Task.WhenAll(members.Where(member => member is not null).Select(member => member!.DisposeAsync().AsTask()));
        }
        finally
        {
            handoff.Release(); stop.Dispose(); keepAliveStop.Dispose();
            // Observe failure even when cancellation occurred before the slot started watching it, or cleanup itself failed.
            if (failed.Task.IsFaulted) _ = failed.Task.Exception;
        }
    }
}

internal sealed class ArenaCapacityRunner(FantasyServiceRuntime runtime, FailureScenarios.Fixture fixture,
    List<object> evidence, CancellationToken cancellation)
{
    readonly ArenaCapacityOptions options = ArenaCapacityOptions.Load(Environment.GetEnvironmentVariable);
    readonly ConcurrentBag<LoadRoom> completed = [];
    readonly ConcurrentBag<LoadRoom> owned = [];
    readonly ConcurrentDictionary<int, LoadRoom> live = [];
    readonly ConcurrentQueue<double> latencies = [];
    readonly List<(string Node, DateTimeOffset Utc, JsonElement Data)> diagnostics = [];
    readonly Dictionary<string, QualificationWorkerWindow> windows = [];
    readonly Dictionary<string, long> lifecycle = [];
    readonly string runDirectory = HostSettings.Required("AINATIVE_ACCEPTANCE_RUN_DIRECTORY");
    readonly bool driverTimingEnabled = Environment.GetEnvironmentVariable("AINATIVE_QUALIFICATION_DRIVER_TIMING") == "1";
    readonly string driverWaitMode = QualificationDriverExecution.SelectWaitMode(
        Environment.GetEnvironmentVariable("AINATIVE_QUALIFICATION_DRIVER_WAIT"), OperatingSystem.IsWindows());
    readonly ThreadPriority driverThreadPriority = QualificationDriverExecution.SelectThreadPriority(
        Environment.GetEnvironmentVariable("AINATIVE_QUALIFICATION_DRIVER_PRIORITY"), OperatingSystem.IsWindows(),
        QualificationDriverExecution.SelectWaitMode(Environment.GetEnvironmentVariable("AINATIVE_QUALIFICATION_DRIVER_WAIT"), OperatingSystem.IsWindows()));
    long measurementStart = long.MaxValue, measurementEnd = long.MaxValue;
    long occupancyTimestamp, occupiedTicks, slotTicks, maximumGapTicks;
    readonly object occupancyGate = new();
    readonly SemaphoreSlim creationGate = new(1, 1);
    readonly object phaseGate = new();
    readonly List<RoomPhase> phases = [];
    readonly Dictionary<(int Slot, int Generation), int> phaseCounts = [];
    long omittedPhases;
    bool collecting;
    int activeSlots;

    public async Task RunAsync()
    {
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        using CancellationTokenSource diagnosticsStop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        using QualificationWaitWitness? waitWitness = driverTimingEnabled ? new() : null;
        List<Task> slotTasks = [];
        Task? diagnosticTask = null;
        bool terminalPassed = false;
        try
        {
            for (int slot = 0; slot < options.RoomCount; slot++)
            {
                LoadRoom room = await CreateRoom(slot, 0, stop.Token);
                RegisterRoom(slot, room);
                slotTasks.Add(SlotLoop(slot, room, stop.Token));
            }
            Task failedSlot = Task.WhenAny(slotTasks).Unwrap();
            Task warmup = Task.Delay(TimeSpan.FromSeconds(options.WarmupSeconds), stop.Token);
            if (await Task.WhenAny(warmup, failedSlot) == failedSlot) await failedSlot;
            await warmup;
            measurementStart = Stopwatch.GetTimestamp();
            measurementEnd = measurementStart + (long)(options.DurationSeconds * (double)Stopwatch.Frequency);
            waitWitness?.Start(measurementStart, measurementEnd);
            lock (occupancyGate) { occupancyTimestamp = measurementStart; collecting = true; }
            await CollectDiagnostics();
            diagnosticTask = Task.Run(async () =>
            {
                try
                {
                    while (Stopwatch.GetTimestamp() < measurementEnd)
                    {
                        await Task.Delay(2000, diagnosticsStop.Token);
                        if (Stopwatch.GetTimestamp() < measurementEnd) await CollectDiagnostics();
                    }
                }
                catch (OperationCanceledException) when (diagnosticsStop.IsCancellationRequested) { }
            }, stop.Token);
            Task measure = Task.Delay(TimeSpan.FromSeconds(options.DurationSeconds), stop.Token);
            if (await Task.WhenAny(measure, failedSlot, diagnosticTask) == failedSlot) await failedSlot;
            if (diagnosticTask.IsFaulted) await diagnosticTask;
            await measure;
            waitWitness?.Stop();
            AccountOccupancy();
            lock (occupancyGate) collecting = false;
            diagnosticsStop.Cancel();
            await diagnosticTask;
            await CollectDiagnostics();
            // Every current room remains driven through its ordinary score/time finish and settlement.
            await Task.WhenAll(slotTasks);
            JsonElement terminalDelivery = await CollectTerminalDelivery();
            Check(QualificationTerminalDeliveryGate.Passes(terminalDelivery), "qualification-terminal-delivery-failed");
            List<object> replay = [];
            foreach (LoadRoom room in completed.OrderBy(room => room.Allocation.RoomId))
            {
                string identityFile = Path.Combine(runDirectory, room.Allocation.NodeId + ".replay-identity.json");
                using var json = JsonDocument.Parse(await File.ReadAllTextAsync(identityFile, cancellation));
                var identity = json.RootElement;
                var verified = ArenaReplayVerifier.Verify(room.ReplayPath(runDirectory), new(
                    identity.GetProperty("Source").GetString()!, identity.GetProperty("Fantasy").GetString()!,
                    identity.GetProperty("Protocol").GetString()!, identity.GetProperty("Configuration").GetString()!));
                Check(verified.RoomId == room.Allocation.RoomId && verified.MatchId == room.Allocation.MatchId &&
                    verified.FinalTick == room.Final!.RoomTick && verified.FinalHash == room.Final.StateHash, "qualification-replay-final-state");
                replay.Add(new { capture = room.ReplayPath(runDirectory), verified });
            }
            var workerReports = windows.Select(pair => new { worker = pair.Key, data = pair.Value.Report() }).ToArray();
            var resources = diagnostics.GroupBy(point => point.Node).Select(group =>
            {
                var first = group.First(); var last = group.Last();
                var a = first.Data.GetProperty("process"); var b = last.Data.GetProperty("process");
                double seconds = (last.Utc - first.Utc).TotalSeconds;
                double cpu = (b.GetProperty("totalProcessorSeconds").GetDouble() - a.GetProperty("totalProcessorSeconds").GetDouble()) /
                    seconds / b.GetProperty("logicalProcessors").GetInt32() * 100;
                long peak = group.Max(point => point.Data.GetProperty("process").GetProperty("peakWorkingSetBytes").GetInt64());
                long available = group.Min(point => point.Data.GetProperty("process").GetProperty("availableMemoryBytes").GetInt64());
                long commandMaximum = group.Max(point => point.Data.GetProperty("queues").GetProperty("commandDepth").GetInt64());
                long eventMaximum = group.Max(point => point.Data.GetProperty("queues").GetProperty("eventDepth").GetInt64());
                long eventDrops = last.Data.GetProperty("queues").GetProperty("droppedEvents").GetInt64() - first.Data.GetProperty("queues").GetProperty("droppedEvents").GetInt64();
                long packetDrops = last.Data.GetProperty("queues").GetProperty("droppedPackets").GetInt64() - first.Data.GetProperty("queues").GetProperty("droppedPackets").GetInt64();
                long terminalDeliveryFailures = group.Max(point => point.Data.GetProperty("queues").GetProperty("terminalDeliveryFailures").GetInt64());
                long terminalDeliveryTimeouts = group.Max(point => point.Data.GetProperty("queues").GetProperty("terminalDeliveryTimeouts").GetInt64());
                bool healthy = group.All(point => point.Data.GetProperty("replay").GetProperty("healthy").GetBoolean() &&
                    point.Data.GetProperty("replay").GetProperty("incompleteCaptures").GetInt64() == 0);
                return new { node = group.Key, cpuPercent = cpu, peakWorkingSetBytes = peak, availableMemoryBytes = available,
                    cpuDenominator = "Environment.ProcessorCount logical processors; no explicit process CPU quota", availableMemorySource = "GC.GetGCMemoryInfo().TotalAvailableMemoryBytes",
                    hostAggregateHeadroomQualified = false,
                    commandMaximum, eventMaximum, eventDrops, packetDrops, terminalDeliveryFailures, terminalDeliveryTimeouts,
                    gcStart = first.Data.GetProperty("gc"), gcEnd = last.Data.GetProperty("gc"),
                    processAllocatedBytesDuringWindow = last.Data.GetProperty("gc").GetProperty("allocatedBytes").GetInt64() - first.Data.GetProperty("gc").GetProperty("allocatedBytes").GetInt64(),
                    memoryStartBytes = a.GetProperty("workingSetBytes").GetInt64(), memoryEndBytes = b.GetProperty("workingSetBytes").GetInt64(),
                    memorySlopeBytesPerSecond = (b.GetProperty("workingSetBytes").GetInt64() - a.GetProperty("workingSetBytes").GetInt64()) / seconds,
                    passed = QualificationResourceGate.Passes(cpu, peak, available) && eventDrops == 0 && terminalDeliveryFailures == 0 && healthy &&
                        commandMaximum <= options.WorkerCount * options.RoomsPerWorker * 256 && eventMaximum <= options.WorkerCount * options.RoomsPerWorker * 256 };
            }).ToArray();
            var clients = completed.SelectMany(room => room.Clients.Select(client => client.Report(measurementStart, measurementEnd, options.InputHz,
                room.Combat.MeasuredFire(client.Entity), room.Allocation.RoomId, room.Slot, room.Generation))).ToArray();
            var combatReports = completed.Select(room => room.Combat.Report()).ToArray();
            long Combat(string field) => combatReports.Sum(report => report.GetProperty(field).GetInt64());
            double occupancy = slotTicks > 0 ? occupiedTicks / (double)slotTicks : 0;
            double[] samples = latencies.Order().ToArray();
            double Percent(double p) => samples.Length == 0 ? 0 : samples[Math.Clamp((int)Math.Ceiling(samples.Length * p) - 1, 0, samples.Length - 1)];
            bool passed = workerReports.Length == 2 * options.WorkerCount && workerReports.All(worker => worker.data.GetProperty("passed").GetBoolean()) &&
                resources.Length == 2 && resources.All(resource => resource.passed) && clients.All(client => client.GetProperty("passed").GetBoolean()) &&
                occupancy >= .95 && replay.Count == completed.Count && completed.Count >= options.RoomCount && samples.Length > 100 &&
                Combat("measuredFire") > 0 && Combat("measuredHit") > 0 && Combat("measuredKills") > 0 && Combat("measuredRespawns") > 0 && Combat("measuredSwitches") > 0 && Combat("measuredSequenceGaps") == 0 &&
                completed.Sum(room => room.Clients.Sum(client => client.AmmoExhaustedInputs)) > 0 &&
                workerReports.All(worker => worker.data.GetProperty("observedTicks").GetInt64() >= options.DurationSeconds * 59.5);
            using var currentProcess = Process.GetCurrentProcess();
            var report = new { scenario = "actual-arena-capacity-qualification", passed, options,
                clientHarness = new { battleMessageOwnership = "one independent MultiThread Scene per room generation",
                    inputDriverWait = driverWaitMode,
                    configuredInputDriverThreadPriority = driverThreadPriority.ToString(),
                    inputDriverPriorityApplication = driverWaitMode == "windows-high-resolution"
                        ? "set before each owned driver starts; dynamic OS priority not sampled"
                        : "not applied; task-delay pool priority not measured",
                    processPriorityClassAtReport = currentProcess.PriorityClass.ToString(),
                    weaponGenerator = "staggered-ammo-cycle-v2: four-second maximum dwell; send one observed-empty Fire before early rotation",
                    clientsPerGroup = options.PlayersPerRoom, maximumConcurrentRoomGroups = options.RoomCount,
                    gateMessageOwnership = "single acceptance root Scene", resourceAccounting = "whole-host audit includes all client threads and sockets" },
                measurementStartUtc = diagnostics.First().Utc, measurementEndUtc = diagnostics.Last().Utc,
                workers = workerReports, nodes = resources, players = clients, completedRooms = completed.Count, terminalDelivery,
                occupancy = new { ratio = occupancy, minimumRatio = .95, expectedSlots = options.RoomCount,
                    activeRoomSeconds = occupiedTicks / (double)Stopwatch.Frequency, expectedRoomSeconds = slotTicks / (double)Stopwatch.Frequency,
                    maximumReplacementSeconds = maximumGapTicks / (double)Stopwatch.Frequency },
                combat = new { fireEvents = Combat("fire"), damageEvents = Combat("hit"), kills = Combat("kills"), respawns = Combat("respawns"),
                    measuredFire = Combat("measuredFire"), measuredHit = Combat("measuredHit"), measuredKills = Combat("measuredKills"), measuredRespawns = Combat("measuredRespawns"),
                    measuredSwitches = Combat("measuredSwitches"), measuredSequenceGaps = Combat("measuredSequenceGaps"),
                    estimatedAmmoExhaustedInputAttempts = completed.Sum(room => room.Clients.Sum(client => client.AmmoExhaustedInputs)),
                    noFireInputAttempts = completed.Sum(room => room.Clients.Sum(client => client.NoFireInputs)),
                    endedByScore = completed.Count(room => room.Final!.RoomTick - room.MatchStartTick < (ulong)options.MatchTicks),
                    endedByTime = completed.Count(room => room.Final!.RoomTick - room.MatchStartTick >= (ulong)options.MatchTicks) },
                acknowledgement = new { sampleCount = samples.Length, p50Milliseconds = Percent(.5), p95Milliseconds = Percent(.95),
                    p99Milliseconds = Percent(.99), maximumMilliseconds = samples.LastOrDefault(), percentileMethod = "nearest-rank; actual input-send to recipient-specific consumed ACK" },
                replay, identities = new[] { "battle-1", "battle-2" }.Select(node => ReadIdentity(Path.Combine(runDirectory, node + ".replay-identity.json"))).ToArray(),
                binaries = Directory.EnumerateFiles(Path.Combine(runDirectory, "bin", "battle-1"), "AiNative.*.dll").Order().Select(path => new
                    { name = Path.GetFileName(path), sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) }).ToArray(),
                hardware = new { cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"), logicalProcessors = Environment.ProcessorCount,
                    os = System.Runtime.InteropServices.RuntimeInformation.OSDescription, runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription },
                limitations = new[] { "Real Arena workload; does not qualify the legacy 64-player room", "Application payload counters do not prove on-wire Regional/Degraded impairment budgets", "Per-process 80% CPU/GC-available-memory limits do not establish 20% headroom for two nodes sharing one host", "Every measured business Tick requires zero allocation; all mailbox/lifecycle bytes and exact steady coverage are retained" } };
            File.WriteAllText(Path.Combine(runDirectory, "qualification-detail.json"), JsonSerializer.Serialize(report));
            if (driverTimingEnabled)
                File.WriteAllText(Path.Combine(runDirectory, "qualification-driver-timing.json"), JsonSerializer.Serialize(new
                {
                    scope = "Diagnostic only; measured-loop histograms, bounded slow-cycle samples; send schedule and gates unchanged",
                    waitWitness = waitWitness?.Report(),
                    process = new { allocatedBytes = GC.GetTotalAllocatedBytes(false), gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2),
                        threadPoolThreads = ThreadPool.ThreadCount, pendingWork = ThreadPool.PendingWorkItemCount, gcPausePercent = GC.GetGCMemoryInfo().PauseTimePercentage },
                    rooms = owned.OrderBy(room => room.Slot).ThenBy(room => room.Generation).Select(room => new
                    {
                        room.Slot, room.Generation, room.Allocation.RoomId, room.DiagnosticStartedUtc, room.DiagnosticStartedTicks,
                        room.DiagnosticInitialThread, room.DiagnosticContext, room.DiagnosticThreadChanges,
                        room.MeasuredDeadlineResets, room.MeasuredSkippedPeriods, room.SlowCyclesOmitted,
                        histograms = room.Timing?.Report(), slowCycles = room.SlowCycles?.Take(room.SlowCycleCount)
                    })
                }));
            File.WriteAllText(Path.Combine(runDirectory, "qualification-latency-samples.json"), JsonSerializer.Serialize(samples));
            evidence.Add(report);
            Check(passed, "qualification-gates");
            terminalPassed = true;
        }
        catch (Exception error)
        {
            string? Reason(Exception? failure) => QualificationFailureDiagnostics.Find(failure, "qualification-");
            var failedRooms = owned.Select(room => new
            {
                room.Allocation.RoomId, room.Allocation.NodeId, room.Slot, room.Generation, driverStatus = room.Driver.Status.ToString(),
                driverFailureReason = Reason(room.Driver.Exception),
                matchStartTick = room.MatchStartTick, finalTick = room.Final?.RoomTick,
                clients = room.Clients.Select(client => new { client.Entity, client.Sequence, client.LastAck, client.AcceptedInputs,
                    pendingInputCount = client.Pending.Count, roomTick = client.Last.RoomTick, phase = client.Last.MatchPhase.ToString(),
                    client.Last.RemainingTicks, ackAgeSeconds = (Stopwatch.GetTimestamp() - client.LastAckTimestamp) / (double)Stopwatch.Frequency,
                    transport = QualificationInputSend.Capture(client.Wire.Transport) }).ToArray()
            }).ToArray();
            await File.WriteAllTextAsync(Path.Combine(runDirectory, "qualification-failure.json"), JsonSerializer.Serialize(new
            { passed = false, failureType = error.GetType().Name, failureReason = Reason(error),
                statusRejection = QualificationFailureDiagnostics.Find(error, "match-status-rejected:"), options, completedRooms = completed.Count,
                liveRooms = live.Count, measuredAcknowledgements = latencies.Count, recordedUtc = DateTimeOffset.UtcNow,
                sendFailure = error is QualificationInputSendException sendFailure ? (JsonElement?)sendFailure.Details : null,
                rooms = failedRooms }));
            await CollectFailureNodes();
            throw;
        }
        finally
        {
            bool passedBeforeCleanup = terminalPassed;
            stop.Cancel();
            diagnosticsStop.Cancel();
            try { await Task.WhenAll(slotTasks); } catch (Exception) { /* Original failure is retained above; all slots have terminated. */ }
            foreach (LoadRoom room in owned) { try { await room.Driver; } catch (Exception) { } }
            foreach (LoadRoom room in owned)
            {
                long disposing = Stopwatch.GetTimestamp();
                try { await room.DisposeAsync(); } catch (Exception) { }
                RecordPhase(room.Slot, room.Generation, "terminal-dispose-end", room.Allocation, disposing);
            }
            try { await Task.WhenAll(owned.Select(room => room.CleanupPartiesAsync())); }
            catch
            {
                terminalPassed = false;
                RecordPhase(-1, -1, "terminal-party-cleanup-unconfirmed");
            }
            if (diagnosticTask is not null) { try { await diagnosticTask; } catch (Exception) { } }
            WritePhaseEvidence(terminalPassed);
            if (passedBeforeCleanup && !terminalPassed) throw new InvalidOperationException("qualification-party-cleanup-unconfirmed");
        }
    }

    QualificationPreparedRoster<FailureScenarios.Fixture.PreparedMember> PrepareRoster(int slot, int generation, CancellationToken token)
    {
        long preparing = RecordPhase(slot, generation, "preparation-start");
        int readyCount = 0;
        return new(options.PlayersPerRoom, async (index, operation) =>
        {
            long memberStart = Stopwatch.GetTimestamp();
            var member = await fixture.Prepare(operation, cleanupParty: true);
            RecordPhase(slot, generation, "preparation-completed", started: memberStart, member: index + 1);
            if (Interlocked.Increment(ref readyCount) == options.PlayersPerRoom)
                RecordPhase(slot, generation, "roster-prepared", started: preparing);
            return member;
        }, fixture.KeepPreparedAlive, TimeSpan.FromSeconds(10), token);
    }

    async Task<LoadRoom> CreateRoom(int slot, int generation, CancellationToken token,
        FailureScenarios.Fixture.PreparedMember[]? nextMembers = null)
    {
        using CancellationTokenSource creationStop = CancellationTokenSource.CreateLinkedTokenSource(token);
        CancellationToken operation = creationStop.Token;
        var prepared = new FailureScenarios.Fixture.PreparedMember?[options.PlayersPerRoom];
        var joined = new LoadClient?[options.PlayersPerRoom];
        FantasyBattleProbeGroup? group = null;
        QualificationRoomResources? resources = null;
        try
        {
            FailureScenarios.Fixture.PreparedMember[] members;
            if (nextMembers is null)
            {
                await using var initial = PrepareRoster(slot, generation, operation);
                members = await initial.TakeAsync(_ => Task.CompletedTask);
            }
            else members = nextMembers;
            Array.Copy(members, prepared, members.Length);
            operation.ThrowIfCancellationRequested();
            RecordPhase(slot, generation, "roster-handoff");
            // Only the contiguous PartyQueue roster must serialize across replacement rooms.
            long gateWait = RecordPhase(slot, generation, "creation-gate-wait");
            long rosterStart = 0;
            try
            {
                await QualificationRosterAdmission.EnqueueAsync(creationGate, members.Length, async i =>
                {
                    if (i == 0) rosterStart = RecordPhase(slot, generation, "creation-gate-acquired", started: gateWait);
                    long queueStart = Stopwatch.GetTimestamp();
                    // A cancelled waiter cannot retract an accepted backend mutation. Await the bounded reply.
                    await fixture.Enqueue(members[i], CancellationToken.None);
                    RecordPhase(slot, generation, "queue-completed", started: queueStart, member: i + 1);
                }, operation);
                RecordPhase(slot, generation, "roster-queued", started: rosterStart);
            }
            finally { if (rosterStart != 0) RecordPhase(slot, generation, "creation-gate-released"); }
            long groupStart = RecordPhase(slot, generation, "probe-group-create-start");
            group = await runtime.CreateBattleProbeGroupAsync(operation);
            resources = new(group, () => Task.WhenAll(joined.Where(client => client is not null).Select(client => client!.DisposeAsync().AsTask())));
            operation.ThrowIfCancellationRequested();
            RecordPhase(slot, generation, "probe-group-created", started: groupStart);
            long joiningStart = Stopwatch.GetTimestamp();
            var results = await Task.WhenAll(Enumerable.Range(0, members.Length).Select(JoinMember));
            RoomAllocation allocation = results[0].allocation;
            Check(results.All(result => result.allocation.RoomId == allocation.RoomId), "qualification-same-roster-room");
            RecordPhase(slot, generation, "all-ready-joined-fresh", allocation, joiningStart);
            var room = new LoadRoom(allocation, results.Select(result => result.client).ToList(), slot, generation, resources);
            owned.Add(room);
            room.Driver = driverWaitMode == "windows-high-resolution"
                ? QualificationDriverExecution.Run(wait => DriveRoom(room, token, wait), () => new WindowsQualificationDriverWait(token), driverThreadPriority)
                : DriveRoom(room, token);
            return room;

            async Task<(LoadClient client, RoomAllocation allocation)> JoinMember(int index)
            {
                var member = members[index];
                int memberOrdinal = index + 1;
                long readyStart = Stopwatch.GetTimestamp();
                FantasyKcpProbe? wire = null;
                try
                {
                    MatchReady ready = await fixture.Ready(member.gate, member.login, member.request, cancellation: operation);
                    RoomAllocation memberAllocation = ready.Status.Allocation;
                    RecordPhase(slot, generation, "ready-completed", memberAllocation, readyStart, memberOrdinal);
                    Check(memberAllocation.PlayerIds.Count == members.Length &&
                        memberAllocation.PlayerIds.ToHashSet(StringComparer.Ordinal).SetEquals(members.Select(player => player.login.PlayerId)),
                        "qualification-same-roster-room");
                    long connectStart = Stopwatch.GetTimestamp();
                    operation.ThrowIfCancellationRequested();
                    // Connect has its own timeout; settle it before cancellation so its successful probe cannot be orphaned.
                    wire = await group!.ConnectAsync(memberAllocation.Address, CancellationToken.None);
                    operation.ThrowIfCancellationRequested();
                    RecordPhase(slot, generation, "battle-connect-completed", memberAllocation, connectStart, memberOrdinal);
                    long joinStart = Stopwatch.GetTimestamp();
                    JoinRoomResponse response = await fixture.Join(wire.Transport, ready, operation);
                    RecordPhase(slot, generation, "join-completed", memberAllocation, joinStart, memberOrdinal);
                    long snapshotStart = Stopwatch.GetTimestamp();
                    Snapshot snapshot = await fixture.FreshSnapshot(wire.Transport, operation);
                    var client = new LoadClient(member, wire, response.EntityId, snapshot);
                    joined[index] = client;
                    RecordPhase(slot, generation, "fresh-snapshot-completed", memberAllocation, snapshotStart, memberOrdinal);
                    return (client, memberAllocation);
                }
                catch
                {
                    creationStop.Cancel();
                    if (wire is not null && joined[index] is null) { try { await wire.DisposeAsync(); } catch (Exception) { } }
                    throw;
                }
            }
        }
        catch
        {
            // WhenAll has settled every sibling before ownership is cleaned; no task can publish a late connection.
            creationStop.Cancel();
            try
            {
                if (resources is not null) await resources.DisposeAsync();
                else if (group is not null) await group.DisposeAsync();
            }
            finally
            {
                // Party cleanup remains required even when KCP/group close also failed.
                await Task.WhenAll(prepared.Where(member => member is not null).Select(member => member!.DisposeAsync().AsTask()));
            }
            throw;
        }
    }

    async Task SlotLoop(int slot, LoadRoom room, CancellationToken token)
    {
        Task? retiring = null;
        try
        {
            while (true)
            {
                // Exactly one next generation per slot; no queue or room allocation occurs during preparation.
                await using var next = Stopwatch.GetTimestamp() < measurementEnd
                    ? PrepareRoster(slot, room.Generation + 1, token) : null;
                if (next is not null)
                {
                    if (await Task.WhenAny(room.Driver, next.Failure) == next.Failure) await next.Failure;
                    if (next.Failure.IsFaulted) await next.Failure;
                }
                await room.Driver;
                long gapStart = Stopwatch.GetTimestamp();
                FailureScenarios.Fixture.PreparedMember[]? members = null;
                if (next is not null)
                {
                    members = await next.TakeAsync(FinishOriginalRoom);
                    RecordPhase(slot, room.Generation + 1, "roster-keepalive-stopped");
                }
                else await FinishOriginalRoom(token);
                if (Stopwatch.GetTimestamp() >= measurementEnd)
                {
                    // A fully prepared, unqueued roster can remain at the measurement boundary. Keep its owner until cleanup.
                    if (members is not null) await Task.WhenAll(members.Select(member => member.DisposeAsync().AsTask()));
                    return;
                }
                room = await CreateRoom(slot, room.Generation + 1, token, members);
                long gap = Stopwatch.GetTimestamp() - gapStart;
                lock (occupancyGate) maximumGapTicks = Math.Max(maximumGapTicks, gap);
                RegisterRoom(slot, room);

                async Task FinishOriginalRoom(CancellationToken operation)
                {
                    AccountOccupancy(); live.TryRemove(slot, out _);
                    long settling = RecordPhase(room.Slot, room.Generation, "settlement-start", room.Allocation);
                    await Task.WhenAll(room.Clients.Select(async (client, index) =>
                    {
                        await QualificationSettlement.VerifyOnceAsync(client.Login.PlayerId, client.Login.SessionToken,
                            async ct =>
                            {
                                long opening = Stopwatch.GetTimestamp();
                                var connection = await runtime.ConnectGateProbeAsync("127.0.0.1:23001", CancellationToken.None);
                                RecordPhase(room.Slot, room.Generation, "settlement-connect-completed", room.Allocation, opening, index + 1);
                                return connection;
                            },
                            async (connection, playerId, sessionToken, ct) =>
                            {
                                long reading = Stopwatch.GetTimestamp();
                                ServiceReply reply = await ((FantasyBackendProbe)connection).CallAsync(ServiceMethods.Profile,
                                    new ProfileRequest { PlayerId = playerId }.ToByteArray(), sessionToken, ct);
                                PlayerProfile? profile = reply.Success ? reply.Read(PlayerProfile.Parser) : null;
                                string phase = profile is null ? "settlement-profile-rejected" : profile.Played switch
                                { 0 => "settlement-profile-pending", 1 => "settlement-profile-confirmed", _ => "settlement-profile-duplicate" };
                                RecordPhase(room.Slot, room.Generation, phase,
                                    room.Allocation, reading, index + 1);
                                return profile;
                            }, operation);
                    }));
                    RecordPhase(room.Slot, room.Generation, "settlement-end", room.Allocation, settling);
                    long replayWait = Stopwatch.GetTimestamp();
                    await fixture.Until(() => File.Exists(room.ReplayPath(runDirectory)), "qualification-published-replay", 15);
                    RecordPhase(room.Slot, room.Generation, "replay-published", room.Allocation, replayWait);
                    await WaitForReleasedCapacity(room, operation);
                    completed.Add(room);
                    long disposing = Stopwatch.GetTimestamp();
                    await room.DisposeAsync();
                    RecordPhase(room.Slot, room.Generation, "probe-group-closed", room.Allocation, disposing);
                    RecordPhase(room.Slot, room.Generation, "dispose-end", room.Allocation, disposing);
                    // A previous cleanup must finish before another is retained. The new cleanup is outside the replacement path.
                    if (retiring is not null) await retiring;
                    retiring = CleanupRoomParties(room);
                }
            }
        }
        finally { if (retiring is not null) await retiring; }
    }

    async Task WaitForReleasedCapacity(LoadRoom room, CancellationToken token)
    {
        long started = RecordPhase(room.Slot, room.Generation, "capacity-release-wait", room.Allocation);
        int port = room.Allocation.NodeId switch { "battle-1" => 24106, "battle-2" => 24107, _ => throw new InvalidOperationException("qualification-unknown-node") };
        using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(3) };
        while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(15))
        {
            using var json = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/health/diagnostics", token));
            if (!json.RootElement.GetProperty("queues").GetProperty("rooms").EnumerateArray()
                .Any(entry => entry.GetProperty("roomId").GetString() == room.Allocation.RoomId))
            {
                RecordPhase(room.Slot, room.Generation, "capacity-released", room.Allocation, started); return;
            }
            await Task.Delay(100, token);
        }
        throw new TimeoutException("qualification-original-capacity-not-released");
    }

    async Task CleanupRoomParties(LoadRoom room)
    {
        long started = RecordPhase(room.Slot, room.Generation, "party-cleanup-start", room.Allocation);
        try
        {
            await room.CleanupPartiesAsync();
            RecordPhase(room.Slot, room.Generation, "party-cleanup-end", room.Allocation, started);
        }
        catch { RecordPhase(room.Slot, room.Generation, "party-cleanup-unconfirmed", room.Allocation, started); throw; }
    }

    void AccountOccupancy()
    {
        lock (occupancyGate)
        {
            if (!collecting) return;
            long now = Math.Min(Stopwatch.GetTimestamp(), measurementEnd), delta = Math.Max(0, now - occupancyTimestamp);
            occupiedTicks += activeSlots * delta;
            slotTicks += options.RoomCount * delta; occupancyTimestamp = now;
        }
    }

    void RegisterRoom(int slot, LoadRoom room)
    {
        lock (occupancyGate) { AccountOccupancy(); live[slot] = room; if (room.InputEnded == 0) activeSlots++; }
        RecordPhase(slot, room.Generation, room.Generation == 0 ? "initial-registered" : "replacement-registered", room.Allocation);
    }

    void EndInputLoad(LoadRoom room, long now)
    {
        lock (occupancyGate)
        {
            if (room.InputEnded != 0) return;
            AccountOccupancy(); room.InputEnded = now;
            if (live.TryGetValue(room.Slot, out var registered) && ReferenceEquals(registered, room)) activeSlots--;
            foreach (LoadClient client in room.Clients) client.Ended = now;
        }
        RecordPhase(room.Slot, room.Generation, "input-load-ended", room.Allocation);
    }

    // Acceptance lifecycle diagnostics only: bounded records, no account/session/token or payload fields.
    sealed record RoomPhase(string phase, int slot, int generation, string? nodeId, string? roomId,
        int? member, DateTimeOffset utc, long monotonicTicks, long stopwatchFrequency, double? durationSeconds);

    long RecordPhase(int slot, int generation, string phase, RoomAllocation? allocation = null, long? started = null, int? member = null)
    {
        lock (phaseGate)
        {
            long now = Stopwatch.GetTimestamp();
            var key = (slot, generation);
            phaseCounts.TryGetValue(key, out int count);
            // Include per-member settlement timing while bounding retries and exposing omissions in terminal evidence.
            if (count >= 128 || phases.Count >= 100_000) { omittedPhases++; return now; }
            phases.Add(new(phase, slot, generation, allocation?.NodeId, allocation?.RoomId, member,
                DateTimeOffset.UtcNow, now, Stopwatch.Frequency,
                started is { } begin ? (now - begin) / (double)Stopwatch.Frequency : null));
            phaseCounts[key] = count + 1;
            return now;
        }
    }

    void WritePhaseEvidence(bool passed)
    {
        string path = Path.Combine(runDirectory, "qualification-room-phases.ndjson");
        try
        {
            RoomPhase[] retained;
            long omitted;
            lock (phaseGate) { retained = phases.ToArray(); omitted = omittedPhases; }
            using (StreamWriter writer = new(path + ".pending", append: false, Encoding.UTF8))
            {
                foreach (RoomPhase phase in retained) writer.WriteLine(JsonSerializer.Serialize(phase));
                writer.WriteLine(JsonSerializer.Serialize(new { phase = "capture-terminal", passed, utc = DateTimeOffset.UtcNow,
                    monotonicTicks = Stopwatch.GetTimestamp(), stopwatchFrequency = Stopwatch.Frequency,
                    recordCount = retained.Length, omittedRecords = omitted, maximumRecordsPerRoom = 128, maximumRecords = 100_000 }));
            }
            File.Move(path + ".pending", path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Keep the original qualification outcome; leave any partial evidence for diagnosis.
            Console.Error.WriteLine(JsonSerializer.Serialize(new { eventName = "qualification-room-phase-write-failed", errorType = error.GetType().Name }));
        }
    }

    async Task<JsonElement> CollectTerminalDelivery()
    {
        // Correctness after all tail rooms retire; never append to measured performance samples.
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        List<object> nodes = [];
        foreach (var node in new[] { (Id: "battle-1", Port: 24106), (Id: "battle-2", Port: 24107) })
        {
            using var json = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{node.Port}/health/diagnostics", cancellation));
            var queues = json.RootElement.GetProperty("queues");
            nodes.Add(new { node = node.Id,
                terminalDelivered = queues.GetProperty("terminalDelivered").GetInt64(),
                terminalDeliveryFailures = queues.GetProperty("terminalDeliveryFailures").GetInt64(),
                terminalDeliveryTimeouts = queues.GetProperty("terminalDeliveryTimeouts").GetInt64() });
        }
        var report = JsonSerializer.SerializeToElement(new { recordedUtc = DateTimeOffset.UtcNow, scope = "after-all-tail-room-settlements-and-releases", nodes });
        await File.WriteAllTextAsync(Path.Combine(runDirectory, "qualification-terminal-delivery.json"), report.GetRawText(), cancellation);
        return report;
    }
    async Task CollectDiagnostics()
    {
        using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(3) };
        foreach (var node in new[] { (Id: "battle-1", Port: 24106), (Id: "battle-2", Port: 24107) })
        {
            using var json = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{node.Port}/health/diagnostics", cancellation));
            var data = json.RootElement.Clone(); var utc = DateTimeOffset.UtcNow;
            long count = data.GetProperty("queues").GetProperty("lifecycleCount").GetInt64();
            bool transition = lifecycle.TryGetValue(node.Id, out long old) && old != count;
            lifecycle[node.Id] = count;
            foreach (var worker in data.GetProperty("workers").EnumerateArray())
            {
                string key = node.Id + "/worker-" + worker.GetProperty("workerId").GetInt32();
                if (!windows.TryGetValue(key, out var window)) windows[key] = window = new();
                window.Observe(worker, transition);
            }
            diagnostics.Add((node.Id, utc, data));
            await File.AppendAllTextAsync(Path.Combine(runDirectory, "qualification-diagnostics.ndjson"), JsonSerializer.Serialize(new { node = node.Id, utc, data }) + Environment.NewLine, cancellation);
        }
    }

    async Task CollectFailureNodes()
    {
        using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(2) };
        List<object> nodes = [];
        foreach (var node in new[] { (Id: "battle-1", Port: 24106), (Id: "battle-2", Port: 24107) })
        {
            try
            {
                using var json = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{node.Port}/health/diagnostics"));
                nodes.Add(new { node = node.Id, utc = DateTimeOffset.UtcNow, data = json.RootElement.Clone() });
            }
            catch (Exception error) { nodes.Add(new { node = node.Id, diagnosticFailureType = error.GetType().Name }); }
        }
        await File.WriteAllTextAsync(Path.Combine(runDirectory, "qualification-failure-nodes.json"), JsonSerializer.Serialize(nodes));
    }

    async Task DriveRoom(LoadRoom room, CancellationToken token, Action? ownedWait = null)
    {
        RecordPhase(room.Slot, room.Generation, "driver-start", room.Allocation);
        if (driverTimingEnabled)
        {
            room.Timing = new(); room.SlowCycles = new DriverSlowCycle[256];
            room.DiagnosticStartedUtc = DateTimeOffset.UtcNow; room.DiagnosticStartedTicks = Stopwatch.GetTimestamp();
            room.DiagnosticInitialThread = Environment.CurrentManagedThreadId;
            room.DiagnosticContext = SynchronizationContext.Current?.GetType().FullName;
        }
        int lastThread = room.DiagnosticInitialThread;
        long priorDelayStart = 0;
        var random = new Random(unchecked(options.Seed + room.Slot * 7919 + room.Generation * 104729));
        byte[] receive = new byte[1200], send = new byte[1200];
        long period = Stopwatch.Frequency / options.InputHz, next = Stopwatch.GetTimestamp();
        foreach (LoadClient client in room.Clients) client.Started = next;
        long finishingTimestamp = 0;
        while (true)
        {
            long now = Stopwatch.GetTimestamp();
            bool timingMeasured = room.Timing is not null && now >= measurementStart && now < measurementEnd;
            long delayElapsed = priorDelayStart == 0 ? 0 : now - priorDelayStart;
            if (timingMeasured)
            {
                if (priorDelayStart != 0) room.Timing!.DelayResume.Record(delayElapsed);
                int thread = Environment.CurrentManagedThreadId;
                if (thread != lastThread) room.DiagnosticThreadChanges++;
                lastThread = thread;
            }
            foreach (LoadClient client in room.Clients)
            {
                for (int n = 0; n < 64 && client.Wire.Transport.TryReceive(receive, out var packet); n++)
                {
                    client.ReceivedBytes += packet.WrittenBytes;
                    if (!packet.IsComplete || RealtimeProtocolCodec.TryDecode(receive.AsSpan(0, packet.WrittenBytes), out var decoded) != ProtocolDecodeStatus.Accepted) continue;
                    if (decoded.Message is Snapshot snapshot)
                    {
                        client.Last = snapshot;
                        if (now >= measurementStart && now < measurementEnd)
                        {
                            var player = snapshot.Players.First(player => player.EntityId == client.Entity);
                            if (!client.MeasuredPositionSeen) { client.MeasuredPositionSeen = true; client.MeasureX = player.PositionXMilli; client.MeasureZ = player.PositionZMilli; }
                            else if (player.PositionXMilli != client.MeasureX || player.PositionZMilli != client.MeasureZ) client.MeasuredMoved = true;
                        }
                        if (snapshot.MatchPhase == ArenaMatchPhase.ArenaMatchActive && room.MatchStartTick == 0)
                            room.MatchStartTick = snapshot.RoomTick - ((ulong)options.MatchTicks - snapshot.RemainingTicks);
                        if (snapshot.LastProcessedInputSequence > client.LastAck)
                        {
                            client.LastAck = snapshot.LastProcessedInputSequence; client.LastAckTimestamp = now;
                            if (client.Pending.TryGetValue(client.LastAck, out long sent) && sent >= measurementStart && sent <= measurementEnd)
                            { client.MeasuredAcks++; latencies.Enqueue((now - sent) * 1000d / Stopwatch.Frequency); }
                            foreach (uint old in client.Pending.Keys.Where(seq => seq <= client.LastAck).ToArray())
                            { client.Pending.Remove(old); client.LookCommands.Remove(old); }
                        }
                    }
                    else if (decoded.Message is ReliableEvent reliable && ReferenceEquals(client, room.Clients[0]))
                    {
                        room.Combat.Observe(reliable, now >= measurementStart && now < measurementEnd);
                        if (reliable.CombatEvent?.EventType == ArenaCombatEventType.ArenaEventFire) room.LastFireTimestamp = now;
                    }
                }
            }
            long receiveEnded = timingMeasured ? Stopwatch.GetTimestamp() : 0;
            if (timingMeasured) room.Timing!.Receive.Record(receiveEnded - now);
            bool finishing = room.Clients.Any(client => client.Last.MatchPhase == ArenaMatchPhase.ArenaMatchFinished);
            if (finishing) EndInputLoad(room, now);
            if (room.Clients.All(client => client.Last.MatchPhase == ArenaMatchPhase.ArenaMatchFinished))
            {
                lock (occupancyGate) room.Final = room.Clients[0].Last;
                RecordPhase(room.Slot, room.Generation, "final-snapshot", room.Allocation);
                return;
            }
            if (finishing)
            {
                if (finishingTimestamp == 0) finishingTimestamp = now;
                Check(now - finishingTimestamp <= Stopwatch.Frequency * 10, "qualification-final-snapshot-all-players-timeout");
            }
            if (now >= next && !finishing)
            {
                if (timingMeasured) room.Timing!.DeadlineLateness.Record(Math.Max(0, receiveEnded - next));
                next += period;
                if (now > next + period)
                {
                    if (timingMeasured) { room.MeasuredDeadlineResets++; room.MeasuredSkippedPeriods += (now - next) / period + 1; }
                    foreach (LoadClient client in room.Clients) client.MissedSendDeadlines++;
                    next = now + period;
                }
                long sendingStarted = timingMeasured ? Stopwatch.GetTimestamp() : 0;
                foreach (LoadClient client in room.Clients)
                {
                    var self = client.Last.Players.First(player => player.EntityId == client.Entity).Clone();
                    // Inputs carry deltas. Predict outstanding look commands so a 20Hz snapshot does not apply the same turn three times.
                    foreach (var outstanding in client.LookCommands.Values)
                    {
                        self.YawMillidegrees = (self.YawMillidegrees + outstanding.Yaw + 360000) % 360000;
                        self.PitchMillidegrees = Math.Clamp(self.PitchMillidegrees + outstanding.Pitch, -89000, 89000);
                    }
                    var enemy = client.Last.Players.Where(player => player.EntityId != client.Entity && player.Alive)
                        .OrderBy(player => Math.Abs((long)player.PositionXMilli - self.PositionXMilli) + Math.Abs((long)player.PositionZMilli - self.PositionZMilli)).FirstOrDefault();
                    var look = enemy is null ? (Yaw: 0, Pitch: 0) : QualificationAim.LookDelta(self, enemy);
                    uint sequence = ++client.Sequence;
                    var movement = enemy is null ? (X: 0, Z: 0) : QualificationAim.MoveToward(self, enemy, random.Next(2) == 0 ? 250 : -250);
                    var input = new InputCommand { Sequence = sequence, RoomTick = client.Last.RoomTick + 1,
                        MoveXMilli = movement.X, MoveYMilli = movement.Z, LookYawMilli = look.Yaw,
                        LookPitchMilli = look.Pitch, Buttons = 1, WeaponId = room.Combat.SelectWeapon(client.Entity) };
                    Check(client.Pending.Count < 512, "qualification-bounded-unacknowledged-inputs");
                    client.Pending[sequence] = now;
                    client.LookCommands[sequence] = look;
                    Check(RealtimeProtocolCodec.TryEncode(MessageId.InputCommand, input, send, out var channel, out int length), "qualification-input-encode");
                    await QualificationInputSend.SendAsync(client.Wire.Transport, channel, send.AsMemory(0, length), token);
                    client.AcceptedInputs++;
                    client.SentBytes += length;
                    long acceptedAt = Stopwatch.GetTimestamp();
                    if (acceptedAt >= measurementStart && acceptedAt < measurementEnd)
                    {
                        client.MeasuredSent++;
                        if (client.FirstMeasuredInputSequence == 0) client.FirstMeasuredInputSequence = sequence;
                        client.LastMeasuredInputSequence = sequence;
                    }
                    bool emptyAttempt = room.Combat.RecordAcceptedFireAttempt(client.Entity, input.WeaponId);
                    if (now >= measurementStart && now < measurementEnd && emptyAttempt) client.AmmoExhaustedInputs++;
                    if (room.LastFireTimestamp != 0 && now - room.LastFireTimestamp > Stopwatch.Frequency * 5) client.NoFireInputs++;
                    if (now - client.LastAckTimestamp > Stopwatch.Frequency * 5 && now - client.Started > Stopwatch.Frequency * 5)
                        throw new InvalidOperationException("qualification-player-ack-stalled");
                }
                if (timingMeasured) room.Timing!.SendBatch.Record(Stopwatch.GetTimestamp() - sendingStarted);
            }
            if (timingMeasured)
            {
                long ended = Stopwatch.GetTimestamp();
                room.Timing!.LoopWork.Record(ended - now);
                if (ended - now > period / 2 || delayElapsed > period)
                {
                    if (room.SlowCycleCount < room.SlowCycles!.Length)
                        room.SlowCycles[room.SlowCycleCount++] = new(now, receiveEnded, ended, delayElapsed,
                            Environment.CurrentManagedThreadId, GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), ThreadPool.PendingWorkItemCount);
                    else room.SlowCyclesOmitted++;
                }
            }
            priorDelayStart = room.Timing is not null ? Stopwatch.GetTimestamp() : 0;
            if (ownedWait is null) await Task.Delay(1, token);
            else ownedWait();
        }
    }

    static void Check(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
    static JsonElement ReadIdentity(string path) { using var document = JsonDocument.Parse(File.ReadAllText(path)); return document.RootElement.Clone(); }

    readonly record struct DriverSlowCycle(long LoopStartedTicks, long ReceiveEndedTicks, long LoopEndedTicks, long DelayResumeTicks,
        int ThreadId, int Gen0, int Gen1, int Gen2, long PendingThreadPoolWork);
    sealed class LoadRoom(RoomAllocation allocation, List<LoadClient> clients, int slot, int generation,
        QualificationRoomResources resources) : IAsyncDisposable
    {
        public readonly RoomAllocation Allocation = allocation;
        public readonly List<LoadClient> Clients = clients;
        public readonly int Slot = slot, Generation = generation;
        public Task Driver = Task.CompletedTask;
        public volatile Snapshot? Final;
        public long InputEnded;
        public ulong MatchStartTick;
        public long LastFireTimestamp;
        public QualificationDriverTiming? Timing;
        public DateTimeOffset DiagnosticStartedUtc;
        public long DiagnosticStartedTicks, MeasuredDeadlineResets, MeasuredSkippedPeriods, SlowCyclesOmitted;
        public int DiagnosticInitialThread, DiagnosticThreadChanges, SlowCycleCount;
        public string? DiagnosticContext;
        public DriverSlowCycle[]? SlowCycles;
        public readonly QualificationCombatEvidence Combat = new(clients.Select(client => client.Entity).ToArray());
        public string ReplayPath(string run) => Path.Combine(run, "replay", Allocation.NodeId,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Allocation.AllocationId + ":" + Allocation.RoomId + ":" + Allocation.BootEpoch))).ToLowerInvariant() + ".anar");
        public ValueTask DisposeAsync() => resources.DisposeAsync();
        public Task CleanupPartiesAsync() => Task.WhenAll(Clients.Select(client => client.Member.DisposeAsync().AsTask()));
    }
    sealed class LoadClient(FailureScenarios.Fixture.PreparedMember member, FantasyKcpProbe wire, uint entity, Snapshot snapshot) : IAsyncDisposable
    {
        public readonly FailureScenarios.Fixture.PreparedMember Member = member;
        public readonly FantasyBackendProbe Gate = member.gate;
        public readonly LoginResult Login = member.login;
        public readonly FantasyKcpProbe Wire = wire;
        public readonly uint Entity = entity;
        public Snapshot Last = snapshot;
        public uint Sequence = snapshot.LastProcessedInputSequence, LastAck = snapshot.LastProcessedInputSequence;
        public uint FirstMeasuredInputSequence, LastMeasuredInputSequence;
        public long Started, Ended, LastAckTimestamp = Stopwatch.GetTimestamp(), MeasuredSent, MeasuredAcks,
            MissedSendDeadlines, ReceivedBytes, SentBytes, NoFireInputs, AmmoExhaustedInputs, AcceptedInputs;
        public bool MeasuredPositionSeen, MeasuredMoved;
        public int MeasureX, MeasureZ;
        public readonly Dictionary<uint, long> Pending = [];
        public readonly SortedDictionary<uint, (int Yaw, int Pitch)> LookCommands = [];
        int disposed;
        public JsonElement Report(long start, long end, int inputHz, long measuredFire, string roomId, int slot, int generation)
        {
            double seconds = Math.Max(0, Math.Min(Ended, end) - Math.Max(Started, start)) / (double)Stopwatch.Frequency;
            double rate = seconds > 0 ? MeasuredSent / seconds : 0;
            return JsonSerializer.SerializeToElement(new { player = Login.PlayerId, Entity, roomId, slot, generation, measuredSeconds = seconds, measuredInputs = MeasuredSent,
                firstMeasuredInputSequence = FirstMeasuredInputSequence, lastMeasuredInputSequence = LastMeasuredInputSequence, totalAcceptedInputs = AcceptedInputs,
                acknowledgedSamples = MeasuredAcks, measuredInputHz = rate, lastAcknowledgedSequence = LastAck, missedSendDeadlines = MissedSendDeadlines,
                applicationSentBytes = SentBytes, applicationReceivedBytes = ReceivedBytes,
                moved = MeasuredMoved, measuredFireEvents = measuredFire, estimatedAmmoExhaustedInputAttempts = AmmoExhaustedInputs,
                passed = seconds <= 0 || (MeasuredAcks > 0 && Math.Abs(MeasuredSent - inputHz * seconds) <= Math.Max(1, inputHz * seconds * .01) &&
                    (seconds <= 5 || MeasuredMoved && measuredFire > 0)) });
        }
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try { await Wire.DisposeAsync(); } finally { await Member.DisposeTransportAsync(); }
        }
    }
}
