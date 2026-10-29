using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AiNative.Client.Fantasy;
using AiNative.Client.Protocol;
using AiNative.Client.Protocol.Protobuf;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;

namespace AiNative.Client.Application.Tests
{
    public sealed class TopologyClientFlowRecoveryTests
    {
        [Test]
        public async Task ExpiredInteractiveSession_OffersAuthenticationAndKeepsOriginalBattle()
        {
            using var gate = new GatePeer();
            using var backend = new GateBackendSession(
                await FantasyGateClient.ConnectAsync(gate.Options)
            );
            await backend.LoginAsync("original-user", "original-password");
            var owner = new GameObject("ExpiredInteractiveFlow");
            try
            {
                var flow = CreateFlow(owner, gate.Options, backend, false);
                BattleClientSession battle = flow.Battle;
                SetProperty(
                    backend,
                    "ExpiresUnixSeconds",
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                );
                await Poll(flow);
                Assert.That(flow.BackendStatus, Is.Not.Empty);
                Assert.That(flow.State, Is.EqualTo("Battle"));
                Assert.That(flow.Battle, Is.SameAs(battle));
                Assert.That(
                    (flow.PlayerId, flow.MatchId, flow.RoomId),
                    Is.EqualTo(("original-player", "original-match", "original-room"))
                );
                Assert.That(
                    flow.AuthenticationRequired,
                    Is.True,
                    "A connected socket with an expired credential must expose login controls."
                );
                Assert.That(
                    backend.IsConnected,
                    Is.True,
                    "Credential expiry does not mean the socket disconnected."
                );
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(owner);
            }
        }

        [Test]
        public async Task ExpiredAutomatedSession_ReauthenticatesSameAccountAndCompletesOriginalSettlement()
        {
            using var gate = new GatePeer();
            using var backend = new GateBackendSession(
                await FantasyGateClient.ConnectAsync(gate.Options)
            );
            await backend.LoginAsync("original-user", "original-password");
            var owner = new GameObject("ExpiredAutomatedFlow");
            try
            {
                var flow = CreateFlow(owner, gate.Options, backend, true);
                BattleClientSession battle = flow.Battle;
                SetProperty(flow, "State", "Settling");
                SetProperty(flow, "Reconnected", true);
                SetProperty(
                    backend,
                    "ExpiresUnixSeconds",
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                );
                await Poll(flow);
                Assert.That(flow.Completed && flow.SettlementConfirmed, Is.True);
                Assert.That(flow.BackendReconnectCount, Is.EqualTo(1));
                Assert.That(flow.Battle, Is.SameAs(battle));
                Assert.That(
                    (flow.PlayerId, flow.MatchId, flow.RoomId),
                    Is.EqualTo(("original-player", "original-match", "original-room"))
                );
                Assert.That(
                    gate.LoginUsers.ToArray(),
                    Is.EqualTo(new[] { "original-user", "original-user" })
                );
                Assert.That(
                    gate.LoginPasswords.ToArray(),
                    Is.EqualTo(new[] { "original-password", "original-password" })
                );
                Assert.That(
                    gate.SettlementMatches.ToArray(),
                    Is.EqualTo(new[] { "original-match" })
                );
                Assert.That(flow.Error, Is.Empty);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(owner);
            }
        }

        [Test]
        public async Task ActualReceiptBeforeTerminalDoesNotPreventLaterOrdinaryUpdateReception()
        {
            using var gate = new GatePeer();
            using var backend = new GateBackendSession(
                await FantasyGateClient.ConnectAsync(gate.Options)
            );
            await backend.LoginAsync("original-user", "original-password");
            var owner = new GameObject("ReceiptBeforeTerminal");
            try
            {
                var flow = CreateFlow(owner, gate.Options, backend, false, createBattle: false);
                var transport = new FakeTransport();
                var battle = ActiveBattle(transport, new FakeConnector(transport));
                SetProperty(flow, "Battle", battle);
                transport.Enqueue(
                    TopologyTerminalReceiveTests.Frame(111),
                    BattleClientProtocolV1.SnapshotChannel,
                    1
                );
                battle.Pump(0);
                await Poll(flow);
                Assert.That(
                    flow.Completed && flow.SettlementConfirmed,
                    Is.True,
                    "The exact receipt confirms settlement independently of terminal reception."
                );
                Assert.That(
                    battle.ArenaPhase,
                    Is.EqualTo(AiNative.Gameplay.ArenaMatchPhase.Active)
                );
                transport.Enqueue(
                    TopologyTerminalReceiveTests.Frame(112, true),
                    BattleClientProtocolV1.SnapshotChannel,
                    1
                );
                transport.Close();
                typeof(TopologyClientFlow)
                    .GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(flow, null);
                Assert.That(
                    battle.ArenaPhase,
                    Is.EqualTo(AiNative.Gameplay.ArenaMatchPhase.Finished)
                );
                Assert.That(flow.Completed && flow.SettlementConfirmed, Is.True);
                Assert.That(
                    gate.SettlementMatches.ToArray(),
                    Is.EqualTo(new[] { "original-match" })
                );
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(owner);
            }
        }

        private static TopologyClientFlow CreateFlow(
            GameObject owner,
            GateConnectionOptions options,
            GateBackendSession backend,
            bool automated,
            string resultPath = "",
            bool createBattle = true
        )
        {
            var flow = owner.AddComponent<TopologyClientFlow>();
            FrameworkClientRoot.GetOrCreate(owner);
            flow.Initialize(options, automated, resultPath: resultPath);
            SetField(flow, "Backend", backend);
            SetField(flow, "_username", "original-user");
            SetField(flow, "_password", "original-password");
            var match = new GateMatch();
            SetProperty(match, "MatchId", "original-match");
            SetProperty(match, "RoomId", "original-room");
            SetProperty(match, "BootEpoch", "original-boot");
            SetProperty(match, "NodeId", "original-node");
            SetField(flow, "CurrentMatch", match);
            SetField(flow, "QueueRequestId", "original-request");
            if (createBattle)
            {
                SetProperty(
                    flow,
                    "Battle",
                    new BattleClientSession(
                        new ProtobufBattleProtocolCodec(),
                        "127.0.0.1",
                        22000,
                        new BattleAdmissionInfo(
                            "original-room",
                            "original-boot",
                            "ticket",
                            "original-node",
                            "original-player"
                        )
                    )
                );
            }
            SetProperty(flow, "State", "Battle");
            // 单元夹具手动驱动运行态帧，真实模块启动由 PlayMode 覆盖。
            SetField(flow, "_started", true);
            return flow;
        }

        [Test]
        public async Task ServerRejectedSession_RetriesAuthenticationWithoutFailingAutomatedBattle()
        {
            using var gate = new GatePeer();
            using var backend = new GateBackendSession(
                await FantasyGateClient.ConnectAsync(gate.Options)
            );
            await backend.LoginAsync("original-user", "original-password");
            var owner = new GameObject("RejectedAutomatedFlow");
            try
            {
                var flow = CreateFlow(owner, gate.Options, backend, true);
                gate.RejectNextProfile = true;
                typeof(TopologyClientFlow)
                    .GetMethod("Run", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(
                        flow,
                        new object[] { new Func<UniTask>(() => Poll(flow).AsUniTask()), false }
                    );
                while (
                    (bool)
                        typeof(TopologyClientFlow)
                            .GetField("_busy", BindingFlags.Instance | BindingFlags.NonPublic)
                            .GetValue(flow)
                )
                {
                    await Task.Yield();
                }

                Assert.That(
                    flow.Completed,
                    Is.False,
                    "An expired server credential must not end the automated battle."
                );
                Assert.That(flow.Error, Is.Empty);
                Assert.That(flow.State, Is.EqualTo("Battle"));
                SetProperty(flow, "State", "Settling");
                SetProperty(flow, "Reconnected", true);
                await Poll(flow);
                Assert.That(flow.SettlementConfirmed && flow.Completed, Is.True);
                Assert.That(
                    gate.LoginUsers.ToArray(),
                    Is.EqualTo(new[] { "original-user", "original-user" })
                );
                Assert.That(flow.MatchId, Is.EqualTo("original-match"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(owner);
            }
        }

        [Test]
        public async Task RecoveryRejectsDifferentPlayerWithoutReplacingOriginalBattleOrAllocation()
        {
            using var gate = new GatePeer();
            using var backend = new GateBackendSession(
                await FantasyGateClient.ConnectAsync(gate.Options)
            );
            await backend.LoginAsync("original-user", "original-password");
            var owner = new GameObject("MismatchedRecoveryFlow");
            try
            {
                var flow = CreateFlow(owner, gate.Options, backend, true);
                BattleClientSession battle = flow.Battle;
                SetProperty(
                    backend,
                    "ExpiresUnixSeconds",
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                );
                gate.LoginPlayer = "different-player";
                GateCallException rejection = null;
                try
                {
                    await Poll(flow);
                }
                catch (GateCallException exception)
                {
                    rejection = exception;
                }

                Assert.That(rejection, Is.Not.Null);
                Assert.That(rejection.Code, Is.EqualTo("account-mismatch-active-battle"));
                Assert.That(flow.Battle, Is.SameAs(battle));
                Assert.That(
                    (flow.PlayerId, flow.MatchId, flow.RoomId),
                    Is.EqualTo(("original-player", "original-match", "original-room"))
                );
                Assert.That(flow.BackendReconnectCount, Is.Zero);
                Assert.That(gate.SettlementMatches, Is.Empty);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(owner);
            }
        }

        [Test]
        public async Task WaitingForBackendAuthentication_LeavesFixedTickPredictionRunning()
        {
            using var gate = new GatePeer();
            using var backend = new GateBackendSession(
                await FantasyGateClient.ConnectAsync(gate.Options)
            );
            await backend.LoginAsync("original-user", "original-password");
            var owner = new GameObject("RecoveringBattleFlow");
            try
            {
                var flow = CreateFlow(owner, gate.Options, backend, true, createBattle: false);
                var transport = new FakeTransport();
                var battle = new BattleClientSession(
                    new ProtobufBattleProtocolCodec(),
                    "127.0.0.1",
                    22000,
                    "fixture",
                    4,
                    new FakeConnector(transport),
                    new BattleAdmissionInfo(
                        "original-room",
                        "original-boot",
                        "ticket",
                        "original-node",
                        "original-player"
                    )
                );
                SetProperty(flow, "Battle", battle);
                SetProperty(flow, "Reconnected", true);
                battle.Start();
                battle.Pump(0);
                transport.Enqueue(
                    TestFrames.Login(42, 1, "original-room", "original-boot"),
                    BattleClientProtocolV1.ControlChannel,
                    1
                );
                transport.Enqueue(
                    TestFrames.Join(19, 7, 60),
                    BattleClientProtocolV1.ControlChannel,
                    1
                );
                transport.Enqueue(
                    TestFrames.Snapshot(7, 110, 0),
                    BattleClientProtocolV1.SnapshotChannel,
                    1
                );
                battle.Pump(0);
                Assert.That(battle.IsPredictionInitialized, Is.True);
                SetProperty(
                    backend,
                    "ExpiresUnixSeconds",
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                );
                gate.PauseLogin = true;
                Task recovering = Poll(flow);
                await Task.WhenAny(gate.LoginBlocked.Task, Task.Delay(2500));
                Assert.That(
                    gate.LoginBlocked.Task.IsCompleted,
                    Is.True,
                    "The replacement login did not arrive."
                );
                Assert.That(recovering.IsCompleted, Is.False);
                typeof(TopologyClientFlow)
                    .GetMethod("FixedUpdate", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(flow, null);
                Assert.That(
                    flow.PreparedInputs,
                    Is.EqualTo(1),
                    "Backend authentication must not stop battle prediction."
                );
                Assert.That(battle.QueuedInputFrames, Is.EqualTo(1));
                Assert.That(flow.Battle, Is.SameAs(battle));
                gate.ReleaseLogin.TrySetResult(true);
                await recovering;
                Assert.That(flow.SettlementConfirmed, Is.True);
            }
            finally
            {
                gate.ReleaseLogin.TrySetResult(true);
                UnityEngine.Object.DestroyImmediate(owner);
            }
        }

        [Test]
        public async Task PauseResume_WaitingForAdmissionDoesNotPredictOrQueueStaleInputs()
        {
            using var gate = new GatePeer { SettlementConfirmed = false };
            using var backend = new GateBackendSession(
                await FantasyGateClient.ConnectAsync(gate.Options)
            );
            await backend.LoginAsync("original-user", "original-password");
            var owner = new GameObject("WaitingForResumeAdmission");
            Task recovering = null;
            try
            {
                var flow = CreateFlow(owner, gate.Options, backend, true, createBattle: false);
                var transport = new FakeTransport();
                var battle = ActiveBattle(
                    transport,
                    new FakeConnector(transport, new FakeTransport())
                );
                SetProperty(flow, "Battle", battle);
                Pause(flow, true);
                SetProperty(
                    backend,
                    "ExpiresUnixSeconds",
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                );
                gate.PauseLogin = true;
                Pause(flow, false);
                recovering = Poll(flow);
                await Task.WhenAny(gate.LoginBlocked.Task, Task.Delay(2500));
                Assert.That(
                    gate.LoginBlocked.Task.IsCompleted,
                    Is.True,
                    "Resume authentication must be waiting for fresh admission."
                );
                Assert.That(recovering.IsCompleted, Is.False);
                Assert.That(
                    battle.State,
                    Is.EqualTo(BattleClientState.Active),
                    "The old connection remains active until admission renews."
                );
                MethodInfo fixedUpdate = typeof(TopologyClientFlow).GetMethod(
                    "FixedUpdate",
                    BindingFlags.Instance | BindingFlags.NonPublic
                );
                for (int step = 0; step < 8; step++)
                {
                    fixedUpdate.Invoke(flow, null);
                }

                Assert.That(
                    flow.PreparedInputs,
                    Is.Zero,
                    "Resume must not predict commands against the suspended connection."
                );
                Assert.That(
                    battle.QueuedInputFrames,
                    Is.Zero,
                    "Admission waiting must not fill the old input queue."
                );
                gate.ReleaseLogin.TrySetResult(true);
                await recovering;
                Assert.That(battle.State, Is.EqualTo(BattleClientState.Reconnecting));
                Assert.That(flow.Battle, Is.SameAs(battle));
                Assert.That(flow.ResumeCompletedCount, Is.EqualTo(1));
            }
            finally
            {
                gate.ReleaseLogin.TrySetResult(true);
                if (recovering != null)
                {
                    await recovering;
                }

                UnityEngine.Object.DestroyImmediate(owner);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task PauseResume_RefreshesOriginalAdmissionAndRestartsBattleOnce(bool faulted)
        {
            using var gate = new GatePeer { SettlementConfirmed = false };
            using var backend = new GateBackendSession(
                await FantasyGateClient.ConnectAsync(gate.Options)
            );
            await backend.LoginAsync("original-user", "original-password");
            var owner = new GameObject("ResumingOriginalBattle");
            try
            {
                var flow = CreateFlow(owner, gate.Options, backend, true, createBattle: false);
                var transport = new FakeTransport();
                var replacement = new FakeTransport();
                var connector = new FakeConnector(
                    transport,
                    faulted ? null : replacement,
                    null,
                    null,
                    replacement
                );
                var battle = ActiveBattle(transport, connector);
                SetProperty(flow, "Battle", battle);
                Pause(flow, false); // Unity sends an initial false; it must not start recovery.
                await Poll(flow);
                Assert.That(battle.State, Is.EqualTo(BattleClientState.Active));
                Pause(flow, true);
                if (faulted)
                {
                    transport.Close();
                    battle.Pump(0);
                    battle.Pump(.3f);
                    battle.Pump(0);
                    battle.Pump(.6f);
                    battle.Pump(0);
                    battle.Pump(1.1f);
                    battle.Pump(0);
                    Assert.That(
                        battle.State,
                        Is.EqualTo(BattleClientState.Faulted),
                        "The fixture must exhaust ordinary reconnect retries."
                    );
                }

                SetProperty(
                    backend,
                    "ExpiresUnixSeconds",
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                );
                Pause(flow, false);
                await Poll(flow);
                Assert.That(
                    battle.State,
                    Is.EqualTo(BattleClientState.Reconnecting),
                    "Resume must restart the original battle, including exhausted connections."
                );
                Assert.That(flow.Battle, Is.SameAs(battle));
                Assert.That(
                    (flow.PlayerId, flow.MatchId, flow.RoomId),
                    Is.EqualTo(("original-player", "original-match", "original-room"))
                );
                Assert.That(flow.BackendReconnectCount, Is.EqualTo(1));
                int before = connector.CallCount;
                battle.Pump(.3f);
                battle.Pump(0);
                Assert.That(connector.CallCount, Is.EqualTo(before + 1));
                Assert.That(
                    replacement.SentFrames.Count,
                    Is.EqualTo(1),
                    "The fresh transport must receive the renewed entry ticket login."
                );
                Assert.That(
                    Encoding.UTF8.GetString(replacement.SentFrames[0]),
                    Does.Contain("renewed-ticket")
                );
                Pause(flow, false);
                await Poll(flow);
                Assert.That(
                    connector.CallCount,
                    Is.EqualTo(before + 1),
                    "Duplicate resume must not create another connection."
                );
                Assert.That(gate.LoginUsers.Count, Is.EqualTo(2));
                Assert.That(gate.MatchRequests.ToArray(), Has.All.EqualTo("original-request"));
                Assert.That(flow.Completed, Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(owner);
            }
        }

        [TestCase("match")]
        [TestCase("room")]
        [TestCase("node")]
        [TestCase("boot")]
        [TestCase("player")]
        public async Task PauseResume_RejectsChangedIdentityAndPreservesOriginalAllocation(
            string changed
        )
        {
            using var gate = new GatePeer
            {
                SettlementConfirmed = false,
                ChangedAllocation = changed,
            };
            using var backend = new GateBackendSession(
                await FantasyGateClient.ConnectAsync(gate.Options)
            );
            await backend.LoginAsync("original-user", "original-password");
            var owner = new GameObject("RejectingResumeAllocation");
            try
            {
                var flow = CreateFlow(owner, gate.Options, backend, true, createBattle: false);
                var transport = new FakeTransport();
                var connector = new FakeConnector(transport, new FakeTransport());
                var battle = ActiveBattle(transport, connector);
                SetProperty(flow, "Battle", battle);
                if (changed == "player")
                {
                    gate.LoginPlayer = "different-player";
                    SetProperty(
                        backend,
                        "ExpiresUnixSeconds",
                        DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    );
                }

                Pause(flow, true);
                Pause(flow, false);
                Exception rejection = null;
                try
                {
                    await Poll(flow);
                }
                catch (Exception exception)
                {
                    rejection = exception;
                }

                Assert.That(
                    rejection,
                    Is.Not.Null,
                    "Every component of the original allocation identity must be checked before reconnecting."
                );
                Assert.That(battle.State, Is.EqualTo(BattleClientState.Active));
                Assert.That(connector.CallCount, Is.EqualTo(1));
                Assert.That(flow.Battle, Is.SameAs(battle));
                Assert.That(
                    (flow.PlayerId, flow.MatchId, flow.RoomId),
                    Is.EqualTo(("original-player", "original-match", "original-room"))
                );
                Assert.That(flow.SettlementConfirmed, Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(owner);
            }
        }

        [Test]
        public async Task BackgroundPause_SuspendsBattlePumpAndFocusLossDoesNotResume()
        {
            using var gate = new GatePeer { SettlementConfirmed = false };
            using var backend = new GateBackendSession(
                await FantasyGateClient.ConnectAsync(gate.Options)
            );
            await backend.LoginAsync("original-user", "original-password");
            var owner = new GameObject("PausedBattle");
            try
            {
                var flow = CreateFlow(owner, gate.Options, backend, false, createBattle: false);
                var transport = new FakeTransport();
                var connector = new FakeConnector(transport);
                var battle = ActiveBattle(transport, connector);
                SetProperty(flow, "Battle", battle);
                Pause(flow, true);
                transport.Close();
                typeof(TopologyClientFlow)
                    .GetMethod("OnApplicationFocus", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.Invoke(flow, new object[] { false });
                typeof(TopologyClientFlow)
                    .GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(flow, null);
                Assert.That(
                    battle.State,
                    Is.EqualTo(BattleClientState.Active),
                    "Background time must not consume reconnect attempts before fresh admission is available."
                );
                Assert.That(gate.MatchRequests, Is.Empty);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(owner);
            }
        }

        private static BattleClientSession ActiveBattle(
            FakeTransport transport,
            FakeConnector connector
        )
        {
            var battle = new BattleClientSession(
                new ProtobufBattleProtocolCodec(),
                "127.0.0.1",
                22000,
                "fixture",
                4,
                connector,
                new BattleAdmissionInfo(
                    "original-room",
                    "original-boot",
                    "ticket",
                    "original-node",
                    "original-player"
                )
            );
            battle.Start();
            battle.Pump(0);
            transport.Enqueue(
                TestFrames.Login(42, 1, "original-room", "original-boot"),
                BattleClientProtocolV1.ControlChannel,
                1
            );
            transport.Enqueue(TestFrames.Join(19, 7, 60), BattleClientProtocolV1.ControlChannel, 1);
            transport.Enqueue(
                TestFrames.Snapshot(7, 110, 0),
                BattleClientProtocolV1.SnapshotChannel,
                1
            );
            battle.Pump(0);
            Assert.That(battle.IsPredictionInitialized, Is.True);
            return battle;
        }

        [Test]
        public async Task Pause_WritesPublicLifecycleCheckpointWithoutCredentials()
        {
            using var gate = new GatePeer();
            using var backend = new GateBackendSession(
                await FantasyGateClient.ConnectAsync(gate.Options)
            );
            await backend.LoginAsync("original-user", "original-password");
            string result = Path.Combine(
                Path.GetTempPath(),
                "ainative-checkpoint-" + Guid.NewGuid().ToString("N") + ".json"
            );
            var owner = new GameObject("CheckpointBattle");
            try
            {
                var flow = CreateFlow(owner, gate.Options, backend, true, result);
                Pause(flow, true);
                Assert.That(
                    File.Exists(result),
                    Is.True,
                    "A real-device driver needs a persistent pause checkpoint before returning to the app."
                );
                string json = File.ReadAllText(result);
                Assert.That(
                    json,
                    Does.Contain("original-match")
                        .And.Contain("original-room")
                        .And.Contain("original-node")
                        .And.Contain("original-boot")
                );
                Assert.That(
                    json,
                    Does.Not.Contain("original-user")
                        .And.Not.Contain("original-password")
                        .And.Not.Contain("renewed-credential")
                        .And.Not.Contain("ticket")
                );
                Assert.That(
                    json,
                    Does.Contain("\"paused\": true").And.Contain("\"completed\": false")
                );
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(owner);
                if (File.Exists(result))
                {
                    File.Delete(result);
                }
            }
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task PauseResume_LostQueueKeepsOriginalSettlementWithoutStartingAnotherBattle(
            bool confirmed
        )
        {
            using var gate = new GatePeer
            {
                AllocationLost = true,
                SettlementConfirmed = confirmed,
            };
            using var backend = new GateBackendSession(
                await FantasyGateClient.ConnectAsync(gate.Options)
            );
            await backend.LoginAsync("original-user", "original-password");
            var owner = new GameObject("LostQueueResume");
            try
            {
                var flow = CreateFlow(owner, gate.Options, backend, true, createBattle: false);
                var transport = new FakeTransport();
                var replacement = new FakeTransport();
                var connector = new FakeConnector(transport, replacement);
                var battle = ActiveBattle(transport, connector);
                SetProperty(flow, "Battle", battle);
                SetProperty(flow, "Reconnected", true);
                Pause(flow, true);
                Pause(flow, false);
                Exception rejection = null;
                try
                {
                    await Poll(flow);
                }
                catch (Exception exception)
                {
                    rejection = exception;
                }

                Assert.That(
                    rejection,
                    Is.Null,
                    "Losing the queue cannot prevent querying the original receipt."
                );
                Assert.That(flow.Completed, Is.EqualTo(confirmed));
                Assert.That(flow.SettlementConfirmed, Is.EqualTo(confirmed));
                Assert.That(flow.Battle, Is.SameAs(battle));
                Assert.That(
                    (flow.PlayerId, flow.MatchId, flow.RoomId),
                    Is.EqualTo(("original-player", "original-match", "original-room"))
                );
                Assert.That(battle.State, Is.EqualTo(BattleClientState.Active));
                Assert.That(connector.CallCount, Is.EqualTo(1));
                Assert.That(
                    gate.SettlementMatches.ToArray(),
                    Is.EqualTo(new[] { "original-match" })
                );
                Assert.That(
                    gate.Methods.ToArray(),
                    Does.Not.Contain("player.register").And.Not.Contain("match.queue")
                );
                Assert.That(flow.Error, Is.Empty);
                if (!confirmed)
                {
                    Assert.That(flow.BackendStatus, Is.Not.Empty);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(owner);
            }
        }

        [Test]
        public async Task PauseResume_RejectsReceiptForAnotherMatchAndWaitsForOriginalReceipt()
        {
            using var gate = new GatePeer
            {
                AllocationLost = true,
                SettlementConfirmed = true,
                ReceiptMatch = "another-match",
            };
            using var backend = new GateBackendSession(
                await FantasyGateClient.ConnectAsync(gate.Options)
            );
            await backend.LoginAsync("original-user", "original-password");
            var owner = new GameObject("MismatchedReceiptResume");
            try
            {
                var flow = CreateFlow(owner, gate.Options, backend, true, createBattle: false);
                var transport = new FakeTransport();
                var connector = new FakeConnector(transport, new FakeTransport());
                var battle = ActiveBattle(transport, connector);
                SetProperty(flow, "Battle", battle);
                SetProperty(flow, "Reconnected", true);
                Pause(flow, true);
                Pause(flow, false);
                GateCallException rejection = null;
                try
                {
                    await Poll(flow);
                }
                catch (GateCallException exception)
                {
                    rejection = exception;
                }

                Assert.That(rejection?.Code, Is.EqualTo("settlement-mismatch-active-battle"));
                Assert.That(flow.Completed || flow.SettlementConfirmed, Is.False);
                Assert.That(flow.Battle, Is.SameAs(battle));
                Assert.That(flow.MatchId, Is.EqualTo("original-match"));
                Assert.That(battle.State, Is.EqualTo(BattleClientState.Active));
                Assert.That(connector.CallCount, Is.EqualTo(1));
                gate.ReceiptMatch = "original-match";
                await Poll(flow);
                Assert.That(flow.Completed && flow.SettlementConfirmed, Is.True);
                Assert.That(
                    gate.SettlementMatches.ToArray(),
                    Is.EqualTo(new[] { "original-match", "original-match" })
                );
                Assert.That(
                    gate.Methods.ToArray(),
                    Does.Not.Contain("player.register").And.Not.Contain("match.queue")
                );
                Assert.That(flow.Battle, Is.SameAs(battle));
                Assert.That(connector.CallCount, Is.EqualTo(1));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(owner);
            }
        }

        [Test]
        public async Task CompletedReturn_UsesClearedAuthoritativePartyWithoutQueryingPrunedMatch()
        {
            using var gate = new GatePeer { PartyQueueRequest = "", MatchExpired = true };
            using var backend = new GateBackendSession(
                await FantasyGateClient.ConnectAsync(gate.Options)
            );
            await backend.LoginAsync("original-user", "original-password");
            var owner = new GameObject("CompletedReturnWithPrunedMatch");
            try
            {
                var flow = CreateFinishedFlow(owner, gate, backend);
                SetProperty(flow, "PreparedInputs", 400L);
                SetProperty(flow, "Reconnected", true);
                SetField(flow, "_resumePending", true);
                SetField(flow, "_baselinePlayed", 0L);
                await Return(flow);
                Assert.That(flow.State, Is.EqualTo("Lobby"));
                Assert.That(flow.Battle, Is.Null);
                Assert.That(flow.MatchId, Is.Empty);
                Assert.That(flow.RoomId, Is.Empty);
                Assert.That(
                    flow.Completed || flow.SettlementConfirmed || flow.Reconnected,
                    Is.False
                );
                Assert.That(flow.PreparedInputs, Is.Zero);
                Assert.That(GetField(flow, "QueueRequestId"), Is.EqualTo(""));
                Assert.That(GetField(flow, "_resumePending"), Is.EqualTo(false));
                Assert.That(GetField(flow, "_baselinePlayed"), Is.EqualTo(flow.Profile.Played));
                Assert.That(flow.Profile.Played, Is.EqualTo(1));
                Assert.That(GetField(flow, "Backend"), Is.SameAs(backend));
                Assert.That(backend.IsAuthenticated, Is.True);
                Assert.That(flow.PlayerId, Is.EqualTo("original-player"));
                Assert.That(flow.Party.PartyId, Is.EqualTo("original-party"));
                Assert.That(flow.Party.QueueRequestId, Is.Empty);
                Assert.That(flow.Party.Members[0].Ready, Is.False);
                Assert.That(
                    gate.MatchRequests,
                    Is.Empty,
                    "A pruned Match record must not prevent returning after Lobby has released its queue."
                );
                Assert.That(gate.Methods.ToArray(), Does.Not.Contain("match.cancel"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(owner);
            }
        }

        [TestCase("original-request", "match-still-active")]
        [TestCase("another-request", "state-changed")]
        public async Task CompletedReturn_RefusesPartyWithLiveOrDifferentQueue(
            string queue,
            string error
        )
        {
            using var gate = new GatePeer { PartyQueueRequest = queue };
            using var backend = new GateBackendSession(
                await FantasyGateClient.ConnectAsync(gate.Options)
            );
            await backend.LoginAsync("original-user", "original-password");
            var owner = new GameObject("CompletedReturnWithActiveQueue");
            try
            {
                var flow = CreateFinishedFlow(owner, gate, backend);
                // 私有流程超时策略缩短，仅此永不释放的测试不等待产品的 20 秒上限。
                if (queue == "original-request")
                {
                    SetField(flow, "_returnToLobbyTimeout", TimeSpan.FromMilliseconds(100));
                }
                BattleClientSession battle = flow.Battle;
                GateCallException rejection = null;
                try
                {
                    await Return(flow);
                }
                catch (GateCallException exception)
                {
                    rejection = exception;
                }

                Assert.That(rejection?.Code, Is.EqualTo(error));
                Assert.That(flow.State, Is.EqualTo("Finished"));
                Assert.That(flow.Completed && flow.SettlementConfirmed, Is.True);
                Assert.That(flow.Battle, Is.SameAs(battle));
                Assert.That(flow.MatchId, Is.EqualTo("original-match"));
                Assert.That(GetField(flow, "QueueRequestId"), Is.EqualTo("original-request"));
                if (queue != "original-request")
                {
                    Assert.That(gate.MatchRequests, Is.Empty);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(owner);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task CompletedReturn_WaitsForOriginalAllocationReleaseAndKeepsCommandBusy(
            bool invalidAllocationWindow
        )
        {
            using var gate = new GatePeer
            {
                PauseMatch = true,
                TerminalAfterStatusRequests = 2,
                TransientInvalidAllocation = invalidAllocationWindow,
            };
            using var backend = new GateBackendSession(
                await FantasyGateClient.ConnectAsync(gate.Options)
            );
            await backend.LoginAsync("original-user", "original-password");
            var owner = new GameObject("DelayedRoomRelease");
            TopologyClientFlow flow = null;
            TopologyViewModel viewModel = null;
            try
            {
                flow = CreateFinishedFlow(owner, gate, backend);
                BattleClientSession battle = flow.Battle;
                viewModel = new TopologyViewModel(flow, _ => { });
                Task returning = viewModel.Back.ExecuteAsync().AsTask();
                await Task.WhenAny(gate.MatchBlocked.Task, Task.Delay(2500));
                Assert.That(gate.MatchBlocked.Task.IsCompleted, Is.True);
                Assert.That(viewModel.Back.IsExecuting, Is.True);
                Assert.That(viewModel.Back.CanExecute, Is.False);
                Assert.That(flow.UiBusy, Is.True);
                Assert.That(flow.Battle, Is.SameAs(battle));
                Assert.That(battle.State, Is.Not.EqualTo(BattleClientState.Disposed));
                viewModel.Refresh();
                Assert.That(viewModel.Status.Value, Is.EqualTo("正在等待房间释放…"));
                gate.ReleaseMatch.TrySetResult(true);
                await Task.WhenAny(returning, Task.Delay(4000));
                Assert.That(returning.IsCompleted, Is.True, "终态传播后必须完成返回。");
                await returning;
                Assert.That(flow.Error, Is.Empty);
                Assert.That(viewModel.Back.Error, Is.Null);
                Assert.That(flow.State, Is.EqualTo("Lobby"));
                Assert.That(flow.Battle, Is.Null);
                Assert.That(battle.State, Is.EqualTo(BattleClientState.Disposed));
                Assert.That(flow.Party.QueueRequestId, Is.Empty);
                Assert.That(gate.MatchRequests.Count, Is.EqualTo(2));
                Assert.That(gate.Methods.ToArray(), Does.Not.Contain("match.cancel"));
            }
            finally
            {
                gate.ReleaseMatch.TrySetResult(true);
                viewModel?.Dispose();
                if (flow != null)
                {
                    await flow.StopAsync();
                }
                UnityEngine.Object.DestroyImmediate(owner);
            }
        }

        [TestCase("unexpected", "", "state-changed")]
        [TestCase("ready", "match", "allocation-mismatch-active-battle")]
        public async Task CompletedReturn_RejectsUnknownStateOrChangedAllocationImmediately(
            string state,
            string changedAllocation,
            string error
        )
        {
            using var gate = new GatePeer
            {
                MatchState = state,
                ChangedAllocation = changedAllocation,
            };
            using var backend = new GateBackendSession(
                await FantasyGateClient.ConnectAsync(gate.Options)
            );
            await backend.LoginAsync("original-user", "original-password");
            var owner = new GameObject("InvalidReleaseIdentity");
            TopologyClientFlow flow = null;
            try
            {
                flow = CreateFinishedFlow(owner, gate, backend);
                BattleClientSession battle = flow.Battle;
                Task returning = Return(flow);
                await Task.WhenAny(returning, Task.Delay(2500));
                Assert.That(returning.IsCompleted, Is.True, "未知状态或身份变化不得进入重试等待。");
                GateCallException rejection = Assert.ThrowsAsync<GateCallException>(async () =>
                    await returning
                );
                Assert.That(rejection.Code, Is.EqualTo(error));
                Assert.That(gate.MatchRequests.Count, Is.EqualTo(1));
                Assert.That(flow.Battle, Is.SameAs(battle));
                Assert.That(battle.State, Is.Not.EqualTo(BattleClientState.Disposed));
            }
            finally
            {
                if (flow != null)
                {
                    await flow.StopAsync();
                }
                UnityEngine.Object.DestroyImmediate(owner);
            }
        }

        [Test]
        public async Task CompletedReturn_CancellationPreservesOriginalBattleAndQueue()
        {
            using var gate = new GatePeer { PauseMatch = true };
            using var backend = new GateBackendSession(
                await FantasyGateClient.ConnectAsync(gate.Options)
            );
            await backend.LoginAsync("original-user", "original-password");
            var owner = new GameObject("CancelledRoomRelease");
            TopologyClientFlow flow = null;
            try
            {
                flow = CreateFinishedFlow(owner, gate, backend);
                BattleClientSession battle = flow.Battle;
                Task returning = Return(flow);
                await Task.WhenAny(gate.MatchBlocked.Task, Task.Delay(2500));
                Assert.That(gate.MatchBlocked.Task.IsCompleted, Is.True);
                ((CancellationTokenSource)GetField(flow, "_lifetime")).Cancel();
                await Task.WhenAny(returning, Task.Delay(2500));
                Assert.That(returning.IsCompleted, Is.True);
                Assert.CatchAsync<OperationCanceledException>(async () => await returning);
                Assert.That(flow.Battle, Is.SameAs(battle));
                Assert.That(battle.State, Is.Not.EqualTo(BattleClientState.Disposed));
                Assert.That(flow.State, Is.EqualTo("Finished"));
                Assert.That(GetField(flow, "QueueRequestId"), Is.EqualTo("original-request"));
                Assert.That(gate.Methods.ToArray(), Does.Not.Contain("match.cancel"));
            }
            finally
            {
                gate.ReleaseMatch.TrySetResult(true);
                if (flow != null)
                {
                    await flow.StopAsync();
                }
                UnityEngine.Object.DestroyImmediate(owner);
            }
        }

        [Test]
        public async Task CompletedReauthentication_KeepsFinishedScreenAndOriginalReceipt()
        {
            using var gate = new GatePeer();
            using var backend = new GateBackendSession(
                await FantasyGateClient.ConnectAsync(gate.Options)
            );
            await backend.LoginAsync("original-user", "original-password");
            var owner = new GameObject("CompletedReauthentication");
            try
            {
                var flow = CreateFinishedFlow(owner, gate, backend);
                BattleClientSession battle = flow.Battle;
                SetProperty(
                    backend,
                    "ExpiresUnixSeconds",
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                );
                flow.Authenticate(
                    false,
                    "original_user",
                    "original-password",
                    gate.Options.Host,
                    gate.Options.Port.ToString(),
                    false,
                    ""
                );
                DateTime deadline = DateTime.UtcNow.AddSeconds(3);
                while (flow.UiBusy && DateTime.UtcNow < deadline)
                {
                    await Task.Yield();
                }

                Assert.That(flow.UiBusy, Is.False);
                Assert.That(flow.Error, Is.Empty);
                Assert.That(flow.AuthenticationRequired, Is.False);
                Assert.That(flow.State, Is.EqualTo("Finished"));
                Assert.That(flow.Completed && flow.SettlementConfirmed, Is.True);
                Assert.That(flow.Battle, Is.SameAs(battle));
                Assert.That(flow.MatchId, Is.EqualTo("original-match"));
                Assert.That(flow.BackendReconnectCount, Is.EqualTo(1));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(owner);
            }
        }

        private static TopologyClientFlow CreateFinishedFlow(
            GameObject owner,
            GatePeer gate,
            GateBackendSession backend
        )
        {
            var flow = CreateFlow(owner, gate.Options, backend, false);
            var party = new GateParty();
            SetProperty(party, "PartyId", "original-party");
            SetProperty(party, "QueueRequestId", "original-request");
            SetField(flow, "LobbyParty", party);
            SetProperty(flow, "Completed", true);
            SetProperty(flow, "SettlementConfirmed", true);
            SetProperty(flow, "State", "Finished");
            return flow;
        }

        [Test]
        public async Task DestroyAfterSuccessfulMatchResponse_DoesNotStartOrphanedBattleFromLateContinuation()
        {
            using var gate = new GatePeer { PauseMatch = true };
            using var backend = new GateBackendSession(
                await FantasyGateClient.ConnectAsync(gate.Options)
            );
            await backend.LoginAsync("original-user", "original-password");
            var owner = new GameObject("LateMatchContinuation");
            var held = new HeldContinuationContext();
            TopologyClientFlow flow = null;
            try
            {
                flow = owner.AddComponent<TopologyClientFlow>();
                FrameworkClientRoot.GetOrCreate(owner);
                flow.Initialize(gate.Options, false);
                SetField(flow, "Backend", backend);
                SetField(flow, "QueueRequestId", "original-request");
                SetProperty(flow, "State", "Queued");
                Task polling;
                SynchronizationContext original = SynchronizationContext.Current;
                try
                {
                    // 仅暂停本次操作的主线程继续执行，不暂停测试运行器。
                    SynchronizationContext.SetSynchronizationContext(held);
                    polling = (
                        (UniTask)
                            typeof(TopologyClientFlow)
                                .GetMethod(
                                    "PollCoreAsync",
                                    BindingFlags.Instance | BindingFlags.NonPublic
                                )
                                .Invoke(flow, null)
                    ).AsTask();
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(original);
                }

                await Task.WhenAny(gate.MatchBlocked.Task, Task.Delay(2500));
                Assert.That(gate.MatchBlocked.Task.IsCompleted, Is.True);
                gate.ReleaseMatch.TrySetResult(true);
                await Task.WhenAny(held.Posted.Task, Task.Delay(2500));
                Assert.That(
                    held.Posted.Task.IsCompleted,
                    Is.True,
                    "The successful match response must have reached the captured main-thread continuation."
                );
                Assert.That(polling.IsCompleted, Is.False);
                // EditMode 需要显式调用生命周期；网络响应已成功，
                // 因此仅取消令牌无法撤销该结果。
                typeof(TopologyClientFlow)
                    .GetMethod("OnDestroy", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(flow, null);
                for (int attempt = 0; attempt < 250 && !polling.IsCompleted; attempt++)
                {
                    held.Drain();
                    if (!polling.IsCompleted)
                    {
                        await Task.Delay(10);
                    }
                }

                Assert.That(
                    polling.IsCompleted,
                    Is.True,
                    "The released continuation must finish without hanging the test runner."
                );
                await polling;
                Assert.That(
                    flow.Battle,
                    Is.Null,
                    "A late match response must not create a connection after its owner is destroyed."
                );
                Assert.That(
                    flow.State,
                    Is.EqualTo("Queued"),
                    "The disposed flow must not enter Battle."
                );
            }
            finally
            {
                gate.ReleaseMatch.TrySetResult(true);
                held.Drain();
                if (flow != null && flow.Battle != null)
                {
                    await flow.Battle.DisposeAsync();
                }

                UnityEngine.Object.DestroyImmediate(owner);
            }
        }

        private sealed class HeldContinuationContext : SynchronizationContext
        {
            private readonly ConcurrentQueue<Action> _callbacks = new ConcurrentQueue<Action>();
            internal readonly TaskCompletionSource<bool> Posted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );

            public override void Post(SendOrPostCallback callback, object state)
            {
                _callbacks.Enqueue(() => callback(state));
                Posted.TrySetResult(true);
            }

            internal void Drain()
            {
                while (_callbacks.TryDequeue(out Action callback))
                {
                    callback();
                }
            }
        }

        private static Task Return(TopologyClientFlow flow) =>
            (
                (UniTask)
                    typeof(TopologyClientFlow)
                        .GetMethod(
                            "ReturnToLobbyAsync",
                            BindingFlags.Instance | BindingFlags.NonPublic
                        )
                        .Invoke(flow, null)
            ).AsTask();

        private static object GetField(object target, string field)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            FieldInfo member = target.GetType().GetField(field, flags);
            return member != null
                ? member.GetValue(target)
                : target.GetType().GetProperty(field, flags).GetValue(target);
        }

        private static void Pause(TopologyClientFlow flow, bool paused) =>
            typeof(TopologyClientFlow)
                .GetMethod("OnApplicationPause", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.Invoke(flow, new object[] { paused });

        private static Task Poll(TopologyClientFlow flow) =>
            (
                (UniTask)
                    typeof(TopologyClientFlow)
                        .GetMethod("PollAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(flow, null)
            ).AsTask();

        private static void SetField(object target, string field, object value)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            FieldInfo member = target.GetType().GetField(field, flags);
            if (member != null)
            {
                member.SetValue(target, value);
            }
            else
            {
                target.GetType().GetProperty(field, flags).SetValue(target, value);
            }
        }

        private static void SetProperty(object target, string property, object value) =>
            target
                .GetType()
                .GetProperty(property)
                .GetSetMethod(true)
                .Invoke(target, new[] { value });

        // 使用真实 Fantasy TCP 帧及最小 backend.proto 测试对端；服务线程不执行 Unity 操作。
        private sealed class GatePeer : IDisposable
        {
            private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
            private readonly CancellationTokenSource _stop = new CancellationTokenSource();
            private readonly ConcurrentBag<TcpClient> _peers = new ConcurrentBag<TcpClient>();
            internal readonly ConcurrentQueue<string> LoginUsers = new ConcurrentQueue<string>();
            internal readonly ConcurrentQueue<string> LoginPasswords =
                new ConcurrentQueue<string>();
            internal readonly ConcurrentQueue<string> SettlementMatches =
                new ConcurrentQueue<string>();
            internal readonly ConcurrentQueue<string> MatchRequests = new ConcurrentQueue<string>();
            internal readonly ConcurrentQueue<string> Methods = new ConcurrentQueue<string>();
            internal bool SettlementConfirmed = true;
            internal bool AllocationLost;
            internal bool MatchExpired;
            internal int TerminalAfterStatusRequests;
            internal bool TransientInvalidAllocation;
            internal string MatchState = "ready";
            internal bool PauseMatch;
            internal readonly TaskCompletionSource<bool> MatchBlocked =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource<bool> ReleaseMatch =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal string PartyQueueRequest = "original-request";
            internal string ReceiptMatch = "original-match";
            internal string ChangedAllocation = "";
            internal bool RejectNextProfile;
            internal bool PauseLogin;
            internal string LoginPlayer = "original-player";
            internal readonly TaskCompletionSource<bool> LoginBlocked =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource<bool> ReleaseLogin =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal GateConnectionOptions Options { get; }

            internal GatePeer()
            {
                _listener.Start();
                Options = new GateConnectionOptions(
                    "127.0.0.1",
                    ((IPEndPoint)_listener.LocalEndpoint).Port,
                    false,
                    timeoutMilliseconds: 2000
                );
                _ = Accept();
            }

            private async Task Accept()
            {
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        TcpClient peer = await _listener
                            .AcceptTcpClientAsync()
                            .ConfigureAwait(false);
                        _peers.Add(peer);
                        _ = Serve(peer);
                    }
                }
                catch (ObjectDisposedException) { }
                catch (SocketException) { }
            }

            private async Task Serve(TcpClient peer)
            {
                try
                {
                    using NetworkStream stream = peer.GetStream();
                    while (!_stop.IsCancellationRequested)
                    {
                        byte[] header = new byte[20];
                        await Read(stream, header, _stop.Token).ConfigureAwait(false);
                        byte[] outer = new byte[BinaryPrimitives.ReadInt32LittleEndian(header)];
                        await Read(stream, outer, _stop.Token).ConfigureAwait(false);
                        byte[] request = Data(outer, 1);
                        string method = Text(request, 2),
                            correlation = Text(request, 1),
                            error = "";
                        byte[] body = Data(request, 3),
                            reply;
                        Methods.Enqueue(method);
                        if (method == "player.login")
                        {
                            LoginUsers.Enqueue(Text(body, 1));
                            LoginPasswords.Enqueue(Text(body, 2));
                            if (PauseLogin)
                            {
                                LoginBlocked.TrySetResult(true);
                                await ReleaseLogin.Task.ConfigureAwait(false);
                            }

                            reply = Join(
                                Field(1, LoginPlayer),
                                Field(2, "renewed-credential"),
                                Number(
                                    3,
                                    (ulong)DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds()
                                )
                            );
                        }
                        else if (method == "player.profile")
                        {
                            reply = Join(Field(1, "original-player"), Number(3, 1));
                            if (RejectNextProfile)
                            {
                                RejectNextProfile = false;
                                error = "invalid_session";
                                reply = Array.Empty<byte>();
                            }
                        }
                        else if (method == "lobby.get")
                        {
                            reply = Join(
                                Field(1, "original-party"),
                                Field(2, "original-player"),
                                Number(3, 2),
                                Bytes(4, Field(1, "original-player")),
                                Field(5, PartyQueueRequest)
                            );
                        }
                        else if (method == "player.settlement-status")
                        {
                            SettlementMatches.Enqueue(Text(body, 1));
                            reply = Join(
                                Field(1, ReceiptMatch),
                                Number(3, SettlementConfirmed ? 1UL : 0UL)
                            );
                        }
                        else if (method == "match.status")
                        {
                            MatchRequests.Enqueue(Text(body, 1));
                            if (PauseMatch)
                            {
                                MatchBlocked.TrySetResult(true);
                                await ReleaseMatch.Task.ConfigureAwait(false);
                            }

                            if (AllocationLost)
                            {
                                reply = Bytes(
                                    1,
                                    Join(
                                        Field(1, "original-request"),
                                        Field(2, "Failed"),
                                        Field(5, "queue_lost")
                                    )
                                );
                            }
                            else
                            {
                                reply = Join(
                                    Bytes(
                                        1,
                                        Join(
                                            Field(1, "original-request"),
                                            Field(
                                                2,
                                                TerminalAfterStatusRequests > 0
                                                && MatchRequests.Count
                                                    >= TerminalAfterStatusRequests
                                                    ? "failed"
                                                    : MatchState
                                            ),
                                            Field(
                                                3,
                                                ChangedAllocation == "match"
                                                    ? "other-match"
                                                    : "original-match"
                                            ),
                                            Bytes(
                                                4,
                                                Join(
                                                    Field(
                                                        3,
                                                        ChangedAllocation == "room"
                                                            ? "other-room"
                                                            : "original-room"
                                                    ),
                                                    Field(
                                                        4,
                                                        ChangedAllocation == "node"
                                                            ? "other-node"
                                                            : "original-node"
                                                    ),
                                                    Field(
                                                        5,
                                                        ChangedAllocation == "boot"
                                                            ? "other-boot"
                                                            : "original-boot"
                                                    ),
                                                    Field(6, "127.0.0.1:22000")
                                                )
                                            )
                                        )
                                    ),
                                    Field(2, "renewed-ticket")
                                );
                            }
                            if (
                                TerminalAfterStatusRequests > 0
                                && MatchRequests.Count >= TerminalAfterStatusRequests
                            )
                            {
                                PartyQueueRequest = "";
                            }
                            else if (TransientInvalidAllocation)
                            {
                                error = "invalid_allocation";
                                reply = Array.Empty<byte>();
                            }
                            if (MatchExpired)
                            {
                                error = "queue_expired";
                                reply = Array.Empty<byte>();
                            }
                        }
                        else
                        {
                            throw new InvalidDataException("Unexpected method: " + method);
                        }

                        byte[] response = Bytes(
                            2,
                            Join(Field(1, correlation), Bytes(2, reply), Field(3, error))
                        );
                        byte[] frame = new byte[20 + response.Length];
                        BinaryPrimitives.WriteInt32LittleEndian(frame, response.Length);
                        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), 402653361u);
                        BinaryPrimitives.WriteUInt32LittleEndian(
                            frame.AsSpan(8),
                            BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8))
                        );
                        response.CopyTo(frame, 20);
                        await stream
                            .WriteAsync(frame, 0, frame.Length, _stop.Token)
                            .ConfigureAwait(false);
                    }
                }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
                catch (OperationCanceledException) { }
            }

            public void Dispose()
            {
                ReleaseLogin.TrySetResult(true);
                ReleaseMatch.TrySetResult(true);
                _stop.Cancel();
                _listener.Stop();
                foreach (TcpClient peer in _peers)
                {
                    peer.Dispose();
                }
            }

            private static async Task Read(Stream stream, byte[] buffer, CancellationToken token)
            {
                int offset = 0;
                while (offset < buffer.Length)
                {
                    int count = await stream
                        .ReadAsync(buffer, offset, buffer.Length - offset, token)
                        .ConfigureAwait(false);
                    if (count == 0)
                    {
                        throw new EndOfStreamException();
                    }

                    offset += count;
                }
            }

            private static byte[] Data(byte[] message, int field)
            {
                int offset = 0;
                while (offset < message.Length)
                {
                    ulong key = Varint(message, ref offset);
                    int wire = (int)(key & 7);
                    if (wire == 0)
                    {
                        Varint(message, ref offset);
                        continue;
                    }

                    if (wire != 2)
                    {
                        throw new InvalidDataException();
                    }

                    int length = checked((int)Varint(message, ref offset));
                    if ((int)(key >> 3) == field)
                    {
                        var value = new byte[length];
                        Array.Copy(message, offset, value, 0, length);
                        return value;
                    }

                    offset += length;
                }

                return Array.Empty<byte>();
            }

            private static string Text(byte[] message, int field) =>
                Encoding.UTF8.GetString(Data(message, field));

            private static byte[] Field(int field, string value) =>
                Bytes(field, Encoding.UTF8.GetBytes(value));

            private static byte[] Bytes(int field, byte[] value) =>
                Join(Varint((ulong)((field << 3) | 2)), Varint((ulong)value.Length), value);

            private static byte[] Number(int field, ulong value) =>
                Join(Varint((ulong)(field << 3)), Varint(value));

            private static byte[] Join(params byte[][] values)
            {
                using var output = new MemoryStream();
                foreach (byte[] value in values)
                {
                    output.Write(value, 0, value.Length);
                }

                return output.ToArray();
            }

            private static byte[] Varint(ulong value)
            {
                using var output = new MemoryStream();
                while (value >= 128)
                {
                    output.WriteByte((byte)((value & 127) | 128));
                    value >>= 7;
                }

                output.WriteByte((byte)value);
                return output.ToArray();
            }

            private static ulong Varint(byte[] buffer, ref int offset)
            {
                ulong value = 0;
                for (int shift = 0; shift < 64; shift += 7)
                {
                    byte next = buffer[offset++];
                    value |= (ulong)(next & 127) << shift;
                    if ((next & 128) == 0)
                    {
                        return value;
                    }
                }

                throw new InvalidDataException();
            }
        }
    }
}
