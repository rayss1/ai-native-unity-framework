using System.Diagnostics;
using System.Net;
using AiNative.BattleHost;
using AiNative.Gameplay;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using NUnit.Framework;

namespace AiNative.BattleHost.Tests;

public sealed class HealthAndBudgetTests
{
    [Test]
    public void ReadinessRequiresBothRoomAndNetworkWhenFantasyIsEnabled()
    {
        RuntimeReadiness readiness = new(networkRequired: true);

        readiness.MarkRoomReady();
        Assert.That(readiness.IsReady, Is.False);

        readiness.MarkNetworkReady();
        Assert.That(readiness.IsReady, Is.True);

        readiness.BeginDrain();
        Assert.That(readiness.IsReady, Is.False);
    }

    [Test]
    public async Task DrainMakesReadinessUnavailableWithoutFailingLiveness()
    {
        await using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("AINATIVE_ENABLE_EVALUATION_ENDPOINTS", "true");
                builder.UseSetting("AINATIVE_FANTASY_ENABLED", "false");
            });
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage live = await client.GetAsync("/health/live");
        HttpResponseMessage ready = await client.GetAsync("/health/ready");
        HttpResponseMessage drain = await client.PostAsync("/admin/drain", content: null);
        HttpResponseMessage draining = await client.GetAsync("/health/ready");

        Assert.That(live.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(ready.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(drain.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        Assert.That(draining.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
    }

    [Test]
    public void SyntheticRoomTickHasZeroSteadyStateManagedAllocation()
    {
        SyntheticRoom room = new(64);
        for (int index = 0; index < 600; index++)
        {
            room.Tick();
        }

        const int measuredTicks = 3600;
        long[] durations = new long[measuredTicks];
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < measuredTicks; index++)
        {
            long started = Stopwatch.GetTimestamp();
            room.Tick();
            durations[index] = Stopwatch.GetTimestamp() - started;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Array.Sort(durations);

        double p99 = ToMilliseconds(durations[PercentileIndex(measuredTicks, 0.99)]);
        double p999 = ToMilliseconds(durations[PercentileIndex(measuredTicks, 0.999)]);
        int slowTicks = durations.Count(duration => ToMilliseconds(duration) > 16.67);

        TestContext.WriteLine(
            $"runtime={Environment.Version}; os={Environment.OSVersion}; processors={Environment.ProcessorCount}; " +
            $"warmup=600; ticks={measuredTicks}; p99_ms={p99:F4}; p999_ms={p999:F4}; slow_ticks={slowTicks}; allocated={allocated}");

        Assert.That(allocated, Is.Zero);
        Assert.That(p99, Is.LessThanOrEqualTo(16.67));
        Assert.That(p999, Is.LessThanOrEqualTo(20.0));
        Assert.That(slowTicks, Is.LessThanOrEqualTo((int)Math.Floor(measuredTicks * 0.001)));
    }

    [Test]
    public void RecordedInputStreamReplaysToTheExactCanonicalStateHash()
    {
        const int ticks = 3600;
        RecordedInput[] recording = new RecordedInput[ticks];
        Pcg32Random inputRandom = new(seed: 0xA11CE5EEDUL, sequence: 0x13UL);
        SyntheticRoom recorded = new(64);

        for (int tick = 0; tick < ticks; tick++)
        {
            RecordedInput input = new(
                (int)(inputRandom.NextUInt32() % 64),
                (int)(inputRandom.NextUInt32() % 2001) - 1000,
                (int)(inputRandom.NextUInt32() % 2001) - 1000);
            recording[tick] = input;
            recorded.ApplyInput(input.EntityIndex, input.MoveXMilli, input.MoveYMilli);
            recorded.Tick();
        }

        ulong recordedHash = recorded.ComputeStateHash();
        SyntheticRoom replayed = new(64);
        foreach (RecordedInput input in recording)
        {
            replayed.ApplyInput(input.EntityIndex, input.MoveXMilli, input.MoveYMilli);
            replayed.Tick();
        }

        ulong replayedHash = replayed.ComputeStateHash();
        Assert.That(replayedHash, Is.EqualTo(recordedHash));
        Assert.That(replayed.CreateSnapshot(ticks).StateHash, Is.EqualTo(recordedHash));

        replayed.ApplyInput(0, 1000, 0);
        Assert.That(replayed.ComputeStateHash(), Is.Not.EqualTo(recordedHash));
    }

    private static int PercentileIndex(int count, double percentile) =>
        Math.Clamp((int)Math.Ceiling(count * percentile) - 1, 0, count - 1);

    private static double ToMilliseconds(long timestampDelta) =>
        timestampDelta * 1000d / Stopwatch.Frequency;

    private readonly record struct RecordedInput(int EntityIndex, int MoveXMilli, int MoveYMilli);
}
