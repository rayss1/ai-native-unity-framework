using System;
using System.Buffers.Binary;
using AiNative.Client.Protocol;
using AiNative.Gameplay;
using AiNative.Realtime;
using NUnit.Framework;

namespace AiNative.Client.Prediction.Tests
{
    public sealed class CodecInjectionTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void Sequence_exhaustion_cannot_be_reopened_by_an_unacknowledged_snapshot(bool arena)
        {
            using var codec = new FixtureCodec();
            var legacy = new ClientPredictionAdapter(new FakeRealtimeTransport(), codec, 7);
            var fps = new ArenaClientPredictionAdapter(new FakeRealtimeTransport(), codec, 7);
            byte[] snapshot = new byte[FixtureCodec.FrameBytes], input = new byte[codec.MaximumInputFrameBytes];
            codec.Snapshot(snapshot, 100, uint.MaxValue - 1, 0, arena);
            Apply(arena, legacy, fps, codec, snapshot, 2);
            Prepare(arena, legacy, fps, input);
            Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(10)), Is.EqualTo(uint.MaxValue));
            codec.Snapshot(snapshot, 101, uint.MaxValue - 1, 0, arena);
            Apply(arena, legacy, fps, codec, snapshot, 2);
            if (arena)
                Assert.That(fps.PrepareInput(102, 1000, 0, 0, 0, ArenaButtons.None, ArenaWeaponId.Machinegun, input).Status,
                    Is.EqualTo(ArenaPredictionPrepareStatus.SequenceExhausted));
            else
                Assert.That(legacy.PrepareInput(102, 1000, 0, input).Status, Is.EqualTo(PredictionPrepareStatus.SequenceExhausted));
        }
        [TestCase(false)]
        [TestCase(true)]
        public void Alternate_format_initializes_predicts_and_reconciles_without_protobuf(bool arena)
        {
            using var codec = new FixtureCodec();
            var legacy = new ClientPredictionAdapter(new FakeRealtimeTransport(), codec, 7);
            var fps = new ArenaClientPredictionAdapter(new FakeRealtimeTransport(), codec, 7);
            byte[] snapshot = new byte[FixtureCodec.FrameBytes], input = new byte[codec.MaximumInputFrameBytes];
            codec.Snapshot(snapshot, 100, 5, 0, arena);
            Apply(arena, legacy, fps, codec, snapshot, 2);
            Prepare(arena, legacy, fps, input);
            Assert.That(input[0], Is.EqualTo(0xD7), "Fixture format deliberately has no Protobuf message ID or tags.");
            Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(10)), Is.EqualTo(6));
            Assert.That(BinaryPrimitives.ReadInt32LittleEndian(input.AsSpan(14)), Is.EqualTo(1000));
            Prepare(arena, legacy, fps, input);
            codec.Snapshot(snapshot, 101, 6, 3, arena);
            Apply(arena, legacy, fps, codec, snapshot, 2);
            if (arena)
            {
                Assert.That(fps.Current.LastProcessedInputSequence, Is.EqualTo(7));
                Assert.That(fps.Current.Tick, Is.EqualTo(102));
                Assert.That(fps.Current.PositionXMillimetres, Is.GreaterThan(3));
            }
            else
            {
                Assert.That(legacy.TryGetPredictedState(out var state), Is.True);
                Assert.That(state, Is.EqualTo(new KinematicState(102, 7, 53, 0)));
            }
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void Failed_encoding_or_short_buffer_does_not_advance_prediction_or_sequence(bool arena, bool shortBuffer)
        {
            using var codec = new FixtureCodec();
            var legacy = new ClientPredictionAdapter(new FakeRealtimeTransport(), codec, 7);
            var fps = new ArenaClientPredictionAdapter(new FakeRealtimeTransport(), codec, 7);
            byte[] snapshot = new byte[FixtureCodec.FrameBytes];
            codec.Snapshot(snapshot, 100, 5, 0, arena);
            Apply(arena, legacy, fps, codec, snapshot, 2);
            legacy.TryGetPredictedState(out var beforeLegacy);
            var beforeArena = arena ? fps.Current : default;
            byte[] input = new byte[codec.MaximumInputFrameBytes];
            codec.RejectEncoding = !shortBuffer;
            if (arena)
            {
                var result = fps.PrepareInput(101, 1000, 0, 0, 0, ArenaButtons.None, ArenaWeaponId.Machinegun,
                    shortBuffer ? input.AsSpan(0, input.Length - 1) : input.AsSpan());
                Assert.That(result.Status, Is.EqualTo(ArenaPredictionPrepareStatus.BufferTooSmall));
                Assert.That(result.WrittenBytes, Is.Zero);
                Assert.That(fps.Current, Is.EqualTo(beforeArena));
            }
            else
            {
                var result = legacy.PrepareInput(101, 1000, 0, shortBuffer ? input.AsSpan(0, input.Length - 1) : input.AsSpan());
                Assert.That(result.Status, Is.EqualTo(PredictionPrepareStatus.BufferTooSmall));
                Assert.That(result.WrittenBytes, Is.Zero);
                legacy.TryGetPredictedState(out var after);
                Assert.That(after, Is.EqualTo(beforeLegacy));
            }
            codec.RejectEncoding = false;
            Prepare(arena, legacy, fps, input);
            Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(10)), Is.EqualTo(6));
        }

        [TestCase(false, 0)]
        [TestCase(true, 0)]
        [TestCase(false, 1)]
        [TestCase(true, 1)]
        [TestCase(false, 2)]
        [TestCase(true, 2)]
        [TestCase(false, 3)]
        [TestCase(true, 3)]
        public void Malformed_stale_tick_or_old_connection_cannot_mutate_prediction(bool arena, int failure)
        {
            using var codec = new FixtureCodec();
            var legacy = new ClientPredictionAdapter(new FakeRealtimeTransport(), codec, 7);
            var fps = new ArenaClientPredictionAdapter(new FakeRealtimeTransport(), codec, 7);
            byte[] snapshot = new byte[FixtureCodec.FrameBytes], input = new byte[codec.MaximumInputFrameBytes];
            codec.Snapshot(snapshot, 100, 5, 0, arena);
            Apply(arena, legacy, fps, codec, snapshot, 2);
            Prepare(arena, legacy, fps, input);
            legacy.TryGetPredictedState(out var beforeLegacy);
            var beforeArena = arena ? fps.Current : default;
            codec.Snapshot(snapshot, failure == 1 ? 99UL : 200UL, failure == 3 ? 4u : 1000u, 4000, arena);
            if (failure == 0) snapshot[0] = 0;
            Apply(arena, legacy, fps, codec, snapshot, failure == 2 ? 1u : 2u);
            if (arena) Assert.That(fps.Current, Is.EqualTo(beforeArena));
            else { legacy.TryGetPredictedState(out var after); Assert.That(after, Is.EqualTo(beforeLegacy)); }
            Prepare(arena, legacy, fps, input);
            Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(10)), Is.EqualTo(7), "Rejected packets must not change the next operation number.");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Ten_thousand_encoding_prediction_and_history_replay_cycles_allocate_zero(bool arena)
        {
            using var codec = new FixtureCodec();
            var legacy = new ClientPredictionAdapter(new FakeRealtimeTransport(), codec, 7);
            var fps = new ArenaClientPredictionAdapter(new FakeRealtimeTransport(), codec, 7);
            byte[] snapshot = new byte[FixtureCodec.FrameBytes], input = new byte[codec.MaximumInputFrameBytes];
            codec.Snapshot(snapshot, 100, 0, 0, arena);
            Apply(arena, legacy, fps, codec, snapshot, 2);
            for (int i = 0; i < 1000; i++) Cycle(arena, legacy, fps, codec, snapshot, input, i);

            int replayed = 0;
            using var allocationMeter = new CalibratedAllocationMeter(); allocationMeter.RequireSupported(); allocationMeter.Begin();
            for (int i = 1000; i < 11000; i++) replayed += Cycle(arena, legacy, fps, codec, snapshot, input, i);
            long allocated = allocationMeter.End();
            Assert.That(allocated, Is.Zero);
            Assert.That(replayed, Is.EqualTo(10000), "Every measured reconciliation must replay one pending input.");
            uint sequence = arena ? fps.Current.LastProcessedInputSequence : legacy.LastPreparedInputSequence;
            Assert.That(sequence, Is.EqualTo(22000));
        }

        private static int Cycle(bool arena, ClientPredictionAdapter legacy, ArenaClientPredictionAdapter fps,
            FixtureCodec codec, byte[] snapshot, byte[] input, int index)
        {
            Prepare(arena, legacy, fps, input);
            uint acknowledgement = BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(10));
            Prepare(arena, legacy, fps, input);
            codec.Snapshot(snapshot, (ulong)(101 + index * 2), acknowledgement, 0, arena);
            return Apply(arena, legacy, fps, codec, snapshot, 2); // The second input remains pending and must replay.
        }

        private static void Prepare(bool arena, ClientPredictionAdapter legacy, ArenaClientPredictionAdapter fps, byte[] input)
        {
            if (arena) fps.PrepareInput(101, 1000, 0, 0, 0, ArenaButtons.None, ArenaWeaponId.Machinegun, input);
            else legacy.PrepareInput(101, 1000, 0, input);
        }

        private static int Apply(bool arena, ClientPredictionAdapter legacy, ArenaClientPredictionAdapter fps,
            FixtureCodec codec, byte[] snapshot, uint epoch)
        {
            var packet = new ReceivedPacket(codec.SnapshotChannel, snapshot.Length, snapshot.Length, 1, epoch);
            if (arena) return fps.ApplySnapshot(snapshot, packet).Reconciliation.ReplayedInputCount;
            return legacy.ApplyPacket(snapshot, packet).Reconciliation.ReplayedInputCount;
        }

        // Test-only fixed-width format. It has neither protobuf fields nor the production envelope.
        private sealed class FixtureCodec : IBattleProtocolCodec
        {
            public const int FrameBytes = 32;
            public bool RejectEncoding;
            private bool _disposed;
            public int MaximumFrameBytes => FrameBytes;
            public int MaximumInputFrameBytes => FrameBytes;
            public TransportChannel ControlChannel => new TransportChannel(40, TransportDelivery.Reliable, TransportOrdering.Ordered);
            public TransportChannel InputChannel => new TransportChannel(41, TransportDelivery.Unreliable, TransportOrdering.Sequenced);
            public TransportChannel SnapshotChannel => new TransportChannel(42, TransportDelivery.Unreliable, TransportOrdering.Sequenced);
            public TransportChannel EventChannel => new TransportChannel(43, TransportDelivery.Reliable, TransportOrdering.Ordered);
            public bool TryEncodeInput(in ArenaInput input, Span<byte> destination, out int writtenBytes)
                => Encode(input.ClientTick, input.Sequence, input.MoveXMilli, input.MoveZMilli, destination, out writtenBytes);
            public bool TryEncodeLegacyInput(ulong tick, in KinematicInput input, Span<byte> destination, out int writtenBytes)
                => Encode(tick, input.Sequence, input.MoveXMilli, input.MoveZMilli, destination, out writtenBytes);
            private bool Encode(ulong tick, uint sequence, int x, int z, Span<byte> bytes, out int written)
            {
                written = 0;
                if (_disposed || RejectEncoding || bytes.Length < FrameBytes) return false;
                bytes.Slice(0, FrameBytes).Clear(); bytes[0] = 0xD7; bytes[1] = 1;
                BinaryPrimitives.WriteUInt64LittleEndian(bytes.Slice(2), tick);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.Slice(10), sequence);
                BinaryPrimitives.WriteInt32LittleEndian(bytes.Slice(14), x);
                BinaryPrimitives.WriteInt32LittleEndian(bytes.Slice(18), z);
                written = FrameBytes; return true;
            }
            public void Snapshot(Span<byte> bytes, ulong tick, uint ack, int x, bool arena)
            {
                bytes.Clear(); bytes[0] = 0xD7; bytes[1] = 2;
                BinaryPrimitives.WriteUInt64LittleEndian(bytes.Slice(2), tick);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.Slice(10), ack);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.Slice(14), 7);
                BinaryPrimitives.WriteInt32LittleEndian(bytes.Slice(18), x);
                bytes[30] = arena ? (byte)1 : (byte)0;
            }
            public BattleDecodeStatus TryDecode(ReadOnlySpan<byte> bytes, BattlePacketBuffer destination)
            {
                if (_disposed) return BattleDecodeStatus.Disposed;
                if (bytes.Length != FrameBytes || bytes[0] != 0xD7 || bytes[1] != 2) return BattleDecodeStatus.MalformedPayload;
                ulong tick = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(2));
                if (tick > long.MaxValue) return BattleDecodeStatus.MalformedPayload;
                uint ack = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(10));
                var state = new ArenaPlayerState((long)tick, BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(18)), 0, 0) { LastProcessedInputSequence = ack };
                destination.Kind = BattleMessageKind.Snapshot; destination.Tick = tick;
                destination.Acknowledgement = ack; destination.PlayerCount = 1; destination.PickupCount = 0;
                destination.HasArenaData = bytes[30] != 0;
                destination.Players[0] = new ArenaSnapshotPlayer(BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(14)), state);
                return BattleDecodeStatus.Accepted;
            }
            public bool TryEncodeLogin(string build, in BattleAdmission admission, Span<byte> destination, out int writtenBytes) { writtenBytes = 0; return false; }
            public bool TryEncodeJoin(ulong session, uint room, Span<byte> destination, out int writtenBytes) { writtenBytes = 0; return false; }
            public bool TryEncodeReconnect(ulong session, uint epoch, ulong tick, Span<byte> destination, out int writtenBytes) { writtenBytes = 0; return false; }
            public void Reset() { }
            public void Dispose() { _disposed = true; }
        }
    }
}
