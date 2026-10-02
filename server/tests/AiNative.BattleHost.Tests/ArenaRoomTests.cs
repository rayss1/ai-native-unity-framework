using AiNative.Gameplay;
using NUnit.Framework;

namespace AiNative.BattleHost.Tests;

public sealed class ArenaRoomTests
{
    [Test]
    public void RoomStartsAtTwoPlayersAndCapsAtEight()
    {
        ArenaRoom room = new();

        Assert.That(room.Phase, Is.EqualTo(ArenaMatchPhase.Waiting));
        Assert.That(room.TryJoin(out uint first), Is.True);
        Assert.That(room.Phase, Is.EqualTo(ArenaMatchPhase.Waiting));
        Assert.That(room.TryJoin(out uint second), Is.True);
        Assert.That(first, Is.EqualTo(1));
        Assert.That(second, Is.EqualTo(2));
        Assert.That(room.Phase, Is.EqualTo(ArenaMatchPhase.Active));

        for (int index = 2; index < ArenaRoom.MaxPlayers; index++)
        {
            Assert.That(room.TryJoin(out uint entityId), Is.True);
            Assert.That(entityId, Is.EqualTo((uint)index + 1));
        }

        Assert.That(room.TryJoin(out _), Is.False);
        Assert.That(room.ConnectedCount, Is.EqualTo(8));
    }

    [Test]
    public void InputMovesPlayerAndAcknowledgesSequence()
    {
        ArenaRoom room = new();
        Assert.That(room.TryJoin(out uint entityId), Is.True);
        Assert.That(room.TryJoin(out _), Is.True);

        ArenaInput input = new(
            sequence: 1,
            clientTick: 1,
            moveXMilli: 1000,
            moveZMilli: 0,
            lookYawMilli: 0,
            lookPitchMilli: 0,
            buttons: ArenaButtons.None,
            weapon: ArenaWeaponId.Machinegun);
        Assert.That(room.SubmitInput(entityId, input), Is.True);
        room.TickOnce();

        Assert.That(room.TryGetPlayer(entityId, out ArenaPlayerState state), Is.True);
        Assert.That(state.LastProcessedInputSequence, Is.EqualTo(1));
        Assert.That(state.PositionXMillimetres, Is.Not.Zero);
        Assert.That(room.SubmitInput(entityId, input), Is.False);
    }

    [Test]
    public void MachinegunHitProducesDamageAndKillScore()
    {
        ArenaRoom room = new();
        Assert.That(room.TryJoin(out uint shooter), Is.True);
        Assert.That(room.TryJoin(out uint target), Is.True);

        for (uint sequence = 1; sequence <= ArenaRoom.RespawnProtectionTicks + 1; sequence++)
        {
            room.SubmitInput(shooter, new ArenaInput(
                sequence, sequence, 0, 0, 0, 0, ArenaButtons.None, ArenaWeaponId.Machinegun));
            room.SubmitInput(target, new ArenaInput(
                sequence, sequence, 0, 0, 0, 0, ArenaButtons.None, ArenaWeaponId.Machinegun));
            room.TickOnce();
        }

        room.SubmitInput(shooter, new ArenaInput(
            100, room.Tick + 1, 0, 0, 90000, 0, ArenaButtons.Fire, ArenaWeaponId.Machinegun));
        room.TickOnce();

        Assert.That(room.TryGetPlayer(target, out ArenaPlayerState targetState), Is.True);
        Assert.That(targetState.Health, Is.EqualTo(80));
        Assert.That(targetState.Alive, Is.True);
    }

    [Test]
    public void DeadPlayerRespawnsOnNextTickWithHealthAndArmorReset()
    {
        ArenaRoom room = new();
        Assert.That(room.TryJoin(out uint shooter), Is.True);
        Assert.That(room.TryJoin(out uint target), Is.True);

        for (uint sequence = 1; sequence <= ArenaRoom.RespawnProtectionTicks + 1; sequence++)
        {
            room.SubmitInput(shooter, new ArenaInput(
                sequence, sequence, 0, 0, 0, 0, ArenaButtons.None, ArenaWeaponId.Machinegun));
            room.SubmitInput(target, new ArenaInput(
                sequence, sequence, 0, 0, 0, 0, ArenaButtons.None, ArenaWeaponId.Machinegun));
            room.TickOnce();
        }

        for (int shot = 0; shot < 5; shot++)
        {
            room.SubmitInput(shooter, new ArenaInput(
                (uint)(100 + shot),
                room.Tick + 1,
                0,
                0,
                shot == 0 ? 90000 : 0,
                0,
                ArenaButtons.Fire,
                ArenaWeaponId.Machinegun));
            room.TickOnce();
            for (int wait = 0; wait < 6; wait++) room.TickOnce();
        }

        Assert.That(room.TryGetPlayer(target, out ArenaPlayerState respawned), Is.True);
        Assert.That(respawned.Alive, Is.True);
        Assert.That(respawned.Health, Is.EqualTo(ArenaCombatRules.MaximumHealth));
        Assert.That(respawned.Armor, Is.Zero);
    }
    [Test]
    public void IdleTicksDoNotInventAcknowledgements()
    {
        ArenaRoom room = new(); room.TryJoin(out uint id);
        for (int i = 0; i < 3; i++) room.TickOnce();
        room.TryGetPlayer(id, out var idle);
        Assert.That(idle.LastProcessedInputSequence, Is.Zero);
        Assert.That(room.SubmitInput(id, new ArenaInput(1, room.Tick + 1, 0, 0, 0, 0, ArenaButtons.None, ArenaWeaponId.None)), Is.True);
        room.TickOnce(); room.TickOnce(); room.TryGetPlayer(id, out var after);
        Assert.That(after.LastProcessedInputSequence, Is.EqualTo(1));
    }

    [Test]
    public void OrderedInputsAreConsumedWithoutOverwriteAndMaximumSequenceIsOccupied()
    {
        ArenaRoom room = new(); room.TryJoin(out uint id);
        Assert.That(room.SubmitInput(id, new ArenaInput(1, 1, 1000, 0, 0, 0, ArenaButtons.None, ArenaWeaponId.None)), Is.True);
        Assert.That(room.SubmitInput(id, new ArenaInput(uint.MaxValue, 1, 0, 0, 0, 0, ArenaButtons.None, ArenaWeaponId.None)), Is.True);
        room.TickOnce(); room.TryGetPlayer(id, out var first);
        Assert.That(first.LastProcessedInputSequence, Is.EqualTo(1));
        Assert.That(first.PositionXMillimetres, Is.EqualTo(-47984));
        room.TickOnce(); Assert.That(room.TryGetPlayer(id, out var last), Is.True);
        Assert.That(last.LastProcessedInputSequence, Is.EqualTo(uint.MaxValue));
    }

    [Test]
    public void MalformedEntityAndLookAreRejectedWithoutMutation()
    {
        ArenaRoom room = new(); room.TryJoin(out uint id);
        var input = new ArenaInput(1, 1, 0, 0, int.MaxValue, 0, ArenaButtons.None, ArenaWeaponId.None);
        Assert.That(room.SubmitInput(uint.MaxValue, input), Is.False);
        Assert.That(room.SubmitInput(id, input), Is.False);
        Assert.DoesNotThrow(room.TickOnce);
    }

    [Test]
    public void WaitingLifetimeDoesNotConsumeMatchTimeAndActiveDoesNotRestart()
    {
        ArenaRoom room = new();
        for (int i = 0; i < ArenaRoom.MatchLengthTicks + 1; i++) room.TickOnce();
        Assert.That(room.Phase, Is.EqualTo(ArenaMatchPhase.Waiting));
        room.TryJoin(out uint id); room.TryJoin(out _);
        Assert.That(room.RemainingTicks, Is.EqualTo(ArenaRoom.MatchLengthTicks));
        room.TickOnce(); room.Leave(id);
        Assert.That(room.Phase, Is.EqualTo(ArenaMatchPhase.Active));
        Assert.That(room.RemainingTicks, Is.EqualTo(ArenaRoom.MatchLengthTicks - 1));
    }

    [Test]
    public void WeaponDifferenceChangesAuthoritativeHash()
    {
        ArenaRoom a = new(); ArenaRoom b = new(); a.TryJoin(out uint id); b.TryJoin(out _);
        a.SubmitInput(id, new ArenaInput(1, 1, 0, 0, 0, 0, ArenaButtons.None, ArenaWeaponId.Shotgun));
        b.SubmitInput(id, new ArenaInput(1, 1, 0, 0, 0, 0, ArenaButtons.None, ArenaWeaponId.Machinegun));
        a.TickOnce(); b.TickOnce();
        Assert.That(a.ComputeStateHash(), Is.Not.EqualTo(b.ComputeStateHash()));
    }    [Test]
    public void RocketLeavesMuzzleAtConfiguredSpeedWithoutImmediateGroundExplosion()
    {
        ArenaRoom room = new(); room.TryJoin(out uint id);
        room.SubmitInput(id, new ArenaInput(1, 1, 0, 0, 0, 0, ArenaButtons.Fire, ArenaWeaponId.Rocket));
        room.TickOnce(); room.TryGetPlayer(id, out var state);
        Assert.That(state.Health, Is.EqualTo(100));
    }

    [Test]
    public void UndrainedEventsAreBoundedAndReportOverflow()
    {
        ArenaRoom room = new(); room.TryJoin(out uint id);
        for (uint sequence = 1; sequence <= 300; sequence++)
        {
            var weapon = sequence % 2 == 0 ? ArenaWeaponId.Machinegun : ArenaWeaponId.Shotgun;
            room.SubmitInput(id, new ArenaInput(sequence, room.Tick + 1, 0, 0, 0, 0, ArenaButtons.None, weapon));
            room.TickOnce();
        }
        var events = new ArenaCombatEventRecord[400];
        Assert.That(room.DrainEvents(events), Is.EqualTo(ArenaRoom.MaximumEvents));
        Assert.That(room.DroppedEventCount, Is.EqualTo(300 - ArenaRoom.MaximumEvents));
    }

    [Test]
    public void PendingCommandsAffectDeterministicHash()
    {
        ArenaRoom a = new(); ArenaRoom b = new(); a.TryJoin(out uint id); b.TryJoin(out _);
        a.SubmitInput(id, new ArenaInput(1, 1, 1000, 0, 0, 0, ArenaButtons.None, ArenaWeaponId.None));
        b.SubmitInput(id, new ArenaInput(1, 1, 0, 0, 0, 0, ArenaButtons.None, ArenaWeaponId.None));
        Assert.That(a.ComputeStateHash(), Is.Not.EqualTo(b.ComputeStateHash()));
    }
    [Test]
    public void RocketTravelsTwentyFiveMetresPerSecondBeforeImpact()
    {
        ArenaRoom room = new(); room.TryJoin(out uint shooter); room.TryJoin(out uint target);
        room.SubmitInput(shooter, new ArenaInput(1, 1, 0, 0, 90000, 0, ArenaButtons.Fire, ArenaWeaponId.Rocket));
        for (int i = 0; i < 120; i++) room.TickOnce();
        var events = new ArenaCombatEventRecord[256]; int count = room.DrainEvents(events);
        var hits = events.Take(count).Where(e => e.Kind == ArenaCombatEventKind.Hit && e.TargetEntityId == target).ToArray();
        Assert.That(hits.Length, Is.EqualTo(1));
        Assert.That(hits[0].Tick, Is.InRange(110UL, 117UL));
    }
    [Test]
    public void BoundedInputIntakeRejectsOverflowWithoutLosingAcceptedCommands()
    {
        ArenaRoom room = new(); room.TryJoin(out uint id);
        for (uint sequence = 1; sequence <= ArenaRoom.InputQueueCapacity; sequence++)
            Assert.That(room.SubmitInput(id, new ArenaInput(sequence, 1, 0, 0, 0, 0, ArenaButtons.None, ArenaWeaponId.None)), Is.True);
        Assert.That(room.SubmitInput(id, new ArenaInput(33, 1, 0, 0, 0, 0, ArenaButtons.None, ArenaWeaponId.None)), Is.False);
        for (int i = 0; i < ArenaRoom.InputQueueCapacity; i++) room.TickOnce();
        room.TryGetPlayer(id, out var state);
        Assert.That(state.LastProcessedInputSequence, Is.EqualTo(ArenaRoom.InputQueueCapacity));
    }

    [Test]
    public void FinishedMatchCannotAcceptNewPlayersOrRestart()
    {
        ArenaRoom room = new(); room.TryJoin(out uint id); room.TryJoin(out _);
        for (int i = 0; i < ArenaRoom.MatchLengthTicks; i++) room.TickOnce();
        Assert.That(room.Phase, Is.EqualTo(ArenaMatchPhase.Finished));
        room.Leave(id);
        Assert.That(room.TryJoin(out _), Is.False);
        room.TickOnce(); Assert.That(room.Phase, Is.EqualTo(ArenaMatchPhase.Finished));
    }
    [Test]
    public void SharedArenaPredictionMatchesLateJoinedServerMovementVector()
    {
        ArenaRoom room = new(); for (int i = 0; i < 100; i++) room.TickOnce();
        room.TryJoin(out uint id); room.TryGetPlayer(id, out var baseline);
        var history = new ArenaPredictionHistory(16); history.Initialize(baseline);
        for (uint sequence = 1; sequence <= 10; sequence++)
        {
            var input = new ArenaInput(sequence, room.Tick + 1, 1000, 0, 1000, 500, sequence == 1 ? ArenaButtons.Jump : ArenaButtons.None, ArenaWeaponId.Machinegun);
            var predicted = history.Predict(input, out _);
            Assert.That(room.SubmitInput(id, input), Is.True);
            room.TickOnce(); room.TryGetPlayer(id, out var authoritative);
            Assert.That(predicted, Is.EqualTo(authoritative));
            Assert.That(history.Reconcile(authoritative).Status, Is.EqualTo(ReconciliationStatus.Matched));
        }
    }
}
