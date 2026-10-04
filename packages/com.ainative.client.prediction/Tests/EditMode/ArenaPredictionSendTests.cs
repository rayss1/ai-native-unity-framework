using System;
using System.Threading;
using System.Threading.Tasks;
using AiNative.Gameplay;
using AiNative.Realtime;
using NUnit.Framework;

namespace AiNative.Client.Prediction.Tests
{
    public sealed class ArenaPredictionSendTests
    {
        [Test]
        public void PreparedCallerFrameIsTheFrameSentWithoutPredictingAgain()
        {
            FakeRealtimeTransport transport = new FakeRealtimeTransport();
            ArenaClientPredictionAdapter adapter = new ArenaClientPredictionAdapter(transport, 1);
            adapter.Initialize(new ArenaPlayerState(100, 0, 0, 0));
            byte[] frame = new byte[ArenaClientPredictionAdapter.RequiredInputBufferBytes];
            ArenaPredictionPrepareResult prepared = adapter.PrepareInput(101, 1000, -500,
                90000, 0, ArenaButtons.None, ArenaWeaponId.Machinegun, frame);
            long tick = adapter.Current.Tick;

            SendResult sent = adapter.SendPreparedAsync(frame.AsMemory(0, prepared.WrittenBytes)).GetAwaiter().GetResult();

            Assert.That(sent.Status, Is.EqualTo(SendStatus.Accepted));
            Assert.That(transport.LastPayload.ToArray(), Is.EqualTo(frame.AsSpan(0, prepared.WrittenBytes).ToArray()));
            Assert.That(transport.LastChannel, Is.EqualTo(ArenaClientProtocolV1.InputChannel));
            Assert.That(adapter.Current.Tick, Is.EqualTo(tick));
        }

        [TestCase(SendStatus.WouldBlock)]
        [TestCase(SendStatus.DroppedByPolicy)]
        [TestCase(SendStatus.Closed)]
        [TestCase(SendStatus.Faulted)]
        public void SendOutcomeIsObservableAndRetryPreservesPrediction(SendStatus status)
        {
            FakeRealtimeTransport transport = new FakeRealtimeTransport { NextSendStatus = status };
            ArenaClientPredictionAdapter adapter = new ArenaClientPredictionAdapter(transport, 1);
            adapter.Initialize(new ArenaPlayerState(100, 0, 0, 0));
            byte[] frame = new byte[ArenaClientPredictionAdapter.RequiredInputBufferBytes];
            var prepared = adapter.PrepareInput(101, 1000, 0, 0, 0, ArenaButtons.None, ArenaWeaponId.Machinegun, frame);
            var payload = frame.AsMemory(0, prepared.WrittenBytes);
            Assert.That(adapter.SendPreparedAsync(payload).Result.Status, Is.EqualTo(status));
            transport.NextSendStatus = SendStatus.Accepted;
            Assert.That(adapter.SendPreparedAsync(payload).Result.Status, Is.EqualTo(SendStatus.Accepted));
            Assert.That(adapter.Current.Tick, Is.EqualTo(prepared.PredictedState.Tick));
            Assert.That(transport.LastPayload.ToArray(), Is.EqualTo(payload.ToArray()));
        }

        [Test]
        public void InvalidCancelledAndDisposedSendsDoNotReachTransport()
        {
            FakeRealtimeTransport transport = new FakeRealtimeTransport();
            ArenaClientPredictionAdapter adapter = new ArenaClientPredictionAdapter(transport, 1);
            Assert.Throws<ArgumentException>(() => adapter.SendPreparedAsync(ReadOnlyMemory<byte>.Empty));
            Assert.That(adapter.SendPreparedAsync(new byte[ArenaClientPredictionAdapter.RequiredInputBufferBytes + 1]).Result.Status,
                Is.EqualTo(SendStatus.PayloadTooLarge));
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                Assert.ThrowsAsync<TaskCanceledException>(async () => await adapter.SendPreparedAsync(new byte[1], cancellation.Token));
            }
            adapter.DisposeAsync().GetAwaiter().GetResult();
            Assert.That(adapter.SendPreparedAsync(new byte[1]).Result.Status, Is.EqualTo(SendStatus.Closed));
            Assert.That(transport.SendCount, Is.Zero);
        }

        [Test]
        public async Task DelayedTransportOwnsEachExplicitFrameUntilObservedCompletion()
        {
            var transport = new DeferredTransport();
            var adapter = new ArenaClientPredictionAdapter(transport, 1);
            adapter.Initialize(new ArenaPlayerState(100, 0, 0, 0));
            byte[] first = new byte[ArenaClientPredictionAdapter.RequiredInputBufferBytes];
            var prepared = adapter.PrepareInput(101, 1000, 0, 0, 0, ArenaButtons.None, ArenaWeaponId.Machinegun, first);
            var pending = adapter.SendPreparedAsync(first.AsMemory(0, prepared.WrittenBytes));
            Assert.That(pending.IsCompleted, Is.False);
            byte[] second = new byte[first.Length];
            adapter.PrepareInput(102, -1000, 0, 0, 0, ArenaButtons.None, ArenaWeaponId.Machinegun, second);
            Assert.That(transport.Payload.ToArray(), Is.EqualTo(first.AsSpan(0, prepared.WrittenBytes).ToArray()));
            transport.Completion.SetResult(new SendResult(SendStatus.WouldBlock, 0));
            Assert.That((await pending).Status, Is.EqualTo(SendStatus.WouldBlock));
        }

        private sealed class DeferredTransport : IRealtimeTransport
        {
            public readonly TaskCompletionSource<SendResult> Completion = new TaskCompletionSource<SendResult>();
            public ReadOnlyMemory<byte> Payload;
            public CancellationToken Token;
            public bool ThrowOnSend;
            public TransportState State => TransportState.Connected;
            public ValueTask<SendResult> SendAsync(TransportChannel channel, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
            {
                if (ThrowOnSend) throw new InvalidOperationException("send-failed");
                Payload = payload;
                Token = cancellationToken;
                return new ValueTask<SendResult>(Completion.Task);
            }
            public bool TryReceive(Span<byte> destination, out ReceivedPacket packet) { packet = default; return false; }
            public ValueTask DisposeAsync() => default;
        }

        [Test]
        public void InFlightCancellationAndTransportFailuresRemainObservable()
        {
            var transport = new DeferredTransport();
            var adapter = new ArenaClientPredictionAdapter(transport, 1);
            using (var cancellation = new CancellationTokenSource())
            {
                var pending = adapter.SendPreparedAsync(new byte[1], cancellation.Token);
                Assert.That(transport.Token, Is.EqualTo(cancellation.Token));
                cancellation.Cancel();
                Assert.That(transport.Token.IsCancellationRequested, Is.True);
                transport.Completion.SetCanceled();
                Assert.ThrowsAsync<TaskCanceledException>(async () => await pending);
            }
            var faulted = new DeferredTransport();
            var failure = new InvalidOperationException("async-send-failed");
            var pendingFailure = new ArenaClientPredictionAdapter(faulted, 1).SendPreparedAsync(new byte[1]);
            faulted.Completion.SetException(failure);
            Assert.That(Assert.ThrowsAsync<InvalidOperationException>(async () => await pendingFailure), Is.SameAs(failure));
            var throwing = new ArenaClientPredictionAdapter(new DeferredTransport { ThrowOnSend = true }, 1);
            Assert.Throws<InvalidOperationException>(() => throwing.SendPreparedAsync(new byte[1]));
        }
    }
}
