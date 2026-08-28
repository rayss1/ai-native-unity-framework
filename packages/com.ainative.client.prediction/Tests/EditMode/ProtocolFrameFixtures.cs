using System;
using System.Collections.Generic;
using AiNative.Realtime;

namespace AiNative.Client.Prediction.Tests
{
    internal static class ProtocolFrameFixtures
    {
        public static byte[] Snapshot(
            uint entityId,
            ulong roomTick,
            uint acknowledgement,
            int positionX,
            int positionZ,
            uint protocolMajor = 1,
            bool includePlayer = true)
        {
            List<byte> payload = new List<byte>();
            WriteKey(payload, 1, 0);
            WriteVarint(payload, protocolMajor);
            WriteKey(payload, 2, 1);
            WriteFixed64(payload, roomTick);
            if (includePlayer)
            {
                List<byte> player = new List<byte>();
                WriteKey(player, 1, 0);
                WriteVarint(player, entityId);
                if (positionX != 0)
                {
                    WriteKey(player, 2, 0);
                    WriteVarint(player, ZigZag(positionX));
                }

                if (positionZ != 0)
                {
                    WriteKey(player, 4, 0);
                    WriteVarint(player, ZigZag(positionZ));
                }

                WriteKey(payload, 4, 2);
                WriteVarint(payload, (uint)player.Count);
                payload.AddRange(player);
            }

            if (acknowledgement != 0)
            {
                WriteKey(payload, 6, 0);
                WriteVarint(payload, acknowledgement);
            }

            return Envelope(1101, payload);
        }

        public static byte[] Reconnect(
            uint connectionEpoch,
            ulong resumeTick,
            byte[] snapshotFrame)
        {
            List<byte> payload = new List<byte>();
            WriteKey(payload, 1, 0);
            WriteVarint(payload, connectionEpoch);
            WriteKey(payload, 2, 1);
            WriteFixed64(payload, resumeTick);
            WriteKey(payload, 3, 2);
            WriteVarint(payload, checked((uint)(snapshotFrame.Length - 2)));
            for (int index = 2; index < snapshotFrame.Length; index++)
            {
                payload.Add(snapshotFrame[index]);
            }

            return Envelope(1201, payload);
        }

        public static ReceivedPacket Packet(
            byte[] frame,
            TransportChannel channel,
            uint connectionEpoch,
            int? requiredBytes = null)
            => new ReceivedPacket(
                channel,
                frame.Length,
                requiredBytes ?? frame.Length,
                1,
                connectionEpoch);

        private static byte[] Envelope(ushort messageId, List<byte> payload)
        {
            byte[] frame = new byte[payload.Count + 2];
            frame[0] = (byte)messageId;
            frame[1] = (byte)(messageId >> 8);
            payload.CopyTo(frame, 2);
            return frame;
        }

        private static void WriteKey(List<byte> destination, int fieldNumber, int wireType)
            => WriteVarint(destination, checked((uint)((fieldNumber << 3) | wireType)));

        private static void WriteFixed64(List<byte> destination, ulong value)
        {
            for (int index = 0; index < 8; index++)
            {
                destination.Add((byte)(value >> (index * 8)));
            }
        }

        private static void WriteVarint(List<byte> destination, uint value)
        {
            while (value >= 0x80)
            {
                destination.Add((byte)(value | 0x80));
                value >>= 7;
            }

            destination.Add((byte)value);
        }

        private static uint ZigZag(int value)
        {
            unchecked
            {
                return (uint)((value << 1) ^ (value >> 31));
            }
        }
    }
}
