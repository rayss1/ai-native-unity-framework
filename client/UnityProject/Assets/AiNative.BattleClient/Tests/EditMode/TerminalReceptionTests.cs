using System;
using System.Collections.Generic;
using AiNative.Client.Prediction;
using AiNative.Gameplay;
using NUnit.Framework;

namespace AiNative.Client.Application.Tests
{
    public sealed class TerminalReceptionTests
    {
        [Test]
        public void AlreadyAcceptedFinishedSnapshotIsAppliedBeforeClosedTransportIsDisposed()
        {
            var transport = new FakeTransport();
            var session = ActiveSession(transport);
            try
            {
                transport.Enqueue(Frame(110, finished: true), BattleClientProtocolV1.SnapshotChannel, 1);
                transport.Close();
                session.Pump(0);
                Assert.That(session.ArenaPhase, Is.EqualTo(ArenaMatchPhase.Finished));
                Assert.That(session.LastReceivedTick, Is.EqualTo(110));
                Assert.That(session.State, Is.EqualTo(BattleClientState.Active));
            }
            finally { session.DisposeAsync().GetAwaiter().GetResult(); }
        }

        [Test]
        public void ClosedTransportWithOnlyActiveSnapshotsStillReconnectsAfterDraining()
        {
            var transport = new FakeTransport();
            var session = ActiveSession(transport);
            try
            {
                transport.Enqueue(Frame(110), BattleClientProtocolV1.SnapshotChannel, 1);
                transport.Close(); session.Pump(0);
                Assert.That(session.State, Is.EqualTo(BattleClientState.Reconnecting));
                Assert.That(session.ArenaPhase, Is.Not.EqualTo(ArenaMatchPhase.Finished));
            }
            finally { session.DisposeAsync().GetAwaiter().GetResult(); }
        }

        [Test]
        public void TerminalAfterReceiveBudgetIsRetainedUntilLaterPumpDespiteQueuedInput()
        {
            var transport = new FakeTransport();
            var session = ActiveSession(transport);
            try
            {
                Assert.That(session.PredictAndQueueInput(101, 1000, 0), Is.EqualTo(PredictionPrepareStatus.Prepared));
                for (int i = 0; i < 300; i++) transport.Enqueue(Frame((ulong)(101 + i)), BattleClientProtocolV1.SnapshotChannel, 1);
                transport.Enqueue(Frame(401, finished: true), BattleClientProtocolV1.SnapshotChannel, 1);
                transport.Close(); session.Pump(0);
                Assert.That(session.State, Is.EqualTo(BattleClientState.Active), "A budget-exhausted pump must retain the old connection.");
                Assert.That(session.LastReceivedTick, Is.EqualTo(356));
                Assert.That(session.ArenaPhase, Is.Not.EqualTo(ArenaMatchPhase.Finished));
                session.Pump(0);
                Assert.That(session.ArenaPhase, Is.EqualTo(ArenaMatchPhase.Finished));
                Assert.That(session.LastReceivedTick, Is.EqualTo(401));
                Assert.That(session.QueuedInputFrames, Is.Zero);
            }
            finally { session.DisposeAsync().GetAwaiter().GetResult(); }
        }

        [Test]
        public void StaleEpochFinishedSnapshotCannotSuppressReconnect()
        {
            var transport = new FakeTransport();
            var session = ActiveSession(transport);
            try
            {
                transport.Enqueue(Frame(110, finished: true), BattleClientProtocolV1.SnapshotChannel, 99);
                transport.Close(); session.Pump(0);
                Assert.That(session.ArenaPhase, Is.Not.EqualTo(ArenaMatchPhase.Finished));
                Assert.That(session.State, Is.EqualTo(BattleClientState.Reconnecting));
            }
            finally { session.DisposeAsync().GetAwaiter().GetResult(); }
        }

        [Test]
        public void ExactBudgetWithoutTerminalDefersThenReconnectsOnConfirmedEmpty()
        {
            var transport = new FakeTransport();
            var session = ActiveSession(transport);
            try
            {
                for (int i = 0; i < 256; i++) transport.Enqueue(Frame((ulong)(101 + i)), BattleClientProtocolV1.SnapshotChannel, 1);
                transport.Close(); session.Pump(0);
                Assert.That(session.State, Is.EqualTo(BattleClientState.Active));
                session.Pump(0);
                Assert.That(session.State, Is.EqualTo(BattleClientState.Reconnecting));
            }
            finally { session.DisposeAsync().GetAwaiter().GetResult(); }
        }

        [Test]
        public void CloseDuringSendDefersReconnectUntilAcceptedTerminalIsRead()
        {
            var transport = new FakeTransport(); var session = ActiveSession(transport);
            try
            {
                session.PredictAndQueueInput(101, 1000, 0);
                transport.BeforeSend = () =>
                {
                    transport.BeforeSend = null;
                    transport.Enqueue(Frame(110, finished: true), BattleClientProtocolV1.SnapshotChannel, 1);
                    transport.Close();
                };
                session.Pump(0);
                Assert.That(session.State, Is.EqualTo(BattleClientState.Active));
                session.Pump(0);
                Assert.That(session.ArenaPhase, Is.EqualTo(ArenaMatchPhase.Finished));
            }
            finally { session.DisposeAsync().GetAwaiter().GetResult(); }
        }

        [Test]
        public void CloseRacingEmptyReadRetainsNewlyAcceptedTerminalUntilNextPump()
        {
            var transport = new FakeTransport(); var session = ActiveSession(transport);
            try
            {
                transport.BeforeEmptyReceive = () =>
                {
                    transport.BeforeEmptyReceive = null;
                    transport.Enqueue(Frame(110, finished: true), BattleClientProtocolV1.SnapshotChannel, 1);
                    transport.Close();
                };
                session.Pump(0);
                Assert.That(session.State, Is.EqualTo(BattleClientState.Active));
                session.Pump(0);
                Assert.That(session.ArenaPhase, Is.EqualTo(ArenaMatchPhase.Finished));
            }
            finally { session.DisposeAsync().GetAwaiter().GetResult(); }
        }
        private static BattleClientSession ActiveSession(FakeTransport transport)
        {
            var session = new BattleClientSession("127.0.0.1", 22000, "terminal-test", 4, new FakeConnector(transport));
            session.Start(); session.Pump(0);
            transport.Enqueue(TestFrames.Login(42, 1), BattleClientProtocolV1.ControlChannel, 1);
            transport.Enqueue(TestFrames.Join(1, 7, 60), BattleClientProtocolV1.ControlChannel, 1);
            transport.Enqueue(Frame(100), BattleClientProtocolV1.SnapshotChannel, 1);
            session.Pump(0);
            Assert.That(session.State, Is.EqualTo(BattleClientState.Active));
            Assert.That(session.ArenaPhase, Is.EqualTo(ArenaMatchPhase.Active));
            return session;
        }

        private static byte[] Frame(ulong tick, bool finished = false)
        {
            var frame = new List<byte> { 0x4d, 0x04, 0x08, 1, 0x11 };
            frame.AddRange(BitConverter.GetBytes(tick));
            frame.AddRange(new byte[] { 0x22, 8, 0x08, 7, 0x30, 100, 0x50, 1, 0x60, 1, 0x38, finished ? (byte)2 : (byte)1 });
            return frame.ToArray();
        }
    }
}
