using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AiNative.Realtime;
using NUnit.Framework;

namespace AiNative.Client.Fantasy.Tests
{
    public sealed class FantasyKcpRealtimeTransportTests
    {
        [Test]
        public async Task EnvelopeAndChannels_MatchServerWireContract()
        {
            var session = new FakeFantasyClientSession();
            var transport = new FantasyKcpRealtimeTransport(session);
            TransportChannel channel = ReliableOrdered(0);

            SendResult result = await transport.SendAsync(channel, new byte[] { 7, 8, 9 });

            Assert.That(result.Status, Is.EqualTo(SendStatus.Accepted));
            Assert.That(session.Sent, Has.Count.EqualTo(1));
            Assert.That(session.Sent[0].OpCode(), Is.EqualTo(134217729U));
            Assert.That(session.Sent[0].ChannelId, Is.EqualTo(0U));
            Assert.That(session.Sent[0].Payload, Is.EqualTo(new byte[] { 7, 8, 9 }));
            Assert.That(FantasyClientSessionRouter.Deliver(
                session.RuntimeId,
                2,
                new byte[] { 4 },
                1), Is.True);
            Assert.That(transport.TryReceive(new byte[1], out ReceivedPacket packet), Is.True);
            Assert.That(packet.Channel, Is.EqualTo(UnreliableSequenced(2)));
            await transport.DisposeAsync();
        }

        [Test]
        public async Task Send_AssignsStrictlyIncreasingSequences()
        {
            var session = new FakeFantasyClientSession();
            var transport = new FantasyKcpRealtimeTransport(session);

            await transport.SendAsync(ReliableOrdered(3), new byte[] { 1 });
            await transport.SendAsync(ReliableOrdered(3), new byte[] { 2 });

            Assert.That(session.Sent[0].Sequence, Is.EqualTo(1UL));
            Assert.That(session.Sent[1].Sequence, Is.EqualTo(2UL));
            await transport.DisposeAsync();
        }

        [Test]
        public async Task Epoch_IsNonZeroNonDecreasingAndAppliedAtDequeue()
        {
            var session = new FakeFantasyClientSession();
            var transport = new FantasyKcpRealtimeTransport(session);
            Assert.That(transport.TryEnqueueReceived(1, new byte[] { 1 }, 10), Is.True);
            Assert.That(transport.TryEnqueueReceived(1, new byte[] { 2 }, 9), Is.False);
            Assert.That(transport.Diagnostics.StaleSequences, Is.EqualTo(1));

            Assert.That(transport.TryAdvanceConnectionEpoch(0), Is.False);
            Assert.That(transport.TryAdvanceConnectionEpoch(8), Is.True);
            Assert.That(transport.TryAdvanceConnectionEpoch(8), Is.True);
            Assert.That(transport.TryAdvanceConnectionEpoch(7), Is.False);
            Assert.That(transport.TryReceive(new byte[1], out ReceivedPacket packet), Is.True);
            Assert.That(packet.ConnectionEpoch, Is.EqualTo(8U));
            Assert.That(transport.TryEnqueueReceived(1, new byte[] { 2 }, 1), Is.True,
                "Advancing an epoch resets per-channel sequence history.");
            await transport.DisposeAsync();
        }

        [Test]
        public async Task Receive_ReportsTruncationWithoutOverwritingDestination()
        {
            var session = new FakeFantasyClientSession();
            var transport = new FantasyKcpRealtimeTransport(session);
            Assert.That(transport.TryEnqueueReceived(0, new byte[] { 1, 2, 3, 4 }, 1), Is.True);
            var destination = new byte[2];

            Assert.That(transport.TryReceive(destination, out ReceivedPacket packet), Is.True);
            Assert.That(destination, Is.EqualTo(new byte[] { 1, 2 }));
            Assert.That(packet.WrittenBytes, Is.EqualTo(2));
            Assert.That(packet.RequiredBytes, Is.EqualTo(4));
            Assert.That(packet.IsComplete, Is.False);
            await transport.DisposeAsync();
        }

        [Test]
        public async Task BoundedQueues_ReturnBackpressureAndDropExcessInbound()
        {
            var session = new FakeFantasyClientSession { RunPostsImmediately = false };
            var transport = new FantasyKcpRealtimeTransport(
                session,
                maximumQueuedPackets: 1,
                maximumQueuedBytes: 4);

            SendResult first = await transport.SendAsync(ReliableOrdered(0), new byte[] { 1, 2, 3, 4 });
            SendResult second = await transport.SendAsync(ReliableOrdered(0), new byte[] { 5 });
            Assert.That(first.Status, Is.EqualTo(SendStatus.Accepted));
            Assert.That(second.Status, Is.EqualTo(SendStatus.WouldBlock));
            Assert.That(transport.TryEnqueueReceived(0, new byte[] { 1, 2, 3, 4 }, 1), Is.True);
            Assert.That(transport.TryEnqueueReceived(0, new byte[] { 5 }, 2), Is.False);
            Assert.That(transport.Diagnostics.SendBackpressure, Is.EqualTo(1));
            Assert.That(transport.Diagnostics.InboundDropped, Is.EqualTo(1));
            await transport.DisposeAsync();
        }

        [Test]
        public async Task FrameAndChannelBoundaries_AreEnforced()
        {
            var session = new FakeFantasyClientSession();
            var transport = new FantasyKcpRealtimeTransport(session);
            var maximum = new byte[FantasyKcpRealtimeTransport.MaximumFrameBytes];
            var oversized = new byte[FantasyKcpRealtimeTransport.MaximumFrameBytes + 1];

            SendResult accepted = await transport.SendAsync(UnreliableSequenced(1), maximum);
            SendResult tooLarge = await transport.SendAsync(UnreliableSequenced(1), oversized);
            SendResult invalid = await transport.SendAsync(
                new TransportChannel(1, TransportDelivery.Reliable, TransportOrdering.Ordered),
                new byte[1]);

            Assert.That(accepted.Status, Is.EqualTo(SendStatus.Accepted));
            Assert.That(tooLarge.Status, Is.EqualTo(SendStatus.PayloadTooLarge));
            Assert.That(invalid.Status, Is.EqualTo(SendStatus.DroppedByPolicy));
            Assert.That(transport.Diagnostics.OversizedFrames, Is.EqualTo(1));
            Assert.That(transport.Diagnostics.InvalidChannels, Is.EqualTo(1));
            await transport.DisposeAsync();

            var faultingSession = new FakeFantasyClientSession { ThrowWhenPosting = true };
            var faultingTransport = new FantasyKcpRealtimeTransport(faultingSession);
            await faultingTransport.SendAsync(ReliableOrdered(0), new byte[1]);
            SendResult faulted = await faultingTransport.SendAsync(ReliableOrdered(0), new byte[1]);
            Assert.That(faulted.Status, Is.EqualTo(SendStatus.Faulted));

            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                Assert.ThrowsAsync<TaskCanceledException>(async () =>
                    await faultingTransport.SendAsync(
                        ReliableOrdered(0),
                        new byte[1],
                        cancelled.Token));
            }

            await faultingTransport.DisposeAsync();
        }

        [Test]
        public async Task Dispose_UnregistersSessionAndIgnoresLateDelivery()
        {
            var session = new FakeFantasyClientSession();
            var transport = new FantasyKcpRealtimeTransport(session);
            Assert.That(FantasyClientSessionRouter.Deliver(
                session.RuntimeId,
                0,
                new byte[] { 1 },
                1), Is.True);

            await transport.DisposeAsync();

            Assert.That(FantasyClientSessionRouter.Deliver(
                session.RuntimeId,
                0,
                new byte[] { 2 },
                2), Is.False);
            Assert.That(session.IsClosed, Is.True);
            Assert.That(transport.State, Is.EqualTo(TransportState.Closed));
        }

        [Test]
        public async Task DisconnectThenDispose_ReleasesSessionAndRejectsLateCallbacks()
        {
            var session = new FakeFantasyClientSession { RunPostsImmediately = false };
            var transport = new FantasyKcpRealtimeTransport(session);
            await transport.SendAsync(ReliableOrdered(0), new byte[] { 1 });
            transport.NotifyDisconnected();

            await transport.DisposeAsync();
            session.RunPostedActions();
            transport.NotifyDisconnected();

            Assert.That(session.DisposeCount, Is.EqualTo(1));
            Assert.That(transport.State, Is.EqualTo(TransportState.Closed));
            Assert.That(session.Sent, Is.Empty);
            Assert.That(FantasyClientSessionRouter.Deliver(session.RuntimeId, 0, new byte[] { 2 }, 1), Is.False);
        }

        [Test]
        public async Task RepeatedDispose_KeepsClosedAndReleasesSessionOnce()
        {
            var session = new FakeFantasyClientSession();
            var transport = new FantasyKcpRealtimeTransport(session);
            await transport.DisposeAsync();
            await transport.DisposeAsync();
            await transport.DisposeAsync();

            Assert.That(session.DisposeCount, Is.EqualTo(1));
            Assert.That(transport.State, Is.EqualTo(TransportState.Closed));
        }

        [Test]
        public void ConcurrentDispose_ReleasesSessionOnce()
        {
            var session = new FakeFantasyClientSession();
            var transport = new FantasyKcpRealtimeTransport(session);
            Parallel.For(0, 32, _ => transport.DisposeAsync().GetAwaiter().GetResult());

            Assert.That(session.DisposeCount, Is.EqualTo(1));
            Assert.That(transport.State, Is.EqualTo(TransportState.Closed));
        }

        [Test]
        public async Task RuntimeIdResetBeforeDisconnect_UnregistersOriginalRoutingIdentity()
        {
            var session = new FakeFantasyClientSession();
            long registeredId = session.RuntimeId;
            var transport = new FantasyKcpRealtimeTransport(session);
            FantasyKcpRealtimeTransport replacement = null;
            try
            {
                session.ResetRuntimeId(); // Fantasy Entity disposal clears RuntimeId before the disconnect callback.
                transport.NotifyDisconnected();
                await transport.DisposeAsync();

                Assert.DoesNotThrow(() => replacement = new FantasyKcpRealtimeTransport(new FakeFantasyClientSession(registeredId)), "Disconnection must remove the original router key even after Fantasy clears its session ID.");
                Assert.That(session.DisposeCount, Is.EqualTo(1));
            }
            finally
            {
                FantasyClientSessionRouter.Remove(registeredId, transport);
                if (replacement != null) await replacement.DisposeAsync();
            }
        }

        [Test]
        public async Task SendPassingConnectivityCheckBeforeDispose_DoesNotAcceptPacketAfterDisposal()
        {
            var session = new FakeFantasyClientSession { RunPostsImmediately = false };
            var transport = new FantasyKcpRealtimeTransport(session);
            session.BeforeClosedCheck = () => { session.BeforeClosedCheck = null; transport.DisposeAsync().GetAwaiter().GetResult(); };

            SendResult result = await transport.SendAsync(ReliableOrdered(0), new byte[] { 1 });

            Assert.That(result.Status, Is.EqualTo(SendStatus.Closed));
            session.RunPostedActions();
            Assert.That(session.Sent, Is.Empty);
            Assert.That(transport.State, Is.EqualTo(TransportState.Closed));
        }

        [Test]
        public async Task PostFailureAfterDispose_DoesNotResurrectFaultedTransport()
        {
            var session = new FakeFantasyClientSession { ThrowWhenPosting = true };
            var transport = new FantasyKcpRealtimeTransport(session);
            session.BeforePost = () => transport.DisposeAsync().GetAwaiter().GetResult();

            await transport.SendAsync(ReliableOrdered(0), new byte[] { 1 });

            Assert.That(transport.State, Is.EqualTo(TransportState.Closed));
            Assert.That(session.DisposeCount, Is.EqualTo(1));
        }

        [Test]
        public async Task SendFailureAfterDispose_DoesNotResurrectFaultedTransport()
        {
            var session = new FakeFantasyClientSession { ThrowWhenSending = true };
            var transport = new FantasyKcpRealtimeTransport(session);
            session.BeforeSend = () => transport.DisposeAsync().GetAwaiter().GetResult();

            await transport.SendAsync(ReliableOrdered(0), new byte[] { 1 });

            Assert.That(transport.State, Is.EqualTo(TransportState.Closed));
            Assert.That(session.DisposeCount, Is.EqualTo(1));
            Assert.That(session.Sent, Is.Empty);
        }

        [Test]
        public void InboundCallbackWaitingDuringDispose_CannotRetainPacketAfterDisposal()
        {
            var session = new FakeFantasyClientSession();
            var transport = new FantasyKcpRealtimeTransport(session);
            object sequenceGate = typeof(FantasyKcpRealtimeTransport).GetField("_sequenceGate", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(transport);
            bool received = false;
            var callback = new Thread(() => received = transport.TryEnqueueReceived(0, new byte[] { 1 }, 1));
            Monitor.Enter(sequenceGate);
            try
            {
                callback.Start();
                Assert.That(SpinWait.SpinUntil(() => (callback.ThreadState & ThreadState.WaitSleepJoin) != 0, 2000), Is.True, "The callback must pass its initial state check and wait for sequence admission.");
                transport.DisposeAsync().GetAwaiter().GetResult();
            }
            finally { Monitor.Exit(sequenceGate); }
            Assert.That(callback.Join(2000), Is.True);

            Assert.That(received, Is.False);
            Assert.That(transport.TryReceive(new byte[1], out _), Is.False);
            Assert.That(transport.State, Is.EqualTo(TransportState.Closed));
        }

        [Test]
        public async Task WarmedSendQueuePath_HasZeroManagedAllocation()
        {
            var session = new FakeFantasyClientSession { RunPostsImmediately = false };
            var transport = new FantasyKcpRealtimeTransport(session, 8, 1024);
            var payload = new byte[32];
            await transport.SendAsync(ReliableOrdered(0), payload);
            session.RunPostedActions();

            long before = GC.GetAllocatedBytesForCurrentThread();
            ValueTask<SendResult> pending = transport.SendAsync(ReliableOrdered(0), payload);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.That(pending.Result.Status, Is.EqualTo(SendStatus.Accepted));
            Assert.That(allocated, Is.Zero);
            session.RunPostedActions();
            await transport.DisposeAsync();
        }

        private static TransportChannel ReliableOrdered(byte id) =>
            new TransportChannel(id, TransportDelivery.Reliable, TransportOrdering.Ordered);

        private static TransportChannel UnreliableSequenced(byte id) =>
            new TransportChannel(id, TransportDelivery.Unreliable, TransportOrdering.Sequenced);
    }

    internal sealed class FakeFantasyClientSession : IFantasyClientSession
    {
        private static long _nextRuntimeId;
        private readonly Action[] _posted = new Action[2048];
        private int _postedCount;
        private bool _isClosed;

        internal FakeFantasyClientSession(long runtimeId = 0)
        {
            RuntimeId = runtimeId == 0 ? System.Threading.Interlocked.Increment(ref _nextRuntimeId) : runtimeId;
        }

        public long RuntimeId { get; private set; }
        internal void ResetRuntimeId() => RuntimeId = 0;

        public bool IsClosed { get { bool closed = _isClosed; BeforeClosedCheck?.Invoke(); return closed; } }

        internal bool RunPostsImmediately { get; set; } = true;

        internal bool ThrowWhenPosting { get; set; }
        internal bool ThrowWhenSending { get; set; }
        internal Action BeforePost { get; set; }
        internal Action BeforeSend { get; set; }
        internal Action BeforeClosedCheck { get; set; }

        internal int DisposeCount;

        internal List<FantasyRealtimeEnvelope> Sent { get; } =
            new List<FantasyRealtimeEnvelope>();

        public void Post(Action action)
        {
            BeforePost?.Invoke();
            if (ThrowWhenPosting)
            {
                throw new InvalidOperationException("Injected post failure.");
            }

            if (RunPostsImmediately)
            {
                action();
                return;
            }

            _posted[_postedCount++] = action;
        }

        public void Send(FantasyRealtimeEnvelope envelope)
        {
            BeforeSend?.Invoke();
            if (ThrowWhenSending) throw new InvalidOperationException("Injected send failure.");
            Sent.Add(envelope);
        }

        public void Dispose() { Interlocked.Increment(ref DisposeCount); _isClosed = true; }

        internal void RunPostedActions()
        {
            int count = _postedCount;
            _postedCount = 0;
            for (int i = 0; i < count; i++)
            {
                Action action = _posted[i];
                _posted[i] = null;
                action();
            }
        }
    }
}
