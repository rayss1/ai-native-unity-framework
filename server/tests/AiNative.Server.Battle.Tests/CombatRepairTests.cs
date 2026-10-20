using System.Reflection;
using AiNative.BattleHost;
using AiNative.Gameplay;
using AiNative.Server.Battle;
using NUnit.Framework;

namespace AiNative.Server.Battle.Tests;

public sealed class CombatRepairTests
{
    [TestCase(0, 80)]
    [TestCase(3400, 80)]
    [TestCase(20000, 100)]
    public void MachinegunUsesBodyVolumeRatherThanAngleToFeet(int pitch, int expectedHealth)
    {
        var room = ReadyRoom();
        Place(room, 1, -10000, -17000);
        Place(room, 2, 0, -17000);
        Step(room, ArenaButtons.Fire, yaw: 90000, pitch: pitch);
        room.TryGetPlayer(2, out var target);
        Assert.That(target.Health, Is.EqualTo(expectedHealth));
    }

    [Test]
    public void FirstCapsuleIntersectionWinsEvenWhenFartherTargetHasLowerEntityId()
    {
        var room = ReadyRoom();
        room.TryJoin(out uint nearer);
        for (int i = 0; i < 61; i++) room.TickOnce();
        Place(room, 1, -10000, -17000);
        Place(room, 2, 10000, -17000);
        Place(room, nearer, 0, -17000);
        Step(room, ArenaButtons.Fire, yaw: 90000);
        room.TryGetPlayer(2, out var far);
        room.TryGetPlayer(nearer, out var near);
        Assert.That(far.Health, Is.EqualTo(100));
        Assert.That(near.Health, Is.EqualTo(80));
    }

    [TestCase(400, 80)]
    [TestCase(600, 100)]
    public void MachinegunRespectsCapsuleWidth(int lateralOffset, int expectedHealth)
    {
        var room = ReadyRoom();
        Place(room, 1, -10000, -17000);
        Place(room, 2, 0, -17000 + lateralOffset);
        Step(room, ArenaButtons.Fire, yaw: 90000);
        room.TryGetPlayer(2, out var target);
        Assert.That(target.Health, Is.EqualTo(expectedHealth));
    }

    [Test]
    public void EmptyMachinegunAutomaticallyReloadsAndCannotFireBefore120Ticks()
    {
        var room = ReadyRoom();
        int fired = 0;
        for (int i = 0; i < 40; i++)
        {
            fired += Step(room, ArenaButtons.Fire);
            for (int tick = 0; tick < 5; tick++) room.TickOnce();
        }
        Assert.That(fired, Is.EqualTo(40));
        Assert.That(Step(room, ArenaButtons.Fire), Is.Zero, "The empty trigger begins reload.");
        for (int tick = 1; tick < 120; tick++)
            Assert.That(Step(room, ArenaButtons.Fire), Is.Zero, $"Reload tick {tick} fired early.");
        Assert.That(Step(room, ArenaButtons.Fire), Is.EqualTo(1), "Fire resumes at the reload deadline.");
    }

    [Test]
    public void RepeatedReloadAndWeaponSwitchCannotSkipOrRestartReloadDeadline()
    {
        var room = ReadyRoom();
        Assert.That(Step(room, ArenaButtons.Fire), Is.EqualTo(1));
        Assert.That(Step(room, ArenaButtons.Reload), Is.Zero);
        for (int tick = 1; tick < 120; tick++)
        {
            var weapon = tick == 10 ? ArenaWeaponId.Shotgun : ArenaWeaponId.Machinegun;
            var buttons = tick == 10 ? ArenaButtons.Reload : ArenaButtons.Reload | ArenaButtons.Fire;
            Assert.That(Step(room, buttons, weapon), Is.Zero);
        }
        Assert.That(Step(room, ArenaButtons.Fire), Is.EqualTo(1));
    }

    [Test]
    public void ReloadingFullMagazineDoesNotDelayFire()
    {
        var room = ReadyRoom();
        Step(room, ArenaButtons.Reload);
        Assert.That(Step(room, ArenaButtons.Fire), Is.EqualTo(1));
    }

    [Test]
    public void RejoiningClearsPendingReload()
    {
        var room = ReadyRoom();
        Step(room, ArenaButtons.Fire);
        Step(room, ArenaButtons.Reload);
        Assert.That(room.Leave(1), Is.True);
        Assert.That(room.TryJoin(out uint replacement), Is.True);
        Assert.That(replacement, Is.EqualTo(1));
        Assert.That(Step(room, ArenaButtons.Fire), Is.EqualTo(1));
    }

    [Test]
    public void StateHashDistinguishesPendingReloadWithIdenticalVisiblePlayers()
    {
        var loading = ReadyRoom();
        var idle = ReadyRoom();
        Step(loading, ArenaButtons.Fire); Step(idle, ArenaButtons.Fire);
        Step(loading, ArenaButtons.Reload); Step(idle, ArenaButtons.None);
        loading.TryGetPlayer(1, out var a); idle.TryGetPlayer(1, out var b);
        Assert.That(a, Is.EqualTo(b));
        Assert.That(loading.ComputeStateHash(), Is.Not.EqualTo(idle.ComputeStateHash()));
    }

    [Test]
    public void AutomaticAndExplicitReloadReplayWithEveryTickHash()
    {
        var identity = new ArenaReplayIdentity("source", "fantasy", "protocol", "config");
        var room = new ArenaRoom(1000);
        var capture = new ArenaReplayCapture(identity, "room", "allocation", "match", "node", "boot", 1000, 2000);
        room.TryJoin(out uint shooter); capture.RecordJoin(room.Tick, shooter);
        room.TryJoin(out uint target); capture.RecordJoin(room.Tick, target);
        var events = new ArenaCombatEventRecord[ArenaRoom.MaximumEvents];
        int fired = 0;
        for (uint sequence = 1; sequence <= 600; sequence++)
        {
            var input = new ArenaInput(sequence, room.Tick + 1, 0, 0, 0, 0,
                sequence == 500 ? ArenaButtons.Reload : ArenaButtons.Fire, ArenaWeaponId.Machinegun);
            Assert.That(room.SubmitInput(shooter, input), Is.True);
            capture.RecordInput(room.Tick, shooter, input);
            room.TickOnce(); capture.RecordTick(room.Tick, room.ComputeStateHash());
            int count = room.DrainEvents(events);
            for (int i = 0; i < count; i++) if (events[i].Kind == ArenaCombatEventKind.Fire) fired++;
        }
        Assert.That(fired, Is.GreaterThan(40), "The recording must cross automatic reload.");
        capture.Complete(room.Tick, room.ComputeStateHash());
        using var stream = new MemoryStream();
        using var writer = new ArenaReplayWriter(stream, capture);
        writer.Drain(); stream.Position = 0;
        Assert.That(ArenaReplayVerifier.Verify(stream, identity).FinalHash, Is.EqualTo(room.ComputeStateHash()));
    }

    private static ArenaRoom ReadyRoom()
    {
        var room = new ArenaRoom(100000);
        room.TryJoin(out _); room.TryJoin(out _);
        for (int tick = 0; tick < 61; tick++) room.TickOnce();
        return room;
    }

    private static int Step(ArenaRoom room, ArenaButtons buttons,
        ArenaWeaponId weapon = ArenaWeaponId.Machinegun, int yaw = 0, int pitch = 0)
    {
        var events = new ArenaCombatEventRecord[ArenaRoom.MaximumEvents];
        room.DrainEvents(events);
        Assert.That(room.SubmitInput(1, new ArenaInput(checked((uint)room.Tick + 1), room.Tick + 1,
            0, 0, yaw, pitch, buttons, weapon)), Is.True);
        room.TickOnce();
        int count = room.DrainEvents(events);
        int fired = 0;
        for (int i = 0; i < count; i++) if (events[i].Kind == ArenaCombatEventKind.Fire) fired++;
        return fired;
    }

    // Isolate ray geometry without spending thousands of movement ticks on spawn spacing.
    // Shots use the next tick so no historical frame is queried after this fixture setup.
    private static void Place(ArenaRoom room, uint entity, int x, int z)
    {
        var players = (ArenaPlayerState[])typeof(ArenaRoom)
            .GetField("_players", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(room)!;
        players[entity - 1].PositionXMillimetres = x;
        players[entity - 1].PositionZMillimetres = z;
    }
}
