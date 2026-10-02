using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AiNative.Client.Fantasy
{
    // backend.proto and the pinned Fantasy outer TCP framing. No generated server types escape this adapter.
    internal static class GateWire
    {
        internal const int MaxBody = 60000;
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
        internal static byte[] Text(int field, string value) => Bytes(field, Utf8.GetBytes(value ?? ""));
        internal static byte[] Bytes(int field, byte[] value)
        {
            if (value.Length == 0) return Array.Empty<byte>();
            if (field < 1 || value.Length > MaxBody) throw new InvalidDataException("gate-field-limit");
            using var output = new MemoryStream();
            Varint(output, (ulong)((field << 3) | 2)); Varint(output, (ulong)value.Length); output.Write(value, 0, value.Length); return output.ToArray();
        }
        internal static byte[] Number(int field, ulong value)
        {
            if (value == 0) return Array.Empty<byte>();
            using var output = new MemoryStream(); Varint(output, (ulong)(field << 3)); Varint(output, value); return output.ToArray();
        }
        internal static byte[] Join(params byte[][] fields)
        {
            using var output = new MemoryStream(); foreach (byte[] field in fields) { if (output.Length + field.Length > MaxBody) throw new InvalidDataException("gate-body-limit"); output.Write(field, 0, field.Length); } return output.ToArray();
        }
        internal static byte[] Request(string method, byte[] body, string credential, string correlation) => Join(Text(1, correlation), Text(2, method), Bytes(3, body), Text(4, credential));
        internal static byte[] Frame(uint opcode, uint rpc, byte[] body)
        {
            if (body.Length > MaxBody) throw new InvalidDataException("gate-frame-limit");
            byte[] frame = new byte[20 + body.Length]; BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length == 0 ? -1 : body.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), opcode); BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(8), rpc); body.CopyTo(frame, 20); return frame;
        }
        internal static byte[] Response(byte[] bytes, string correlation)
        {
            var message = Read(bytes);
            if (String(message, 1) != correlation) throw new InvalidDataException("gate-response-correlation");
            string error = String(message, 3); if (error.Length != 0) throw new GateCallException(error);
            return Data(message, 2);
        }
        internal static GateProfile Profile(byte[] bytes)
        {
            var m = Read(bytes); return new GateProfile { PlayerId = String(m, 1), DisplayName = String(m, 2), Played = checked((long)Value(m, 3)), Won = checked((long)Value(m, 4)), Kills = checked((long)Value(m, 5)) };
        }
        internal static GateParty Party(byte[] bytes)
        {
            var m = Read(bytes); var members = new List<GatePartyMember>();
            foreach (Field f in m) if (f.Id == 4) { var member = Read(RequireData(f)); members.Add(new GatePartyMember { PlayerId = String(member, 1), Ready = Value(member, 2) != 0 }); if (members.Count > 64) throw new InvalidDataException("gate-party-limit"); }
            return new GateParty { PartyId = String(m, 1), LeaderId = String(m, 2), Version = Value(m, 3), Members = members.ToArray(), QueueRequestId = String(m, 5) };
        }
        internal static GateMatch Match(byte[] bytes)
        {
            var ready = Read(bytes); var status = Read(Data(ready, 1)); var allocation = Read(Data(status, 4));
            return new GateMatch { RequestId = String(status, 1), State = String(status, 2), MatchId = String(status, 3), Failure = String(status, 5), RoomId = String(allocation, 3), NodeId = String(allocation, 4), BootEpoch = String(allocation, 5), Address = String(allocation, 6), EntryTicket = String(ready, 2) };
        }
        internal readonly struct Field
        {
            internal int Id { get; }
            internal int Wire { get; }
            internal byte[] Data { get; }
            internal ulong Value { get; }
            internal Field(int id, int wire, byte[] data, ulong value) { Id = id; Wire = wire; Data = data; Value = value; }
        }
        internal static List<Field> Read(byte[] bytes)
        {
            if (bytes.Length > MaxBody) throw new InvalidDataException("gate-body-limit");
            var fields = new List<Field>(); int offset = 0;
            while (offset < bytes.Length)
            {
                ulong key = ReadVarint(bytes, ref offset); int id = checked((int)(key >> 3)), wire = (int)(key & 7); if (id == 0 || fields.Count >= 1024) throw new InvalidDataException("gate-field-limit");
                byte[] data = Array.Empty<byte>(); ulong value = 0;
                if (wire == 0) value = ReadVarint(bytes, ref offset);
                else if (wire == 2)
                {
                    ulong length = ReadVarint(bytes, ref offset); if (length > (ulong)(bytes.Length - offset)) throw new InvalidDataException("gate-field-truncated");
                    data = new byte[(int)length]; Array.Copy(bytes, offset, data, 0, data.Length); offset += data.Length;
                }
                else if (wire == 1 || wire == 5) { int length = wire == 1 ? 8 : 4; if (bytes.Length - offset < length) throw new InvalidDataException("gate-field-truncated"); offset += length; }
                else throw new InvalidDataException("gate-invalid-wire");
                fields.Add(new Field(id, wire, data, value));
            }
            return fields;
        }
        internal static string String(List<Field> fields, int id) => Utf8.GetString(Data(fields, id));
        internal static byte[] Data(List<Field> fields, int id) { for (int i = fields.Count - 1; i >= 0; --i) if (fields[i].Id == id) return RequireData(fields[i]); return Array.Empty<byte>(); }
        internal static ulong Value(List<Field> fields, int id) { for (int i = fields.Count - 1; i >= 0; --i) if (fields[i].Id == id) { if (fields[i].Wire != 0) throw new InvalidDataException("gate-invalid-wire"); return fields[i].Value; } return 0; }
        private static byte[] RequireData(Field field) { if (field.Wire != 2) throw new InvalidDataException("gate-invalid-wire"); return field.Data; }
        private static ulong ReadVarint(byte[] bytes, ref int offset)
        {
            ulong result = 0; for (int i = 0; i < 10; ++i) { if (offset >= bytes.Length) throw new InvalidDataException("gate-varint-truncated"); byte b = bytes[offset++]; if (i == 9 && b > 1) throw new InvalidDataException("gate-varint-overflow"); result |= (ulong)(b & 127) << (i * 7); if ((b & 128) == 0) return result; } throw new InvalidDataException("gate-varint-overflow");
        }
        private static void Varint(Stream output, ulong value) { while (value >= 128) { output.WriteByte((byte)((value & 127) | 128)); value >>= 7; } output.WriteByte((byte)value); }
    }
}
