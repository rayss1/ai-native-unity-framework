using System.Buffers;
using System.Collections.Concurrent;
using AiNative.Realtime;
using global::Fantasy.Network;

namespace AiNative.Server.Fantasy;

internal interface IFantasySessionSender : IDisposable
{
    bool IsClosed { get; }

    SendStatus Send(TransportChannel channel, ReadOnlySpan<byte> payload);
}

internal interface IFantasyOutboundDispatcher
{
    bool IsClosed { get; }

    void Post(Action action);

    void Send(FantasyRealtimeEnvelope envelope);

    void DisposeSession();
}

internal sealed class FantasyOutboundDispatcher(Session session) : IFantasyOutboundDispatcher
{
    public bool IsClosed => session.IsDisposed;

    public void Post(Action action) => session.Scene.ThreadSynchronizationContext.Post(action);

    public void Send(FantasyRealtimeEnvelope envelope) => session.Send(envelope);

    public void DisposeSession() => session.Dispose();
}

internal sealed class FantasySessionSender : IFantasySessionSender
{
    private readonly IFantasyOutboundDispatcher _dispatcher;
    private readonly ConcurrentQueue<FantasyRealtimeEnvelope> _outbound = new();
    private readonly object _snapshotGate = new();
    private readonly int _maxOutboundBytes;
    private readonly int _maxOutboundPackets;
    private int _disposed;
    private int _drainScheduled;
    private int _outboundBytes;
    private int _outboundPackets;
    private long _sequence;
    private long _snapshotReplacements;
    private FantasyRealtimeEnvelope? _latestSnapshot;

    public FantasySessionSender(
        Session session,
        int maxOutboundBytes = 256 * 1024,
        int maxOutboundPackets = 1024)
        : this(new FantasyOutboundDispatcher(session), maxOutboundBytes, maxOutboundPackets)
    {
    }

    internal FantasySessionSender(
        IFantasyOutboundDispatcher dispatcher,
        int maxOutboundBytes = 256 * 1024,
        int maxOutboundPackets = 1024)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxOutboundBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxOutboundPackets);
        _dispatcher = dispatcher;
        _maxOutboundBytes = maxOutboundBytes;
        _maxOutboundPackets = maxOutboundPackets;
    }

    public bool IsClosed => Volatile.Read(ref _disposed) != 0 || _dispatcher.IsClosed;

    internal int PendingOutboundBytes => Volatile.Read(ref _outboundBytes);

    internal int PendingOutboundPackets => Volatile.Read(ref _outboundPackets);

    internal long SnapshotReplacementCount => Interlocked.Read(ref _snapshotReplacements);

    public SendStatus Send(TransportChannel channel, ReadOnlySpan<byte> payload)
    {
        if (IsClosed)
        {
            return SendStatus.Closed;
        }

        if (channel.Id == 1)
        {
            return SendReplaceableSnapshot(channel, payload);
        }

        int packetCount = Interlocked.Increment(ref _outboundPackets);
        if (packetCount > _maxOutboundPackets)
        {
            Interlocked.Decrement(ref _outboundPackets);
            return SendStatus.WouldBlock;
        }

        int totalBytes = Interlocked.Add(ref _outboundBytes, payload.Length);
        if (totalBytes > _maxOutboundBytes)
        {
            Interlocked.Add(ref _outboundBytes, -payload.Length);
            Interlocked.Decrement(ref _outboundPackets);
            return SendStatus.WouldBlock;
        }

        try
        {
            FantasyRealtimeEnvelope envelope = new()
            {
                ChannelId = channel.Id,
                Payload = payload.ToArray(),
                Sequence = unchecked((ulong)Interlocked.Increment(ref _sequence)),
            };
            _outbound.Enqueue(envelope);
        }
        catch
        {
            Interlocked.Add(ref _outboundBytes, -payload.Length);
            Interlocked.Decrement(ref _outboundPackets);
            throw;
        }

        if (IsClosed)
        {
            DrainDisposedOutbound();
            return SendStatus.Closed;
        }

        ScheduleDrain();
        return SendStatus.Accepted;
    }

    private SendStatus SendReplaceableSnapshot(TransportChannel channel, ReadOnlySpan<byte> payload)
    {
        FantasyRealtimeEnvelope replacement = new()
        {
            ChannelId = channel.Id,
            Payload = payload.ToArray(),
            Sequence = unchecked((ulong)Interlocked.Increment(ref _sequence)),
        };

        lock (_snapshotGate)
        {
            if (IsClosed)
            {
                replacement.Dispose();
                return SendStatus.Closed;
            }

            FantasyRealtimeEnvelope? previous = _latestSnapshot;
            int previousBytes = previous?.Payload.Length ?? 0;
            int byteDelta = payload.Length - previousBytes;
            int packetDelta = previous is null ? 1 : 0;
            int packets = Interlocked.Add(ref _outboundPackets, packetDelta);
            int bytes = Interlocked.Add(ref _outboundBytes, byteDelta);
            if (packets > _maxOutboundPackets || bytes > _maxOutboundBytes)
            {
                Interlocked.Add(ref _outboundPackets, -packetDelta);
                Interlocked.Add(ref _outboundBytes, -byteDelta);
                replacement.Dispose();
                return SendStatus.WouldBlock;
            }

            _latestSnapshot = replacement;
            if (previous is not null)
            {
                Interlocked.Increment(ref _snapshotReplacements);
                previous.Dispose();
            }
        }

        ScheduleDrain();
        return SendStatus.Accepted;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        DrainDisposedOutbound();

        if (!_dispatcher.IsClosed)
        {
            _dispatcher.Post(_dispatcher.DisposeSession);
        }
    }

    private void ScheduleDrain()
    {
        if (Interlocked.CompareExchange(ref _drainScheduled, 1, 0) == 0)
        {
            _dispatcher.Post(DrainOnSceneThread);
        }
    }

    private void DrainOnSceneThread()
    {
        while (_outbound.TryDequeue(out FantasyRealtimeEnvelope? envelope))
        {
            Interlocked.Add(ref _outboundBytes, -envelope.Payload.Length);
            Interlocked.Decrement(ref _outboundPackets);
            if (IsClosed)
            {
                envelope.Dispose();
                continue;
            }

            _dispatcher.Send(envelope);
        }

        FantasyRealtimeEnvelope? snapshot;
        lock (_snapshotGate)
        {
            snapshot = _latestSnapshot;
            _latestSnapshot = null;
            if (snapshot is not null)
            {
                Interlocked.Add(ref _outboundBytes, -snapshot.Payload.Length);
                Interlocked.Decrement(ref _outboundPackets);
            }
        }

        if (snapshot is not null)
        {
            if (IsClosed)
            {
                snapshot.Dispose();
            }
            else
            {
                _dispatcher.Send(snapshot);
            }
        }

        Volatile.Write(ref _drainScheduled, 0);
        if (!_outbound.IsEmpty || HasPendingSnapshot())
        {
            ScheduleDrain();
        }
    }

    private void DrainDisposedOutbound()
    {
        while (_outbound.TryDequeue(out FantasyRealtimeEnvelope? envelope))
        {
            Interlocked.Add(ref _outboundBytes, -envelope.Payload.Length);
            Interlocked.Decrement(ref _outboundPackets);
            envelope.Dispose();
        }

        lock (_snapshotGate)
        {
            if (_latestSnapshot is { } snapshot)
            {
                _latestSnapshot = null;
                Interlocked.Add(ref _outboundBytes, -snapshot.Payload.Length);
                Interlocked.Decrement(ref _outboundPackets);
                snapshot.Dispose();
            }
        }
    }

    private bool HasPendingSnapshot()
    {
        lock (_snapshotGate)
        {
            return _latestSnapshot is not null;
        }
    }
}

internal sealed class FantasyRealtimeTransport : IRealtimeTransport
{
    private const int MaxDatagramBytes = 1200;
    private readonly IFantasySessionSender _sender;
    private readonly ConcurrentQueue<InboundPacket> _inbound = new();
    private readonly int _maxInboundBytes;
    private readonly int _maxInboundPackets;
    private int _inboundBytes;
    private int _inboundPackets;
    private int _state = (int)TransportState.Connected;

    public FantasyRealtimeTransport(
        IFantasySessionSender sender,
        int maxInboundBytes = 256 * 1024,
        int maxInboundPackets = 1024)
    {
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxInboundBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxInboundPackets);
        _sender = sender;
        _maxInboundBytes = maxInboundBytes;
        _maxInboundPackets = maxInboundPackets;
    }

    public TransportState State => (TransportState)Volatile.Read(ref _state);

    public ValueTask<SendResult> SendAsync(
        TransportChannel channel,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return new ValueTask<SendResult>(new SendResult(SendStatus.DroppedByPolicy));
        }

        if (State != TransportState.Connected)
        {
            return new ValueTask<SendResult>(new SendResult(SendStatus.Closed));
        }

        if (_sender.IsClosed)
        {
            Volatile.Write(ref _state, (int)TransportState.Closed);
            return new ValueTask<SendResult>(new SendResult(SendStatus.Closed));
        }

        if (payload.Length > MaxDatagramBytes)
        {
            return new ValueTask<SendResult>(new SendResult(SendStatus.PayloadTooLarge));
        }

        try
        {
            SendStatus status = _sender.Send(channel, payload.Span);
            return new ValueTask<SendResult>(new SendResult(
                status,
                status == SendStatus.Accepted ? payload.Length : 0));
        }
        catch (ObjectDisposedException)
        {
            Volatile.Write(ref _state, (int)TransportState.Closed);
            return new ValueTask<SendResult>(new SendResult(SendStatus.Closed));
        }
        catch
        {
            Volatile.Write(ref _state, (int)TransportState.Faulted);
            return new ValueTask<SendResult>(new SendResult(SendStatus.Faulted));
        }
    }

    public bool TryReceive(Span<byte> destination, out ReceivedPacket packet)
    {
        if (!_inbound.TryDequeue(out InboundPacket inbound))
        {
            packet = default;
            return false;
        }

        Interlocked.Add(ref _inboundBytes, -inbound.Length);
        Interlocked.Decrement(ref _inboundPackets);
        int written = Math.Min(destination.Length, inbound.Length);
        inbound.Buffer.AsSpan(0, written).CopyTo(destination);
        packet = new ReceivedPacket(
            inbound.Channel,
            written,
            inbound.Length,
            inbound.Sequence,
            inbound.ConnectionEpoch);
        ArrayPool<byte>.Shared.Return(inbound.Buffer);
        return true;
    }

    internal bool TryEnqueueReceived(
        TransportChannel channel,
        ReadOnlySpan<byte> payload,
        ulong sequence,
        uint connectionEpoch)
    {
        if (payload.Length > MaxDatagramBytes || State != TransportState.Connected)
        {
            return false;
        }

        int packetCount = Interlocked.Increment(ref _inboundPackets);
        if (packetCount > _maxInboundPackets)
        {
            Interlocked.Decrement(ref _inboundPackets);
            return false;
        }

        int totalBytes = Interlocked.Add(ref _inboundBytes, payload.Length);
        if (totalBytes > _maxInboundBytes)
        {
            Interlocked.Add(ref _inboundBytes, -payload.Length);
            Interlocked.Decrement(ref _inboundPackets);
            return false;
        }

        byte[]? buffer = null;
        try
        {
            buffer = ArrayPool<byte>.Shared.Rent(payload.Length);
            payload.CopyTo(buffer);
            _inbound.Enqueue(new InboundPacket(buffer, payload.Length, channel, sequence, connectionEpoch));
            buffer = null;
            if (State != TransportState.Connected)
            {
                DrainInbound();
                return false;
            }

            return true;
        }
        catch
        {
            if (buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            Interlocked.Add(ref _inboundBytes, -payload.Length);
            Interlocked.Decrement(ref _inboundPackets);
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        if ((TransportState)Interlocked.Exchange(ref _state, (int)TransportState.Draining) == TransportState.Closed)
        {
            return ValueTask.CompletedTask;
        }

        DrainInbound();
        _sender.Dispose();
        Volatile.Write(ref _state, (int)TransportState.Closed);
        return ValueTask.CompletedTask;
    }

    private void DrainInbound()
    {
        while (_inbound.TryDequeue(out InboundPacket inbound))
        {
            Interlocked.Add(ref _inboundBytes, -inbound.Length);
            Interlocked.Decrement(ref _inboundPackets);
            ArrayPool<byte>.Shared.Return(inbound.Buffer);
        }
    }

    private readonly record struct InboundPacket(
        byte[] Buffer,
        int Length,
        TransportChannel Channel,
        ulong Sequence,
        uint ConnectionEpoch);
}
