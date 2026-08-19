using System.Buffers;
using System.Collections.Concurrent;
using AiNative.Realtime;
using global::Fantasy.Network;
using global::Fantasy.Serialize;

namespace AiNative.Server.Fantasy;

internal interface IFantasySessionSender : IDisposable
{
    bool IsClosed { get; }

    void Send(TransportChannel channel, ReadOnlySpan<byte> payload);
}

internal sealed class FantasySessionSender(Session session) : IFantasySessionSender
{
    public bool IsClosed => session.IsDisposed;

    public void Send(TransportChannel channel, ReadOnlySpan<byte> payload)
    {
        MemoryStreamBuffer buffer = new(MemoryStreamBufferSource.Pack, payload.Length);
        buffer.Write(payload);
        session.Send(buffer, typeof(byte[]), channel.Id);
    }

    public void Dispose() => session.Dispose();
}

internal sealed class FantasyRealtimeTransport : IRealtimeTransport
{
    private const int MaxDatagramBytes = 1200;
    private readonly IFantasySessionSender _sender;
    private readonly ConcurrentQueue<InboundPacket> _inbound = new();
    private readonly int _maxInboundBytes;
    private int _inboundBytes;
    private int _state = (int)TransportState.Connected;

    public FantasyRealtimeTransport(IFantasySessionSender sender, int maxInboundBytes = 256 * 1024)
    {
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxInboundBytes);
        _sender = sender;
        _maxInboundBytes = maxInboundBytes;
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

        if (State != TransportState.Connected || _sender.IsClosed)
        {
            return new ValueTask<SendResult>(new SendResult(SendStatus.Closed));
        }

        if (payload.Length > MaxDatagramBytes)
        {
            return new ValueTask<SendResult>(new SendResult(SendStatus.PayloadTooLarge));
        }

        try
        {
            _sender.Send(channel, payload.Span);
            return new ValueTask<SendResult>(new SendResult(SendStatus.Accepted, payload.Length));
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

        int total = Interlocked.Add(ref _inboundBytes, payload.Length);
        if (total > _maxInboundBytes)
        {
            Interlocked.Add(ref _inboundBytes, -payload.Length);
            return false;
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(payload.Length);
        payload.CopyTo(buffer);
        _inbound.Enqueue(new InboundPacket(buffer, payload.Length, channel, sequence, connectionEpoch));
        return true;
    }

    public ValueTask DisposeAsync()
    {
        if ((TransportState)Interlocked.Exchange(ref _state, (int)TransportState.Draining) == TransportState.Closed)
        {
            return ValueTask.CompletedTask;
        }

        while (_inbound.TryDequeue(out InboundPacket inbound))
        {
            ArrayPool<byte>.Shared.Return(inbound.Buffer);
        }

        Volatile.Write(ref _inboundBytes, 0);
        _sender.Dispose();
        Volatile.Write(ref _state, (int)TransportState.Closed);
        return ValueTask.CompletedTask;
    }

    private readonly record struct InboundPacket(
        byte[] Buffer,
        int Length,
        TransportChannel Channel,
        ulong Sequence,
        uint ConnectionEpoch);
}
