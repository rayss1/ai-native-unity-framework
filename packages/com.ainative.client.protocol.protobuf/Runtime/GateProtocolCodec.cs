using System;
using System.IO;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using AiNative.Protocol.Backend.V1;
using AiNative.Protocol.Transport.V1;

namespace AiNative.Client.Protocol.Protobuf
{
    /// <summary>Generated backend protocol facade. No generated message types cross this boundary.</summary>
    public static class GateProtocolCodec
    {
        public const int MaxBody = 60000;
        public static byte[] Account(string username, string password) => Encode(new AccountRequest { Username = username, Password = password });
        public static byte[] ProfileRequest(string player) => Encode(new ProfileRequest { PlayerId = player });
        public static byte[] SettlementRequest(string match) => Encode(new SettlementQuery { MatchId = match });
        public static byte[] PartyRequest(string party, string target = "", bool ready = false, ulong version = 0) => Encode(new PartyCommand { PartyId = party, TargetPlayerId = target, Ready = ready, ExpectedVersion = version });
        public static byte[] QueueRequest(string party, ulong version, string request) => Encode(new PartyQueueRequest { PartyId = party, ExpectedVersion = version, RequestId = request });
        public static byte[] MatchRequest(string request) => Encode(new MatchQuery { RequestId = request });
        public static byte[] Request(string method, byte[] body, string credential, string correlation) => Encode(new ControlRequest { Method = method, Body = ByteString.CopyFrom(body), Credential = credential, CorrelationId = correlation });
        public static byte[] Envelope(byte[] request) => Encode(new GateRequestEnvelope { Payload = ByteString.CopyFrom(request) });
        public static (uint Error, byte[] Payload) ResponseEnvelope(byte[] bytes) { var m = Parse(bytes, GateResponseEnvelope.Parser); return (m.ErrorCode, m.Payload.ToByteArray()); }
        public static (string Correlation, string Error, byte[] Payload) Response(byte[] bytes) { var m = Parse(bytes, ControlResponse.Parser); return (m.CorrelationId, m.Error, m.Body.ToByteArray()); }
        public static (string Player, string Credential, long Expiry) Login(byte[] bytes) { var m = Parse(bytes, LoginResult.Parser); if (m.ExpiresUnixSeconds < 0) throw new InvalidDataException("gate-negative-expiry"); return (m.PlayerId, m.SessionToken, m.ExpiresUnixSeconds); }
        public static (string Match, bool Confirmed) Settlement(byte[] bytes) { var m = Parse(bytes, SettlementReceipt.Parser); return (m.MatchId, m.Confirmed); }
        public static (string Player, string Name, long Played, long Won, long Kills) Profile(byte[] bytes)
        {
            var m = Parse(bytes, PlayerProfile.Parser);
            if (m.Played < 0 || m.Won < 0 || m.Kills < 0) throw new InvalidDataException("gate-negative-statistic");
            return (m.PlayerId, m.DisplayName, m.Played, m.Won, m.Kills);
        }
        public static GateProtocolParty Party(byte[] bytes) => Map(Parse(bytes, PartyState.Parser));
        public static GateProtocolParty[] Invitations(byte[] bytes)
        {
            var m = Parse(bytes, PartyInvitations.Parser);
            if (m.Parties.Count > 64) throw new InvalidDataException("gate-invitation-limit");
            var parties = new GateProtocolParty[m.Parties.Count];
            for (int i = 0; i < parties.Length; i++) parties[i] = Map(m.Parties[i]);
            return parties;
        }
        public static GateProtocolMatch Match(byte[] bytes, bool queueResponse = false)
        {
            MatchReady ready = queueResponse ? new MatchReady { Status = Parse(bytes, MatchStatus.Parser) } : Parse(bytes, MatchReady.Parser);
            var status = ready.Status; var allocation = status?.Allocation;
            return new GateProtocolMatch { RequestId = status?.RequestId ?? "", State = status?.State ?? "", MatchId = status?.MatchId ?? "", Failure = status?.Failure ?? "", RoomId = allocation?.RoomId ?? "", NodeId = allocation?.NodeId ?? "", BootEpoch = allocation?.BootEpoch ?? "", Address = allocation?.Address ?? "", EntryTicket = ready.EntryTicket };
        }
        private static GateProtocolParty Map(PartyState m)
        {
            if (m.Members.Count > 64) throw new InvalidDataException("gate-party-limit");
            var members = new GateProtocolMember[m.Members.Count];
            for (int i = 0; i < members.Length; i++) members[i] = new GateProtocolMember(m.Members[i].PlayerId, m.Members[i].Ready);
            return new GateProtocolParty { PartyId = m.PartyId, LeaderId = m.LeaderId, Version = m.Version, QueueRequestId = m.QueueRequestId, Members = members };
        }
        private static byte[] Encode(IMessage message)
        {
            if (message.CalculateSize() > MaxBody) throw new InvalidDataException("gate-body-limit");
            return message.ToByteArray();
        }
        private static T Parse<T>(byte[] bytes, MessageParser<T> parser) where T : IMessage<T>, new()
        {
            if (bytes == null || bytes.Length > MaxBody) throw new InvalidDataException("gate-body-limit");
            try
            {
                // Structural validation retains existing defensive limits without materializing field buffers.
                using (var input = new CodedInputStream(bytes)) Validate(input, bytes.Length, DescriptorCache<T>.Value, 0);
                return parser.ParseFrom(bytes);
            }
            catch (InvalidProtocolBufferException e) { throw new InvalidDataException("gate-invalid-protobuf", e); }
        }
        private static void Validate(CodedInputStream input, long end, MessageDescriptor descriptor, int depth)
        {
            if (depth > 64) throw new InvalidDataException("gate-recursion-limit");
            int count = 0;
            while (input.Position < end)
            {
                uint tag = input.ReadTag();
                if (tag == 0 || ++count > 1024) throw new InvalidDataException("gate-field-limit");
                var field = descriptor.FindFieldByNumber((int)(tag >> 3));
                if ((tag & 7) == 3 || (tag & 7) == 4) throw new InvalidDataException("gate-invalid-wire");
                if (field != null)
                {
                    uint expected = field.FieldType == FieldType.String || field.FieldType == FieldType.Bytes || field.FieldType == FieldType.Message ? 2u : 0u;
                    if ((tag & 7) != expected) throw new InvalidDataException("gate-invalid-wire");
                    if (field.FieldType == FieldType.Message)
                    {
                        int length = input.ReadLength();
                        long nestedEnd = input.Position + length;
                        if (length < 0 || nestedEnd > end) throw new InvalidDataException("gate-field-truncated");
                        Validate(input, nestedEnd, field.MessageType, depth + 1);
                        continue;
                    }
                    if (field.FieldType == FieldType.UInt32)
                    {
                        if (input.ReadUInt64() > uint.MaxValue) throw new InvalidDataException("gate-integer-overflow");
                        if (input.Position > end) throw new InvalidDataException("gate-field-truncated");
                        continue;
                    }
                }
                input.SkipLastField();
                if (input.Position > end) throw new InvalidDataException("gate-field-truncated");
            }
        }
        private static class DescriptorCache<T> where T : IMessage<T>, new()
        {
            internal static readonly MessageDescriptor Value = new T().Descriptor;
        }
    }
    public readonly struct GateProtocolMember
    {
        public readonly string PlayerId; public readonly bool Ready;
        public GateProtocolMember(string playerId, bool ready) { PlayerId = playerId; Ready = ready; }
    }
    public sealed class GateProtocolParty
    {
        public string PartyId = "", LeaderId = "", QueueRequestId = "";
        public ulong Version;
        public GateProtocolMember[] Members = Array.Empty<GateProtocolMember>();
    }
    public sealed class GateProtocolMatch
    {
        public string RequestId = "", State = "", MatchId = "", Failure = "", RoomId = "", NodeId = "", BootEpoch = "", Address = "", EntryTicket = "";
    }
}
