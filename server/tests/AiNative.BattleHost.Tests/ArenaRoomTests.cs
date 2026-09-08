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
}
