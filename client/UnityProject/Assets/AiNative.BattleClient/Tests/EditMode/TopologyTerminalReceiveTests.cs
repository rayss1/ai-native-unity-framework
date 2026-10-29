using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AiNative.Client.Assets;
using AiNative.Client.Fantasy;
using AiNative.Client.Protocol;
using AiNative.Client.Protocol.Protobuf;
using AiNative.Client.UI;
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
        public void OrdinaryUpdateDrainsLateTerminalAcrossBudgetWithoutSendingOrReconnecting(
            string state,
            bool completed
        )
        {
            var owner = new GameObject("LateTerminalFlow");
            try
            {
                var transport = new FakeTransport();
                var flow = CreateFlow(owner, transport);
                flow.Battle.PredictAndQueueInput(101, 1000, 0);
                int sentBefore = transport.SentFrames.Count;
                Set(flow, "State", state);
                Set(flow, "Completed", completed);
                Invoke(flow, "FixedUpdate");
                Assert.That(
                    flow.PreparedInputs,
                    Is.Zero,
                    "Receipt-tail states must not predict new input."
                );
                for (int i = 0; i < 300; i++)
                {
                    transport.Enqueue(
                        Frame((ulong)(101 + i)),
                        BattleClientProtocolV1.SnapshotChannel,
                        1
                    );
                }

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
                int receives = 0;
                transport.BeforeEmptyReceive = () => receives++;
                Update(flow);
                Update(flow);
                Assert.That(
                    receives,
                    Is.Zero,
                    "Closed and drained completion must not poll the old transport forever."
                );
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(owner);
            }
        }

        [Test]
        public void ReceiveOnlyCompletedFlowRejectsStaleEpochAndNeverReconnectsWithoutTerminal()
        {
            var owner = new GameObject("ClosedCompletedFlow");
            try
            {
                var transport = new FakeTransport();
                var flow = CreateFlow(owner, transport);
                Set(flow, "State", "Finished");
                Set(flow, "Completed", true);
                transport.Enqueue(Frame(110, true), BattleClientProtocolV1.SnapshotChannel, 99);
                transport.Close();
                Update(flow);
                Assert.That(flow.Battle.ArenaPhase, Is.EqualTo(ArenaMatchPhase.Active));
                Assert.That(flow.Battle.LastReceivedTick, Is.EqualTo(100));
                Assert.That(flow.Battle.State, Is.EqualTo(BattleClientState.Active));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(owner);
            }
        }

        [Test]
        public void ConfirmedSettlementEvidenceWaitsForLateTerminalWithoutChangingCompleted()
        {
            string path = Path.Combine(
                Path.GetTempPath(),
                "terminal-evidence-" + Guid.NewGuid().ToString("N") + ".json"
            );
            var owner = new GameObject("ConfirmedTerminalEvidence");
            try
            {
                var transport = new FakeTransport();
                var flow = CreateFlow(owner, transport, path);
                Set(flow, "State", "Finished");
                Set(flow, "Completed", true);
                Set(flow, "SettlementConfirmed", true);
                Invoke(flow, "WriteEvidence", true);
                Assert.That(File.ReadAllText(path), Does.Contain("\"success\": false"));
                Assert.That(flow.Completed && flow.SettlementConfirmed, Is.True);
                transport.Enqueue(Frame(110, true), BattleClientProtocolV1.SnapshotChannel, 1);
                transport.Close();
                Update(flow);
                string json = File.ReadAllText(path);
                Assert.That(
                    json,
                    Does.Contain("\"success\": true")
                        .And.Contain("\"finalReceived\": true")
                        .And.Contain("\"receiveDrainCompleted\": true")
                );
                Assert.That(flow.Completed && flow.SettlementConfirmed, Is.True);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(owner);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        [Test]
        public void AutomaticEvidenceDeadlineFailsHonestlyButDoesNotUndoConfirmedSettlement()
        {
            string path = Path.Combine(
                Path.GetTempPath(),
                "terminal-deadline-" + Guid.NewGuid().ToString("N") + ".json"
            );
            var owner = new GameObject("TerminalEvidenceDeadline");
            try
            {
                var transport = new FakeTransport();
                var flow = CreateFlow(owner, transport, path);
                Set(flow, "State", "Finished");
                Set(flow, "Completed", true);
                Set(flow, "SettlementConfirmed", true);
                Invoke(flow, "WriteEvidence", true);
                Assert.That(File.ReadAllText(path), Does.Contain("\"success\": false"));
                Invoke(flow, "AdvanceTerminalCompletion", 4.9f);
                Assert.That(flow.Error, Is.Empty);
                Invoke(flow, "AdvanceTerminalCompletion", 0.1f);
                string json = File.ReadAllText(path);
                Assert.That(
                    json,
                    Does.Contain("\"success\": false")
                        .And.Contain("terminal-receive-timeout")
                        .And.Contain("\"finalReceived\": false")
                );
                Assert.That(flow.Completed && flow.SettlementConfirmed, Is.True);
                Assert.That(flow.State, Is.EqualTo("Finished"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(owner);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        [Test]
        public async Task StoppingOneApplicationReleasesOnlyItsOwnedBattle()
        {
            var firstOwner = new GameObject("FirstBattleOwner");
            var secondOwner = new GameObject("SecondBattleOwner");
            TopologyClientFlow first = null;
            TopologyClientFlow second = null;
            try
            {
                first = CreateFlow(firstOwner, new FakeTransport());
                second = CreateFlow(secondOwner, new FakeTransport());
                BattleClientSession firstBattle = first.Battle;
                await firstOwner.GetComponent<FrameworkClientRoot>().StopAsync();
                await firstOwner.GetComponent<FrameworkClientRoot>().StopAsync();
                Assert.That(firstBattle.State, Is.EqualTo(BattleClientState.Disposed));
                Assert.That(second.Battle.State, Is.EqualTo(BattleClientState.Active));
                Assert.That(second.Battle.IsPredictionInitialized, Is.True);
            }
            finally
            {
                if (first != null)
                {
                    await firstOwner.GetComponent<FrameworkClientRoot>().StopAsync();
                }
                if (second != null)
                {
                    await secondOwner.GetComponent<FrameworkClientRoot>().StopAsync();
                }
                UnityEngine.Object.DestroyImmediate(firstOwner);
                UnityEngine.Object.DestroyImmediate(secondOwner);
            }
        }

        [Test]
        public async Task ModalBlocksOwnedFlowsInputWithoutStoppingPrediction()
        {
            var owner = new GameObject("ModalInputFixture");
            var otherOwner = new GameObject("OtherInputFixture");
            var ui = new UiService();
            var modal = new GameObject("Modal");
            Func<bool> previousFocus = TopologyClientFlow.EditorHasGameFocus;
            TopologyClientFlow flow = null;
            TopologyClientFlow other = null;
            try
            {
                TopologyClientFlow.EditorHasGameFocus = () => true;
                flow = CreateFlow(owner, new FakeTransport(), ui: ui);
                other = CreateFlow(otherOwner, new FakeTransport());
                Set(flow, "State", "Battle");
                Set(other, "State", "Battle");
                typeof(TopologyClientFlow)
                    .GetField("_manualMove", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(flow, new Vector2Int(1000, 0));
                typeof(TopologyClientFlow)
                    .GetField("_manualAim", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(flow, true);
                Assert.That(flow.IsCharacterAiming, Is.True);
                Assert.That(flow.Battle.TryGetArenaState(out var before), Is.True);
                ui.Register(
                    "test-modal",
                    UiLayer.Modal,
                    _ => new ValueTask<UiView>(
                        new UiView(
                            modal,
                            () =>
                            {
                                UnityEngine.Object.DestroyImmediate(modal);
                                return default;
                            }
                        )
                    )
                );
                await ui.OpenAsync("test-modal");
                Assert.That(flow.IsCharacterAiming, Is.False, "模态窗口必须阻止动态输入采样。");
                Assert.That(
                    (bool)
                        typeof(TopologyClientFlow)
                            .GetProperty(
                                "CanSampleCharacterInput",
                                BindingFlags.Instance | BindingFlags.NonPublic
                            )
                            .GetValue(other),
                    Is.True,
                    "其他应用的 UI 不得阻止此应用采样。"
                );
                Invoke(flow, "FixedUpdate");
                Assert.That(
                    flow.PreparedInputs,
                    Is.EqualTo(1),
                    "模态窗口只抑制输入，固定 Tick 仍推进。"
                );
                Assert.That(flow.Battle.TryGetArenaState(out var blocked), Is.True);
                Assert.That(blocked.PositionXMillimetres, Is.EqualTo(before.PositionXMillimetres));
                await ui.CloseAsync("test-modal");
                Assert.That(flow.IsCharacterAiming, Is.True);
                Invoke(flow, "FixedUpdate");
                Assert.That(flow.Battle.TryGetArenaState(out var resumed), Is.True);
                Assert.That(
                    resumed.PositionXMillimetres,
                    Is.GreaterThan(blocked.PositionXMillimetres)
                );
                Assert.That(flow.PreparedInputs, Is.EqualTo(2));
            }
            finally
            {
                TopologyClientFlow.EditorHasGameFocus = previousFocus;
                if (flow != null)
                {
                    await flow.StopAsync();
                }
                if (other != null)
                {
                    await other.StopAsync();
                }
                await ui.DisposeAsync();
                UnityEngine.Object.DestroyImmediate(owner);
                UnityEngine.Object.DestroyImmediate(otherOwner);
                if (modal != null)
                {
                    UnityEngine.Object.DestroyImmediate(modal);
                }
            }
        }

        private sealed class UnusedAssets : IAssetService
        {
            public ValueTask<AssetHandle<T>> LoadAsync<T>(
                AssetId id,
                CancellationToken cancellationToken = default
            )
            {
                throw new InvalidOperationException("This input fixture does not load UI assets.");
            }

            public bool TryGetLoaded<T>(AssetId id, out AssetHandle<T> handle)
            {
                handle = null;
                return false;
            }

            public ValueTask PreloadAsync(
                AssetGroupId group,
                CancellationToken cancellationToken = default
            )
            {
                throw new InvalidOperationException("This input fixture does not preload assets.");
            }

            public ValueTask ReleaseAsync(AssetHandle handle)
            {
                throw new InvalidOperationException("This input fixture owns no asset handles.");
            }
        }

        private static TopologyClientFlow CreateFlow(
            GameObject owner,
            FakeTransport transport,
            string path = "",
            UiService ui = null
        )
        {
            var flow = owner.AddComponent<TopologyClientFlow>();
            FrameworkClientRoot.GetOrCreate(owner);
            flow.Initialize(new GateConnectionOptions("127.0.0.1", 23001, false), resultPath: path);
            var battle = new BattleClientSession(
                new ProtobufBattleProtocolCodec(),
                "127.0.0.1",
                22000,
                "receipt-test",
                4,
                new FakeConnector(transport)
            );
            battle.Start();
            battle.Pump(0);
            transport.Enqueue(TestFrames.Login(42, 1), BattleClientProtocolV1.ControlChannel, 1);
            transport.Enqueue(TestFrames.Join(1, 7, 60), BattleClientProtocolV1.ControlChannel, 1);
            transport.Enqueue(Frame(100), BattleClientProtocolV1.SnapshotChannel, 1);
            battle.Pump(0);
            Set(flow, "Battle", battle);
            if (ui != null)
            {
                flow.ConfigureUi(new UnusedAssets(), ui);
            }
            // 单元夹具手动驱动运行态帧，真实模块启动由 PlayMode 覆盖。
            typeof(TopologyClientFlow)
                .GetField("_started", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(flow, true);
            return flow;
        }

        internal static byte[] Frame(ulong tick, bool finished = false)
        {
            var bytes = new System.Collections.Generic.List<byte> { 0x4d, 0x04, 0x08, 1, 0x11 };
            bytes.AddRange(BitConverter.GetBytes(tick));
            bytes.AddRange(
                new byte[]
                {
                    0x22,
                    8,
                    0x08,
                    7,
                    0x30,
                    100,
                    0x50,
                    1,
                    0x60,
                    1,
                    0x38,
                    finished ? (byte)2 : (byte)1,
                }
            );
            return bytes.ToArray();
        }

        private static void Set(object target, string property, object value) =>
            target
                .GetType()
                .GetProperty(property)
                .GetSetMethod(true)
                .Invoke(target, new[] { value });

        private static void Update(TopologyClientFlow flow) => Invoke(flow, "Update");

        private static void Invoke(TopologyClientFlow flow, string method, params object[] args) =>
            typeof(TopologyClientFlow)
                .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(flow, args);
    }
}
