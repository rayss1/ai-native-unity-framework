using System;
using System.Threading;
using System.Threading.Tasks;
using AiNative.Realtime;

namespace AiNative.Client.Prediction.Tests
{
    internal sealed class FakeRealtimeTransport : IRealtimeTransport
    {
        private readonly byte[] _lastPayload = new byte[1200];

        public TransportState State { get; set; } = TransportState.Connected;

        public SendStatus NextSendStatus { get; set; } = SendStatus.Accepted;

        public TransportChannel LastChannel { get; private set; }

        public int LastPayloadLength { get; private set; }

        public int SendCount { get; private set; }

        public ReadOnlySpan<byte> LastPayload => _lastPayload.AsSpan(0, LastPayloadLength);

        public ValueTask<SendResult> SendAsync(
            TransportChannel channel,
            ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new ValueTask<SendResult>(Task.FromCanceled<SendResult>(cancellationToken));
            }

            LastChannel = channel;
            LastPayloadLength = payload.Length;
            payload.Span.CopyTo(_lastPayload);
            SendCount++;
            int acceptedBytes = NextSendStatus == SendStatus.Accepted ? payload.Length : 0;
            return new ValueTask<SendResult>(new SendResult(NextSendStatus, acceptedBytes));
        }

        public bool TryReceive(Span<byte> destination, out ReceivedPacket packet)
        {
            packet = default;
            return false;
        }

        public ValueTask DisposeAsync()
        {
            State = TransportState.Closed;
            return default;
        }
    }
}
