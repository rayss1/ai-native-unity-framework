using System;
using AiNative.Gameplay;
using AiNative.Realtime;
using NUnit.Framework;

namespace AiNative.Client.Prediction.Tests
{
    public sealed class ClientPredictionAdapterTests
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
        public void InputSendUsesProtocolV1BytesAndInputChannel()
        {
            FakeRealtimeTransport transport = new FakeRealtimeTransport();
            ClientPredictionAdapter adapter = InitializedAdapter(transport, 7, 1);

            PredictionSendResult result = adapter.SendInputAsync(42, 1000, -500)
                .GetAwaiter()
                .GetResult();

            Assert.That(result.Status, Is.EqualTo(PredictionSendStatus.Accepted));
            Assert.That(result.PredictedState.PositionXMillimetres, Is.EqualTo(50));
            Assert.That(result.PredictedState.PositionZMillimetres, Is.EqualTo(-25));
            Assert.That(transport.LastChannel.Id, Is.EqualTo(2));
            Assert.That(transport.LastChannel.Delivery, Is.EqualTo(TransportDelivery.Unreliable));
            Assert.That(transport.LastChannel.Ordering, Is.EqualTo(TransportOrdering.Sequenced));
            Assert.That(transport.LastPayload.ToArray(), Is.EqualTo(new byte[]
            {
                0x4c, 0x04,
                0x09, 0x2a, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x10, 0x01,
                0x18, 0xd0, 0x0f,
                0x20, 0xe7, 0x07,
            }));

            FakeRealtimeTransport maximumTransport = new FakeRealtimeTransport();
            ClientPredictionAdapter maximumAdapter = new ClientPredictionAdapter(
                maximumTransport,
                7,
                16);
            byte[] maximumBaseline = ProtocolFrameFixtures.Snapshot(
                7,
                10,
                uint.MaxValue - 1,
                0,
                0);
            maximumAdapter.ApplyPacket(
                maximumBaseline,
                ProtocolFrameFixtures.Packet(maximumBaseline, SnapshotChannel, 1));
            byte[] exactBuffer = new byte[ClientPredictionAdapter.RequiredInputBufferBytes];
            PredictionPrepareResult maximum = maximumAdapter.PrepareInput(
                ulong.MaxValue,
                int.MinValue,
                int.MaxValue,
                exactBuffer);
            Assert.That(maximum.Status, Is.EqualTo(PredictionPrepareStatus.Prepared));
            Assert.That(maximum.WrittenBytes, Is.EqualTo(exactBuffer.Length));
        }

        [Test]
        public void SnapshotAcknowledgementRewindsAndReplaysNewerInput()
        {
            FakeRealtimeTransport transport = new FakeRealtimeTransport();
            ClientPredictionAdapter adapter = InitializedAdapter(transport, 7, 1);
            adapter.SendInputAsync(11, 1000, 0).GetAwaiter().GetResult();
            adapter.SendInputAsync(12, 1000, 0).GetAwaiter().GetResult();
            byte[] snapshot = ProtocolFrameFixtures.Snapshot(7, 11, 1, 40, 0);

            SnapshotApplyResult applied = adapter.ApplyPacket(
                snapshot,
                ProtocolFrameFixtures.Packet(snapshot, SnapshotChannel, 1));

            Assert.That(applied.Status, Is.EqualTo(SnapshotApplyStatus.Reconciled));
            Assert.That(applied.Reconciliation.Status, Is.EqualTo(ReconciliationStatus.Corrected));
            Assert.That(applied.Reconciliation.DiscardedInputCount, Is.EqualTo(1));
            Assert.That(applied.Reconciliation.ReplayedInputCount, Is.EqualTo(1));
            Assert.That(applied.CorrectionMagnitudeMillimetres, Is.EqualTo(10));
            Assert.That(applied.Reconciliation.After.PositionXMillimetres, Is.EqualTo(90));
            Assert.That(adapter.Diagnostics.Corrections, Is.EqualTo(1));
        }

        [Test]
        public void MatchingSnapshotDoesNotRecordCorrection()
        {
            FakeRealtimeTransport transport = new FakeRealtimeTransport();
            ClientPredictionAdapter adapter = InitializedAdapter(transport, 7, 1);
            adapter.SendInputAsync(11, 1000, 0).GetAwaiter().GetResult();
            byte[] snapshot = ProtocolFrameFixtures.Snapshot(7, 11, 1, 50, 0);

            SnapshotApplyResult applied = adapter.ApplyPacket(
                snapshot,
                ProtocolFrameFixtures.Packet(snapshot, SnapshotChannel, 1));

            Assert.That(applied.Reconciliation.Status, Is.EqualTo(ReconciliationStatus.Matched));
            Assert.That(applied.CorrectionMagnitudeMillimetres, Is.Zero);
            Assert.That(adapter.Diagnostics.Corrections, Is.Zero);
        }

        [Test]
        public void MissingPlayerAndProtocolMismatchFailClosed()
        {
            FakeRealtimeTransport transport = new FakeRealtimeTransport();
            ClientPredictionAdapter adapter = new ClientPredictionAdapter(transport, 7);
            byte[] missing = ProtocolFrameFixtures.Snapshot(7, 10, 0, 0, 0, includePlayer: false);
            byte[] incompatible = ProtocolFrameFixtures.Snapshot(7, 10, 0, 0, 0, protocolMajor: 2);

            SnapshotApplyResult missingResult = adapter.ApplyPacket(
                missing,
                ProtocolFrameFixtures.Packet(missing, SnapshotChannel, 1));
            SnapshotApplyResult incompatibleResult = adapter.ApplyPacket(
                incompatible,
                ProtocolFrameFixtures.Packet(incompatible, SnapshotChannel, 1));

            Assert.That(missingResult.Status, Is.EqualTo(SnapshotApplyStatus.PlayerMissing));
            Assert.That(incompatibleResult.Status, Is.EqualTo(SnapshotApplyStatus.ProtocolMismatch));
            Assert.That(adapter.IsInitialized, Is.False);
        }

        [Test]
        public void TruncatedAndWrongChannelPacketsDoNotChangePrediction()
        {
            FakeRealtimeTransport transport = new FakeRealtimeTransport();
            ClientPredictionAdapter adapter = InitializedAdapter(transport, 7, 1);
            byte[] snapshot = ProtocolFrameFixtures.Snapshot(7, 11, 0, 50, 0);
            ReceivedPacket truncated = ProtocolFrameFixtures.Packet(
                snapshot,
                SnapshotChannel,
                1,
                snapshot.Length + 1);
            ReceivedPacket wrongChannel = ProtocolFrameFixtures.Packet(
                snapshot,
                ControlChannel,
                1);

            SnapshotApplyResult truncatedResult = adapter.ApplyPacket(snapshot, truncated);
            SnapshotApplyResult wrongChannelResult = adapter.ApplyPacket(snapshot, wrongChannel);

            Assert.That(truncatedResult.Status, Is.EqualTo(SnapshotApplyStatus.Truncated));
            Assert.That(wrongChannelResult.Status, Is.EqualTo(SnapshotApplyStatus.WrongChannel));
            Assert.That(adapter.Diagnostics.AcceptedSnapshots, Is.EqualTo(1));
        }

        [Test]
        public void ReconnectResponseAdvancesEpochAndReconciles()
        {
            FakeRealtimeTransport transport = new FakeRealtimeTransport();
            ClientPredictionAdapter adapter = InitializedAdapter(transport, 7, 1);
            adapter.SendInputAsync(11, 1000, 0).GetAwaiter().GetResult();
            byte[] snapshot = ProtocolFrameFixtures.Snapshot(7, 11, 1, 45, 0);
            byte[] reconnect = ProtocolFrameFixtures.Reconnect(2, 11, snapshot);

            SnapshotApplyResult applied = adapter.ApplyPacket(
                reconnect,
                ProtocolFrameFixtures.Packet(reconnect, ControlChannel, 2));

            Assert.That(applied.Status, Is.EqualTo(SnapshotApplyStatus.Reconciled));
            Assert.That(applied.ConnectionEpoch, Is.EqualTo(2));
            Assert.That(applied.Reconciliation.Status, Is.EqualTo(ReconciliationStatus.Corrected));
            Assert.That(adapter.ConnectionEpoch, Is.EqualTo(2));

            byte[] stale = ProtocolFrameFixtures.Snapshot(7, 12, 1, 45, 0);
            SnapshotApplyResult staleResult = adapter.ApplyPacket(
                stale,
                ProtocolFrameFixtures.Packet(stale, SnapshotChannel, 1));
            Assert.That(staleResult.Status, Is.EqualTo(SnapshotApplyStatus.StaleConnectionEpoch));
        }

        [Test]
        public void TransportBackpressureRemainsObservableAfterPrediction()
        {
            FakeRealtimeTransport transport = new FakeRealtimeTransport
            {
                NextSendStatus = SendStatus.WouldBlock,
            };
            ClientPredictionAdapter adapter = InitializedAdapter(transport, 7, 1);

            PredictionSendResult result = adapter.SendInputAsync(11, 1000, 0)
                .GetAwaiter()
                .GetResult();

            Assert.That(result.Status, Is.EqualTo(PredictionSendStatus.WouldBlock));
            Assert.That(result.Predicted, Is.True);
            Assert.That(adapter.TryGetPredictedState(out KinematicState state), Is.True);
            Assert.That(state.LastProcessedInputSequence, Is.EqualTo(1));
        }

        [Test]
        public void SteadyStatePredictionAndInputEncodingAllocateNothing()
        {
            FakeRealtimeTransport transport = new FakeRealtimeTransport();
            ClientPredictionAdapter adapter = InitializedAdapter(transport, 7, 1, 1024);
            byte[] inputBuffer = new byte[ClientPredictionAdapter.RequiredInputBufferBytes];
            for (ulong tick = 1; tick <= 2000; tick++)
            {
                adapter.PrepareInput(tick, 1, -1, inputBuffer);
            }

            byte[] acknowledged = ProtocolFrameFixtures.Snapshot(7, 2010, 2000, 0, 0);
            adapter.ApplyPacket(
                acknowledged,
                ProtocolFrameFixtures.Packet(acknowledged, SnapshotChannel, 1));
            for (ulong tick = 2001; tick <= 3000; tick++)
            {
                adapter.PrepareInput(tick, 1, -1, inputBuffer);
            }

            byte[] secondAcknowledgement = ProtocolFrameFixtures.Snapshot(7, 3010, 3000, 0, 0);
            adapter.ApplyPacket(
                secondAcknowledgement,
                ProtocolFrameFixtures.Packet(secondAcknowledgement, SnapshotChannel, 1));
            PredictionPrepareStatus lastStatus = PredictionPrepareStatus.NotInitialized;
            _ = GC.GetAllocatedBytesForCurrentThread();
            long before = GC.GetAllocatedBytesForCurrentThread();

            for (ulong tick = 3001; tick <= 4000; tick++)
            {
                lastStatus = adapter.PrepareInput(tick, 1, -1, inputBuffer)
                    .Status;
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            for (ulong tick = 4001; tick <= 5000; tick++)
            {
                adapter.PrepareInput(tick, 1, -1, inputBuffer);
            }

            Assert.That(lastStatus, Is.EqualTo(PredictionPrepareStatus.Prepared));
            Assert.That(allocated, Is.Zero);
            Assert.That(adapter.Diagnostics.DroppedInputs, Is.GreaterThan(0));
        }

        private static ClientPredictionAdapter InitializedAdapter(
            FakeRealtimeTransport transport,
            uint entityId,
            uint connectionEpoch,
            int capacity = 16)
        {
            ClientPredictionAdapter adapter = new ClientPredictionAdapter(
                transport,
                entityId,
                capacity);
            byte[] baseline = ProtocolFrameFixtures.Snapshot(entityId, 10, 0, 0, 0);
            SnapshotApplyResult initialized = adapter.ApplyPacket(
                baseline,
                ProtocolFrameFixtures.Packet(baseline, SnapshotChannel, connectionEpoch));
            Assert.That(initialized.Status, Is.EqualTo(SnapshotApplyStatus.Initialized));
            return adapter;
        }
    }
}
