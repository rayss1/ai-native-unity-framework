using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AiNative.Realtime;

namespace AiNative.Client.Fantasy
{
    public sealed class FantasyKcpRealtimeTransport : IRealtimeTransport
    {
        public const int OuterKcpMtu = 1150;
        public const int MaximumFrameBytes = 1200;
        public const int DefaultMaximumQueuedPackets = 1024;
        public const int DefaultMaximumQueuedBytes = 256 * 1024;

        private readonly IFantasyClientSession _session;
        private readonly BoundedPacketQueue _inbound;
        private readonly BoundedPacketQueue _outbound;
        private readonly Action _drainOutboundAction;
        private readonly object _sequenceGate = new object();
        private readonly ulong[] _lastInboundSequences = new ulong[4];
        private int _connectionEpoch;
        private int _drainScheduled;
        private int _state;
        private long _connectionFaults;
        private long _inboundDropped;
        private long _invalidChannels;
        private long _oversizedFrames;
        private long _receivesAccepted;
        private long _sendBackpressure;
        private long _sendsAccepted;
        private long _staleSequences;
        private long _outboundSequence;

        internal FantasyKcpRealtimeTransport(
            IFantasyClientSession session,
            int maximumQueuedPackets = DefaultMaximumQueuedPackets,
            int maximumQueuedBytes = DefaultMaximumQueuedBytes)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _inbound = new BoundedPacketQueue(maximumQueuedPackets, maximumQueuedBytes);
            _outbound = new BoundedPacketQueue(maximumQueuedPackets, maximumQueuedBytes);
            _drainOutboundAction = DrainOutboundOnFantasyThread;
            _state = (int)TransportState.Connected;

            if (!FantasyClientSessionRouter.Register(session.RuntimeId, this))
            {
                _state = (int)TransportState.Faulted;
                throw new InvalidOperationException(
                    $"A Fantasy client transport is already registered for session {session.RuntimeId}.");
            }
        }

        public TransportState State => (TransportState)Volatile.Read(ref _state);

        public uint ConnectionEpoch => unchecked((uint)Volatile.Read(ref _connectionEpoch));

        public FantasyKcpTransportDiagnostics Diagnostics =>
            new FantasyKcpTransportDiagnostics(
                Interlocked.Read(ref _sendsAccepted),
                Interlocked.Read(ref _sendBackpressure),
                Interlocked.Read(ref _receivesAccepted),
                Interlocked.Read(ref _oversizedFrames),
                Interlocked.Read(ref _invalidChannels),
                Interlocked.Read(ref _staleSequences),
                Interlocked.Read(ref _inboundDropped),
                Interlocked.Read(ref _connectionFaults));

        public static ValueTask<FantasyKcpConnectResult> ConnectAsync(
            FantasyKcpTransportOptions options,
            CancellationToken cancellationToken = default)
        {
            return new ValueTask<FantasyKcpConnectResult>(
                ConnectCoreAsync(options, FantasyClientConnector.Instance, cancellationToken));
        }

        public bool TryAdvanceConnectionEpoch(uint connectionEpoch)
        {
            if (connectionEpoch == 0)
            {
                return false;
            }

            while (true)
            {
                int currentBits = Volatile.Read(ref _connectionEpoch);
                uint current = unchecked((uint)currentBits);
                if (connectionEpoch < current)
                {
                    return false;
                }

                if (connectionEpoch == current)
                {
                    return true;
                }

                if (Interlocked.CompareExchange(
                        ref _connectionEpoch,
                        unchecked((int)connectionEpoch),
                        currentBits) == currentBits)
                {
                    lock (_sequenceGate)
                    {
                        Array.Clear(_lastInboundSequences, 0, _lastInboundSequences.Length);
                    }

                    return true;
                }
            }
        }

        public ValueTask<SendResult> SendAsync(
            TransportChannel channel,
            ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new ValueTask<SendResult>(Task.FromCanceled<SendResult>(cancellationToken));
            }

            if (State == TransportState.Faulted)
            {
                return Completed(new SendResult(SendStatus.Faulted));
            }

            if (State != TransportState.Connected)
            {
                return Completed(new SendResult(SendStatus.Closed));
            }

            if (_session.IsClosed)
            {
                TransitionClosed();
                return Completed(new SendResult(SendStatus.Closed));
            }

            if (!IsSupportedChannel(channel))
            {
                Interlocked.Increment(ref _invalidChannels);
                return Completed(new SendResult(SendStatus.DroppedByPolicy));
            }

            if (payload.Length > MaximumFrameBytes)
            {
                Interlocked.Increment(ref _oversizedFrames);
                return Completed(new SendResult(SendStatus.PayloadTooLarge));
            }

            ulong sequence = unchecked((ulong)Interlocked.Increment(ref _outboundSequence));
            if (!_outbound.TryEnqueue(channel.Id, payload.Span, sequence))
            {
                Interlocked.Increment(ref _sendBackpressure);
                return Completed(new SendResult(SendStatus.WouldBlock));
            }

            Interlocked.Increment(ref _sendsAccepted);
            ScheduleOutboundDrain();
            return Completed(new SendResult(SendStatus.Accepted, payload.Length));
        }

        public bool TryReceive(Span<byte> destination, out ReceivedPacket packet)
        {
            if (!_inbound.TryDequeue(out BoundedPacketQueue.Packet inbound))
            {
                packet = default;
                return false;
            }

            int written = Math.Min(destination.Length, inbound.Length);
            inbound.Buffer.AsSpan(0, written).CopyTo(destination);
            packet = new ReceivedPacket(
                MapChannel(inbound.ChannelId),
                written,
                inbound.Length,
                inbound.Sequence,
                unchecked((uint)Volatile.Read(ref _connectionEpoch)));
            inbound.Return();
            return true;
        }

        public ValueTask DisposeAsync()
        {
            TransportState previous = (TransportState)Interlocked.Exchange(
                ref _state,
                (int)TransportState.Draining);
            if (previous == TransportState.Closed)
            {
                return default;
            }

            FantasyClientSessionRouter.Remove(_session.RuntimeId, this);
            _inbound.Drain();
            _outbound.Drain();
            _session.Dispose();
            Volatile.Write(ref _state, (int)TransportState.Closed);
            return default;
        }

        internal static Task<FantasyKcpConnectResult> ConnectCoreAsync(
            FantasyKcpTransportOptions options,
            IFantasyClientConnector connector,
            CancellationToken cancellationToken)
        {
            if (options == null || !options.IsValid || connector == null)
            {
                return Task.FromResult(new FantasyKcpConnectResult(
                    FantasyKcpConnectStatus.InvalidConfiguration,
                    null,
                    new ArgumentException("Host, port, and timeout must be valid.")));
            }

            return ConnectValidatedAsync(options, connector, cancellationToken);
        }

        internal bool TryEnqueueReceived(uint channelId, ReadOnlySpan<byte> payload, ulong sequence)
        {
            if (State != TransportState.Connected)
            {
                Interlocked.Increment(ref _inboundDropped);
                return false;
            }

            if (channelId > 3)
            {
                Interlocked.Increment(ref _invalidChannels);
                Interlocked.Increment(ref _inboundDropped);
                return false;
            }

            if (payload.Length > MaximumFrameBytes)
            {
                Interlocked.Increment(ref _oversizedFrames);
                Interlocked.Increment(ref _inboundDropped);
                return false;
            }

            lock (_sequenceGate)
            {
                if (sequence <= _lastInboundSequences[channelId])
                {
                    Interlocked.Increment(ref _staleSequences);
                    Interlocked.Increment(ref _inboundDropped);
                    return false;
                }

                if (!_inbound.TryEnqueue((byte)channelId, payload, sequence))
                {
                    Interlocked.Increment(ref _inboundDropped);
                    return false;
                }

                _lastInboundSequences[channelId] = sequence;
            }

            Interlocked.Increment(ref _receivesAccepted);
            return true;
        }

        internal void NotifyDisconnected()
        {
            if (State == TransportState.Connected || State == TransportState.Connecting)
            {
                Interlocked.Increment(ref _connectionFaults);
                TransitionClosed();
            }
        }

        private static async Task<FantasyKcpConnectResult> ConnectValidatedAsync(
            FantasyKcpTransportOptions options,
            IFantasyClientConnector connector,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new FantasyKcpConnectResult(FantasyKcpConnectStatus.Cancelled, null, null);
            }

            var stopwatch = Stopwatch.StartNew();
            using (var timeout = new CancellationTokenSource(options.ConnectTimeoutMilliseconds))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeout.Token))
            {
                FantasyKcpRealtimeTransport transport = null;
                IFantasyClientSession session = null;
                int disconnected = 0;
                try
                {
                    session = await connector.ConnectAsync(
                        options,
                        () =>
                        {
                            Interlocked.Exchange(ref disconnected, 1);
                            transport?.NotifyDisconnected();
                        },
                        linked.Token).ConfigureAwait(false);

                    transport = new FantasyKcpRealtimeTransport(session);
                    if (Volatile.Read(ref disconnected) != 0)
                    {
                        transport.NotifyDisconnected();
                    }

                    return new FantasyKcpConnectResult(
                        FantasyKcpConnectStatus.Connected,
                        transport,
                        null);
                }
                catch (OperationCanceledException exception)
                {
                    session?.Dispose();
                    FantasyKcpConnectStatus status = cancellationToken.IsCancellationRequested
                        ? FantasyKcpConnectStatus.Cancelled
                        : FantasyKcpConnectStatus.TimedOut;
                    return new FantasyKcpConnectResult(status, null, exception);
                }
                catch (Exception exception)
                {
                    session?.Dispose();
                    FantasyKcpConnectStatus status =
                        stopwatch.ElapsedMilliseconds >= options.ConnectTimeoutMilliseconds
                            ? FantasyKcpConnectStatus.TimedOut
                            : FantasyKcpConnectStatus.Faulted;
                    return new FantasyKcpConnectResult(status, null, exception);
                }
            }
        }

        private static ValueTask<SendResult> Completed(SendResult result) =>
            new ValueTask<SendResult>(result);

        private void ScheduleOutboundDrain()
        {
            if (Interlocked.CompareExchange(ref _drainScheduled, 1, 0) != 0)
            {
                return;
            }

            try
            {
                _session.Post(_drainOutboundAction);
            }
            catch
            {
                Interlocked.Increment(ref _connectionFaults);
                Volatile.Write(ref _state, (int)TransportState.Faulted);
                Volatile.Write(ref _drainScheduled, 0);
                _outbound.Drain();
            }
        }

        private void DrainOutboundOnFantasyThread()
        {
            try
            {
                while (_outbound.TryDequeue(out BoundedPacketQueue.Packet packet))
                {
                    try
                    {
                        if (State != TransportState.Connected || _session.IsClosed)
                        {
                            TransitionClosed();
                            continue;
                        }

                        var payload = new byte[packet.Length];
                        packet.Buffer.AsSpan(0, packet.Length).CopyTo(payload);
                        _session.Send(new FantasyRealtimeEnvelope
                        {
                            ChannelId = packet.ChannelId,
                            Payload = payload,
                            Sequence = packet.Sequence,
                        });
                    }
                    finally
                    {
                        packet.Return();
                    }
                }
            }
            catch
            {
                Interlocked.Increment(ref _connectionFaults);
                Volatile.Write(ref _state, (int)TransportState.Faulted);
                _outbound.Drain();
            }
            finally
            {
                Volatile.Write(ref _drainScheduled, 0);
                if (_outbound.Count != 0 && State == TransportState.Connected)
                {
                    ScheduleOutboundDrain();
                }
            }
        }

        private void TransitionClosed()
        {
            TransportState current = State;
            if (current == TransportState.Closed || current == TransportState.Draining)
            {
                return;
            }

            Volatile.Write(ref _state, (int)TransportState.Closed);
            FantasyClientSessionRouter.Remove(_session.RuntimeId, this);
            _inbound.Drain();
            _outbound.Drain();
        }

        private static bool IsSupportedChannel(TransportChannel channel)
        {
            return channel.Equals(MapChannel(channel.Id));
        }

        private static TransportChannel MapChannel(byte channelId)
        {
            switch (channelId)
            {
                case 0:
                    return new TransportChannel(
                        0,
                        TransportDelivery.Reliable,
                        TransportOrdering.Ordered);
                case 1:
                case 2:
                    return new TransportChannel(
                        channelId,
                        TransportDelivery.Unreliable,
                        TransportOrdering.Sequenced);
                case 3:
                    return new TransportChannel(
                        3,
                        TransportDelivery.Reliable,
                        TransportOrdering.Ordered);
                default:
                    return default;
            }
        }
    }
}
