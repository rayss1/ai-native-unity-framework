using System.Threading.Tasks;
using AiNative.Realtime;
using NUnit.Framework;

namespace AiNative.Client.Fantasy.Tests
{
    public sealed class TerminalReceiveTests
    {
        [Test]
        public async Task RemoteClosePreservesAcceptedInboundAndRejectsNewSendsAndDelivery()
        {
            var session = new FakeFantasyClientSession { RunPostsImmediately = false };
            var transport = new FantasyKcpRealtimeTransport(session, 2, 32);
            try
            {
                transport.TryAdvanceConnectionEpoch(7);
                Assert.That(transport.TryEnqueueReceived(1, new byte[] { 10 }, 1), Is.True);
                Assert.That(transport.TryEnqueueReceived(1, new byte[] { 20 }, 2), Is.True);
                Assert.That(transport.TryEnqueueReceived(1, new byte[] { 30 }, 3), Is.False, "Existing inbound bound still applies.");
                transport.NotifyDisconnected();
                Assert.That(transport.State, Is.EqualTo(TransportState.Closed));
                Assert.That(transport.TryEnqueueReceived(1, new byte[] { 40 }, 4), Is.False);
                Assert.That(FantasyClientSessionRouter.Deliver(session.RuntimeId, 1, new byte[] { 40 }, 4), Is.False);
                Assert.That((await transport.SendAsync(new TransportChannel(2, TransportDelivery.Unreliable, TransportOrdering.Sequenced), new byte[] { 1 })).Status, Is.EqualTo(SendStatus.Closed));
                var destination = new byte[1];
                Assert.That(transport.TryReceive(destination, out var first), Is.True);
                Assert.That(destination[0], Is.EqualTo(10)); Assert.That(first.ConnectionEpoch, Is.EqualTo(7));
                Assert.That(transport.TryReceive(destination, out var second), Is.True);
                Assert.That(destination[0], Is.EqualTo(20)); Assert.That(second.ConnectionEpoch, Is.EqualTo(7));
                Assert.That(transport.TryReceive(destination, out _), Is.False);
            }
            finally { await transport.DisposeAsync(); }
        }

        [Test]
        public async Task ExplicitDisposeClearsInboundRetainedAfterRemoteClose()
        {
            var session = new FakeFantasyClientSession();
            var transport = new FantasyKcpRealtimeTransport(session);
            transport.TryEnqueueReceived(1, new byte[] { 10 }, 1);
            transport.NotifyDisconnected();
            await transport.DisposeAsync();
            Assert.That(transport.TryReceive(new byte[1], out _), Is.False);
            Assert.That(session.DisposeCount, Is.EqualTo(1));
        }

        [Test]
        public async Task ClosedSessionObservedBySendAlsoRetainsPreviouslyAcceptedInbound()
        {
            var session = new FakeFantasyClientSession();
            var transport = new FantasyKcpRealtimeTransport(session);
            try
            {
                transport.TryEnqueueReceived(1, new byte[] { 42 }, 1);
                session.Dispose();
                Assert.That((await transport.SendAsync(new TransportChannel(2, TransportDelivery.Unreliable, TransportOrdering.Sequenced), new byte[] { 1 })).Status, Is.EqualTo(SendStatus.Closed));
                var destination = new byte[1];
                Assert.That(transport.TryReceive(destination, out _), Is.True);
                Assert.That(destination[0], Is.EqualTo(42));
            }
            finally { await transport.DisposeAsync(); }
        }
    }
}
