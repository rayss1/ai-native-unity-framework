using System;
using System.Buffers.Binary;
using System.IO;

namespace AiNative.Client.Fantasy
{
    // Pinned Fantasy outer TCP framing, independent of the body serialization library.
    internal static class GateFrame
    {
        internal static byte[] Encode(uint opcode, uint rpc, byte[] body)
        {
            if (body.Length > GateWire.MaxBody)
                throw new InvalidDataException("gate-frame-limit");
            byte[] frame = new byte[20 + body.Length];
            BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length == 0 ? -1 : body.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), opcode);
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(8), rpc);
            body.CopyTo(frame, 20);
            return frame;
        }
    }
}
