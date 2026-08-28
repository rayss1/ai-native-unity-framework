using System;
using AiNative.Protocol.V1;
using AiNative.Realtime;
using Google.Protobuf;
using NUnit.Framework;

namespace AiNative.Client.Prediction.Tests
{
    public sealed class ProtocolParityTests
    {
        private static readonly TransportChannel SnapshotChannel = new TransportChannel(
            1,
            TransportDelivery.Unreliable,
            TransportOrdering.Sequenced);

        private static readonly TransportChannel ControlChannel = new TransportChannel(
            0,
            TransportDelivery.Reliable,
            TransportOrdering.Ordered);

        [Test]
        public void EncodedInputMatchesTrackedProtobufPayload()
        {
            FakeRealtimeTransport transport = new FakeRealtimeTransport();
            ClientPredictionAdapter adapter = Initialize(transport, 7, 1);

            adapter.SendInputAsync(42, 1000, -500).GetAwaiter().GetResult();
            InputCommand expected = new InputCommand
            {
                RoomTick = 42,
                Sequence = 1,
                MoveXMilli = 1000,
                MoveYMilli = -500,
            };
            byte[] expectedFrame = Envelope(MessageId.InputCommand, expected);

            Assert.That(transport.LastPayload.ToArray(), Is.EqualTo(expectedFrame));
        }

        [Test]
        public void GeneratedSnapshotReconcilesThroughUnityRuntimeCodec()
        {
            FakeRealtimeTransport transport = new FakeRealtimeTransport();
            ClientPredictionAdapter adapter = Initialize(transport, 7, 1);
            adapter.SendInputAsync(11, 1000, 0).GetAwaiter().GetResult();
            adapter.SendInputAsync(12, 1000, 0).GetAwaiter().GetResult();
            Snapshot snapshot = new Snapshot
            {
                ProtocolMajor = 1,
                RoomTick = 11,
                LastProcessedInputSequence = 1,
            };
            snapshot.Players.Add(new PlayerState
            {
                EntityId = 7,
                PositionXMilli = 40,
            });
            byte[] frame = Envelope(MessageId.Snapshot, snapshot);
            Array.Resize(ref frame, frame.Length + 3);
            frame[frame.Length - 3] = 0xa0;
            frame[frame.Length - 2] = 0x06;
            frame[frame.Length - 1] = 0x07;

            SnapshotApplyResult applied = adapter.ApplyPacket(
                frame,
                new ReceivedPacket(SnapshotChannel, frame.Length, frame.Length, 1, 1));

            Assert.That(applied.Status, Is.EqualTo(SnapshotApplyStatus.Reconciled));
            Assert.That(applied.Reconciliation.After.PositionXMillimetres, Is.EqualTo(90));
        }

        [Test]
        public void GeneratedReconnectResponseAdvancesConnectionEpoch()
        {
            FakeRealtimeTransport transport = new FakeRealtimeTransport();
            ClientPredictionAdapter adapter = Initialize(transport, 7, 1);
            Snapshot snapshot = new Snapshot
            {
                ProtocolMajor = 1,
                RoomTick = 12,
            };
            snapshot.Players.Add(new PlayerState { EntityId = 7 });
            ReconnectResponse reconnect = new ReconnectResponse
            {
                ConnectionEpoch = 2,
                ResumeTick = 12,
                Snapshot = snapshot,
            };
            byte[] frame = Envelope(MessageId.ReconnectResponse, reconnect);

            SnapshotApplyResult applied = adapter.ApplyPacket(
                frame,
                new ReceivedPacket(ControlChannel, frame.Length, frame.Length, 1, 2));

            Assert.That(applied.Status, Is.EqualTo(SnapshotApplyStatus.Reconciled));
            Assert.That(adapter.ConnectionEpoch, Is.EqualTo(2));
        }

        private static ClientPredictionAdapter Initialize(
            FakeRealtimeTransport transport,
            uint entityId,
            uint connectionEpoch)
        {
            ClientPredictionAdapter adapter = new ClientPredictionAdapter(transport, entityId, 16);
            Snapshot snapshot = new Snapshot
            {
                ProtocolMajor = 1,
                RoomTick = 10,
            };
            snapshot.Players.Add(new PlayerState { EntityId = entityId });
            byte[] frame = Envelope(MessageId.Snapshot, snapshot);
            SnapshotApplyResult applied = adapter.ApplyPacket(
                frame,
                new ReceivedPacket(
                    SnapshotChannel,
                    frame.Length,
                    frame.Length,
                    1,
                    connectionEpoch));
            Assert.That(applied.Status, Is.EqualTo(SnapshotApplyStatus.Initialized));
            return adapter;
        }

        private static byte[] Envelope(MessageId messageId, IMessage message)
        {
            byte[] payload = message.ToByteArray();
            byte[] frame = new byte[payload.Length + 2];
            ushort id = checked((ushort)messageId);
            frame[0] = (byte)id;
            frame[1] = (byte)(id >> 8);
            payload.CopyTo(frame, 2);
            return frame;
        }
    }
}
