using AiNative.BattleHost;
using AiNative.Gameplay;
using AiNative.Server.Battle;
using NUnit.Framework;
namespace AiNative.Server.Battle.Tests;
public sealed class ArenaReplayTests
{
    private static readonly ArenaReplayIdentity Identity = new("source", "fantasy", "protocol", "config");
    [Test]
    public void ConsumedRespawnInputsReplayWithEveryStateHashAndRejectOldFingerprint()
    {
        using var stream = new MemoryStream(); var game = new ArenaRoom(400);
        var capture = new ArenaReplayCapture(Identity, "room", "allocation", "match", "node", "boot", 400, 2000);
        game.TryJoin(out uint shooter); capture.RecordJoin(game.Tick, shooter);
        game.TryJoin(out uint target); capture.RecordJoin(game.Tick, target);
        var events = new ArenaCombatEventRecord[256]; int respawns = 0;
        for (uint sequence = 1; sequence <= 400; sequence++)
        {
            Submit(shooter, new ArenaInput(sequence, game.Tick + 1, 0, 0, sequence == 1 ? 90000 : 0, 0, ArenaButtons.Fire, ArenaWeaponId.Machinegun));
            Submit(target, new ArenaInput(sequence, game.Tick + 1, 0, 0, 0, 0, ArenaButtons.None, ArenaWeaponId.None));
            game.TickOnce(); capture.RecordTick(game.Tick, game.ComputeStateHash());
            game.TryGetPlayer(target, out var state);
            Assert.That(state.LastProcessedInputSequence, Is.EqualTo(sequence));
            int count = game.DrainEvents(events);
            respawns += events.Take(count).Count(e => e.Kind == ArenaCombatEventKind.Respawn);
        }
        Assert.That(respawns, Is.GreaterThan(0));
        capture.Complete(game.Tick, game.ComputeStateHash());
        using var writer = new ArenaReplayWriter(stream, capture); writer.Drain(); stream.Position = 0;
        Assert.That(ArenaReplayVerifier.Verify(stream, Identity).FinalHash, Is.EqualTo(game.ComputeStateHash()));
        byte[] bytes = stream.ToArray();
        byte[] current = System.Text.Encoding.UTF8.GetBytes(ArenaReplayVerifier.GameplayFingerprint);
        int offset = bytes.AsSpan().IndexOf(current);
        Assert.That(offset, Is.GreaterThan(0));
        System.Text.Encoding.UTF8.GetBytes("ce3034f62374b59b906abfb31774454f177b126aca2a5c27b9dfb715ba8a8a9f").CopyTo(bytes, offset);
        Assert.Throws<InvalidDataException>(() => ArenaReplayVerifier.Verify(new MemoryStream(bytes), Identity));
        void Submit(uint entity, ArenaInput input)
        { Assert.That(game.SubmitInput(entity, input), Is.True); capture.RecordInput(game.Tick, entity, input); }
    }
    [Test]
    public void CapturedLifecycleInputsAndEveryTickHashReplayRealArena()
    {
        using var stream = new MemoryStream(); var game = new ArenaRoom(600);
        var capture = new ArenaReplayCapture(Identity, "room", "allocation", "match", "node", "boot", 600, 128);
        game.TryJoin(out uint id); capture.RecordJoin(game.Tick, id);
        game.TryJoin(out uint other); capture.RecordJoin(game.Tick, other);
        for (uint sequence = 1; sequence <= 10; sequence++)
        {
            var input = new ArenaInput(sequence, game.Tick + 1, 1000, 0, 100, 50, sequence == 1 ? ArenaButtons.Jump : ArenaButtons.None, ArenaWeaponId.Machinegun);
            Assert.That(game.SubmitInput(id, input), Is.True); capture.RecordInput(game.Tick, id, input);
            game.TickOnce(); capture.RecordTick(game.Tick, game.ComputeStateHash());
        }
        game.ClearPendingInputs(id); capture.RecordClear(game.Tick, id);
        game.TickOnce(); capture.RecordTick(game.Tick, game.ComputeStateHash()); capture.Complete(game.Tick, game.ComputeStateHash());
        using var writer = new ArenaReplayWriter(stream, capture); writer.Drain(); stream.Position = 0;
        var verified = ArenaReplayVerifier.Verify(stream, Identity);
        Assert.That(verified.FinalTick, Is.EqualTo(11)); Assert.That(verified.FinalHash, Is.EqualTo(game.ComputeStateHash()));
        byte[] tampered = stream.ToArray();
        using var reader = new BinaryReader(new MemoryStream(tampered)); reader.ReadUInt32(); reader.ReadUInt16();
        for (int field = 0; field < 11; field++) reader.ReadBytes(reader.ReadUInt16()); reader.ReadInt32();
        int inputMoveOffset = checked((int)reader.BaseStream.Position + 26 + 25); // two joins then input envelope
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(tampered.AsSpan(inputMoveOffset), -1000);
        Assert.Throws<InvalidDataException>(() => ArenaReplayVerifier.Verify(new MemoryStream(tampered), Identity));
    }
    [Test]
    public void OverflowPermanentlyStopsCaptureAndCannotVerify()
    {
        using var stream = new MemoryStream(); var game = new ArenaRoom(600);
        var capture = new ArenaReplayCapture(Identity, "room", "alloc", "match", "node", "boot", 600, 1);
        game.TickOnce(); capture.RecordTick(game.Tick, game.ComputeStateHash());
        game.TickOnce(); capture.RecordTick(game.Tick, game.ComputeStateHash()); capture.Complete(game.Tick, game.ComputeStateHash());
        Assert.That(capture.Status, Is.EqualTo(ArenaReplayStatus.Overflow));
        using var writer = new ArenaReplayWriter(stream, capture); writer.Drain(); stream.Position = 0;
        Assert.Throws<InvalidDataException>(() => ArenaReplayVerifier.Verify(stream, Identity));
    }
    [Test]
    public void TamperTruncationTrailingBytesAndIdentityDriftFailClosed()
    {
        using var stream = new MemoryStream(); var game = new ArenaRoom(600);
        var capture = new ArenaReplayCapture(Identity, "room", "alloc", "match", "node", "boot", 600, 4);
        game.TickOnce(); capture.RecordTick(game.Tick, game.ComputeStateHash()); capture.Complete(game.Tick, game.ComputeStateHash());
        using var writer = new ArenaReplayWriter(stream, capture); writer.Drain(); byte[] bytes = stream.ToArray();
        Assert.Throws<InvalidDataException>(() => ArenaReplayVerifier.Verify(new MemoryStream(bytes), Identity with { Source = "changed" }));
        Assert.Throws<InvalidDataException>(() => ArenaReplayVerifier.Verify(new MemoryStream(bytes[..^1]), Identity));
        Assert.Throws<InvalidDataException>(() => ArenaReplayVerifier.Verify(new MemoryStream(bytes.Concat(new byte[] { 0 }).ToArray()), Identity));
        bytes[^20] ^= 1; Assert.Throws<InvalidDataException>(() => ArenaReplayVerifier.Verify(new MemoryStream(bytes), Identity));
    }
    [Test]
    public void WarmedCaptureWithOffTickDrainAllocatesZeroOnProducerThread()
    {
        var game = new ArenaRoom(100000); var capture = new ArenaReplayCapture(Identity, "room", "alloc", "match", "node", "boot", 100000, 2000);
        for (int i = 0; i < 100; i++) { game.TickOnce(); capture.RecordTick(game.Tick, game.ComputeStateHash()); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) { game.TickOnce(); capture.RecordTick(game.Tick, game.ComputeStateHash()); }
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }
    [Test]
    public void WriterRejectsHeaderBeforeExceedingExplicitByteBudget()
    {
        using var stream = new MemoryStream();
        var capture = new ArenaReplayCapture(Identity, "room", "alloc", "match", "node", "boot", 600, 4);
        Assert.Throws<InvalidDataException>(() => new ArenaReplayWriter(stream, capture, maximumBytes: 100));
        Assert.That(stream.Length, Is.LessThanOrEqualTo(100));
    }
    [Test]
    public void FullCaptureCannotWriteBeyondReservedBytes()
    {
        var capture = new ArenaReplayCapture(Identity, "room", "alloc", "match", "node", "boot", 600, 4);
        using var header = new MemoryStream(); using (var headerWriter = new ArenaReplayWriter(header, capture)) { }
        long limit = header.Length + 17 + 26;
        var game = new AiNative.BattleHost.ArenaRoom(600);
        game.TickOnce(); capture.RecordTick(game.Tick, game.ComputeStateHash());
        game.TickOnce(); capture.RecordTick(game.Tick, game.ComputeStateHash()); capture.Complete(game.Tick, game.ComputeStateHash());
        using var stream = new MemoryStream(); using var writer = new ArenaReplayWriter(stream, capture, limit);
        Assert.Throws<InvalidDataException>(() => writer.Drain());
        Assert.That(stream.Length, Is.LessThanOrEqualTo(limit)); Assert.That(writer.IsFinalized, Is.False);
    }
}
