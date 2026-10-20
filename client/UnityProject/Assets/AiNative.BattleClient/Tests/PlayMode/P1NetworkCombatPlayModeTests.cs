using System;
using System.Collections;
using AiNative.Client.Fantasy;
using AiNative.Client.Prediction;
using AiNative.Client.Protocol;
using AiNative.Gameplay;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace AiNative.Client.Application.PlayModeTests
{
    public sealed class P1NetworkCombatPlayModeTests
    {
        [UnityTest, Category("ExternalTopology")]
        // Requires plaintext Gate 23001, two-player rooms and at least 7200 match ticks.
        public IEnumerator TwoClientsObserveAuthorityCombatRespawnAndReconnectIdentity()
        {
            var a = new GameObject("P1CombatClientA");
            var b = new GameObject("P1CombatClientB");
            try
            {
                var first = a.AddComponent<TopologyClientFlow>();
                var second = b.AddComponent<TopologyClientFlow>();
                var gate = new GateConnectionOptions("127.0.0.1", 23001, useTls: false);
                first.Initialize(gate, automated: true, deadlineSeconds: 180);
                second.Initialize(gate, automated: true, deadlineSeconds: 180);
                float deadline = Time.realtimeSinceStartup + 60;
                while (Time.realtimeSinceStartup < deadline)
                {
                    TakeControlWhenReady(first);
                    TakeControlWhenReady(second);
                    if (!first.enabled && !second.enabled)
                        break;
                    Assert.That(first.Error + second.Error, Is.Empty);
                    yield return null;
                }
                Assert.That(
                    first.enabled || second.enabled,
                    Is.False,
                    "Both clients must join an active Arena."
                );
                Assert.That(first.MatchId, Is.EqualTo(second.MatchId));
                BattleClientSession shooter = first.Battle,
                    victim = second.Battle;
                uint shooterId = shooter.EntityId,
                    victimId = victim.EntityId;
                var events = new ArenaPresentationEvent[BattleClientSession.CombatEventCapacity];
                bool fire = false,
                    hit = false,
                    kill = false,
                    respawn = false,
                    damaged = false,
                    countedKill = false;
                float accumulator = 0;
                deadline = Time.realtimeSinceStartup + 55;
                while (
                    Time.realtimeSinceStartup < deadline
                    && !(fire && hit && kill && respawn && damaged && countedKill)
                )
                {
                    shooter.Pump(Time.unscaledDeltaTime);
                    victim.Pump(Time.unscaledDeltaTime);
                    accumulator += Mathf.Min(Time.unscaledDeltaTime, .1f);
                    while (accumulator >= 1f / 60)
                    {
                        accumulator -= 1f / 60;
                        if (
                            !shooter.TryGetArenaState(out var own)
                            || !victim.TryGetArenaState(out var target)
                        )
                            continue;
                        damaged |= target.Health < ArenaCombatRules.MaximumHealth;
                        countedKill |= own.Kills > 0;
                        double dx = target.PositionXMillimetres - own.PositionXMillimetres;
                        double dz = target.PositionZMillimetres - own.PositionZMillimetres;
                        double distance = Math.Sqrt(dx * dx + dz * dz);
                        int yaw = (int)Math.Round(Math.Atan2(dx, dz) * 180000 / Math.PI);
                        int yawDelta = (yaw - own.YawMillidegrees) % 360000;
                        if (yawDelta > 180000)
                            yawDelta -= 360000;
                        if (yawDelta < -180000)
                            yawDelta += 360000;
                        // Leave the protected spawn bays through the south lane before exchanging fire.
                        bool inLane =
                            own.PositionZMillimetres < -16000
                            && target.PositionZMillimetres < -16000;
                        int x = inLane && distance > 15000 ? (int)(dx / distance * 1000) : 0;
                        int z = own.PositionZMillimetres > -17000 ? -1000 : 0;
                        shooter.PredictAndQueueArenaInput(
                            shooter.LastReceivedTick + 1,
                            x,
                            z,
                            yawDelta,
                            -own.PitchMillidegrees,
                            inLane && distance < 20000 && !kill
                                ? ArenaButtons.Fire
                                : ArenaButtons.None,
                            ArenaWeaponId.Machinegun
                        );
                        victim.PredictAndQueueArenaInput(
                            victim.LastReceivedTick + 1,
                            0,
                            target.PositionZMillimetres > -17000 ? -1000 : 0,
                            0,
                            0,
                            ArenaButtons.None,
                            ArenaWeaponId.Machinegun
                        );
                    }
                    int count = shooter.DrainCombatEvents(events);
                    for (int i = 0; i < count; i++)
                    {
                        var item = events[i];
                        if (item.SourceEntityId == shooterId)
                        {
                            fire |= item.Kind == ArenaPresentationEventKind.Fire;
                            hit |=
                                item.Kind == ArenaPresentationEventKind.Hit
                                && item.TargetEntityId == victimId
                                && item.Damage > 0;
                            kill |=
                                item.Kind == ArenaPresentationEventKind.Kill
                                && item.TargetEntityId == victimId;
                        }
                        respawn |=
                            item.Kind == ArenaPresentationEventKind.Respawn
                            && item.SourceEntityId == victimId;
                    }
                    victim.DrainCombatEvents(events);
                    yield return null;
                }
                Assert.That(
                    fire && hit && kill && respawn && damaged && countedKill,
                    Is.True,
                    $"fire={fire} hit={hit} kill={kill} respawn={respawn} damaged={damaged} killSnapshot={countedKill}"
                );
                ulong session = victim.SessionId;
                victim.RequestReconnect();
                Assert.That(victim.State, Is.EqualTo(BattleClientState.Reconnecting));
                deadline = Time.realtimeSinceStartup + 20;
                while (
                    Time.realtimeSinceStartup < deadline
                    && (victim.State != BattleClientState.Active || !victim.IsPredictionInitialized)
                )
                {
                    shooter.Pump(Time.unscaledDeltaTime);
                    victim.Pump(Time.unscaledDeltaTime);
                    yield return null;
                }
                Assert.That(victim.State, Is.EqualTo(BattleClientState.Active));
                Assert.That(victim.IsPredictionInitialized, Is.True);
                Assert.That(victim.EntityId, Is.EqualTo(victimId));
                Assert.That(victim.SessionId, Is.EqualTo(session));
                Assert.That(victim.TryGetArenaState(out var restored), Is.True);
                Assert.That(restored.Alive, Is.True);
                Assert.That(restored.Health, Is.EqualTo(ArenaCombatRules.MaximumHealth));
                Debug.Log(
                    $"P1 combat proof: shooter={shooterId}, victim={victimId}, Fire/Hit/Kill/Respawn received; HP and kill snapshots observed; reconnect preserved session={session} entity={victimId}."
                );
            }
            finally
            {
                UnityEngine.Object.Destroy(a);
                UnityEngine.Object.Destroy(b);
            }
        }

        private static void TakeControlWhenReady(TopologyClientFlow flow)
        {
            if (!flow.enabled)
            {
                flow.Battle.Pump(Time.unscaledDeltaTime);
                return;
            }
            if (
                flow.Battle != null
                && flow.Battle.State == BattleClientState.Active
                && flow.Battle.IsPredictionInitialized
                && flow.Battle.ArenaPhase == ArenaMatchPhase.Active
            )
                flow.enabled = false; // Stop automated movement/reconnect; this test now owns pump and input cadence.
        }
    }
}
