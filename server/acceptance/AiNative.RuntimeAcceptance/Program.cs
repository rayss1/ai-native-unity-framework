using System.Diagnostics;
using System.Text.Json;
using AiNative.BattleHost;
using AiNative.Gameplay;
using AiNative.Protocol.V1;
using AiNative.Server.Protocol;

namespace AiNative.RuntimeAcceptance;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Contains("--pcap", StringComparer.Ordinal))
        {
            return PcapCommand.Run(args);
        }

        AcceptanceOptions options = AcceptanceOptions.Parse(args);
        NetworkProfile profile = NetworkProfile.Resolve(options.Profile);
        ScenarioReport report = ScenarioRunner.Run(profile, options);

        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        });
        if (options.OutputPath is { } outputPath)
        {
            string fullPath = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, json + Environment.NewLine);
        }

        Console.WriteLine(json);
        return report.GatesPassed ? 0 : 1;
    }
}

internal sealed record AcceptanceOptions(
    string Profile,
    int WarmupTicks,
    int MeasuredTicks,
    ulong Seed,
    string? OutputPath)
{
    public static AcceptanceOptions Parse(string[] args)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        for (int index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException("Arguments must be supplied as --name value pairs.");
            }

            values[args[index][2..]] = args[index + 1];
        }

        string profile = values.GetValueOrDefault("profile", "regional");
        int warmup = int.Parse(values.GetValueOrDefault("warmup-ticks", "600"), System.Globalization.CultureInfo.InvariantCulture);
        int measured = int.Parse(values.GetValueOrDefault("measured-ticks", "3600"), System.Globalization.CultureInfo.InvariantCulture);
        ulong seed = ulong.Parse(values.GetValueOrDefault("seed", "1592594996"), System.Globalization.CultureInfo.InvariantCulture);
        ArgumentOutOfRangeException.ThrowIfNegative(warmup);
        ArgumentOutOfRangeException.ThrowIfLessThan(measured, 60);
        return new AcceptanceOptions(profile, warmup, measured, seed, values.GetValueOrDefault("output"));
    }
}

internal readonly record struct NetworkProfile(
    string Name,
    int RttMilliseconds,
    int LossBasisPoints,
    int JitterTicks,
    int DuplicateBasisPoints,
    int ReorderBasisPoints,
    bool BlockOneClient)
{
    public static NetworkProfile Resolve(string name) => name.ToLowerInvariant() switch
    {
        "regional" => new("Regional", 100, 100, 1, 50, 100, false),
        "degraded" => new("Degraded", 200, 500, 2, 100, 200, false),
        "backpressure" => new("Backpressure", 100, 100, 1, 50, 100, true),
        _ => throw new ArgumentException($"Unknown impairment profile '{name}'."),
    };
}

internal static class ScenarioRunner
{
    private const int BotCount = 64;
    private const int TickRate = 60;
    private const int SnapshotIntervalTicks = 3;
    private const int EstimatedWireOverheadBytes = 64;

    public static ScenarioReport Run(NetworkProfile profile, AcceptanceOptions options)
    {
        ScenarioMeasurements measurements = Execute(profile, options, profile.BlockOneClient);
        double blockedDelta = 0;
        if (profile.BlockOneClient)
        {
            ScenarioMeasurements baseline = Execute(profile with { BlockOneClient = false }, options, false);
            blockedDelta = measurements.TickP99Milliseconds - baseline.TickP99Milliseconds;
        }

        bool gatesPassed =
            measurements.TickP99Milliseconds <= 16.67 &&
            measurements.TickP999Milliseconds <= 20.0 &&
            measurements.SlowTickPercentage <= 0.1 &&
            measurements.GameplayP99Milliseconds <= 8.0 &&
            measurements.DownstreamP95Kbps <= 256.0 &&
            measurements.UpstreamP95Kbps <= 64.0 &&
            measurements.MaxDatagramBytes <= RealtimeProtocolCodec.MaxDatagramBytes &&
            measurements.MaxOutboundPackets <= 3 &&
            measurements.MaxOutboundBytes <= 256 * 1024 &&
            (!profile.BlockOneClient || blockedDelta < 0.5);

        return new ScenarioReport(
            EvidenceClass: "deterministic-production-codec-impairment",
            QualifiedSocketImpairment: false,
            profile.Name,
            profile.RttMilliseconds,
            profile.LossBasisPoints / 100d,
            profile.JitterTicks,
            profile.DuplicateBasisPoints / 100d,
            profile.ReorderBasisPoints / 100d,
            BotCount,
            TickRate,
            SnapshotRate: TickRate / SnapshotIntervalTicks,
            options.WarmupTicks,
            options.MeasuredTicks,
            options.Seed,
            Environment.Version.ToString(),
            Environment.OSVersion.ToString(),
            Environment.ProcessorCount,
            Environment.GetEnvironmentVariable("AINATIVE_SOURCE_COMMIT") ?? "unrecorded",
            Environment.GetEnvironmentVariable("AINATIVE_FANTASY_COMMIT") ?? "unrecorded",
            Environment.GetEnvironmentVariable("AINATIVE_PROTOCOL_IDENTITY") ?? "unrecorded",
            measurements.TickP99Milliseconds,
            measurements.TickP999Milliseconds,
            measurements.SlowTickPercentage,
            measurements.GameplayP99Milliseconds,
            blockedDelta,
            measurements.DownstreamP95Kbps,
            measurements.UpstreamP95Kbps,
            measurements.MaxDatagramBytes,
            measurements.MaxOutboundPackets,
            measurements.MaxOutboundBytes,
            measurements.NetworkDroppedPackets,
            measurements.QueueDroppedPackets,
            measurements.DuplicatedPackets,
            measurements.ReorderedPackets,
            measurements.FinalStateHash.ToString("x16", System.Globalization.CultureInfo.InvariantCulture),
            gatesPassed);
    }

    private static ScenarioMeasurements Execute(
        NetworkProfile profile,
        AcceptanceOptions options,
        bool blockOneClient)
    {
        int totalTicks = checked(options.WarmupTicks + options.MeasuredTicks);
        int measuredSeconds = (options.MeasuredTicks + TickRate - 1) / TickRate;
        long[,] upstreamBytes = new long[BotCount, measuredSeconds];
        long[,] downstreamBytes = new long[BotCount, measuredSeconds];
        long[] tickDurations = new long[options.MeasuredTicks];
        long[] gameplayDurations = new long[options.MeasuredTicks];
        uint[] lastInputSequence = new uint[BotCount];
        InputCommand?[] pendingInputs = new InputCommand[BotCount];
        SimulatedLink[] upstream = new SimulatedLink[BotCount];
        SimulatedLink[] downstream = new SimulatedLink[BotCount];
        SyntheticRoom room = new(BotCount);
        Pcg32Random inputRandom = new(options.Seed, 0x51UL);
        Pcg32Random impairmentRandom = new(options.Seed ^ 0x9e3779b97f4a7c15UL, 0x13UL);
        int oneWayTicks = Math.Max(1, (int)Math.Ceiling(profile.RttMilliseconds * TickRate / 2000d));
        for (int index = 0; index < BotCount; index++)
        {
            upstream[index] = new SimulatedLink(1024, 256 * 1024);
            downstream[index] = new SimulatedLink(3, 256 * 1024);
        }

        long networkDrops = 0;
        long queueDrops = 0;
        long duplicates = 0;
        long reorders = 0;
        int maxDatagramBytes = 0;
        int maxOutboundPackets = 0;
        int maxOutboundBytes = 0;
        byte[] encodeBuffer = new byte[RealtimeProtocolCodec.MaxDatagramBytes];
        int blockedStart = options.WarmupTicks + (options.MeasuredTicks / 3);
        int blockedEnd = blockedStart + Math.Min(options.MeasuredTicks / 3, 600);

        for (int tick = 0; tick < totalTicks; tick++)
        {
            for (int entity = 0; entity < BotCount; entity++)
            {
                InputCommand command = new()
                {
                    RoomTick = checked((ulong)tick),
                    Sequence = checked((uint)tick + 1),
                    MoveXMilli = (int)(inputRandom.NextUInt32() % 2001) - 1000,
                    MoveYMilli = (int)(inputRandom.NextUInt32() % 2001) - 1000,
                    Buttons = (tick + 1) % 6 == 0 ? 1U : 0U,
                };
                if ((tick & 1) == 0)
                {
                    pendingInputs[entity] = command;
                    continue;
                }

                InputBatch batch = new();
                batch.Commands.Add(pendingInputs[entity] ??
                    throw new InvalidOperationException("The acceptance Input batch lost its first frame."));
                batch.Commands.Add(command);
                pendingInputs[entity] = null;
                if (!RealtimeProtocolCodec.TryEncode(
                    MessageId.InputBatch,
                    batch,
                    encodeBuffer,
                    out _,
                    out int inputBytes))
                {
                    throw new InvalidOperationException("The production codec rejected an acceptance InputBatch.");
                }

                byte[] frame = encodeBuffer.AsSpan(0, inputBytes).ToArray();
                LinkScheduleResult scheduled = Schedule(
                    upstream[entity],
                    frame,
                    tick,
                    oneWayTicks,
                    profile,
                    impairmentRandom);
                networkDrops += scheduled.NetworkDropped;
                queueDrops += scheduled.QueueDropped;
                duplicates += scheduled.Duplicated;
                reorders += scheduled.Reordered;
                RecordWireBytes(upstreamBytes, entity, tick, options.WarmupTicks, scheduled.WireCopies * (inputBytes + EstimatedWireOverheadBytes));
                maxDatagramBytes = Math.Max(maxDatagramBytes, inputBytes);
            }

            long tickStarted = Stopwatch.GetTimestamp();
            for (int entity = 0; entity < BotCount; entity++)
            {
                int capturedEntity = entity;
                upstream[entity].DrainDue(tick, packet =>
                {
                    if (RealtimeProtocolCodec.TryDecode(packet, out DecodedProtocolMessage decoded) != ProtocolDecodeStatus.Accepted ||
                        decoded.MessageId != MessageId.InputBatch ||
                        decoded.Message is not InputBatch batch)
                    {
                        return;
                    }

                    foreach (InputCommand input in batch.Commands)
                    {
                        if (input.Sequence > lastInputSequence[capturedEntity])
                        {
                            lastInputSequence[capturedEntity] = input.Sequence;
                            room.ApplyInput(capturedEntity, input.MoveXMilli, input.MoveYMilli);
                        }
                    }
                });
            }

            long gameplayStarted = Stopwatch.GetTimestamp();
            room.Tick();
            long gameplayElapsed = Stopwatch.GetTimestamp() - gameplayStarted;

            if ((tick + 1) % SnapshotIntervalTicks == 0)
            {
                Snapshot snapshot = room.CreateSnapshot(checked((ulong)tick + 1));
                if (!RealtimeProtocolCodec.TryEncode(
                    MessageId.Snapshot,
                    snapshot,
                    encodeBuffer,
                    out _,
                    out int snapshotBytes))
                {
                    throw new InvalidOperationException("The 64-player Snapshot exceeded the production codec MTU.");
                }

                byte[] frame = encodeBuffer.AsSpan(0, snapshotBytes).ToArray();
                for (int entity = 0; entity < BotCount; entity++)
                {
                    LinkScheduleResult scheduled = Schedule(
                        downstream[entity],
                        frame,
                        tick,
                        oneWayTicks,
                        profile,
                        impairmentRandom);
                    networkDrops += scheduled.NetworkDropped;
                    queueDrops += scheduled.QueueDropped;
                    duplicates += scheduled.Duplicated;
                    reorders += scheduled.Reordered;
                    RecordWireBytes(downstreamBytes, entity, tick, options.WarmupTicks, scheduled.WireCopies * (snapshotBytes + EstimatedWireOverheadBytes));
                }

                maxDatagramBytes = Math.Max(maxDatagramBytes, snapshotBytes);
            }

            for (int entity = 0; entity < BotCount; entity++)
            {
                bool blocked = blockOneClient && entity == 0 && tick >= blockedStart && tick < blockedEnd;
                if (!blocked)
                {
                    downstream[entity].DrainDue(tick, static _ => { });
                }

                maxOutboundPackets = Math.Max(maxOutboundPackets, downstream[entity].Count);
                maxOutboundBytes = Math.Max(maxOutboundBytes, downstream[entity].Bytes);
            }

            if (tick >= options.WarmupTicks)
            {
                int sample = tick - options.WarmupTicks;
                tickDurations[sample] = Stopwatch.GetTimestamp() - tickStarted;
                gameplayDurations[sample] = gameplayElapsed;
            }
        }

        Array.Sort(tickDurations);
        Array.Sort(gameplayDurations);
        double tickP99 = ToMilliseconds(tickDurations[PercentileIndex(tickDurations.Length, 0.99)]);
        double tickP999 = ToMilliseconds(tickDurations[PercentileIndex(tickDurations.Length, 0.999)]);
        double slowPercentage = tickDurations.Count(value => ToMilliseconds(value) > 16.67) * 100d / tickDurations.Length;
        double gameplayP99 = ToMilliseconds(gameplayDurations[PercentileIndex(gameplayDurations.Length, 0.99)]);

        return new ScenarioMeasurements(
            tickP99,
            tickP999,
            slowPercentage,
            gameplayP99,
            PercentileKbps(downstreamBytes, 0.95),
            PercentileKbps(upstreamBytes, 0.95),
            maxDatagramBytes,
            maxOutboundPackets,
            maxOutboundBytes,
            networkDrops,
            queueDrops,
            duplicates,
            reorders,
            room.ComputeStateHash());
    }

    private static LinkScheduleResult Schedule(
        SimulatedLink link,
        byte[] packet,
        int currentTick,
        int oneWayTicks,
        NetworkProfile profile,
        Pcg32Random random)
    {
        int jitter = profile.JitterTicks == 0
            ? 0
            : (int)(random.NextUInt32() % checked((uint)((profile.JitterTicks * 2) + 1))) - profile.JitterTicks;
        int dueTick = currentTick + Math.Max(1, oneWayTicks + jitter);
        bool reordered = random.NextUInt32() % 10_000 < profile.ReorderBasisPoints;
        if (reordered)
        {
            dueTick += 1 + (int)(random.NextUInt32() % 3);
        }

        bool networkDropped = random.NextUInt32() % 10_000 < profile.LossBasisPoints;
        if (networkDropped)
        {
            return new LinkScheduleResult(1, 1, 0, 0, reordered ? 1 : 0);
        }

        int queueDropped = link.TrySchedule(packet, dueTick) ? 0 : 1;
        bool duplicated = random.NextUInt32() % 10_000 < profile.DuplicateBasisPoints;
        int wireCopies = 1;
        if (duplicated)
        {
            wireCopies++;
            queueDropped += link.TrySchedule(packet, dueTick + 1) ? 0 : 1;
        }

        return new LinkScheduleResult(wireCopies, 0, queueDropped, duplicated ? 1 : 0, reordered ? 1 : 0);
    }

    private static void RecordWireBytes(long[,] windows, int entity, int tick, int warmupTicks, int bytes)
    {
        if (tick < warmupTicks)
        {
            return;
        }

        int second = Math.Min(windows.GetLength(1) - 1, (tick - warmupTicks) / TickRate);
        windows[entity, second] += bytes;
    }

    private static double PercentileKbps(long[,] windows, double percentile)
    {
        double[] samples = new double[windows.Length];
        int sample = 0;
        for (int entity = 0; entity < windows.GetLength(0); entity++)
        {
            for (int second = 0; second < windows.GetLength(1); second++)
            {
                samples[sample++] = windows[entity, second] * 8d / 1000d;
            }
        }

        Array.Sort(samples);
        return samples[PercentileIndex(samples.Length, percentile)];
    }

    private static int PercentileIndex(int count, double percentile) =>
        Math.Clamp((int)Math.Ceiling(count * percentile) - 1, 0, count - 1);

    private static double ToMilliseconds(long timestamps) => timestamps * 1000d / Stopwatch.Frequency;
}

internal sealed class SimulatedLink(int maxPackets, int maxBytes)
{
    private readonly PriorityQueue<byte[], (int DueTick, long Sequence)> _packets = new();
    private long _sequence;

    public int Count => _packets.Count;

    public int Bytes { get; private set; }

    public bool TrySchedule(byte[] packet, int dueTick)
    {
        if (_packets.Count >= maxPackets || Bytes + packet.Length > maxBytes)
        {
            return false;
        }

        _packets.Enqueue(packet, (dueTick, ++_sequence));
        Bytes += packet.Length;
        return true;
    }

    public void DrainDue(int currentTick, Action<byte[]> consume)
    {
        while (_packets.TryPeek(out _, out (int DueTick, long Sequence) priority) && priority.DueTick <= currentTick)
        {
            byte[] packet = _packets.Dequeue();
            Bytes -= packet.Length;
            consume(packet);
        }
    }
}

internal readonly record struct LinkScheduleResult(
    int WireCopies,
    int NetworkDropped,
    int QueueDropped,
    int Duplicated,
    int Reordered);

internal readonly record struct ScenarioMeasurements(
    double TickP99Milliseconds,
    double TickP999Milliseconds,
    double SlowTickPercentage,
    double GameplayP99Milliseconds,
    double DownstreamP95Kbps,
    double UpstreamP95Kbps,
    int MaxDatagramBytes,
    int MaxOutboundPackets,
    int MaxOutboundBytes,
    long NetworkDroppedPackets,
    long QueueDroppedPackets,
    long DuplicatedPackets,
    long ReorderedPackets,
    ulong FinalStateHash);

internal sealed record ScenarioReport(
    string EvidenceClass,
    bool QualifiedSocketImpairment,
    string Profile,
    int RttMilliseconds,
    double LossPercent,
    int JitterTicks,
    double DuplicationPercent,
    double ReorderingPercent,
    int BotCount,
    int TickRate,
    int SnapshotRate,
    int WarmupTicks,
    int MeasuredTicks,
    ulong Seed,
    string Runtime,
    string OperatingSystem,
    int ProcessorCount,
    string SourceCommit,
    string FantasyCommit,
    string ProtocolIdentity,
    double TickP99Milliseconds,
    double TickP999Milliseconds,
    double SlowTickPercentage,
    double GameplayP99Milliseconds,
    double BlockedClientTickP99IncrementMilliseconds,
    double DownstreamP95Kbps,
    double UpstreamP95Kbps,
    int MaxDatagramBytes,
    int MaxOutboundPackets,
    int MaxOutboundBytes,
    long NetworkDroppedPackets,
    long QueueDroppedPackets,
    long DuplicatedPackets,
    long ReorderedPackets,
    string FinalStateHash,
    bool GatesPassed);
