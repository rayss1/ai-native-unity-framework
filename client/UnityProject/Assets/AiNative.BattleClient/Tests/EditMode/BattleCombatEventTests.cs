using System;
using System.Collections.Generic;
using AiNative.Client.Prediction;
using NUnit.Framework;

namespace AiNative.Client.Application.Tests
{
    public sealed class BattleCombatEventTests
    {
        [Test]
        public void DuplicateStaleEpochWrongChannelAndMalformedEventsNeverReachPresentation()
        {
            var transport = new FakeTransport();
            var session = Active(transport, new FakeConnector(transport));
            transport.Enqueue(Event(9), ArenaCombatEventProtocolV1.EventChannel, 1);
            transport.Enqueue(Event(9), ArenaCombatEventProtocolV1.EventChannel, 1);
            transport.Enqueue(Event(8), ArenaCombatEventProtocolV1.EventChannel, 1);
            transport.Enqueue(Event(10), ArenaCombatEventProtocolV1.EventChannel, 0);
            transport.Enqueue(Event(10), BattleClientProtocolV1.ControlChannel, 1);
            transport.Enqueue(new byte[] { 0x4e,4,0xff }, ArenaCombatEventProtocolV1.EventChannel, 1);
            transport.Enqueue(Event(10), ArenaCombatEventProtocolV1.EventChannel, 1);
            session.Pump(0);
            var output = new ArenaPresentationEvent[8];
            Assert.That(session.DrainCombatEvents(output), Is.EqualTo(2));
            Assert.That(output[0].Sequence, Is.EqualTo(9));
            Assert.That(output[1].Sequence, Is.EqualTo(10));
            Assert.That(session.DrainCombatEvents(output), Is.Zero);
            Assert.That(session.DroppedCombatEvents, Is.Zero);
            session.DisposeAsync().GetAwaiter().GetResult();
        }

        [Test]
        public void BoundedOverflowDropsOldestAndPartialDrainPreservesOrder()
        {
            var transport = new FakeTransport();
            var session = Active(transport, new FakeConnector(transport));
            for (uint i = 1; i <= 300; i++) transport.Enqueue(Event(i), ArenaCombatEventProtocolV1.EventChannel, 1);
            session.Pump(0); session.Pump(0);
            Assert.That(session.DroppedCombatEvents, Is.EqualTo(44));
            var output = new ArenaPresentationEvent[128];
            Assert.That(session.DrainCombatEvents(Span<ArenaPresentationEvent>.Empty), Is.Zero);
            Assert.That(session.DrainCombatEvents(output), Is.EqualTo(128));
            Assert.That(output[0].Sequence, Is.EqualTo(45));
            Assert.That(output[127].Sequence, Is.EqualTo(172));
            Assert.That(session.DrainCombatEvents(output), Is.EqualTo(128));
            Assert.That(output[0].Sequence, Is.EqualTo(173));
            Assert.That(output[127].Sequence, Is.EqualTo(300));
            Assert.That(session.DrainCombatEvents(output), Is.Zero);
            session.DisposeAsync().GetAwaiter().GetResult();
        }

        [Test]
        public void ReconnectClearsPendingFeedbackButRetainsRoomSequenceWatermark()
        {
            var first = new FakeTransport(); var next = new FakeTransport();
            var session = Active(first, new FakeConnector(first, next));
            first.Enqueue(TestFrames.Snapshot(7, 100, 0), BattleClientProtocolV1.SnapshotChannel, 1);
            first.Enqueue(Event(9), ArenaCombatEventProtocolV1.EventChannel, 1);
            session.Pump(0);
            session.RequestReconnect();
            var output = new ArenaPresentationEvent[8];
            Assert.That(session.DrainCombatEvents(output), Is.Zero);
            session.Pump(.3f); session.Pump(0);
            next.Enqueue(TestFrames.Reconnect(2, 101, TestFrames.SnapshotPayload(7, 101, 0)), BattleClientProtocolV1.ControlChannel, 1);
            session.Pump(0);
            Assert.That(session.State, Is.EqualTo(BattleClientState.Active));
            next.Enqueue(Event(9), ArenaCombatEventProtocolV1.EventChannel, 2);
            next.Enqueue(Event(10), ArenaCombatEventProtocolV1.EventChannel, 1);
            next.Enqueue(Event(10), ArenaCombatEventProtocolV1.EventChannel, 2);
            session.Pump(0);
            Assert.That(session.DrainCombatEvents(output), Is.EqualTo(1));
            Assert.That(output[0].Sequence, Is.EqualTo(10));
            session.DisposeAsync().GetAwaiter().GetResult();
        }

        [Test]
        public void SettlementTailDrainsReliableFeedbackAndDisposeClearsQueue()
        {
            var transport = new FakeTransport();
            var session = Active(transport, new FakeConnector(transport));
            transport.Enqueue(Event(1), ArenaCombatEventProtocolV1.EventChannel, 1);
            session.DrainAcceptedPackets();
            var output = new ArenaPresentationEvent[1];
            Assert.That(session.DrainCombatEvents(output), Is.EqualTo(1));
            transport.Enqueue(Event(2), ArenaCombatEventProtocolV1.EventChannel, 1);
            session.Pump(0);
            session.DisposeAsync().GetAwaiter().GetResult();
            Assert.That(session.DrainCombatEvents(output), Is.Zero);
        }

        private static BattleClientSession Active(FakeTransport transport, FakeConnector connector)
        {
            var session = new BattleClientSession("127.0.0.1", 22000, "test", 4, connector);
            session.Start(); session.Pump(0);
            transport.Enqueue(TestFrames.Login(42, 1), BattleClientProtocolV1.ControlChannel, 1); session.Pump(0);
            transport.Enqueue(TestFrames.Join(1, 7, 60), BattleClientProtocolV1.ControlChannel, 1); session.Pump(0);
            Assert.That(session.State, Is.EqualTo(BattleClientState.Active));
            return session;
        }

        private static byte[] Event(uint sequence)
        {
            var bytes = new List<byte> { 0x4e,4, 9,100,0,0,0,0,0,0,0, 16 };
            do { byte b = (byte)(sequence & 127); sequence >>= 7; bytes.Add((byte)(b | (sequence > 0 ? 128 : 0))); } while (sequence > 0);
            bytes.AddRange(new byte[] { 42,13, 17,100,0,0,0,0,0,0,0, 24,1, 40,1 });
            return bytes.ToArray();
        }
    }
}
