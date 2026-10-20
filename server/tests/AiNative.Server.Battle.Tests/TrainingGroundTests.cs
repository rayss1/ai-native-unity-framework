using System.Collections;
using System.Reflection;
using AiNative.BattleHost;
using AiNative.Gameplay;
using AiNative.Server.Battle;
using NUnit.Framework;

namespace AiNative.Server.Battle.Tests;

public sealed class TrainingGroundTests
{
    [TestCase(ArenaWeaponId.Machinegun)]
    [TestCase(ArenaWeaponId.Shotgun)]
    public void SpawnScreenStopsHitscanDamage(ArenaWeaponId weapon)
    {
        var room = ReadyRoom();
        var wall = ArenaTrainingMap.GetObstacle(4);
        int z = (wall.MinZ + wall.MaxZ) / 2;
        Place(room, 1, wall.MinX - 2000, z);
        Place(room, 2, wall.MaxX + 600, z);
        Fire(room, weapon);
        room.TryGetPlayer(2, out var target);
        Assert.That(target.Health, Is.EqualTo(100));
    }

    [TestCase(ArenaWeaponId.Machinegun)]
    [TestCase(ArenaWeaponId.Shotgun)]
    public void OpenLaneStillAllowsHitscanDamage(ArenaWeaponId weapon)
    {
        var room = ReadyRoom();
        Place(room, 1, -3000, -17000);
        Place(room, 2, 0, -17000);
        Fire(room, weapon);
        room.TryGetPlayer(2, out var target);
        Assert.That(target.Health, Is.LessThan(100));
    }

    [Test]
    public void RocketDetonatesAtScreenAndBlastCannotCrossIt()
    {
        var room = ReadyRoom();
        var wall = ArenaTrainingMap.GetObstacle(4);
        int z = (wall.MinZ + wall.MaxZ) / 2;
        Place(room, 1, wall.MinX - 2000, z);
        Place(room, 2, wall.MaxX + 600, z);
        Fire(room, ArenaWeaponId.Rocket);
        for (int i = 0; i < 12; i++)
            room.TickOnce();
        room.TryGetPlayer(1, out var source);
        room.TryGetPlayer(2, out var target);
        Assert.That(
            Field<IList>(room, "_projectiles").Count,
            Is.Zero,
            "Rocket must stop at the screen, not tunnel through."
        );
        Assert.That(
            source.Health,
            Is.LessThan(100),
            "The exposed side must receive splash damage from impact."
        );
        Assert.That(
            target.Health,
            Is.EqualTo(100),
            "The nearby opposite side must be protected from splash."
        );
    }

    [Test]
    public void EverySupportedSpawnIsInsideBoundaryAndOutsideObstacles()
    {
        var room = new ArenaRoom();
        for (int i = 0; i < ArenaRoom.MaxPlayers; i++)
        {
            Assert.That(room.TryJoin(out uint id), Is.True);
            room.TryGetPlayer(id, out var player);
            Assert.That(
                Math.Abs(player.PositionXMillimetres) + ArenaTrainingMap.PlayerRadius,
                Is.LessThanOrEqualTo(ArenaTrainingMap.HalfExtent)
            );
            Assert.That(
                Math.Abs(player.PositionZMillimetres) + ArenaTrainingMap.PlayerRadius,
                Is.LessThanOrEqualTo(ArenaTrainingMap.HalfExtent)
            );
            for (int j = 0; j < ArenaTrainingMap.ObstacleCount; j++)
            {
                var box = ArenaTrainingMap.GetObstacle(j);
                bool overlaps =
                    player.PositionXMillimetres + ArenaTrainingMap.PlayerRadius > box.MinX
                    && player.PositionXMillimetres - ArenaTrainingMap.PlayerRadius < box.MaxX
                    && player.PositionZMillimetres + ArenaTrainingMap.PlayerRadius > box.MinZ
                    && player.PositionZMillimetres - ArenaTrainingMap.PlayerRadius < box.MaxZ;
                Assert.That(overlaps, Is.False, $"Spawn {i} overlaps box {j}.");
            }
        }
    }

    [Test]
    public void WallCollisionPredictionAndRecordedAuthorityAgreeEveryTick()
    {
        var identity = new ArenaReplayIdentity("source", "fantasy", "protocol", "config");
        var room = new ArenaRoom(600);
        var capture = new ArenaReplayCapture(
            identity,
            "room",
            "allocation",
            "match",
            "node",
            "boot",
            600,
            1000
        );
        room.TryJoin(out uint id);
        capture.RecordJoin(room.Tick, id);
        room.TryGetPlayer(id, out var initial);
        var prediction = new ArenaPredictionHistory(128);
        prediction.Initialize(initial);
        for (uint sequence = 1; sequence <= 120; sequence++)
        {
            var input = new ArenaInput(
                sequence,
                room.Tick + 1,
                1000,
                0,
                0,
                0,
                ArenaButtons.None,
                ArenaWeaponId.Machinegun
            );
            var predicted = prediction.Predict(input, out _);
            Assert.That(room.SubmitInput(id, input), Is.True);
            capture.RecordInput(room.Tick, id, input);
            room.TickOnce();
            capture.RecordTick(room.Tick, room.ComputeStateHash());
            room.TryGetPlayer(id, out var actual);
            Assert.That(predicted, Is.EqualTo(actual));
            Assert.That(
                prediction.Reconcile(actual).Status,
                Is.EqualTo(ReconciliationStatus.Matched)
            );
        }
        room.TryGetPlayer(id, out var stopped);
        Assert.That(
            stopped.PositionXMillimetres,
            Is.EqualTo(ArenaTrainingMap.GetObstacle(4).MinX - ArenaTrainingMap.PlayerRadius)
        );
        Assert.That(stopped.VelocityXMillimetresPerSecond, Is.Zero);
        capture.Complete(room.Tick, room.ComputeStateHash());
        using var stream = new MemoryStream();
        using var writer = new ArenaReplayWriter(stream, capture);
        writer.Drain();
        stream.Position = 0;
        Assert.That(
            ArenaReplayVerifier.Verify(stream, identity).FinalHash,
            Is.EqualTo(room.ComputeStateHash())
        );
    }

    private static ArenaRoom ReadyRoom()
    {
        var room = new ArenaRoom();
        room.TryJoin(out _);
        room.TryJoin(out _);
        for (int i = 0; i <= ArenaRoom.RespawnProtectionTicks; i++)
            room.TickOnce();
        return room;
    }

    private static void Place(ArenaRoom room, uint id, int x, int z)
    {
        var players = Field<ArenaPlayerState[]>(room, "_players");
        players[id - 1].PositionXMillimetres = x;
        players[id - 1].PositionZMillimetres = z;
    }

    private static void Fire(ArenaRoom room, ArenaWeaponId weapon)
    {
        Assert.That(
            room.SubmitInput(
                1,
                new ArenaInput(1, room.Tick + 1, 0, 0, 90000, 0, ArenaButtons.Fire, weapon)
            ),
            Is.True
        );
        room.TickOnce();
    }

    private static T Field<T>(ArenaRoom room, string name) =>
        (T)
            typeof(ArenaRoom)
                .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(room)!;
}
