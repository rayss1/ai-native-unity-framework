using System;
using System.Buffers;

namespace AiNative.Client.Fantasy
{
    internal sealed class BoundedPacketQueue
    {
        private readonly object _gate = new object();
        private readonly Packet[] _slots;
        private readonly int _maxBytes;
        private int _bytes;
        private int _count;
        private int _head;
        private int _tail;

        internal BoundedPacketQueue(int maxPackets, int maxBytes)
        {
            if (maxPackets <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxPackets));
            }

            if (maxBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxBytes));
            }

            _slots = new Packet[maxPackets];
            _maxBytes = maxBytes;
        }

        internal int Count
        {
            get
            {
                lock (_gate)
                {
                    return _count;
                }
            }
        }

        internal bool TryEnqueue(byte channelId, ReadOnlySpan<byte> payload, ulong sequence)
        {
            lock (_gate)
            {
                if (_count == _slots.Length || payload.Length > _maxBytes - _bytes)
                {
                    return false;
                }

                byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Max(payload.Length, 1));
                payload.CopyTo(buffer);
                _slots[_tail] = new Packet(buffer, payload.Length, channelId, sequence);
                _tail = (_tail + 1) % _slots.Length;
                _count++;
                _bytes += payload.Length;
                return true;
            }
        }

        internal bool TryDequeue(out Packet packet)
        {
            lock (_gate)
            {
                if (_count == 0)
                {
                    packet = default;
                    return false;
                }

                packet = _slots[_head];
                _slots[_head] = default;
                _head = (_head + 1) % _slots.Length;
                _count--;
                _bytes -= packet.Length;
                return true;
            }
        }

        internal void Drain()
        {
            while (TryDequeue(out Packet packet))
            {
                packet.Return();
            }
        }

        internal readonly struct Packet
        {
            internal Packet(byte[] buffer, int length, byte channelId, ulong sequence)
            {
                Buffer = buffer;
                Length = length;
                ChannelId = channelId;
                Sequence = sequence;
            }

            internal byte[] Buffer { get; }

            internal int Length { get; }

            internal byte ChannelId { get; }

            internal ulong Sequence { get; }

            internal void Return()
            {
                if (Buffer != null)
                {
                    ArrayPool<byte>.Shared.Return(Buffer);
                }
            }
        }
    }
}
