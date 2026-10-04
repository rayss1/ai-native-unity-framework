using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using AiNative.Protocol.V1;
using AiNative.Realtime;
using System.Reflection;
using AiNative.Protocol.Backend.V1;

public sealed record ArenaCapacityOptions(int RoomCount, int PlayersPerRoom, int WorkerCount, int RoomsPerWorker,
    int InputHz, int WarmupSeconds, int DurationSeconds, int Seed, int MatchTicks)
{
    public static ArenaCapacityOptions Load(Func<string, string?> environment)
    {
        int Read(string key, int fallback, int min, int max)
        {
            string? raw = environment("AINATIVE_QUALIFICATION_" + key);
            if (raw is null) return fallback;
            if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n < min || n > max)
                throw new ArgumentException("Invalid qualification setting: " + key);
            return n;
        }
        var value = new ArenaCapacityOptions(Read("ROOM_COUNT", 8, 1, 32), Read("PLAYERS_PER_ROOM", 8, 2, 8),
            Read("WORKER_COUNT", 2, 1, 16), Read("ROOMS_PER_WORKER", 2, 1, 16), Read("INPUT_HZ", 60, 1, 60),
            Read("WARMUP_SECONDS", 60, 1, 3600), Read("DURATION_SECONDS", 300, 1, 7200),
            Read("SEED", 20261002, 1, int.MaxValue), Read("MATCH_TICKS", 36000, 60, 216000));
        if (value.WorkerCount * value.RoomsPerWorker > 16 || value.RoomCount != 2 * value.WorkerCount * value.RoomsPerWorker)
            throw new ArgumentException("Qualification must fill the exact two-node configured capacity.");
        return value;
    }
}

/// <summary>Consumes real diagnostic samples with explicit Tick identities; gaps never become a passing percentile.</summary>
public sealed class QualificationWorkerWindow
{
    readonly Dictionary<long, long> _samples = [];
    readonly Dictionary<long, long> _businessMicros = [];
    readonly Dictionary<long, (long Room, long Mailbox, long Lifecycle)> _allocations = [];
    long _first = -1, _last, _allocated, _steadyAllocated, _lifecycleAllocated, _steadyTicks, _lifecycleTicks;
    public void Observe(JsonElement worker, bool lifecycle)
    {
        long end = worker.GetProperty("tickCount").GetInt64(), allocation = worker.GetProperty("allocatedBytes").GetInt64();
        if (_first < 0) { _first = _last = end; _allocated = allocation; return; }
        if (end < _last || allocation < _allocated) throw new InvalidOperationException("Worker counter reset during qualification.");
        var ids = worker.GetProperty("sampleTickIds").EnumerateArray().Select(x => x.GetInt64()).ToArray();
        var micros = worker.GetProperty("recentTickMicros").EnumerateArray().Select(x => x.GetInt64()).ToArray();
        var rooms = worker.GetProperty("sampleRoomAllocatedBytes").EnumerateArray().Select(x => x.GetInt64()).ToArray();
        var mailbox = worker.GetProperty("sampleMailboxAllocatedBytes").EnumerateArray().Select(x => x.GetInt64()).ToArray();
        var transitions = worker.GetProperty("sampleLifecycleOperations").EnumerateArray().Select(x => x.GetInt64()).ToArray();
        var businessMicros = worker.GetProperty("sampleRoomMicros").EnumerateArray().Select(x => x.GetInt64()).ToArray();
        if (ids.Length != micros.Length || ids.Length != rooms.Length || ids.Length != mailbox.Length || ids.Length != transitions.Length || ids.Length != businessMicros.Length)
            throw new InvalidOperationException("Worker sample identity mismatch.");
        for (int i = 0; i < ids.Length; i++)
        {
            long id = ids[i];
            if (id <= _first || id > end) continue;
            if (micros[i] < 0 || (_samples.TryGetValue(id, out long prior) && prior != micros[i]))
                throw new InvalidOperationException("Worker sample changed for an existing Tick.");
            _samples[id] = micros[i];
            if (businessMicros[i] < 0 || (_businessMicros.TryGetValue(id, out long priorBusiness) && priorBusiness != businessMicros[i]))
                throw new InvalidOperationException("Business Tick sample changed for an existing Tick.");
            _businessMicros[id] = businessMicros[i];
            var allocationPoint = (rooms[i], mailbox[i], transitions[i]);
            if (_allocations.TryGetValue(id, out var previous) && previous != allocationPoint)
                throw new InvalidOperationException("Worker allocation attribution changed for an existing Tick.");
            _allocations[id] = allocationPoint;
        }
        if (lifecycle) { _lifecycleAllocated += allocation - _allocated; _lifecycleTicks += end - _last; }
        else { _steadyAllocated += allocation - _allocated; _steadyTicks += end - _last; }
        _last = end; _allocated = allocation;
    }
    public JsonElement Report()
    {
        long[] sorted = _samples.Values.Order().ToArray();
        long[] businessSorted = _businessMicros.Values.Order().ToArray();
        long businessP99 = businessSorted.Length == 0 ? 0 : businessSorted[(int)Math.Ceiling(businessSorted.Length * .99) - 1];
        long P(double p) => sorted.Length == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
        bool complete = _first >= 0 && sorted.LongLength == _last - _first;
        long over = sorted.LongCount(x => x > 16667);
        long business = _allocations.Values.Sum(point => point.Room);
        long steady = _allocations.Values.Where(point => point.Lifecycle == 0).Sum(point => point.Room + point.Mailbox);
        long lifecycleBytes = _allocations.Values.Where(point => point.Lifecycle != 0).Sum(point => point.Mailbox);
        long steadyCount = _allocations.Values.LongCount(point => point.Lifecycle == 0);
        bool passed = complete && sorted.Length > 0 && steadyCount >= sorted.LongLength * .95 && business == 0 && steady == 0 &&
            businessP99 <= 6400 && P(.99) <= 13336 && P(.999) <= 16000 && over <= sorted.LongLength * .0008;
        return JsonSerializer.SerializeToElement(new { firstTick = _first + 1, lastTick = _last, observedTicks = _last - _first,
            sampleCount = sorted.Length, complete, steadyTicks = steadyCount, lifecycleTicks = sorted.LongLength - steadyCount,
            steadyCoverage = sorted.Length == 0 ? 0 : steadyCount / (double)sorted.Length,
            businessAllocatedBytes = business, steadyAllocatedBytes = steady, lifecycleAllocatedBytes = lifecycleBytes,
            totalAllocatedBytes = _allocations.Values.Sum(point => point.Room + point.Mailbox),
            diagnosticCounterAllocatedBytes = _steadyAllocated + _lifecycleAllocated,
            businessTickP99Micros = businessP99, businessTickScope = "All room.Tick calls on this worker, including replay/hash/frame; conservative bound for Gameplay+physics",
            p50Micros = P(.5), p95Micros = P(.95), p99Micros = P(.99), p999Micros = P(.999), maximumMicros = sorted.LastOrDefault(),
            overBudgetTicks = over, percentileMethod = "nearest-rank over exact deduplicated measured Tick IDs", passed });
    }
}

public static class QualificationResourceGate
{
    public static bool Passes(double cpuPercent, long workingSet, long availableBytes) =>
        double.IsFinite(cpuPercent) && cpuPercent >= 0 && cpuPercent <= 80 && workingSet > 0 && availableBytes > 0 &&
        workingSet <= availableBytes * .8;
}

public static class QualificationAim
{
    public static (int X, int Z) MoveToward(PlayerState self, PlayerState target, int strafe)
    {
        double x = (double)target.PositionXMilli - self.PositionXMilli, z = (double)target.PositionZMilli - self.PositionZMilli;
        double distance = Math.Sqrt(x*x + z*z);
        if (distance < 1) return (strafe, -strafe);
        double forward = distance > 4000 ? 1000 : -250;
        return ((int)Math.Clamp((x * forward - z * strafe) / distance, -1000, 1000),
            (int)Math.Clamp((z * forward + x * strafe) / distance, -1000, 1000));
    }
    public static (int Yaw, int Pitch) LookDelta(PlayerState self, PlayerState target)
    {
        double x = (double)target.PositionXMilli - self.PositionXMilli, z = (double)target.PositionZMilli - self.PositionZMilli;
        int yaw = (int)(Math.Atan2(x, z) * 180000 / Math.PI) - self.YawMillidegrees;
        while (yaw > 180000) yaw -= 360000; while (yaw < -180000) yaw += 360000;
        int pitch = (int)(Math.Atan2((double)target.PositionYMilli - self.PositionYMilli, Math.Sqrt(x * x + z * z)) * 180000 / Math.PI) - self.PitchMillidegrees;
        return (yaw, Math.Clamp(pitch, -180000, 180000));
    }
}

public sealed class QualificationCombatEvidence(uint[] entities)
{
    readonly Dictionary<uint, int[]> ammo = entities.ToDictionary(entity => entity, _ => Magazines());
    readonly long[] total = new long[6], measured = new long[6];
    readonly Dictionary<uint, long> playerFire = [];
    uint lastSequence;
    long gaps;
    static int[] Magazines() => [AiNative.Gameplay.ArenaWeaponRules.Machinegun.MagazineSize,
        AiNative.Gameplay.ArenaWeaponRules.Shotgun.MagazineSize, AiNative.Gameplay.ArenaWeaponRules.Rocket.MagazineSize];
    public void Observe(ReliableEvent message, bool measuring)
    {
        if (message.CombatEvent is not { } combat || message.Sequence <= lastSequence) return;
        if (measuring && lastSequence != 0 && message.Sequence != lastSequence + 1) gaps++;
        lastSequence = message.Sequence;
        int kind = (int)combat.EventType;
        if ((uint)kind >= total.Length) throw new InvalidOperationException("Unknown Arena combat event.");
        total[kind]++; if (measuring) measured[kind]++;
        if (kind == 0 && ammo.TryGetValue(combat.SourceEntityId, out int[]? remaining) && (uint)combat.WeaponId is >= 1 and <= 3)
        {
            remaining[(int)combat.WeaponId - 1] = Math.Max(0, remaining[(int)combat.WeaponId - 1] - 1);
            if (measuring) playerFire[combat.SourceEntityId] = playerFire.GetValueOrDefault(combat.SourceEntityId) + 1;
        }
        if (kind == 3 && ammo.ContainsKey(combat.SourceEntityId)) ammo[combat.SourceEntityId] = Magazines();
    }
    public bool AmmoExhausted(uint entity, uint weapon) => weapon is >= 1 and <= 3 && ammo.TryGetValue(entity, out var value) && value[weapon - 1] == 0;
    public long MeasuredFire(uint entity) => playerFire.GetValueOrDefault(entity);
    public JsonElement Report() => JsonSerializer.SerializeToElement(new { fire = total[0], hit = total[1], kills = total[2], respawns = total[3], switches = total[5],
        measuredFire = measured[0], measuredHit = measured[1], measuredKills = measured[2], measuredRespawns = measured[3], measuredSwitches = measured[5], measuredSequenceGaps = gaps });
}

public sealed class QualificationInputSendException(JsonElement details) : InvalidOperationException("qualification-input-send: " + details.GetProperty("sendStatus").GetString())
{ public JsonElement Details { get; } = details; }

public static class QualificationInputSend
{
    public static async ValueTask<SendResult> SendAsync(IRealtimeTransport transport, TransportChannel channel,
        ReadOnlyMemory<byte> payload, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        SendResult result = await transport.SendAsync(channel, payload, token);
        if (result.Status != SendStatus.Accepted)
        {
            token.ThrowIfCancellationRequested();
            throw new QualificationInputSendException(Capture(transport, result.Status, payload.Length, result.AcceptedBytes));
        }
        return result;
    }

    // Failure-only adapter inspection. Whitelisted counters/lifecycle fields contain no credentials or payloads.
    public static JsonElement Capture(IRealtimeTransport transport, SendStatus? status = null, int payloadBytes = 0, int acceptedBytes = 0)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        object? Read(object? source, string name)
        {
            if (source is null) return null;
            try { return source.GetType().GetProperty(name, flags)?.GetValue(source) ?? source.GetType().GetField(name, flags)?.GetValue(source); }
            catch (Exception) { return null; }
        }
        object? sender = Read(transport, "_sender"), dispatcher = Read(sender, "_dispatcher");
        object? session = Read(dispatcher, "<session>P"), scene = Read(dispatcher, "_ownerScene");
        return JsonSerializer.SerializeToElement(new
        {
            sendStatus = status?.ToString(), transportState = transport.State.ToString(), transportType = transport.GetType().FullName,
            payloadBytes, acceptedBytes, pendingOutboundPackets = Read(sender, "PendingOutboundPackets"),
            pendingOutboundBytes = Read(sender, "PendingOutboundBytes"), senderClosed = Read(sender, "IsClosed"),
            pendingInboundPackets = Read(transport, "_inboundPackets"), pendingInboundBytes = Read(transport, "_inboundBytes"),
            sessionRuntimeId = Read(session, "RuntimeId"), sessionDisposed = Read(session, "IsDisposed"),
            sceneDisposed = Read(scene, "IsDisposed"), sessionLastReceiveMilliseconds = Read(session, "LastReceiveTime")
        });
    }
}

public static class QualificationSettlement
{
    public static async Task VerifyOnceAsync(string originalPlayerId, string originalSessionToken,
        Func<CancellationToken, Task<IAsyncDisposable>> open,
        Func<IAsyncDisposable, string, string, CancellationToken, ValueTask<PlayerProfile?>> read, CancellationToken token)
    {
        // A 10-minute match outlives the original Gate TCP session's 30-second idle timeout.
        await using IAsyncDisposable connection = await open(token);
        PlayerProfile? profile = await AcceptanceSettlementPolling.WaitAsync(
            async ct => await read(connection, originalPlayerId, originalSessionToken, ct), token);
        if (profile?.PlayerId != originalPlayerId || profile.Played != 1)
            throw new InvalidOperationException("qualification-original-settlement-once");
    }
}
