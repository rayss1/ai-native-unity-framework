using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AiNative.Client.Prediction;
using AiNative.Realtime;
using NUnit.Framework;

namespace AiNative.Client.Application.Tests
{
    public sealed class BattleClientSessionTests
    {
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
        internal static byte[] Login(ulong sessionId, uint epoch)
        {
            List<byte> payload = Header(BattleClientProtocolV1.LoginResponseMessageId);
            payload.Add(0x09);
            AddFixed64(payload, sessionId);
            payload.Add(0x10);
            AddVarint(payload, epoch);
            return payload.ToArray();
        }

        internal static byte[] Join(uint roomId, uint entityId, uint tickRate)
        {
            List<byte> payload = Header(BattleClientProtocolV1.JoinRoomResponseMessageId);
            payload.Add(0x08); AddVarint(payload, roomId);
            payload.Add(0x10); AddVarint(payload, entityId);
            payload.Add(0x18); AddVarint(payload, tickRate);
            return payload.ToArray();
        }

        internal static byte[] Snapshot(uint entityId, ulong tick, uint acknowledgement)
        {
            List<byte> frame = Header(BattleClientProtocolV1.SnapshotMessageId);
            frame.AddRange(SnapshotPayload(entityId, tick, acknowledgement));
            return frame.ToArray();
        }

        internal static byte[] SnapshotPayload(uint entityId, ulong tick, uint acknowledgement)
        {
            List<byte> player = new List<byte> { 0x08 };
            AddVarint(player, entityId);
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
    }
}
