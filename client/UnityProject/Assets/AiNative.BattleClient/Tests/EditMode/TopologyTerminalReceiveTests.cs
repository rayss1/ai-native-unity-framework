using System;
using System.IO;
using System.Reflection;
using AiNative.Client.Fantasy;
using AiNative.Gameplay;
using NUnit.Framework;
using UnityEngine;

namespace AiNative.Client.Application.Tests
{
    public sealed class TopologyTerminalReceiveTests
    {
        [TestCase("Settling", false)]
        [TestCase("AwaitingResult", false)]
        [TestCase("Finished", true)]
        public void OrdinaryUpdateDrainsLateTerminalAcrossBudgetWithoutSendingOrReconnecting(string state, bool completed)
        {
            var owner = new GameObject("LateTerminalFlow");
            try
            {
                var transport = new FakeTransport(); var flow = CreateFlow(owner, transport);
                flow.Battle.PredictAndQueueInput(101, 1000, 0);
                int sentBefore = transport.SentFrames.Count;
                Set(flow, "State", state); Set(flow, "Completed", completed);
                Invoke(flow, "FixedUpdate");
                Assert.That(flow.PreparedInputs, Is.Zero, "Receipt-tail states must not predict new input.");
                for (int i = 0; i < 300; i++) transport.Enqueue(Frame((ulong)(101 + i)), BattleClientProtocolV1.SnapshotChannel, 1);
                transport.Enqueue(Frame(401, true), BattleClientProtocolV1.SnapshotChannel, 1);
                transport.Close();
                Update(flow);
                Assert.That(flow.Battle.LastReceivedTick, Is.EqualTo(356));
                Assert.That(flow.Battle.State, Is.EqualTo(BattleClientState.Active));
                Update(flow);
                Assert.That(flow.Battle.ArenaPhase, Is.EqualTo(ArenaMatchPhase.Finished));
                Assert.That(flow.Battle.LastReceivedTick, Is.EqualTo(401));
                Assert.That(transport.SentFrames.Count, Is.EqualTo(sentBefore));
                Assert.That(flow.Completed, Is.EqualTo(completed));
                int receives = 0; transport.BeforeEmptyReceive = () => receives++;
                Update(flow); Update(flow);
                Assert.That(receives, Is.Zero, "Closed and drained completion must not poll the old transport forever.");
            }
            finally { UnityEngine.Object.DestroyImmediate(owner); }
        }

        [Test]
        public void ReceiveOnlyCompletedFlowRejectsStaleEpochAndNeverReconnectsWithoutTerminal()
        {
            var owner = new GameObject("ClosedCompletedFlow");
            try
            {
                var transport = new FakeTransport(); var flow = CreateFlow(owner, transport);
                Set(flow, "State", "Finished"); Set(flow, "Completed", true);
                transport.Enqueue(Frame(110, true), BattleClientProtocolV1.SnapshotChannel, 99);
                transport.Close(); Update(flow);
                Assert.That(flow.Battle.ArenaPhase, Is.EqualTo(ArenaMatchPhase.Active));
                Assert.That(flow.Battle.LastReceivedTick, Is.EqualTo(100));
                Assert.That(flow.Battle.State, Is.EqualTo(BattleClientState.Active));
            }
            finally { UnityEngine.Object.DestroyImmediate(owner); }
        }

        [Test]
        public void ConfirmedSettlementEvidenceWaitsForLateTerminalWithoutChangingCompleted()
        {
            string path = Path.Combine(Path.GetTempPath(), "terminal-evidence-" + Guid.NewGuid().ToString("N") + ".json");
            var owner = new GameObject("ConfirmedTerminalEvidence");
            try
            {
                var transport = new FakeTransport(); var flow = CreateFlow(owner, transport, path);
                Set(flow, "State", "Finished"); Set(flow, "Completed", true); Set(flow, "SettlementConfirmed", true);
                Invoke(flow, "WriteEvidence", true);
                Assert.That(File.ReadAllText(path), Does.Contain("\"success\": false"));
                Assert.That(flow.Completed && flow.SettlementConfirmed, Is.True);
                transport.Enqueue(Frame(110, true), BattleClientProtocolV1.SnapshotChannel, 1); transport.Close(); Update(flow);
                string json = File.ReadAllText(path);
                Assert.That(json, Does.Contain("\"success\": true").And.Contain("\"finalReceived\": true").And.Contain("\"receiveDrainCompleted\": true"));
                Assert.That(flow.Completed && flow.SettlementConfirmed, Is.True);
            }
            finally { UnityEngine.Object.DestroyImmediate(owner); if (File.Exists(path)) File.Delete(path); }
        }

        [Test]
        public void AutomaticEvidenceDeadlineFailsHonestlyButDoesNotUndoConfirmedSettlement()
        {
            string path = Path.Combine(Path.GetTempPath(), "terminal-deadline-" + Guid.NewGuid().ToString("N") + ".json");
            var owner = new GameObject("TerminalEvidenceDeadline");
            try
            {
                var transport = new FakeTransport(); var flow = CreateFlow(owner, transport, path);
                Set(flow, "State", "Finished"); Set(flow, "Completed", true); Set(flow, "SettlementConfirmed", true);
                Invoke(flow, "WriteEvidence", true);
                Assert.That(File.ReadAllText(path), Does.Contain("\"success\": false"));
                Invoke(flow, "AdvanceTerminalCompletion", 4.9f);
                Assert.That(flow.Error, Is.Empty);
                Invoke(flow, "AdvanceTerminalCompletion", 0.1f);
                string json = File.ReadAllText(path);
                Assert.That(json, Does.Contain("\"success\": false").And.Contain("terminal-receive-timeout").And.Contain("\"finalReceived\": false"));
                Assert.That(flow.Completed && flow.SettlementConfirmed, Is.True);
                Assert.That(flow.State, Is.EqualTo("Finished"));
            }
            finally { UnityEngine.Object.DestroyImmediate(owner); if (File.Exists(path)) File.Delete(path); }
        }

        private static TopologyClientFlow CreateFlow(GameObject owner, FakeTransport transport, string path = "")
        {
            var flow = owner.AddComponent<TopologyClientFlow>();
            flow.Initialize(new GateConnectionOptions("127.0.0.1", 23001, false), resultPath: path);
            var battle = new BattleClientSession("127.0.0.1", 22000, "receipt-test", 4, new FakeConnector(transport));
            battle.Start(); battle.Pump(0);
            transport.Enqueue(TestFrames.Login(42, 1), BattleClientProtocolV1.ControlChannel, 1);
            transport.Enqueue(TestFrames.Join(1, 7, 60), BattleClientProtocolV1.ControlChannel, 1);
            transport.Enqueue(Frame(100), BattleClientProtocolV1.SnapshotChannel, 1); battle.Pump(0);
            Set(flow, "Battle", battle);
            return flow;
        }
        internal static byte[] Frame(ulong tick, bool finished = false)
        {
            var bytes = new System.Collections.Generic.List<byte> { 0x4d, 0x04, 0x08, 1, 0x11 };
            bytes.AddRange(BitConverter.GetBytes(tick));
            bytes.AddRange(new byte[] { 0x22, 8, 0x08, 7, 0x30, 100, 0x50, 1, 0x60, 1, 0x38, finished ? (byte)2 : (byte)1 });
            return bytes.ToArray();
        }
        private static void Set(object target, string property, object value) => target.GetType().GetProperty(property).GetSetMethod(true).Invoke(target, new[] { value });
        private static void Update(TopologyClientFlow flow) => Invoke(flow, "Update");
        private static void Invoke(TopologyClientFlow flow, string method, params object[] args) => typeof(TopologyClientFlow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(flow, args);
    }
}
