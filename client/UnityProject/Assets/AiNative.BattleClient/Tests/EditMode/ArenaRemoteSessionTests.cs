using System;
using System.Collections.Generic;
using AiNative.Client.Prediction;
using NUnit.Framework;

namespace AiNative.Client.Application.Tests
{
    public sealed class ArenaRemoteSessionTests
    {
        [Test]
        public void RemoteMembershipRejectsStaleEpochAndMalformedSnapshotsAndClearsOnReconnect()
        {
            var initial = new FakeTransport(); var replacement = new FakeTransport();
            var session = new BattleClientSession("127.0.0.1", 22000, "test", 4, new FakeConnector(initial, replacement),
                new BattleAdmissionInfo("r", "e", "t"));
            var poses = new ArenaRemotePose[8];
            try
            {
                session.Start(); session.Pump(0);
                initial.Enqueue(TestFrames.Login(42, 1, "r", "e"), BattleClientProtocolV1.ControlChannel, 1);
                initial.Enqueue(TestFrames.Join(19, 7, 60), BattleClientProtocolV1.ControlChannel, 1);
                initial.Enqueue(Frame(100, 8), BattleClientProtocolV1.SnapshotChannel, 1); session.Pump(0);
                Assert.That(session.AdvanceRemotePresentation(0, poses), Is.EqualTo(1));
                Assert.That(poses[0].EntityId, Is.EqualTo(8));
                initial.Enqueue(Frame(101, 9), BattleClientProtocolV1.SnapshotChannel, 2);
                initial.Enqueue(Frame(99, 9), BattleClientProtocolV1.SnapshotChannel, 1);
                byte[] malformed = Frame(102, 9); Array.Resize(ref malformed, malformed.Length - 1);
                initial.Enqueue(malformed, BattleClientProtocolV1.SnapshotChannel, 1); session.Pump(0);
                Assert.That(session.AdvanceRemotePresentation(0, poses), Is.EqualTo(1));
                Assert.That(poses[0].EntityId, Is.EqualTo(8));
                Assert.That(session.LastReceivedTick, Is.EqualTo(100));
                session.RequestReconnect();
                Assert.That(session.AdvanceRemotePresentation(0, poses), Is.Zero);
                session.Pump(0.3f); session.Pump(0);
                replacement.Enqueue(TestFrames.Login(42, 2, "r", "e", 110), BattleClientProtocolV1.ControlChannel, 1);
                replacement.Enqueue(TestFrames.Join(19, 7, 60), BattleClientProtocolV1.ControlChannel, 2);
                replacement.Enqueue(Frame(100, 9), BattleClientProtocolV1.SnapshotChannel, 1);
                replacement.Enqueue(Frame(109, 9), BattleClientProtocolV1.SnapshotChannel, 2); session.Pump(0);
                Assert.That(session.AdvanceRemotePresentation(0, poses), Is.Zero, "Late packets must not repopulate reset history.");
                replacement.Enqueue(Frame(111, 8), BattleClientProtocolV1.SnapshotChannel, 2); session.Pump(0);
                Assert.That(session.AdvanceRemotePresentation(0, poses), Is.EqualTo(1));
                Assert.That(poses[0].EntityId, Is.EqualTo(8));
            }
            finally { session.DisposeAsync().GetAwaiter().GetResult(); }
            Assert.That(session.AdvanceRemotePresentation(0, poses), Is.Zero);
        }

        [Test]
        public void LegacySnapshotWithMoreThanEightPlayersStillInitializesLocalPrediction()
        {
            var transport = new FakeTransport();
            var session = new BattleClientSession("127.0.0.1", 22000, "test", 4, new FakeConnector(transport));
            try
            {
                session.Start(); session.Pump(0);
                transport.Enqueue(TestFrames.Login(42, 1), BattleClientProtocolV1.ControlChannel, 1);
                transport.Enqueue(TestFrames.Join(1, 7, 60), BattleClientProtocolV1.ControlChannel, 1);
                var frame = new List<byte>(TestFrames.Snapshot(7, 100, 42));
                for (byte id = 8; id <= 16; id++) frame.AddRange(new byte[] { 0x22, 2, 0x08, id });
                transport.Enqueue(frame.ToArray(), BattleClientProtocolV1.SnapshotChannel, 1); session.Pump(0);
                Assert.That(session.IsPredictionInitialized, Is.True);
                Assert.That(session.LastReceivedTick, Is.EqualTo(100));
                Assert.That(session.LastAcknowledgedSequence, Is.EqualTo(42));
            }
            finally { session.DisposeAsync().GetAwaiter().GetResult(); }
        }
        private static byte[] Frame(byte tick, byte remote)
        {
            var bytes = new List<byte> { 0x4d, 0x04, 0x08, 1, 0x11, tick, 0, 0, 0, 0, 0, 0, 0 };
            foreach (byte id in new byte[] { 7, remote }) bytes.AddRange(new byte[] { 0x22, 8, 0x08, id, 0x30, 100, 0x50, 1, 0x60, 1 });
            return bytes.ToArray();
        }
    }
}
