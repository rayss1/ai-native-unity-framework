using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AiNative.Client.Prediction;
using AiNative.Gameplay;
using AiNative.Realtime;
using NUnit.Framework;

namespace AiNative.Client.Application.Tests
{
    public sealed class BattleClientSessionTests
    {
        [Test]
        public void TopologyLoginSendsTicketAndGlobalRoomUsingExistingV1Fields()
        {
            var transport = new FakeTransport();
            var session = CreateTopologySession(new FakeConnector(transport));
            session.Start(); session.Pump(0);
            Assert.That(transport.SentFrames[0], Is.EqualTo(new byte[]
            {
                0xe8, 0x03, 0x08, 1, 0x12, 1, (byte)'x',
                0x1a, 1, (byte)'t', 0x22, 1, (byte)'r',
            }));
        }

        [TestCase("", "e")]
        [TestCase("other-room", "e")]
        [TestCase("r", "")]
        [TestCase("r", "other-boot")]
        public void TopologyLoginRejectsMissingOrMismatchedRoomAndBoot(string room, string boot)
        {
            var transport = new FakeTransport();
            var session = CreateTopologySession(new FakeConnector(transport));
            session.Start(); session.Pump(0);
            transport.Enqueue(TestFrames.Login(42, 1, room, boot), BattleClientProtocolV1.ControlChannel, 1);
            session.Pump(0);
            Assert.That(session.State, Is.EqualTo(BattleClientState.Faulted));
            Assert.That(transport.SentFrames.Count, Is.EqualTo(1));
        }

        [Test]
        public void TopologyJoinAcceptsServerLocalRoomAndEntityAndRejectsWrongPacketEpoch()
        {
            var transport = new FakeTransport();
            var session = CreateTopologySession(new FakeConnector(transport));
            session.Start(); session.Pump(0);
            transport.Enqueue(TestFrames.Login(42, 2, "r", "e"), BattleClientProtocolV1.ControlChannel, 1);
            session.Pump(0);
            transport.Enqueue(TestFrames.Join(19, 3, 60), BattleClientProtocolV1.ControlChannel, 1);
            session.Pump(0);
            Assert.That(session.State, Is.EqualTo(BattleClientState.JoiningRoom));
            transport.Enqueue(TestFrames.Join(19, 3, 60), BattleClientProtocolV1.ControlChannel, 2);
            transport.Enqueue(TestFrames.Snapshot(3, 110, 0), BattleClientProtocolV1.SnapshotChannel, 2);
            session.Pump(0);
            Assert.That(session.State, Is.EqualTo(BattleClientState.Active));
            Assert.That(session.EntityId, Is.EqualTo(3));
            Assert.That(session.IsPredictionInitialized, Is.True);
        }

        [Test]
        public void TopologyReconnectReloginsWithFreshTicketAndClearsOldPredictionAndInputs()
        {
            var initial = new FakeTransport(); var replacement = new FakeTransport();
            var session = CreateTopologySession(new FakeConnector(initial, replacement));
            session.Start(); session.Pump(0);
            initial.Enqueue(TestFrames.Login(42, 1, "r", "e"), BattleClientProtocolV1.ControlChannel, 1);
            initial.Enqueue(TestFrames.Join(19, 7, 60), BattleClientProtocolV1.ControlChannel, 1);
            initial.Enqueue(ArenaSnapshot(100), BattleClientProtocolV1.SnapshotChannel, 1);
            session.Pump(0);
            session.PredictAndQueueArenaInput(101, 1000, 0, 0, 0, ArenaButtons.Jump, ArenaWeaponId.Machinegun);
            Assert.That(session.QueuedInputFrames, Is.EqualTo(1));
            UpdateTopologyAdmission(session, "r", "e", "fresh");
            session.RequestReconnect();
            Assert.That(session.QueuedInputFrames, Is.Zero);
            Assert.That(session.IsPredictionInitialized, Is.False);
            Assert.That(session.TryGetArenaState(out _), Is.False);
            Assert.That(session.TryAdvancePresentation(0, out _), Is.False);
            session.Pump(0.3f); session.Pump(0);
            Assert.That(BattleClientProtocolV1.ReadMessageId(replacement.SentFrames[0]), Is.EqualTo(1000));
            Assert.That(System.Text.Encoding.UTF8.GetString(replacement.SentFrames[0]), Does.Contain("fresh"));
            replacement.Enqueue(TestFrames.Login(42, 1, "r", "e"), BattleClientProtocolV1.ControlChannel, 1);
            replacement.Enqueue(TestFrames.Join(19, 7, 60), BattleClientProtocolV1.ControlChannel, 1);
            replacement.Enqueue(ArenaSnapshot(110), BattleClientProtocolV1.SnapshotChannel, 1);
            session.Pump(0);
            Assert.That(session.State, Is.EqualTo(BattleClientState.Active));
            Assert.That(session.SessionId, Is.EqualTo(42));
            Assert.That(session.EntityId, Is.EqualTo(7));
            Assert.That(session.PredictAndQueueArenaInput(1, 0, 0, 0, 0, ArenaButtons.None, ArenaWeaponId.Machinegun), Is.EqualTo(PredictionPrepareStatus.Prepared));
            session.TryGetArenaState(out var state);
            Assert.That(state.Tick, Is.EqualTo(111));
            Assert.That(state.PositionXMillimetres, Is.Zero);
        }

        [Test]
        public void TopologyLoginRoomTickRejectsEarlierInitialSnapshot()
        {
            var transport = new FakeTransport();
            var session = CreateTopologySession(new FakeConnector(transport));
            session.Start(); session.Pump(0);
            transport.Enqueue(TestFrames.Login(42, 1, "r", "e", 100), BattleClientProtocolV1.ControlChannel, 1);
            transport.Enqueue(TestFrames.Join(19, 3, 60), BattleClientProtocolV1.ControlChannel, 1);
            transport.Enqueue(TestFrames.Snapshot(3, 99, 0), BattleClientProtocolV1.SnapshotChannel, 1);
            session.Pump(0);
            Assert.That(session.LastReceivedTick, Is.EqualTo(100));
            Assert.That(session.IsPredictionInitialized, Is.False);
            transport.Enqueue(TestFrames.Snapshot(3, 101, 9), BattleClientProtocolV1.SnapshotChannel, 1);
            session.Pump(0);
            Assert.That(session.IsPredictionInitialized, Is.True);
            Assert.That(session.LastAcknowledgedSequence, Is.EqualTo(9));
        }

        [TestCase("", "e", "t")]
        [TestCase("r", "", "t")]
        [TestCase("r", "e", " ")]
        public void TopologyAdmissionRequiresRoomBootAndTicket(string room, string boot, string ticket)
            => Assert.Throws<ArgumentException>(() => new BattleAdmissionInfo(room, boot, ticket));

        [TestCase("other", "e")]
        [TestCase("r", "other")]
        public void TopologyAdmissionRefreshRejectsAllocationChanges(string room, string boot)
        {
            var transport = new FakeTransport();
            var session = CreateTopologySession(new FakeConnector(transport));
            Assert.Throws<ArgumentException>(() => session.UpdateAdmission(new BattleAdmissionInfo(room, boot, "fresh")));
            session.Start(); session.Pump(0);
            Assert.That(transport.SentFrames[0], Is.EqualTo(new byte[]
            {
                0xe8, 0x03, 0x08, 1, 0x12, 1, (byte)'x',
                0x1a, 1, (byte)'t', 0x22, 1, (byte)'r',
            }));
        }

        [Test]
        public void TopologyLoginRejectsOversizedTicketWithoutSendingPartialFrame()
        {
            var transport = new FakeTransport();
            var session = new BattleClientSession("localhost", 22000, "x", 4, new FakeConnector(transport),
                new BattleAdmissionInfo("r", "e", new string('t', 1200)));
            session.Start(); session.Pump(0);
            Assert.That(session.State, Is.EqualTo(BattleClientState.Faulted));
            Assert.That(transport.SentFrames, Is.Empty);
        }

        [Test]
        public void TopologyReconnectRetriesTimedOutReloginWithRefreshedTicket()
        {
            var initial = new FakeTransport(); var timedOut = new FakeTransport(); var replacement = new FakeTransport();
            var session = CreateTopologySession(new FakeConnector(initial, timedOut, replacement));
            session.Start(); session.Pump(0);
            initial.Enqueue(TestFrames.Login(42, 1, "r", "e"), BattleClientProtocolV1.ControlChannel, 1);
            initial.Enqueue(TestFrames.Join(19, 7, 60), BattleClientProtocolV1.ControlChannel, 1);
            session.Pump(0); session.RequestReconnect(); session.Pump(0.3f); session.Pump(0);
            session.Pump(5.1f);
            Assert.That(session.State, Is.EqualTo(BattleClientState.Reconnecting));
            session.UpdateAdmission(new BattleAdmissionInfo("r", "e", "fresh"));
            session.Pump(0.6f); session.Pump(0);
            Assert.That(BattleClientProtocolV1.ReadMessageId(replacement.SentFrames[0]), Is.EqualTo(1000));
            Assert.That(System.Text.Encoding.UTF8.GetString(replacement.SentFrames[0]), Does.Contain("fresh"));
        }

        [Test]
        public void FinishedArenaRetainsFinalStateAfterConnectionClosesAndManualReconnect()
        {
            var transport = new FakeTransport();
            var connector = new FakeConnector(transport, new FakeTransport());
            var session = CreateActiveTopologySession(transport, connector);
            transport.Enqueue(FinishedArenaSnapshot(110), BattleClientProtocolV1.SnapshotChannel, 1);
            session.Pump(0);
            Assert.That(session.ArenaPhase, Is.EqualTo(ArenaMatchPhase.Finished));
            transport.Close();
            session.Pump(0);
            session.RequestReconnect();
            session.Pump(1);
            AssertFinalArenaState(session);
            Assert.That(connector.CallCount, Is.EqualTo(1));
        }

        [TestCase(SendStatus.Closed)]
        [TestCase(SendStatus.Faulted)]
        public void FinishedArenaDiscardsQueuedInputsBeforeSendFailureCanTriggerReconnect(SendStatus sendStatus)
        {
            var transport = new FakeTransport();
            var connector = new FakeConnector(transport, new FakeTransport());
            var session = CreateActiveTopologySession(transport, connector);
            transport.Enqueue(ArenaSnapshot(100), BattleClientProtocolV1.SnapshotChannel, 1);
            session.Pump(0);
            session.PredictAndQueueArenaInput(101, 1000, 0, 0, 0, ArenaButtons.Jump, ArenaWeaponId.Machinegun);
            Assert.That(session.QueuedInputFrames, Is.EqualTo(1));
            int sentBeforeFinal = transport.SentFrames.Count;
            transport.NextSendStatus = sendStatus;
            transport.Enqueue(FinishedArenaSnapshot(110), BattleClientProtocolV1.SnapshotChannel, 1);
            session.Pump(0);
            session.Pump(1);
            AssertFinalArenaState(session);
            Assert.That(session.QueuedInputFrames, Is.Zero);
            Assert.That(transport.SentFrames.Count, Is.EqualTo(sentBeforeFinal));
            Assert.That(connector.CallCount, Is.EqualTo(1));
        }

        [Test]
        public void FinishedArenaRejectsFurtherPredictionAndInput()
        {
            var transport = new FakeTransport();
            var session = CreateActiveTopologySession(transport, new FakeConnector(transport));
            transport.Enqueue(FinishedArenaSnapshot(110), BattleClientProtocolV1.SnapshotChannel, 1);
            session.Pump(0);
            Assert.That(session.PredictAndQueueArenaInput(111, 1000, 0, 0, 0, ArenaButtons.Fire, ArenaWeaponId.Machinegun),
                Is.EqualTo(PredictionPrepareStatus.NotInitialized));
            Assert.That(session.PredictAndQueueInput(111, 1000, 0), Is.EqualTo(PredictionPrepareStatus.NotInitialized));
            Assert.That(session.QueuedInputFrames, Is.Zero);
            AssertFinalArenaState(session);
        }

        [Test]
        public void FinishedArenaUsesAuthorityStateWithoutReplayingUnacknowledgedInputs()
        {
            var transport = new FakeTransport();
            var session = CreateActiveTopologySession(transport, new FakeConnector(transport));
            transport.Enqueue(ArenaSnapshot(100), BattleClientProtocolV1.SnapshotChannel, 1);
            session.Pump(0);
            session.PredictAndQueueArenaInput(101, 1000, 0, 0, 0, ArenaButtons.Jump, ArenaWeaponId.Machinegun);
            transport.Enqueue(FinishedArenaSnapshot(110, 0), BattleClientProtocolV1.SnapshotChannel, 1);
            session.Pump(0);
            Assert.That(session.TryGetArenaState(out var state), Is.True);
            Assert.That(state.Tick, Is.EqualTo(110));
            Assert.That(state.LastProcessedInputSequence, Is.Zero);
            Assert.That(state.PositionXMillimetres, Is.Zero);
            Assert.That(state.PositionYMillimetres, Is.Zero);
            Assert.That(session.QueuedInputFrames, Is.Zero);
        }

        private static byte[] FinishedArenaSnapshot(byte tick, byte acknowledgement = 42)
        {
            var frame = new List<byte>(ArenaSnapshot(tick));
            frame.AddRange(new byte[] { 0x30, acknowledgement, 0x38, 2 });
            return frame.ToArray();
        }

        private static void AssertFinalArenaState(BattleClientSession session)
        {
            Assert.That(session.ArenaPhase, Is.EqualTo(ArenaMatchPhase.Finished));
            Assert.That(session.LastReceivedTick, Is.EqualTo(110));
            Assert.That(session.LastAcknowledgedSequence, Is.EqualTo(42));
            Assert.That(session.TryGetArenaState(out var state), Is.True);
            Assert.That(state.Tick, Is.EqualTo(110));
            Assert.That(state.LastProcessedInputSequence, Is.EqualTo(42));
        }

        private static BattleAdmissionInfo CreateTopologyAdmission(string room, string boot, string ticket)
            => new BattleAdmissionInfo(room, boot, ticket);

        private static BattleClientSession CreateTopologySession(FakeConnector connector)
        {
            return new BattleClientSession("127.0.0.1", 22000, "x", 4, connector,
                CreateTopologyAdmission("r", "e", "t"));
        }

        private static BattleClientSession CreateActiveTopologySession(FakeTransport transport, FakeConnector connector)
        {
            var session = CreateTopologySession(connector);
            session.Start(); session.Pump(0);
            transport.Enqueue(TestFrames.Login(42, 1, "r", "e"), BattleClientProtocolV1.ControlChannel, 1);
            transport.Enqueue(TestFrames.Join(19, 7, 60), BattleClientProtocolV1.ControlChannel, 1);
            session.Pump(0);
            Assert.That(session.State, Is.EqualTo(BattleClientState.Active));
            return session;
        }

        private static void UpdateTopologyAdmission(BattleClientSession session, string room, string boot, string ticket)
            => session.UpdateAdmission(CreateTopologyAdmission(room, boot, ticket));

        [Test]
        public void ArenaPredictionUsesAuthorityTickAndRejectsWrongEpochBeforeHudMutation()
        {
            var transport = new FakeTransport();
            var session = CreateActiveSession(transport, out _);
            byte[] snapshot = ArenaSnapshot(100);
            transport.Enqueue(snapshot, BattleClientProtocolV1.SnapshotChannel, 1);
            session.Pump(0);
            Assert.That(session.PredictAndQueueArenaInput(1, 1000, 0, 0, 0, ArenaButtons.Jump, ArenaWeaponId.Machinegun), Is.EqualTo(PredictionPrepareStatus.Prepared));
            Assert.That(session.TryGetArenaState(out var predicted), Is.True);
            Assert.That(predicted.PositionXMillimetres, Is.EqualTo(16));
            Assert.That(predicted.PositionYMillimetres, Is.EqualTo(91));
            Assert.That(predicted.Tick, Is.EqualTo(101));
            transport.Enqueue(ArenaSnapshot(200), BattleClientProtocolV1.SnapshotChannel, 2);
            session.Pump(0);
            session.TryGetArenaState(out var unchanged);
            Assert.That(unchanged, Is.EqualTo(predicted));
            Assert.That(session.LastReceivedTick, Is.EqualTo(100));
        }

        [TestCase(1UL)]
        [TestCase(ulong.MaxValue)]
        public void ArenaSessionWireTickRemainsAtLatestAuthorityDespiteUnacknowledgedPrediction(ulong callerTick)
        {
            var transport = new FakeTransport();
            var session = CreateActiveTopologySession(transport, new FakeConnector(transport));
            transport.Enqueue(ArenaSnapshot(100), BattleClientProtocolV1.SnapshotChannel, 1);
            session.Pump(0);
            for (int index = 0; index < 3; index++)
                Assert.That(session.PredictAndQueueArenaInput(callerTick, 1000, 0, 0, 0, ArenaButtons.None, ArenaWeaponId.Machinegun),
                    Is.EqualTo(PredictionPrepareStatus.Prepared));
            session.Pump(0);
            for (int index = 2; index < transport.SentFrames.Count; index++)
                Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(transport.SentFrames[index].AsSpan(3)), Is.EqualTo(101));

            transport.Enqueue(ArenaSnapshot(103), BattleClientProtocolV1.SnapshotChannel, 1);
            session.Pump(0);
            Assert.That(session.PredictAndQueueArenaInput(callerTick, 1000, 0, 0, 0, ArenaButtons.None, ArenaWeaponId.Machinegun),
                Is.EqualTo(PredictionPrepareStatus.Prepared));
            session.Pump(0);
            Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(transport.SentFrames[transport.SentFrames.Count - 1].AsSpan(3)), Is.EqualTo(104));
            Assert.That(session.TryGetArenaState(out var predicted), Is.True);
            Assert.That(predicted.Tick, Is.EqualTo(107));
            Assert.That(session.LastAcknowledgedSequence, Is.Zero);
        }

        [Test]
        public void ArenaReconnectUsesFullStateAndRetainsArenaSimulation()
        {
            var initial = new FakeTransport(); var replacement = new FakeTransport();
            var connector = new FakeConnector(initial, replacement);
            var session = CreateActiveSession(initial, out _, connector);
            initial.Enqueue(ArenaSnapshot(100), BattleClientProtocolV1.SnapshotChannel, 1); session.Pump(0);
            session.RequestReconnect(); session.Pump(0.3f); session.Pump(0);
            byte[] snapshot = ArenaSnapshot(110);
            replacement.Enqueue(TestFrames.Reconnect(2, 110, snapshot.AsSpan(2).ToArray()), BattleClientProtocolV1.ControlChannel, 2);
            session.Pump(0);
            Assert.That(session.State, Is.EqualTo(BattleClientState.Active));
            Assert.That(session.PredictAndQueueArenaInput(1, 1000, 0, 0, 0, ArenaButtons.Jump, ArenaWeaponId.Machinegun), Is.EqualTo(PredictionPrepareStatus.Prepared));
            session.TryGetArenaState(out var state);
            Assert.That(state.Tick, Is.EqualTo(111));
            Assert.That(state.PositionYMillimetres, Is.EqualTo(91));
        }

        private static byte[] ArenaSnapshot(byte tick)
            => new byte[] { 0x4d, 0x04, 0x08, 1, 0x11, tick, 0, 0, 0, 0, 0, 0, 0, 0x22, 8, 0x08, 7, 0x30, 100, 0x50, 1, 0x60, 1 };

        [Test]
        public void ProtocolV1ControlRequestsMatchFrozenWireShape()
        {
            byte[] frame = new byte[64];

            Assert.That(BattleClientProtocolV1.TryEncodeLogin("x", frame, out int loginBytes), Is.True);
            Assert.That(frame.AsSpan(0, loginBytes).ToArray(), Is.EqualTo(new byte[]
            {
                0xe8, 0x03, 0x08, 0x01, 0x12, 0x01, 0x78,
            }));

            Assert.That(BattleClientProtocolV1.TryEncodeJoin(1, 1, frame, out int joinBytes), Is.True);
            Assert.That(frame.AsSpan(0, joinBytes).ToArray(), Is.EqualTo(new byte[]
            {
                0xf2, 0x03, 0x09, 1, 0, 0, 0, 0, 0, 0, 0, 0x10, 0x01,
            }));

            Assert.That(BattleClientProtocolV1.TryEncodeReconnect(1, 2, 3, frame, out int reconnectBytes), Is.True);
            Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(frame), Is.EqualTo(1200));
            Assert.That(reconnectBytes, Is.EqualTo(22));
        }

        [Test]
        public void LoginJoinAndSnapshotReachActiveInitializedState()
        {
            FakeTransport transport = new FakeTransport();
            BattleClientSession session = CreateActiveSession(transport, out _);

            transport.Enqueue(TestFrames.Snapshot(7, 10, 0), BattleClientProtocolV1.SnapshotChannel, 1);
            session.Pump(0);

            Assert.That(session.State, Is.EqualTo(BattleClientState.Active));
            Assert.That(session.SessionId, Is.EqualTo(42));
            Assert.That(session.EntityId, Is.EqualTo(7));
            Assert.That(session.IsPredictionInitialized, Is.True);
            Assert.That(session.LastReceivedTick, Is.EqualTo(10));
        }

        [Test]
        public void LoginTimeoutMovesSessionToFaulted()
        {
            FakeTransport transport = new FakeTransport();
            BattleClientSession session = new BattleClientSession(
                "127.0.0.1", 22000, "test", 4, new FakeConnector(transport));

            session.Start();
            session.Pump(0);
            session.Pump(5.01f);

            Assert.That(session.State, Is.EqualTo(BattleClientState.Faulted));
            Assert.That(session.FaultReason, Does.Contain("timed out"));
        }

        [Test]
        public void ReconnectAdvancesEpochAndRetainsPredictionInstance()
        {
            FakeTransport initial = new FakeTransport();
            FakeTransport replacement = new FakeTransport();
            FakeConnector connector = new FakeConnector(initial, null, null, replacement);
            BattleClientSession session = CreateActiveSession(initial, out _, connector);
            initial.Enqueue(TestFrames.Snapshot(7, 10, 0), BattleClientProtocolV1.SnapshotChannel, 1);
            session.Pump(0);
            ClientPredictionAdapter prediction = session.PredictionAdapter;

            session.RequestReconnect();
            session.Pump(0.25f);
            session.Pump(0);
            Assert.That(connector.CallCount, Is.EqualTo(2));
            session.Pump(0.49f);
            Assert.That(connector.CallCount, Is.EqualTo(2));
            session.Pump(0.01f);
            session.Pump(0);
            Assert.That(connector.CallCount, Is.EqualTo(3));
            session.Pump(0.99f);
            Assert.That(connector.CallCount, Is.EqualTo(3));
            session.Pump(0.01f);
            session.Pump(0);
            Assert.That(connector.CallCount, Is.EqualTo(4));
            replacement.Enqueue(
                TestFrames.Reconnect(2, 11, TestFrames.SnapshotPayload(7, 11, 0)),
                BattleClientProtocolV1.ControlChannel,
                1);
            session.Pump(0);

            Assert.That(session.State, Is.EqualTo(BattleClientState.Active));
            Assert.That(session.ConnectionEpoch, Is.EqualTo(2));
            Assert.That(session.PredictionAdapter, Is.SameAs(prediction));
        }

        [Test]
        public void FixedTickInputRingIsBoundedWithoutBlocking()
        {
            FakeTransport transport = new FakeTransport();
            BattleClientSession session = CreateActiveSession(transport, out _, inputCapacity: 1);
            transport.Enqueue(TestFrames.Snapshot(7, 10, 0), BattleClientProtocolV1.SnapshotChannel, 1);
            session.Pump(0);
            transport.NextSendStatus = SendStatus.WouldBlock;

            PredictionPrepareStatus first = session.PredictAndQueueInput(11, 1000, 0);
            PredictionPrepareStatus second = session.PredictAndQueueInput(12, 1000, 0);

            Assert.That(first, Is.EqualTo(PredictionPrepareStatus.Prepared));
            Assert.That(second, Is.EqualTo(PredictionPrepareStatus.BufferTooSmall));
            Assert.That(session.QueuedInputFrames, Is.EqualTo(1));
            Assert.That(session.DroppedInputFrames, Is.EqualTo(1));
        }

        [Test]
        public void PredictionDiagnosticsWindowCanResetAfterInitialization()
        {
            FakeTransport transport = new FakeTransport();
            BattleClientSession session = CreateActiveSession(transport, out _);
            transport.Enqueue(TestFrames.Snapshot(7, 10, 0), BattleClientProtocolV1.SnapshotChannel, 1);
            session.Pump(0);
            Assert.That(session.PredictionDiagnostics.AcceptedSnapshots, Is.EqualTo(1));

            Assert.That(session.ResetPredictionDiagnostics(), Is.True);

            Assert.That(session.PredictionDiagnostics.AcceptedSnapshots, Is.Zero);
            Assert.That(session.PredictionDiagnostics.ReconciliationSamples, Is.Zero);
        }

        [Test]
        public void PresentationCorrectionDoesNotMutateAuthoritativePrediction()
        {
            FakeTransport transport = new FakeTransport();
            BattleClientSession session = CreateActiveSession(transport, out _);
            transport.Enqueue(TestFrames.Snapshot(7, 10, 0), BattleClientProtocolV1.SnapshotChannel, 1);
            session.Pump(0);
            session.PredictAndQueueInput(11, 1000, 0);
            transport.Enqueue(TestFrames.Snapshot(7, 11, 1, 40), BattleClientProtocolV1.SnapshotChannel, 1);
            session.Pump(0);

            Assert.That(session.TryAdvancePresentation(0, out PresentationPosition initial), Is.True);
            Assert.That(session.PredictionAdapter.TryGetPredictedState(out KinematicState simulation), Is.True);
            Assert.That(initial.XMillimetres, Is.EqualTo(50d));
            Assert.That(simulation.PositionXMillimetres, Is.EqualTo(40));

            session.TryAdvancePresentation(0.05f, out PresentationPosition halfway);
            session.TryAdvancePresentation(0.05f, out PresentationPosition settled);
            Assert.That(halfway.XMillimetres, Is.EqualTo(45d).Within(0.0001d));
            Assert.That(settled.XMillimetres, Is.EqualTo(40d));
            Assert.That(session.PresentationDiagnostics.SmoothedCorrections, Is.EqualTo(1));
        }

        [Test]
        public void InvalidLoginEpochFailsBeforeJoin()
        {
            FakeTransport transport = new FakeTransport();
            BattleClientSession session = new BattleClientSession(
                "127.0.0.1", 22000, "test", 4, new FakeConnector(transport));
            session.Start();
            session.Pump(0);
            transport.Enqueue(TestFrames.Login(42, 0), BattleClientProtocolV1.ControlChannel, 1);

            session.Pump(0);

            Assert.That(session.State, Is.EqualTo(BattleClientState.Faulted));
            Assert.That(transport.SentFrames.Count, Is.EqualTo(1));
        }

        private static BattleClientSession CreateActiveSession(
            FakeTransport transport,
            out FakeConnector createdConnector,
            FakeConnector connector = null,
            int inputCapacity = 4)
        {
            createdConnector = connector ?? new FakeConnector(transport);
            BattleClientSession session = new BattleClientSession(
                "127.0.0.1", 22000, "test", inputCapacity, createdConnector);
            session.Start();
            session.Pump(0);
            transport.Enqueue(TestFrames.Login(42, 1), BattleClientProtocolV1.ControlChannel, 1);
            session.Pump(0);
            transport.Enqueue(TestFrames.Join(1, 7, 60), BattleClientProtocolV1.ControlChannel, 1);
            session.Pump(0);
            Assert.That(session.State, Is.EqualTo(BattleClientState.Active));
            return session;
        }
    }

    internal sealed class FakeConnector : IBattleTransportConnector
    {
        private readonly Queue<FakeTransport> _transports;

        internal FakeConnector(params FakeTransport[] transports) =>
            _transports = new Queue<FakeTransport>(transports);

        internal int CallCount { get; private set; }

        public ValueTask<BattleTransportConnection> ConnectAsync(
            string host,
            int port,
            int timeoutMilliseconds,
            CancellationToken cancellationToken)
        {
            CallCount++;
            if (_transports.Count == 0) return new ValueTask<BattleTransportConnection>(default(BattleTransportConnection));
            FakeTransport transport = _transports.Dequeue();
            if (transport is null) return new ValueTask<BattleTransportConnection>(default(BattleTransportConnection));
            return new ValueTask<BattleTransportConnection>(new BattleTransportConnection(
                transport,
                transport.TryAdvanceEpoch));
        }
    }

    internal sealed class FakeTransport : IRealtimeTransport
    {
        private readonly Queue<QueuedPacket> _received = new Queue<QueuedPacket>();
        private uint _epoch = 1;

        internal List<byte[]> SentFrames { get; } = new List<byte[]>();

        internal SendStatus NextSendStatus { get; set; } = SendStatus.Accepted;

        public TransportState State { get; private set; } = TransportState.Connected;

        internal bool TryAdvanceEpoch(uint epoch)
        {
            if (epoch == 0 || epoch < _epoch) return false;
            _epoch = epoch;
            return true;
        }

        internal void Enqueue(byte[] frame, TransportChannel channel, uint epoch) =>
            _received.Enqueue(new QueuedPacket(frame, channel, epoch));

        internal void Close() => State = TransportState.Closed;

        public ValueTask<SendResult> SendAsync(
            TransportChannel channel,
            ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken = default)
        {
            if (State != TransportState.Connected)
            {
                return new ValueTask<SendResult>(new SendResult(SendStatus.Closed));
            }

            SendStatus status = NextSendStatus;
            if (status == SendStatus.Accepted) SentFrames.Add(payload.ToArray());
            return new ValueTask<SendResult>(new SendResult(
                status,
                status == SendStatus.Accepted ? payload.Length : 0));
        }

        public bool TryReceive(Span<byte> destination, out ReceivedPacket packet)
        {
            if (_received.Count == 0)
            {
                packet = default;
                return false;
            }

            QueuedPacket queued = _received.Dequeue();
            int written = Math.Min(destination.Length, queued.Frame.Length);
            queued.Frame.AsSpan(0, written).CopyTo(destination);
            packet = new ReceivedPacket(
                queued.Channel,
                written,
                queued.Frame.Length,
                1,
                queued.Epoch);
            return true;
        }

        public ValueTask DisposeAsync()
        {
            State = TransportState.Closed;
            return default;
        }

        private readonly struct QueuedPacket
        {
            internal QueuedPacket(byte[] frame, TransportChannel channel, uint epoch)
            {
                Frame = frame;
                Channel = channel;
                Epoch = epoch;
            }

            internal byte[] Frame { get; }
            internal TransportChannel Channel { get; }
            internal uint Epoch { get; }
        }
    }

    internal static class TestFrames
    {
        internal static byte[] Login(ulong sessionId, uint epoch, string room = "", string boot = "", ulong roomTick = 0)
        {
            List<byte> payload = Header(BattleClientProtocolV1.LoginResponseMessageId);
            payload.Add(0x09);
            AddFixed64(payload, sessionId);
            payload.Add(0x10);
            AddVarint(payload, epoch);
            if (room.Length != 0) AddString(payload, 0x1a, room);
            if (boot.Length != 0) AddString(payload, 0x22, boot);
            if (roomTick != 0) { payload.Add(0x29); AddFixed64(payload, roomTick); }
            return payload.ToArray();
        }

        private static void AddString(List<byte> payload, byte key, string value)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(value);
            payload.Add(key); AddVarint(payload, (ulong)bytes.Length); payload.AddRange(bytes);
        }

        internal static byte[] Join(uint roomId, uint entityId, uint tickRate)
        {
            List<byte> payload = Header(BattleClientProtocolV1.JoinRoomResponseMessageId);
            payload.Add(0x08); AddVarint(payload, roomId);
            payload.Add(0x10); AddVarint(payload, entityId);
            payload.Add(0x18); AddVarint(payload, tickRate);
            return payload.ToArray();
        }

        internal static byte[] Snapshot(
            uint entityId,
            ulong tick,
            uint acknowledgement,
            int positionXMillimetres = 0)
        {
            List<byte> frame = Header(BattleClientProtocolV1.SnapshotMessageId);
            frame.AddRange(SnapshotPayload(
                entityId,
                tick,
                acknowledgement,
                positionXMillimetres));
            return frame.ToArray();
        }

        internal static byte[] SnapshotPayload(
            uint entityId,
            ulong tick,
            uint acknowledgement,
            int positionXMillimetres = 0)
        {
            List<byte> player = new List<byte> { 0x08 };
            AddVarint(player, entityId);
            if (positionXMillimetres != 0)
            {
                player.Add(0x10);
                AddVarint(player, ZigZag(positionXMillimetres));
            }
            List<byte> payload = new List<byte> { 0x08, 0x01, 0x11 };
            AddFixed64(payload, tick);
            payload.Add(0x22); AddVarint(payload, (ulong)player.Count); payload.AddRange(player);
            payload.Add(0x30); AddVarint(payload, acknowledgement);
            return payload.ToArray();
        }

        internal static byte[] Reconnect(uint epoch, ulong tick, byte[] snapshotPayload)
        {
            List<byte> frame = Header(BattleClientProtocolV1.ReconnectResponseMessageId);
            frame.Add(0x08); AddVarint(frame, epoch);
            frame.Add(0x11); AddFixed64(frame, tick);
            frame.Add(0x1a); AddVarint(frame, (ulong)snapshotPayload.Length); frame.AddRange(snapshotPayload);
            return frame.ToArray();
        }

        private static List<byte> Header(ushort messageId) => new List<byte>
        {
            (byte)messageId,
            (byte)(messageId >> 8),
        };

        private static void AddFixed64(List<byte> destination, ulong value)
        {
            for (int index = 0; index < 8; index++) destination.Add((byte)(value >> (index * 8)));
        }

        private static void AddVarint(List<byte> destination, ulong value)
        {
            do
            {
                byte current = (byte)(value & 0x7f);
                value >>= 7;
                if (value != 0) current |= 0x80;
                destination.Add(current);
            }
            while (value != 0);
        }

        private static ulong ZigZag(int value) =>
            unchecked((ulong)((value << 1) ^ (value >> 31)));
    }
}
