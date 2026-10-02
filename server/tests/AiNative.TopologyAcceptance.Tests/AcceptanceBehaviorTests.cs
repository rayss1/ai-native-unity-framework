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
            if (BinaryPrimitives.ReadUInt16LittleEndian(payload.Span) == (ushort)MessageId.InputCommand) Inputs.Enqueue(InputCommand.Parser.ParseFrom(payload.Span[2..]));
            return ValueTask.FromResult(new SendResult(SendStatus.Accepted, payload.Length));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
