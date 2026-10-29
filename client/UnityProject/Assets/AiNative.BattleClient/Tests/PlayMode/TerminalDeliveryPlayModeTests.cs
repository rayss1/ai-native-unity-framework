using System.Collections;
using System.Reflection;
using AiNative.Client.Application;
using AiNative.Client.Fantasy;
using AiNative.Gameplay;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace AiNative.Client.Application.PlayModeTests
{
    public sealed class TerminalDeliveryPlayModeTests
    {
        [UnityTest]
        public IEnumerator RealNetworkContinuesWhileApplicationPumpPausesThenConsumesTerminalAfterClose()
        {
            GameObject a = new GameObject("TerminalDrainClientA"),
                b = new GameObject("TerminalDrainClientB");
            try
            {
                var first = a.AddComponent<TopologyClientFlow>();
                var second = b.AddComponent<TopologyClientFlow>();
                var gate = new GateConnectionOptions("127.0.0.1", 23001, useTls: false);
                first.Initialize(gate, automated: true, deadlineSeconds: 120);
                second.Initialize(gate, automated: true, deadlineSeconds: 120);
                yield return FrameworkTestLifecycle.Start(a);
                yield return FrameworkTestLifecycle.Start(b);
                float readyDeadline = Time.realtimeSinceStartup + 110;
                while (
                    Time.realtimeSinceStartup < readyDeadline
                    && !ReadyToPause(first, second)
                    && first.Error.Length == 0
                    && second.Error.Length == 0
                )
                {
                    yield return null;
                }

                Assert.That(first.Error, Is.Empty);
                Assert.That(second.Error, Is.Empty);
                Assert.That(
                    ReadyToPause(first, second),
                    Is.True,
                    "Both clients must reach the final two seconds after the ordinary reconnect check."
                );
                Assert.That(first.MatchId, Is.EqualTo(second.MatchId));
                // 仅停用应用的 Update/FixedUpdate；Fantasy 独立持有的 Scene 继续
                // 在 Unity 线程更新 socket/KCP，并确认已接收的终局包。
                first.enabled = false;
                second.enabled = false;
                ulong firstTick = first.Battle.LastReceivedTick,
                    secondTick = second.Battle.LastReceivedTick;
                uint firstEpoch = first.Battle.ConnectionEpoch,
                    secondEpoch = second.Battle.ConnectionEpoch;
                object firstTransport = TransportSlot(first.Battle),
                    secondTransport = TransportSlot(second.Battle);
                float closeDeadline = Time.realtimeSinceStartup + 15;
                while (
                    Time.realtimeSinceStartup < closeDeadline
                    && !(Closed(firstTransport) && Closed(secondTransport))
                )
                {
                    yield return null;
                }

                Assert.That(
                    Closed(firstTransport) && Closed(secondTransport),
                    Is.True,
                    "Server release must actually close both real KCP sessions while application consumption is paused."
                );
                Assert.That(first.Battle.LastReceivedTick, Is.EqualTo(firstTick));
                Assert.That(second.Battle.LastReceivedTick, Is.EqualTo(secondTick));
                Assert.That(first.Battle.ArenaPhase, Is.EqualTo(ArenaMatchPhase.Active));
                Assert.That(second.Battle.ArenaPhase, Is.EqualTo(ArenaMatchPhase.Active));
                // 保持应用接收暂停，让真实 Gate 回执先到达。
                float receiptDeadline = Time.realtimeSinceStartup + 15;
                while (
                    Time.realtimeSinceStartup < receiptDeadline
                    && !(first.Completed && second.Completed)
                )
                {
                    var firstReceipt = PollReceipt(first);
                    var secondReceipt = PollReceipt(second);
                    while (
                        Time.realtimeSinceStartup < receiptDeadline
                        && !(firstReceipt.IsCompleted && secondReceipt.IsCompleted)
                    )
                    {
                        yield return null;
                    }

                    Assert.That(firstReceipt.IsCompleted && secondReceipt.IsCompleted, Is.True);
                    firstReceipt.GetAwaiter().GetResult();
                    secondReceipt.GetAwaiter().GetResult();
                    if (!(first.Completed && second.Completed))
                    {
                        yield return new WaitForSecondsRealtime(0.1f);
                    }
                }

                Assert.That(
                    first.Completed
                        && second.Completed
                        && first.SettlementConfirmed
                        && second.SettlementConfirmed,
                    Is.True
                );
                Assert.That(first.Battle.ArenaPhase, Is.EqualTo(ArenaMatchPhase.Active));
                Assert.That(second.Battle.ArenaPhase, Is.EqualTo(ArenaMatchPhase.Active));
                // 覆盖真实 Completed/Settling 的 Update 路径，不直接调用 Battle.Pump。
                first.enabled = true;
                second.enabled = true;
                for (
                    int i = 0;
                    i < 8
                        && (
                            first.Battle.ArenaPhase != ArenaMatchPhase.Finished
                            || second.Battle.ArenaPhase != ArenaMatchPhase.Finished
                        );
                    i++
                )
                {
                    yield return null;
                }

                Assert.That(
                    first.Battle.ArenaPhase,
                    Is.EqualTo(ArenaMatchPhase.Finished),
                    "First client lost its transport-accepted final frame."
                );
                Assert.That(
                    second.Battle.ArenaPhase,
                    Is.EqualTo(ArenaMatchPhase.Finished),
                    "Second client lost its transport-accepted final frame."
                );
                Assert.That(first.Battle.LastReceivedTick, Is.GreaterThan(firstTick));
                Assert.That(second.Battle.LastReceivedTick, Is.GreaterThan(secondTick));
                Assert.That(first.Battle.ConnectionEpoch, Is.EqualTo(firstEpoch));
                Assert.That(second.Battle.ConnectionEpoch, Is.EqualTo(secondEpoch));
                Assert.That(first.Battle.State, Is.EqualTo(BattleClientState.Active));
                Assert.That(second.Battle.State, Is.EqualTo(BattleClientState.Active));
                first.enabled = true;
                second.enabled = true;
                float settleDeadline = Time.realtimeSinceStartup + 20;
                while (
                    Time.realtimeSinceStartup < settleDeadline
                    && !(first.Completed && second.Completed)
                    && first.Error.Length == 0
                    && second.Error.Length == 0
                )
                {
                    yield return null;
                }

                Assert.That(first.Error, Is.Empty);
                Assert.That(second.Error, Is.Empty);
                Assert.That(first.Completed && second.Completed, Is.True);
                Assert.That(first.SettlementConfirmed && second.SettlementConfirmed, Is.True);
                Assert.That(first.Profile.Played, Is.EqualTo(1));
                Assert.That(second.Profile.Played, Is.EqualTo(1));
            }
            finally
            {
                Object.Destroy(a);
                Object.Destroy(b);
            }
        }

        private static bool ReadyToPause(TopologyClientFlow a, TopologyClientFlow b) =>
            a.Battle != null
            && b.Battle != null
            && a.Reconnected
            && b.Reconnected
            && a.Battle.ArenaPhase == ArenaMatchPhase.Active
            && b.Battle.ArenaPhase == ArenaMatchPhase.Active
            && a.Battle.ArenaRemainingTicks > 0
            && a.Battle.ArenaRemainingTicks <= 120
            && b.Battle.ArenaRemainingTicks > 0
            && b.Battle.ArenaRemainingTicks <= 120;

        private static System.Threading.Tasks.Task PollReceipt(TopologyClientFlow flow) =>
            (
                (UniTask)
                    typeof(TopologyClientFlow)
                        .GetMethod("PollAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(flow, null)
            ).AsTask();

        // 通过只读测试检查，避免新增产品启动参数或公开网络端口。
        private static object TransportSlot(BattleClientSession session) =>
            typeof(BattleClientSession)
                .GetField("_transportSlot", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(session);

        private static bool Closed(object slot) =>
            slot.GetType().GetProperty("State").GetValue(slot).ToString() == "Closed";
    }
}
