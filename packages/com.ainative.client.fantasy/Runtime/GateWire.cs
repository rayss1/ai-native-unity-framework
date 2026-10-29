using System;
using System.IO;
using AiNative.Client.Protocol.Protobuf;

namespace AiNative.Client.Fantasy
{
    internal static class GateWire
    {
        internal const int MaxBody = GateProtocolCodec.MaxBody;

        internal static byte[] Request(
            string method,
            byte[] body,
            string credential,
            string correlation
        ) => GateProtocolCodec.Request(method, body, credential, correlation);

        internal static byte[] Response(byte[] bytes, string correlation)
        {
            var m = GateProtocolCodec.Response(bytes);
            if (m.Correlation != correlation)
            {
                throw new InvalidDataException("gate-response-correlation");
            }

            if (m.Error.Length != 0)
            {
                throw new GateCallException(m.Error);
            }

            return m.Payload;
        }

        internal static GateProfile Profile(byte[] bytes)
        {
            var m = GateProtocolCodec.Profile(bytes);
            return new GateProfile
            {
                PlayerId = m.Player,
                DisplayName = m.Name,
                Played = m.Played,
                Won = m.Won,
                Kills = m.Kills,
            };
        }

        internal static GateParty Party(byte[] bytes) => Party(GateProtocolCodec.Party(bytes));

        internal static GateParty Party(GateProtocolParty m)
        {
            var members = new GatePartyMember[m.Members.Length];
            for (int i = 0; i < members.Length; i++)
            {
                members[i] = new GatePartyMember
                {
                    PlayerId = m.Members[i].PlayerId,
                    Ready = m.Members[i].Ready,
                };
            }

            return new GateParty
            {
                PartyId = m.PartyId,
                LeaderId = m.LeaderId,
                Version = m.Version,
                Members = members,
                QueueRequestId = m.QueueRequestId,
            };
        }

        internal static GateMatch Match(byte[] bytes, bool queueResponse = false)
        {
            var m = GateProtocolCodec.Match(bytes, queueResponse);
            return new GateMatch
            {
                RequestId = m.RequestId,
                State = m.State,
                MatchId = m.MatchId,
                Failure = m.Failure,
                RoomId = m.RoomId,
                NodeId = m.NodeId,
                BootEpoch = m.BootEpoch,
                Address = m.Address,
                EntryTicket = m.EntryTicket,
            };
        }
    }
}
