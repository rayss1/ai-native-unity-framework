using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace AiNative.Client.Application.PlayModeTests
{
    public sealed class BattleClientKcpPlayModeTests
    {
        [UnityTest]
        public IEnumerator LocalBattleHostCompletesLoginJoinSnapshotAndAcknowledgement()
        {
            BattleClientSession session = CreateLiveSessionOrIgnore();
            session.Start();
            DateTime deadline = DateTime.UtcNow.AddSeconds(15);
            ulong tick = 0;
            while (DateTime.UtcNow < deadline && session.LastAcknowledgedSequence == 0)
            {
                session.Pump(Time.unscaledDeltaTime);
                if (session.IsPredictionInitialized)
                {
                    session.PredictAndQueueInput(++tick, 1000, 0);
                }

                if (session.State == BattleClientState.Faulted)
                {
                    Assert.Fail("Live Battle Host handshake failed: " + session.FaultReason);
                }

                yield return null;
            }

            Assert.That(session.State, Is.EqualTo(BattleClientState.Active));
            Assert.That(session.SessionId, Is.Not.Zero);
            Assert.That(session.EntityId, Is.Not.Zero);
            Assert.That(session.IsPredictionInitialized, Is.True);
            Assert.That(session.LastAcknowledgedSequence, Is.GreaterThan(0));
            yield return Dispose(session);
        }

        [UnityTest]
        public IEnumerator ForcedDisconnectReconnectsWithNewEpochAndContinuesPrediction()
        {
            BattleClientSession session = CreateLiveSessionOrIgnore();
            session.Start();
            DateTime initialDeadline = DateTime.UtcNow.AddSeconds(15);
            ulong tick = 0;
            while (DateTime.UtcNow < initialDeadline && session.LastAcknowledgedSequence < 3)
            {
                session.Pump(Time.unscaledDeltaTime);
                if (session.IsPredictionInitialized)
                {
                    session.PredictAndQueueInput(++tick, 0, 1000);
                }

                if (session.State == BattleClientState.Faulted)
                {
                    Assert.Fail("Live Battle Host setup failed: " + session.FaultReason);
                }

                yield return null;
            }

            Assert.That(session.LastAcknowledgedSequence, Is.GreaterThanOrEqualTo(3));
            uint initialEpoch = session.ConnectionEpoch;
            uint acknowledgementBeforeReconnect = session.LastAcknowledgedSequence;
            session.RequestReconnect();

            DateTime reconnectDeadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < reconnectDeadline &&
                   !(session.State == BattleClientState.Active &&
                     session.ConnectionEpoch > initialEpoch &&
                     session.LastAcknowledgedSequence > acknowledgementBeforeReconnect))
            {
                session.Pump(Time.unscaledDeltaTime);
                if (session.State == BattleClientState.Active && session.IsPredictionInitialized)
                {
                    session.PredictAndQueueInput(++tick, -1000, 0);
                }

                if (session.State == BattleClientState.Faulted)
                {
                    Assert.Fail("Live Battle Host reconnect failed: " + session.FaultReason);
                }

                yield return null;
            }

            Assert.That(session.ConnectionEpoch, Is.GreaterThan(initialEpoch));
            Assert.That(session.LastAcknowledgedSequence, Is.GreaterThan(acknowledgementBeforeReconnect));
            Assert.That(session.IsPredictionInitialized, Is.True);
            yield return Dispose(session);
        }

        private static BattleClientSession CreateLiveSessionOrIgnore()
        {
            string enabled = Environment.GetEnvironmentVariable("AINATIVE_WS26_RUN_PLAYMODE");
            if (!global::UnityEngine.Application.isBatchMode &&
                !string.Equals(enabled, "1", StringComparison.Ordinal))
            {
                Assert.Ignore(
                    "Start AiNative.BattleHost, then set AINATIVE_WS26_RUN_PLAYMODE=1 for an " +
                    "interactive Editor run. Batch validation runs these tests against its managed Host.");
            }

            string host = Environment.GetEnvironmentVariable("AINATIVE_WS26_HOST") ?? "127.0.0.1";
            string portText = Environment.GetEnvironmentVariable("AINATIVE_WS26_PORT") ?? "22000";
            if (!int.TryParse(portText, out int port))
            {
                Assert.Fail("AINATIVE_WS26_PORT must be a valid integer port.");
            }

            return new BattleClientSession(host, port, "ws26-playmode");
        }

        private static IEnumerator Dispose(BattleClientSession session)
        {
            var task = session.DisposeAsync().AsTask();
            while (!task.IsCompleted) yield return null;
            if (task.IsFaulted)
            {
                throw (Exception)task.Exception ?? new InvalidOperationException("Dispose failed.");
            }
        }
    }
}
