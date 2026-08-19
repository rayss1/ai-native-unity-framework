using System;
using System.Threading;
using System.Threading.Tasks;

namespace AiNative.Realtime
{
    /// <summary>
    /// A bounded, non-blocking realtime transport boundary. Implementations own all
    /// sockets and framework-specific session objects.
    /// </summary>
    public interface IRealtimeTransport : IAsyncDisposable
    {
        TransportState State { get; }

        ValueTask<SendResult> SendAsync(
            TransportChannel channel,
            ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken = default);

        bool TryReceive(Span<byte> destination, out ReceivedPacket packet);
    }
}
