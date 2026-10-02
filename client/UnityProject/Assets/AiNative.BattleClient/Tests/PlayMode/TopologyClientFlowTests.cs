using System.Collections;
using AiNative.Client.Application;
using AiNative.Client.Fantasy;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace AiNative.Client.Application.PlayModeTests
{
    public sealed class TopologyClientFlowTests
    {
        [UnityTest]
        public IEnumerator Two_real_clients_match_reconnect_and_observe_single_persisted_settlement()
        {
            GameObject a = new GameObject("TopologyClientA"), b = new GameObject("TopologyClientB");
            try
            {
                var first = a.AddComponent<TopologyClientFlow>(); var second = b.AddComponent<TopologyClientFlow>();
                var gate = new GateConnectionOptions("127.0.0.1", 23001, useTls: false);
                first.Initialize(gate, automated: true, deadlineSeconds: 120); second.Initialize(gate, automated: true, deadlineSeconds: 120);
                float deadline = Time.realtimeSinceStartup + 125;
                while (Time.realtimeSinceStartup < deadline && !(first.Completed && second.Completed) && first.Error.Length == 0 && second.Error.Length == 0) yield return null;
                Assert.That(first.Error, Is.Empty, $"first state={first.State} tick={first.Battle?.LastReceivedTick} ack={first.Battle?.LastAcknowledgedSequence} phase={first.Battle?.ArenaPhase} reconnected={first.Reconnected} requested={first.ReconnectRequested} priorAck={first.PreReconnectAcknowledgement} inputs={first.PreparedInputs} played={first.Profile?.Played}");
                Assert.That(second.Error, Is.Empty, $"second state={second.State} tick={second.Battle?.LastReceivedTick} ack={second.Battle?.LastAcknowledgedSequence} phase={second.Battle?.ArenaPhase} reconnected={second.Reconnected} requested={second.ReconnectRequested} priorAck={second.PreReconnectAcknowledgement} inputs={second.PreparedInputs} played={second.Profile?.Played}");
                Assert.That(first.Completed && second.Completed, Is.True, "Two-client flow did not finish.");
                Assert.That(first.MatchId, Is.EqualTo(second.MatchId)); Assert.That(first.RoomId, Is.EqualTo(second.RoomId));
                Assert.That(first.Reconnected && second.Reconnected, Is.True);
                Assert.That(first.Battle.LastAcknowledgedSequence, Is.GreaterThanOrEqualTo(first.PreparedInputs * 0.8), "First client's continuous inputs stalled.");
                Assert.That(second.Battle.LastAcknowledgedSequence, Is.GreaterThanOrEqualTo(second.PreparedInputs * 0.8), "Second client's continuous inputs stalled.");
                Assert.That(first.MaxAcknowledgementStallSeconds, Is.LessThan(3));
                Assert.That(second.MaxAcknowledgementStallSeconds, Is.LessThan(3));
                Assert.That(first.Profile.Played, Is.EqualTo(1)); Assert.That(second.Profile.Played, Is.EqualTo(1));
                Assert.That(first.SettlementConfirmed && second.SettlementConfirmed, Is.True, "Both exact match receipts must be confirmed.");
            }
            finally { Object.Destroy(a); Object.Destroy(b); }
        }
    }
}
