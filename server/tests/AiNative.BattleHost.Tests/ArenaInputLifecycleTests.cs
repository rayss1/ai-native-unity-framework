using System.Reflection;
using AiNative.Gameplay;
using NUnit.Framework;

namespace AiNative.BattleHost.Tests;

public sealed class ArenaInputLifecycleTests
{
    [Test]
    public void RespawnConsumesExactlyOneInputWithoutApplyingItsActions()
    {
        var room = Create(); var baseline = Create();
        Kill(room, 1); Kill(baseline, 1);
        Assert.That(room.SubmitInput(1, Input(1, room)), Is.True);
        Assert.That(room.SubmitInput(1, Input(2, room)), Is.True);
        room.TickOnce(); baseline.TickOnce();
        room.TryGetPlayer(1, out var actual); baseline.TryGetPlayer(1, out var expected);
        Assert.That(actual.LastProcessedInputSequence, Is.EqualTo(1));
        expected.LastProcessedInputSequence = 1;
        Assert.That(actual, Is.EqualTo(expected), "Dead input must not move, aim, switch weapon or fire after respawn.");
        var events = new ArenaCombatEventRecord[256];
        Assert.That(room.DrainEvents(events), Is.EqualTo(1));
        Assert.That(events[0].Kind, Is.EqualTo(ArenaCombatEventKind.Respawn));
        room.TickOnce(); room.TryGetPlayer(1, out actual);
        Assert.That(actual.LastProcessedInputSequence, Is.EqualTo(2));
    }

    [Test]
    public void RepeatedRespawnsDoNotAccumulateOneInputPerDeath()
    {
        var room = Create();
        for (uint sequence = 1; sequence <= 64; sequence++)
        {
            Kill(room, 1);
            Assert.That(room.SubmitInput(1, Input(sequence, room)), Is.True);
            room.TickOnce(); room.TryGetPlayer(1, out var state);
            Assert.That(state.LastProcessedInputSequence, Is.EqualTo(sequence));
        }
    }

    [Test]
    public void EmptyRespawnDoesNotInventAnAcknowledgement()
    {
        var room = Create();
        Assert.That(room.SubmitInput(1, Input(7, room)), Is.True);
        room.TickOnce(); Kill(room, 1); room.TickOnce();
        room.TryGetPlayer(1, out var state);
        Assert.That(state.LastProcessedInputSequence, Is.EqualTo(7));
    }

    [Test]
    public void DeathBeforePlayersTurnConsumesWithoutFiringOrRespawningEarly()
    {
        var room = Create();
        for (int i = 0; i <= ArenaRoom.RespawnProtectionTicks; i++) room.TickOnce();
        var players = Field<ArenaPlayerState[]>(room, "_players");
        players[1].Health = 20; players[1].Armor = 0;
        Assert.That(room.SubmitInput(1, new ArenaInput(1, room.Tick + 1, 0, 0, 90000, 0, ArenaButtons.Fire, ArenaWeaponId.Machinegun)), Is.True);
        Assert.That(room.SubmitInput(2, Input(1, room)), Is.True);
        room.TickOnce(); room.TryGetPlayer(2, out var dead);
        Assert.That(dead.Alive, Is.False);
        Assert.That(dead.LastProcessedInputSequence, Is.EqualTo(1));
        var events = new ArenaCombatEventRecord[256]; int count = room.DrainEvents(events);
        Assert.That(events.Take(count).Any(e => e.Kind == ArenaCombatEventKind.Fire && e.SourceEntityId == 2), Is.False);
        Assert.That(events.Take(count).Any(e => e.Kind == ArenaCombatEventKind.Respawn), Is.False);
        Assert.That(room.SubmitInput(2, Input(2, room)), Is.True);
        room.TickOnce(); room.TryGetPlayer(2, out var respawned);
        Assert.That(respawned.Alive, Is.True);
        Assert.That(respawned.LastProcessedInputSequence, Is.EqualTo(2));
    }

    [Test]
    public void FinishedRoomDoesNotConsumePendingInputs()
    {
        var room = Create(1);
        Assert.That(room.SubmitInput(1, Input(1, room)), Is.True);
        Assert.That(room.SubmitInput(1, Input(2, room)), Is.True);
        room.TickOnce(); Assert.That(room.Phase, Is.EqualTo(ArenaMatchPhase.Finished));
        Kill(room, 1); room.TickOnce(); room.TryGetPlayer(1, out var state);
        Assert.That(state.LastProcessedInputSequence, Is.EqualTo(1));
        Assert.That(state.Alive, Is.False);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void DeathAfterPlayersTurnDoesNotConsumeTwice(bool projectile)
    {
        var room = Create();
        for (int i = 0; i <= ArenaRoom.RespawnProtectionTicks; i++) room.TickOnce();
        uint shooter = projectile ? 1U : 2U, target = projectile ? 2U : 1U;
        var players = Field<ArenaPlayerState[]>(room, "_players");
        players[target - 1].Health = 20; players[target - 1].Armor = 0;
        room.SubmitInput(shooter, new ArenaInput(1, room.Tick + 1, 0, 0, projectile ? 90000 : -90000, 0,
            ArenaButtons.Fire, projectile ? ArenaWeaponId.Rocket : ArenaWeaponId.Machinegun));
        uint sequence = 0;
        Assert.That(room.SubmitInput(target, Idle(++sequence)), Is.True);
        bool killed = false;
        for (uint ticks = 1; ticks <= 150; ticks++)
        {
            Assert.That(room.SubmitInput(target, Idle(++sequence)), Is.True);
            room.TickOnce(); room.TryGetPlayer(target, out var state);
            Assert.That(state.LastProcessedInputSequence, Is.EqualTo(ticks));
            if (state.Alive) continue;
            killed = true;
            room.TickOnce(); room.TryGetPlayer(target, out state);
            Assert.That(state.Alive, Is.True);
            Assert.That(state.LastProcessedInputSequence, Is.EqualTo(ticks + 1));
            break;
        }
        Assert.That(killed, Is.True, "Fixture must exercise an actual lethal hit.");
        ArenaInput Idle(uint id) => new(id, room.Tick + 1, 0, 0, 0, 0, ArenaButtons.None, ArenaWeaponId.None);
    }

    [Test]
    public void RespawnInputConsumptionAllocatesNothingOnTickThread()
    {
        var room = Create();
        var players = Field<ArenaPlayerState[]>(room, "_players");
        var deathTicks = Field<ulong[]>(room, "_deathTicks");
        var events = new ArenaCombatEventRecord[256];
        for (uint sequence = 1; sequence <= 100; sequence++) Step(sequence);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (uint sequence = 101; sequence <= 1100; sequence++) Step(sequence);
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
        void Step(uint sequence)
        {
            players[0].Alive = false; players[0].Health = 0; deathTicks[0] = room.Tick;
            if (!room.SubmitInput(1, Input(sequence, room))) throw new InvalidOperationException("input rejected");
            room.TickOnce(); room.DrainEvents(events);
        }
    }

    static ArenaRoom Create(int length = 36000)
    { var room = new ArenaRoom(length); room.TryJoin(out _); room.TryJoin(out _); return room; }
    static ArenaInput Input(uint sequence, ArenaRoom room) => new(sequence, room.Tick + 1, 1000, 1000, 90000, 10000, ArenaButtons.Fire | ArenaButtons.Jump, ArenaWeaponId.Rocket);
    // Establish an exact death boundary without depending on random combat or wall-clock timing.
    static void Kill(ArenaRoom room, uint entity)
    {
        var players = Field<ArenaPlayerState[]>(room, "_players");
        players[entity - 1].Alive = false; players[entity - 1].Health = 0;
        Field<ulong[]>(room, "_deathTicks")[entity - 1] = room.Tick;
    }
    static T Field<T>(ArenaRoom room, string name) => (T)typeof(ArenaRoom).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(room)!;
}
