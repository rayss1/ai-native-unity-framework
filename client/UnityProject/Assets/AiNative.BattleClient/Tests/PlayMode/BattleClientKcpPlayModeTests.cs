using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace AiNative.Client.Application.PlayModeTests
{
    public sealed class BattleClientKcpPlayModeTests
    {
        private System.Threading.Tasks.Task _idleDisposalTask;

        [UnityTearDown]
        public IEnumerator AwaitIdleSessionDisposal()
        {
            System.Threading.Tasks.Task task = _idleDisposalTask;
            _idleDisposalTask = null;
            if (task == null) yield break;

            while (!task.IsCompleted) yield return null;
            // Native Scene disposal is posted to its Unity thread context.
            // Give that queue a frame even when the test aborted on an assertion.
            yield return null;
            task.GetAwaiter().GetResult();
        }

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

        [UnityTest]
        public IEnumerator IdleClientRetainsIdentityAndReceivesSnapshotsBeyondServerIdleTimeout()
        {
            BattleClientSession session = CreateLiveSessionOrIgnore();
            try
            {
                session.Start();
                DateTime handshakeDeadline = DateTime.UtcNow.AddSeconds(15);
                while (DateTime.UtcNow < handshakeDeadline && !session.IsPredictionInitialized)
                {
                    session.Pump(Time.unscaledDeltaTime);
                    Assert.That(session.State, Is.Not.EqualTo(BattleClientState.Faulted), session.FaultReason);
                    yield return null;
                }

                Assert.That(session.State, Is.EqualTo(BattleClientState.Active));
                Assert.That(session.IsPredictionInitialized, Is.True);
                ulong sessionId = session.SessionId;
                uint entityId = session.EntityId;
                uint epoch = session.ConnectionEpoch;
                ulong initialTick = session.LastReceivedTick;
                ulong progressTick = initialTick;
                double nextProgressCheckSeconds = 5;
                // Pinned legacy Fantasy.config: idleTimeout=30000ms, idleInterval=5000ms.
                // 45 seconds crosses the timeout and its next two inspection intervals.
                var idleClock = System.Diagnostics.Stopwatch.StartNew();
                while (idleClock.Elapsed.TotalSeconds < 45)
                {
                    session.Pump(Time.unscaledDeltaTime);
                    Assert.That(session.State, Is.EqualTo(BattleClientState.Active), session.FaultReason);
                    Assert.That(session.SessionId, Is.EqualTo(sessionId));
                    Assert.That(session.EntityId, Is.EqualTo(entityId));
                    Assert.That(session.ConnectionEpoch, Is.EqualTo(epoch));
                    Assert.That(session.LastAcknowledgedSequence, Is.Zero);
                    Assert.That(session.QueuedInputFrames, Is.Zero);
                    if (idleClock.Elapsed.TotalSeconds >= nextProgressCheckSeconds)
                    {
                        Assert.That(session.LastReceivedTick, Is.GreaterThan(progressTick), "Idle snapshots must keep advancing.");
                        progressTick = session.LastReceivedTick;
                        nextProgressCheckSeconds += 5;
                    }
                    yield return null;
                }

                Assert.That(session.LastReceivedTick, Is.GreaterThan(initialTick));
                Debug.Log($"Idle keepalive verified: seconds={idleClock.Elapsed.TotalSeconds:F3}, initialTick={initialTick}, finalTick={session.LastReceivedTick}, epoch={epoch}, acknowledgement={session.LastAcknowledgedSequence}");
            }
            finally
            {
                // Start cleanup even when a live assertion aborts this iterator.
                _idleDisposalTask = session.DisposeAsync().AsTask();
            }
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
