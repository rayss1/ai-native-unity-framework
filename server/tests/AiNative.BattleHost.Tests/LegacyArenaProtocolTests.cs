using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Reflection;
using AiNative.Protocol.V1;
using AiNative.Realtime;
using AiNative.Server.Fantasy;
using AiNative.Server.Protocol;
using Google.Protobuf;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
namespace AiNative.BattleHost.Tests;
public sealed class LegacyArenaProtocolTests
{
    [Test]
    public async Task SnapshotAcknowledgesConsumedBatchCommandRatherThanAcceptedTail()
    {
        await using var f = new Fixture(); f.Join(); f.Game.TickOnce(); f.Game.TickOnce();
        f.Wire.Push(MessageId.InputBatch, new InputBatch { Commands = { new InputCommand { Sequence = 1, RoomTick = 3, MoveXMilli = 1000 }, new InputCommand { Sequence = 2, RoomTick = 3 } } });
        f.Service.PumpInbound(2); f.Game.TickOnce(); f.Service.PublishSnapshots(3);
        Assert.That(f.Wire.Last<Snapshot>(MessageId.Snapshot).LastProcessedInputSequence, Is.EqualTo(1));
    }
    [Test]
    public async Task EventsDrainOnEveryTickEvenWithoutSnapshotCadence()
    {
        await using var f = new Fixture(); f.Join();
        f.Wire.Push(MessageId.InputCommand, new InputCommand { Sequence = 1, RoomTick = 1, WeaponId = 2 });
        f.Service.PumpInbound(0); f.Game.TickOnce(); f.Service.PublishSnapshots(1);
        Assert.That(f.Wire.Output.Any(m => m.Id == MessageId.ReliableEvent), Is.True);
        Assert.That(f.Game.DrainEvents(new ArenaCombatEventRecord[256]), Is.Zero);
    }
    [Test]
    public async Task LegacyFanoutKeepsRecipientAcknowledgementsAndRoomBodiesIsolatedAcrossTicks()
    {
        await using var f = new Fixture(BattleGameModeKind.Acceptance, 4, 2); f.Join();
        uint[] sequences = { 1, 128, 16384, uint.MaxValue };
        for (int index = 0; index < f.Wires.Length; index++)
            f.Wires[index].Push(MessageId.InputCommand, new InputCommand { Sequence = sequences[index], RoomTick = 1, MoveXMilli = (index + 1) * 100 });
        f.Service.PumpInbound(0); f.Rooms.TickAll(); f.Service.PublishSnapshots(3);
        for (int index = 0; index < f.Wires.Length; index++)
        {
            Snapshot expected = f.Rooms[index % 2].CreateSnapshot(3);
            expected.LastProcessedInputSequence = sequences[index];
            Assert.That(f.Wires[index].Last<Snapshot>(MessageId.Snapshot), Is.EqualTo(expected));
        }
        Assert.That(f.Rooms[0].ComputeStateHash(), Is.Not.EqualTo(f.Rooms[1].ComputeStateHash()));
        f.Wires[0].Push(MessageId.InputCommand, new InputCommand { Sequence = 2, RoomTick = 4, MoveYMilli = 500 });
        f.Service.PumpInbound(3); f.Rooms.TickAll(); f.Service.PublishSnapshots(6);
        sequences[0] = 2;
        for (int index = 0; index < f.Wires.Length; index++)
        {
            Snapshot expected = f.Rooms[index % 2].CreateSnapshot(6);
            expected.LastProcessedInputSequence = sequences[index];
            Assert.That(f.Wires[index].Last<Snapshot>(MessageId.Snapshot), Is.EqualTo(expected));
        }
    }
    sealed class Fixture : IAsyncDisposable
    {
        public readonly ArenaRoom Game = new(); public Wire Wire => Wires[0];
        public readonly Wire[] Wires; public readonly BattleRoomSet Rooms;
        public readonly RoomProtocolService Service;
        readonly BattleMetrics Metrics = new(); readonly IAsyncDisposable Gateway; readonly BattleReplayCapture Replay;
        public Fixture(BattleGameModeKind mode = BattleGameModeKind.Arena, int connections = 1, int roomCount = 1)
        {
            var config = new ConfigurationBuilder().Build(); var settings = new BattleHostCapacitySettings(roomCount);
            Rooms = new BattleRoomSet(settings); Wires = Enumerable.Range(0, connections).Select(_ => new Wire()).ToArray();
            Replay = new BattleReplayCapture(config, settings, Metrics);
            Type gatewayType = typeof(RoomProtocolService).GetConstructors().Single().GetParameters()[0].ParameterType;
            Gateway = (IAsyncDisposable)Activator.CreateInstance(gatewayType, 262144, settings.TotalBotCapacity, 1150)!;
            Service = (RoomProtocolService)Activator.CreateInstance(typeof(RoomProtocolService), Gateway, Rooms, Game, new BattleGameModeSettings(mode), Metrics, Replay, NullLogger<RoomProtocolService>.Instance)!;
            object accepted = gatewayType.GetField("_accepted", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Gateway)!;
            Type wireType = accepted.GetType().GetGenericArguments()[0];
            for (int index = 0; index < Wires.Length; index++)
            {
                object connection = Activator.CreateInstance(wireType, BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { (long)index + 1, 1u, Wires[index] }, null)!;
                accepted.GetType().GetMethod("Enqueue")!.Invoke(accepted, new[] { connection });
            }
        }
        public void Join()
        {
            for (int index = 0; index < Wires.Length; index++)
            {
                Wire wire = Wires[index];
                wire.Push(MessageId.LoginRequest, new LoginRequest { ProtocolMajor = 1 }); Service.PumpInbound(0);
                wire.Push(MessageId.JoinRoomRequest, new JoinRoomRequest { SessionId = wire.Last<LoginResponse>(MessageId.LoginResponse).SessionId, RequestedRoom = (uint)(index % Rooms.RoomCount) + 1 }); Service.PumpInbound(0);
            }
        }
        public async ValueTask DisposeAsync() { await Service.DisposeAsync(); await Replay.DisposeAsync(); await Gateway.DisposeAsync(); Metrics.Dispose(); }
    }
    sealed class Wire : IRealtimeTransport
    {
        readonly Queue<(byte[] Bytes, TransportChannel Channel)> _input = new(); public readonly List<(MessageId Id, byte[] Payload)> Output = new();
        public TransportState State => TransportState.Connected;
        public void Push(MessageId id, IMessage message) { var bytes = new byte[1200]; Assert.That(RealtimeProtocolCodec.TryEncode(id, message, bytes, out var channel, out int length), Is.True); _input.Enqueue((bytes[..length], channel)); }
        public bool TryReceive(Span<byte> destination, out ReceivedPacket packet) { if (!_input.TryDequeue(out var item)) { packet = default; return false; } item.Bytes.CopyTo(destination); packet = new ReceivedPacket(item.Channel, item.Bytes.Length, item.Bytes.Length, 1, 1); return true; }
        public ValueTask<SendResult> SendAsync(TransportChannel channel, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default) { Output.Add(((MessageId)BinaryPrimitives.ReadUInt16LittleEndian(bytes.Span), bytes.Span[2..].ToArray())); return ValueTask.FromResult(new SendResult(SendStatus.Accepted, bytes.Length)); }
        public T Last<T>(MessageId id) where T : IMessage<T>, new() => new MessageParser<T>(() => new T()).ParseFrom(Output.Last(m => m.Id == id).Payload);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
