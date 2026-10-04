using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Reflection;
using AiNative.Protocol.V1;
using AiNative.Realtime;
using Google.Protobuf;
using NUnit.Framework;

namespace AiNative.Topology.ToolTests;

public class AcceptanceBehaviorTests
{
    static Type Feature(string name)
    {
        Type? type = Assembly.Load("AiNative.TopologyAcceptance").GetType(name);
        Assert.That(type, Is.Not.Null, $"Missing acceptance behavior: {name}");
        return type!;
    }

    [Test]
    public void GateAndDeadlineCanTargetTenMinuteDeployment()
    {
        Func<string, string?> env = key => key switch
        {
            "AINATIVE_ACCEPTANCE_GATE_ADDRESS" => "172.28.0.11:23001",
            "AINATIVE_ACCEPTANCE_DEADLINE_SECONDS" => "780",
            _ => null
        };
        dynamic options = Feature("AcceptanceOptions").GetMethod("Load")!.Invoke(null, [env])!;
        Assert.That((string)options.GateAddress, Is.EqualTo("172.28.0.11:23001"));
        Assert.That((TimeSpan)options.Deadline, Is.EqualTo(TimeSpan.FromSeconds(780)));
    }

    [TestCase("0")]
    [TestCase("86401")]
    [TestCase("nan")]
    public void InvalidDeadlineIsRejectedBeforeStartingRuntime(string value)
    {
        Func<string, string?> env = key => key == "AINATIVE_ACCEPTANCE_DEADLINE_SECONDS" ? value : null;
        var error = Assert.Throws<TargetInvocationException>(() => Feature("AcceptanceOptions").GetMethod("Load")!.Invoke(null, [env]));
        Assert.That(error!.InnerException, Is.TypeOf<ArgumentException>());
    }

    static dynamic Start(FakeTransport transport, CancellationToken ct = default) => Activator.CreateInstance(Feature("SnapshotInputLoop"), transport, ct)!;

    [Test]
    public async Task InputsFollowEachConnectionsAuthoritativeTickAndSequence()
    {
        var transport = new FakeTransport();
        transport.Enqueue(new Snapshot { RoomTick = 100, LastProcessedInputSequence = 41 });
        dynamic loop = Start(transport);
        try
        {
            await Until(() => transport.Inputs.Count > 0);
            Assert.That(transport.Inputs.First().RoomTick, Is.EqualTo(102));
            Assert.That(transport.Inputs.First().Sequence, Is.EqualTo(42));
            transport.Enqueue(new Snapshot { RoomTick = 200, LastProcessedInputSequence = 80 });
            await Until(() => transport.Inputs.Any(x => x.RoomTick == 202));
            Assert.That(transport.Inputs.First(x => x.RoomTick == 202).Sequence, Is.EqualTo(81));
        }
        finally { await (ValueTask)loop.DisposeAsync(); }
        int stopped = transport.Inputs.Count;
        await Task.Delay(150);
        Assert.That(transport.Inputs.Count, Is.EqualTo(stopped));
    }

    [Test]
    public async Task FinishedSnapshotEndsInputLoopAndIsStillAvailableToConsumer()
    {
        var transport = new FakeTransport();
        transport.Enqueue(new Snapshot { RoomTick = 36000, MatchPhase = ArenaMatchPhase.ArenaMatchFinished });
        dynamic loop = Start(transport);
        try
        {
            Snapshot finished = await (Task<Snapshot>)loop.ReadAsync(TimeSpan.FromSeconds(1));
            Assert.That(finished.RoomTick, Is.EqualTo(36000));
            await ((Task)loop.Completion).WaitAsync(TimeSpan.FromSeconds(1));
            Assert.That(loop.LastSnapshot, Is.Not.Null);
            Assert.That((ulong)loop.LastSnapshot.RoomTick, Is.EqualTo(36000));
            Assert.That(transport.Inputs, Is.Empty);
        }
        finally { await (ValueTask)loop.DisposeAsync(); }
    }

    [Test]
    public async Task CancellationStopsEvenAnAlwaysReadableTransportWithinBudget()
    {
        var transport = new FakeTransport { AlwaysReadable = true };
        using var stop = new CancellationTokenSource();
        dynamic loop = Start(transport, stop.Token);
        stop.CancelAfter(50);
        await ((Task)loop.Completion).WaitAsync(TimeSpan.FromSeconds(1));
        await (ValueTask)loop.DisposeAsync();
    }

    [TestCase(SendStatus.Closed)]
    [TestCase(SendStatus.Faulted)]
    public async Task OrdinaryDisposeStillPropagatesPeerLossSendFailure(SendStatus status)
    {
        dynamic loop = await FailedLoop(new FakeTransport { SendOutcome = status });
        var failure = Assert.CatchAsync<InvalidOperationException>(async () => await (ValueTask)loop.DisposeAsync());
        Assert.That(failure!.Message, Is.EqualTo("continuous-input-send"));
    }

    [TestCase(SendStatus.Closed)]
    [TestCase(SendStatus.Faulted)]
    public async Task VerifiedPeerLossCleanupPreservesExpectedRoomLostTermination(SendStatus status)
    {
        dynamic loop = await FailedLoop(new FakeTransport { SendOutcome = status });
        var failure = Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var resource = (IAsyncDisposable)loop;
            await StopAfterVerifiedPeerLoss(loop);
            throw new InvalidOperationException("expected-room-lost-after-battle-restart");
        });
        Assert.That(failure!.Message, Is.EqualTo("expected-room-lost-after-battle-restart"),
            "A verified peer-loss send failure must not replace the explicit expected termination during await-using cleanup.");
        Assert.That(((Task)loop.Completion).IsFaulted, Is.True, "Cleanup must still observe the original pump failure.");
    }

    [TestCase(SendStatus.WouldBlock)]
    [TestCase(SendStatus.PayloadTooLarge)]
    [TestCase(SendStatus.DroppedByPolicy)]
    public async Task VerifiedPeerLossCleanupDoesNotSuppressOtherSendRejections(SendStatus status)
    {
        dynamic loop = await FailedLoop(new FakeTransport { SendOutcome = status });
        var failure = Assert.ThrowsAsync<InvalidOperationException>(async () => await StopAfterVerifiedPeerLoss(loop));
        Assert.That(failure!.Message, Is.EqualTo("continuous-input-send"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task VerifiedPeerLossCleanupDoesNotSuppressOtherExceptionsEvenWithMatchingMessage(bool matchingMessage)
    {
        Exception expected = matchingMessage ? new InvalidOperationException("continuous-input-send") : new IOException("transport-bug");
        dynamic loop = await FailedLoop(new FakeTransport { SendFailure = expected });
        Exception? observed = null;
        try { await StopAfterVerifiedPeerLoss(loop); }
        catch (Exception error) { observed = error; }
        Assert.That(observed, Is.SameAs(expected));
    }

    static async Task<object> FailedLoop(FakeTransport transport)
    {
        transport.Enqueue(new Snapshot { RoomTick = 100 });
        dynamic loop = Start(transport);
        await Until(() => ((Task)loop.Completion).IsCompleted);
        Assert.That(((Task)loop.Completion).IsFaulted, Is.True);
        return loop;
    }

    static async Task StopAfterVerifiedPeerLoss(object loop)
    {
        MethodInfo? cleanup = loop.GetType().GetMethod("StopAfterVerifiedPeerLossAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        // Exercise the original strict cleanup when the dedicated verified-loss path is absent.
        // This keeps the regression observable as the original exception-masking behavior.
        await (ValueTask)(cleanup?.Invoke(loop, null) ?? ((IAsyncDisposable)loop).DisposeAsync());
    }

    [Test]
    public async Task SettlementPollingQueriesImmediatelyAndDelaysOnlyUnconfirmedResults()
    {
        int calls = 0;
        Func<CancellationToken, Task<AiNative.Protocol.Backend.V1.PlayerProfile?>> probe = _ =>
            Task.FromResult<AiNative.Protocol.Backend.V1.PlayerProfile?>(++calls == 1 ? null : new() { Played = 1 });
        var task = (Task<AiNative.Protocol.Backend.V1.PlayerProfile>)Feature("AcceptanceSettlementPolling")
            .GetMethod("WaitAsync")!.Invoke(null, [probe, CancellationToken.None])!;
        int immediateCalls = calls;
        var profile = await task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(immediateCalls, Is.EqualTo(1), "The first read must run immediately; an unconfirmed reply must yield before retry.");
        Assert.That(calls, Is.EqualTo(2)); Assert.That(profile.Played, Is.EqualTo(1));
    }

    [Test]
    public async Task SettlementPollingSurvivesUnavailablePlayerThenRequiresExactlyOne()
    {
        int calls = 0;
        Func<CancellationToken, Task<AiNative.Protocol.Backend.V1.PlayerProfile?>> probe = _ => Task.FromResult(++calls < 3 ? null : new AiNative.Protocol.Backend.V1.PlayerProfile { Played = 1 });
        Type type = Feature("AcceptanceSettlementPolling");
        var task = (Task<AiNative.Protocol.Backend.V1.PlayerProfile>)type.GetMethod("WaitAsync")!.Invoke(null, [probe, CancellationToken.None])!;
        Assert.That((await task.WaitAsync(TimeSpan.FromSeconds(2))).Played, Is.EqualTo(1));
        Assert.That(calls, Is.EqualTo(3));
    }

    static async Task Until(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(2000);
        while (!predicate()) await Task.Delay(10, deadline.Token);
    }

    sealed class FakeTransport : IRealtimeTransport
    {
        readonly ConcurrentQueue<byte[]> packets = new();
        public ConcurrentQueue<InputCommand> Inputs { get; } = new();
        public bool AlwaysReadable;
        public SendStatus SendOutcome = SendStatus.Accepted;
        public Exception? SendFailure;
        public TransportState State => TransportState.Connected;
        public void Enqueue(Snapshot snapshot)
        {
            byte[] bytes = new byte[2 + snapshot.CalculateSize()];
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)MessageId.Snapshot);
            snapshot.WriteTo(bytes.AsSpan(2));
            packets.Enqueue(bytes);
        }
        public bool TryReceive(Span<byte> destination, out ReceivedPacket packet)
        {
            if (packets.TryDequeue(out var bytes))
            {
                bytes.CopyTo(destination); packet = new(default, bytes.Length, bytes.Length, 0, 0); return true;
            }
            packet = new(default, 0, 0, 0, 0); return AlwaysReadable;
        }
        public ValueTask<SendResult> SendAsync(TransportChannel channel, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (SendFailure is not null) throw SendFailure;
            if (BinaryPrimitives.ReadUInt16LittleEndian(payload.Span) == (ushort)MessageId.InputCommand) Inputs.Enqueue(InputCommand.Parser.ParseFrom(payload.Span[2..]));
            return ValueTask.FromResult(new SendResult(SendOutcome, SendOutcome == SendStatus.Accepted ? payload.Length : 0));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
