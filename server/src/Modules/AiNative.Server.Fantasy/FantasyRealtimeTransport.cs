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

internal sealed class FantasySessionSender : IFantasySessionSender
{
    private readonly Session _session;
    private readonly global::Fantasy.ThreadSynchronizationContext _sceneContext;
    private readonly ConcurrentQueue<FantasyRealtimeEnvelope> _outbound = new();
    private readonly int _maxOutboundBytes;
    private readonly int _maxOutboundPackets;
    private int _disposed;
    private int _drainScheduled;
    private int _outboundBytes;
    private int _outboundPackets;
    private long _sequence;

    public FantasySessionSender(
        Session session,
        int maxOutboundBytes = 256 * 1024,
        int maxOutboundPackets = 1024)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxOutboundBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxOutboundPackets);
        _session = session;
        _sceneContext = session.Scene.ThreadSynchronizationContext;
        _maxOutboundBytes = maxOutboundBytes;
        _maxOutboundPackets = maxOutboundPackets;
    }

    public bool IsClosed => Volatile.Read(ref _disposed) != 0 || _session.IsDisposed;

    public SendStatus Send(TransportChannel channel, ReadOnlySpan<byte> payload)
    {
        if (IsClosed)
        {
            return SendStatus.Closed;
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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        DrainDisposedOutbound();

        if (!_session.IsDisposed)
        {
            _sceneContext.Post(_session.Dispose);
        }
    }

    private void ScheduleDrain()
    {
        if (Interlocked.CompareExchange(ref _drainScheduled, 1, 0) == 0)
        {
            _sceneContext.Post(DrainOnSceneThread);
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

            _session.Send(envelope);
        }

        Volatile.Write(ref _drainScheduled, 0);
        if (!_outbound.IsEmpty)
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
