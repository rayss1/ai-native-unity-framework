using System;
using System.Buffers.Binary;
using System.IO;

namespace AiNative.Client.Fantasy
{
    // 固定的 Fantasy 外层 TCP 帧格式，与消息体序列化库无关。
    // TCP 帧头共 20 字节：消息体长度 [0..3]、opcode [4..7]、rpcId [8..11]，
    // 保留路由字节 [12..19]，Gate 中为零。
    // 消息体：Fantasy 信封 → Gate 业务信封 → 业务参数。
    // opcode 标识信封类型；负载中的 method 选择具体操作。
    internal static class GateFrame
    {
        internal static byte[] Encode(uint opcode, uint rpc, byte[] body)
        {
            if (body.Length > GateWire.MaxBody)
            {
                throw new InvalidDataException("gate-frame-limit");
            }

            byte[] frame = new byte[20 + body.Length];
            BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length == 0 ? -1 : body.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), opcode);
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(8), rpc);
            body.CopyTo(frame, 20);
            return frame;
        }
    }
}
