using System;

namespace AiNative.Realtime
{
    public enum TransportState : byte
    {
        Closed = 0,
        Connecting = 1,
        Connected = 2,
        Draining = 3,
        Faulted = 4,
    }

    public enum TransportDelivery : byte
    {
        Unreliable = 0,
        Reliable = 1,
    }

    public enum TransportOrdering : byte
    {
        Unordered = 0,
        Sequenced = 1,
        Ordered = 2,
    }

    public readonly struct TransportChannel : IEquatable<TransportChannel>
    {
        public TransportChannel(byte id, TransportDelivery delivery, TransportOrdering ordering)
        {
            Id = id;
            Delivery = delivery;
            Ordering = ordering;
        }

        public byte Id { get; }

        public TransportDelivery Delivery { get; }

        public TransportOrdering Ordering { get; }

        public bool Equals(TransportChannel other) =>
            Id == other.Id && Delivery == other.Delivery && Ordering == other.Ordering;

        public override bool Equals(object obj) => obj is TransportChannel other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Id, (byte)Delivery, (byte)Ordering);
    }

    public enum SendStatus : byte
    {
        Accepted = 0,
        WouldBlock = 1,
        DroppedByPolicy = 2,
        Closed = 3,
        PayloadTooLarge = 4,
        Faulted = 5,
    }

    public readonly struct SendResult
    {
        public SendResult(SendStatus status, int acceptedBytes = 0)
        {
            if (acceptedBytes < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(acceptedBytes));
            }

            Status = status;
            AcceptedBytes = acceptedBytes;
        }

        public SendStatus Status { get; }

        public int AcceptedBytes { get; }
    }

    public readonly struct ReceivedPacket
    {
        public ReceivedPacket(
            TransportChannel channel,
            int writtenBytes,
            int requiredBytes,
            ulong sequence,
            uint connectionEpoch)
        {
            if (writtenBytes < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(writtenBytes));
            }

            if (requiredBytes < writtenBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(requiredBytes));
            }

            Channel = channel;
            WrittenBytes = writtenBytes;
            RequiredBytes = requiredBytes;
            Sequence = sequence;
            ConnectionEpoch = connectionEpoch;
        }

        public TransportChannel Channel { get; }

        public int WrittenBytes { get; }

        public int RequiredBytes { get; }

        public ulong Sequence { get; }

        public uint ConnectionEpoch { get; }

        public bool IsComplete => WrittenBytes == RequiredBytes;
    }
}
