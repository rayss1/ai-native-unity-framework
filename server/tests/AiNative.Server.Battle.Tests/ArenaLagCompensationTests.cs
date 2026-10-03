using AiNative.BattleHost;
using AiNative.Gameplay;
using AiNative.Server.Battle;
using NUnit.Framework;

namespace AiNative.Server.Battle.Tests;

public sealed class ArenaLagCompensationTests
{
    [Test]
    public void DelayedHitscanHitsTheCommittedTargetPosition()
    {
        var room = ReadyRoom(out uint shooter, out uint target);
        ulong aimedTick = room.Tick;
        MoveTarget(room, target, 12);
        Assert.That(room.SubmitInput(shooter, Shot(1000, aimedTick)), Is.True);
        room.TickOnce();
        room.TryGetPlayer(target, out var state);
        Assert.That(state.Health, Is.EqualTo(80), "The moving target was on the aim ray at the admitted client tick.");
    }

    [Test]
    public void Full250MillisecondWindowIsAcceptedButOlderInputIsRejected()
    {
        var room = ReadyRoom(out uint shooter, out _);
        Assert.That(room.SubmitInput(shooter, Shot(1000, room.Tick - 15)), Is.True);
        Assert.That(room.SubmitInput(shooter, Shot(1001, room.Tick - 16)), Is.False);
        Assert.That(room.SubmitInput(shooter, Shot(1002, room.Tick + 2)), Is.False);
        Assert.That(room.RejectedInputAgeCount, Is.EqualTo(2));
        room.TickOnce();
        room.TryGetPlayer(2, out var target);
        Assert.That(target.Health, Is.EqualTo(80), "The oldest admitted frame must still exist at execution.");
    }

    [Test]
    public void QueuedShotCannotExtendTheHistoryWindowOrConsumeAmmo()
    {
        var room = ReadyRoom(out uint shooter, out uint target);
        ulong aimTick = room.Tick;
        for (uint i = 0; i < 32; i++)
            Assert.That(room.SubmitInput(shooter, new ArenaInput(1000 + i, aimTick, 0, 0,
                i == 31 ? 90000 : 0, 0, i == 31 ? ArenaButtons.Fire : ArenaButtons.None,
                ArenaWeaponId.Machinegun)), Is.True);
        var events = new ArenaCombatEventRecord[ArenaRoom.MaximumEvents];
        room.DrainEvents(events);
        for (int i = 0; i < 32; i++) room.TickOnce();
        room.TryGetPlayer(target, out var state);
        room.TryGetPlayer(shooter, out var source);
        Assert.That(state.Health, Is.EqualTo(100));
        Assert.That(source.LastProcessedInputSequence, Is.EqualTo(1031));
        Assert.That(room.RejectedShotHistoryCount, Is.EqualTo(1));
        Assert.That(room.DrainEvents(events), Is.Zero, "A rejected stale shot must not emit Fire/Hit.");
    }

    [Test]
    public void RejoinedShooterCannotFireFromItsPreviousLifetime()
    {
        var room = ReadyRoom(out uint shooter, out uint target);
        ulong oldAim = room.Tick - 1;
        Assert.That(room.Leave(shooter), Is.True);
        Assert.That(room.TryJoin(out uint replacement), Is.True);
        Assert.That(replacement, Is.EqualTo(shooter));
        Assert.That(room.SubmitInput(shooter, Shot(1, oldAim)), Is.True);
        room.TickOnce();
        room.TryGetPlayer(target, out var state);
        Assert.That(state.Health, Is.EqualTo(100));
        Assert.That(room.RejectedShotHistoryCount, Is.EqualTo(1));
    }

    [Test]
    public void HistoricalSpawnProtectionRemainsAuthoritativeAfterItExpiresNow()
    {
        var room = new ArenaRoom(); room.TryJoin(out uint shooter); room.TryJoin(out uint target);
        for (int i = 0; i < 61; i++) room.TickOnce();
        Assert.That(room.SubmitInput(shooter, Shot(1, 59)), Is.True);
        room.TickOnce(); room.TryGetPlayer(target, out var state);
        Assert.That(state.Health, Is.EqualTo(100));
    }

    [Test]
    public void StateHashIncludesHistoryEvenWhenCurrentPlayersAreIdentical()
    {
        var left = ReadyRoom(out _, out uint target);
        var right = ReadyRoom(out _, out _);
        MoveTarget(left, target, 5);
        for (int i = 0; i < 5; i++) right.TickOnce();
        left.Leave(target); right.Leave(target); left.TryJoin(out _); right.TryJoin(out _);
        left.TryGetPlayer(target, out var a); right.TryGetPlayer(target, out var b);
        Assert.That(a, Is.EqualTo(b));
        Assert.That(left.ComputeStateHash(), Is.Not.EqualTo(right.ComputeStateHash()));
    }

    [Test]
    public void DelayedHitAndHistoricalStateReplayDeterministically()
    {
        var identity = new ArenaReplayIdentity("source", "fantasy", "protocol", "config");
        var capture = new ArenaReplayCapture(identity, "room", "allocation", "match", "node", "boot", 600, 1024);
        var game = new ArenaRoom(600);
        game.TryJoin(out uint shooter); capture.RecordJoin(game.Tick, shooter);
        game.TryJoin(out uint target); capture.RecordJoin(game.Tick, target);
        for (uint sequence = 1; sequence <= 180; sequence++)
        {
            var input = new ArenaInput(sequence, game.Tick + 1, sequence <= 120 ? 1000 : 0,
                0, 0, 0, ArenaButtons.None, ArenaWeaponId.Machinegun);
            Assert.That(game.SubmitInput(shooter, input), Is.True); capture.RecordInput(game.Tick, shooter, input);
            game.TickOnce(); capture.RecordTick(game.Tick, game.ComputeStateHash());
        }
        ulong aim = game.Tick;
        for (uint sequence = 1; sequence <= 12; sequence++)
        {
            var input = new ArenaInput(sequence, game.Tick + 1, 0, 1000, 0, 0, ArenaButtons.None, ArenaWeaponId.Machinegun);
            game.SubmitInput(target, input); capture.RecordInput(game.Tick, target, input);
            game.TickOnce(); capture.RecordTick(game.Tick, game.ComputeStateHash());
        }
        var shot = Shot(1000, aim); game.SubmitInput(shooter, shot); capture.RecordInput(game.Tick, shooter, shot);
        game.TickOnce(); capture.RecordTick(game.Tick, game.ComputeStateHash());
        game.TryGetPlayer(target, out var state); Assert.That(state.Health, Is.EqualTo(80));
        capture.Complete(game.Tick, game.ComputeStateHash());
        using var stream = new MemoryStream(); using var writer = new ArenaReplayWriter(stream, capture);
        writer.Drain(); stream.Position = 0;
        Assert.That(ArenaReplayVerifier.Verify(stream, identity).FinalHash, Is.EqualTo(game.ComputeStateHash()));
    }

    [Test]
    public void DelayedShotCannotHitAnEntityThatJoinedAfterItsAimTick()
    {
        var room = ReadyRoom(out uint shooter, out uint target);
        Assert.That(room.Leave(target), Is.True);
        for (int i = 0; i < 61; i++) room.TickOnce();
        ulong beforeJoin = room.Tick - 1;
        Assert.That(room.TryJoin(out uint replacement), Is.True);
        Assert.That(replacement, Is.EqualTo(target));
        // Joining and queued old inputs must not turn an absent historical
        // target into a valid hit on its replacement lifetime.
        for (uint sequence = 1000; sequence < 1032; sequence++)
            Assert.That(room.SubmitInput(shooter, new ArenaInput(sequence, beforeJoin, 0, 0,
                sequence == 1000 ? 90000 : 0, 0, ArenaButtons.Fire, ArenaWeaponId.Machinegun)), Is.True);
        for (int i = 0; i < 32; i++) room.TickOnce();
        room.TryGetPlayer(replacement, out var state);
        Assert.That(state.Health, Is.EqualTo(100));
    }

    [Test]
    public void HistoryAndDelayedHitscanAllocateNothingAfterWarmup()
    {
        var room = ReadyRoom(out uint shooter, out uint target);
        var events = new ArenaCombatEventRecord[ArenaRoom.MaximumEvents];
        for (uint sequence = 1000; sequence < 1100; sequence++)
        {
            room.SubmitInput(shooter, new ArenaInput(sequence, room.Tick - 5, 0, 0, 0, 0,
                ArenaButtons.Fire, ArenaWeaponId.Machinegun));
            room.TickOnce(); room.DrainEvents(events); room.ComputeStateHash();
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (uint sequence = 1100; sequence < 2100; sequence++)
        {
            room.SubmitInput(shooter, new ArenaInput(sequence, room.Tick - 5, 0, 0, 0, 0,
                ArenaButtons.Fire, ArenaWeaponId.Machinegun));
            room.TickOnce(); room.DrainEvents(events); room.ComputeStateHash();
        }
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }

    private static ArenaRoom ReadyRoom(out uint shooter, out uint target)
    {
        var room = new ArenaRoom(100000);
        Assert.That(room.TryJoin(out shooter), Is.True);
        Assert.That(room.TryJoin(out target), Is.True);
        for (uint sequence = 1; sequence <= 180; sequence++)
        {
            Assert.That(room.SubmitInput(shooter, new ArenaInput(sequence, room.Tick + 1,
                sequence <= 120 ? 1000 : 0, 0, 0, 0, ArenaButtons.None, ArenaWeaponId.Machinegun)), Is.True);
            room.TickOnce();
        }
        return room;
    }

    private static void MoveTarget(ArenaRoom room, uint target, uint ticks)
    {
        for (uint sequence = 1; sequence <= ticks; sequence++)
        {
            Assert.That(room.SubmitInput(target, new ArenaInput(sequence, room.Tick + 1,
                0, 1000, 0, 0, ArenaButtons.None, ArenaWeaponId.Machinegun)), Is.True);
            room.TickOnce();
        }
    }

    private static ArenaInput Shot(uint sequence, ulong tick) =>
        new(sequence, tick, 0, 0, 90000, 0, ArenaButtons.Fire, ArenaWeaponId.Machinegun);
}
