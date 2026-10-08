using AiNative.Protocol.V1;
using AiNative.Realtime;
using AiNative.Server.Protocol;

namespace AiNative.BattleHost;

// Host-owned, Tick-local cache. The legacy room body is identical for every recipient.
internal sealed class PreparedLegacySnapshot
{
    private readonly byte[] _frame = new byte[RealtimeProtocolCodec.MaxDatagramBytes];
    private int _length;
    public bool IsPrepared => _length != 0;
    public TransportChannel Channel { get; private set; }

    public void Reset() { _length = 0; Channel = default; }

    public bool TryPrepare(Snapshot snapshot)
    {
        Reset();
        // Field 6 must be absent from the common body: each recipient gets exactly one ACK.
        if (snapshot.LastProcessedInputSequence != 0 ||
            !RealtimeProtocolCodec.TryEncode(MessageId.Snapshot, snapshot, _frame, out var channel, out int length))
            return false;
        Channel = channel;
        _length = length;
        return true;
    }

    public bool TryWriteRecipient(uint acknowledgedSequence, Span<byte> destination, out int writtenBytes)
    {
        writtenBytes = 0;
        if (!IsPrepared) return false;
        int acknowledgementBytes = 0;
        if (acknowledgedSequence != 0)
        {
            acknowledgementBytes = 2; // field-6 key plus at least one uint32 varint byte.
            for (uint remaining = acknowledgedSequence >> 7; remaining != 0; remaining >>= 7)
                acknowledgementBytes++;
        }
        int length = _length + acknowledgementBytes;
        if (length > RealtimeProtocolCodec.MaxDatagramBytes || length > destination.Length) return false;
        _frame.AsSpan(0, _length).CopyTo(destination);
        int offset = _length;
        if (acknowledgedSequence != 0)
        {
            destination[offset++] = 0x30; // Snapshot.last_processed_input_sequence, field 6 / wire 0.
            uint remaining = acknowledgedSequence;
            while (remaining >= 0x80)
            {
                destination[offset++] = (byte)((remaining & 0x7f) | 0x80);
                remaining >>= 7;
            }
            destination[offset] = (byte)remaining;
        }
        writtenBytes = length;
        return true;
    }
}
