using AiNative.Gameplay;
using NUnit.Framework;

namespace AiNative.Gameplay.Tests
{
    public sealed class ArenaGameplayTests
    {
        [Test]
        public void GroundAccelerationAndFrictionAreDeterministic()
        {
            ArenaPlayerState state = new ArenaPlayerState(0, 0, 0, 0);
            ArenaInput forward = new ArenaInput(1, 1, 0, 1000, 0, 0, ArenaButtons.None, ArenaWeaponId.Machinegun);
            ArenaPlayerState accelerated = ArenaMovement.Step(state, forward);

            Assert.That(accelerated.VelocityZMillimetresPerSecond, Is.EqualTo(1000));
            Assert.That(accelerated.PositionZMillimetres, Is.EqualTo(16));

            ArenaInput idle = new ArenaInput(2, 2, 0, 0, 0, 0, ArenaButtons.None, ArenaWeaponId.Machinegun);
            ArenaPlayerState slowed = ArenaMovement.Step(accelerated, idle);
            Assert.That(slowed.VelocityZMillimetresPerSecond, Is.EqualTo(867));
        }

        [Test]
        public void JumpAppliesGravityAndLandsOnTheGround()
        {
            ArenaPlayerState state = new ArenaPlayerState(0, 0, 0, 0);
            ArenaInput jump = new ArenaInput(1, 1, 0, 0, 0, 0, ArenaButtons.Jump, ArenaWeaponId.Machinegun);
            ArenaPlayerState airborne = ArenaMovement.Step(state, jump);

            Assert.That(airborne.Grounded, Is.False);
            Assert.That(airborne.VelocityYMillimetresPerSecond, Is.EqualTo(5500));
            Assert.That(airborne.PositionYMillimetres, Is.EqualTo(91));

            ArenaPlayerState current = airborne;
            for (uint sequence = 2; sequence < 80 && !current.Grounded; sequence++)
            {
                ArenaInput input = new ArenaInput(sequence, sequence, 0, 0, 0, 0, ArenaButtons.None, ArenaWeaponId.Machinegun);
                current = ArenaMovement.Step(current, input);
            }

            Assert.That(current.Grounded, Is.True);
            Assert.That(current.PositionYMillimetres, Is.Zero);
        }

        [Test]
        public void ViewAnglesWrapAndClamp()
        {
            ArenaPlayerState state = new ArenaPlayerState(0, 0, 0, 0);
            ArenaInput input = new ArenaInput(1, 1, 0, 0, 400000, 100000, ArenaButtons.None, ArenaWeaponId.Machinegun);
            ArenaPlayerState next = ArenaMovement.Step(state, input);

            Assert.That(next.YawMillidegrees, Is.EqualTo(40000));
            Assert.That(next.PitchMillidegrees, Is.EqualTo(ArenaMovement.MaximumPitchMillidegrees));
        }

        [Test]
        public void ArmorAbsorbsHalfDamageAndDeathClearsVelocity()
        {
            ArenaPlayerState player = new ArenaPlayerState(0, 0, 0, 0) { Armor = 20 };
            int healthDamage = ArenaCombatRules.ApplyDamage(ref player, 40);

            Assert.That(healthDamage, Is.EqualTo(20));
            Assert.That(player.Health, Is.EqualTo(80));
            Assert.That(player.Armor, Is.Zero);

            ArenaCombatRules.ApplyDamage(ref player, 200);
            Assert.That(player.Alive, Is.False);
            Assert.That(player.VelocityXMillimetresPerSecond, Is.Zero);
        }

        [Test]
        public void RocketSplashFallsOffAndSelfDamageIsHalved()
        {
            Assert.That(ArenaCombatRules.RocketDamageAtDistance(0, false), Is.EqualTo(80));
            Assert.That(ArenaCombatRules.RocketDamageAtDistance(2000, false), Is.EqualTo(40));
            Assert.That(ArenaCombatRules.RocketDamageAtDistance(2000, true), Is.EqualTo(20));
            Assert.That(ArenaCombatRules.RocketDamageAtDistance(4001, false), Is.Zero);
        }

        [Test]
        public void RespawnRestoresBaselineStateWithoutChangingKills()
        {
            ArenaPlayerState player = new ArenaPlayerState(20, 100, 0, 200)
            {
                Health = 0,
                Armor = 60,
                Alive = false,
                Kills = 3,
                Weapon = ArenaWeaponId.Rocket,
            };

            ArenaCombatRules.Respawn(ref player, -1000, 0, 2000);

            Assert.That(player.Alive, Is.True);
            Assert.That(player.Health, Is.EqualTo(100));
            Assert.That(player.Armor, Is.Zero);
            Assert.That(player.Weapon, Is.EqualTo(ArenaWeaponId.Machinegun));
            Assert.That(player.Kills, Is.EqualTo(3));
        }

        [Test]
        public void PredictionHistoryRewindsAndReplaysArenaInputs()
        {
            ArenaPredictionHistory history = new ArenaPredictionHistory(8);
            ArenaPlayerState baseline = new ArenaPlayerState(0, 0, 0, 0);
            history.Initialize(baseline);
            history.Predict(new ArenaInput(1, 1, 1000, 0, 0, 0, ArenaButtons.None, ArenaWeaponId.Machinegun), out _);
            history.Predict(new ArenaInput(2, 2, 0, 1000, 0, 0, ArenaButtons.None, ArenaWeaponId.Machinegun), out _);

            ArenaPlayerState authoritative = baseline;
            authoritative.Tick = 1;
            authoritative.LastProcessedInputSequence = 1;
            authoritative.PositionXMillimetres = 10;
            ReconciliationResult result = history.Reconcile(authoritative);

            Assert.That(result.Status, Is.EqualTo(ReconciliationStatus.Corrected));
            Assert.That(history.Current.PositionXMillimetres, Is.EqualTo(10));
            Assert.That(history.Current.PositionZMillimetres, Is.EqualTo(16));
        }
    }
}
