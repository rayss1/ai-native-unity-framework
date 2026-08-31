using System;
using global::Fantasy;
using Fantasy.Async;
using Fantasy.Network;
using Fantasy.Network.Interface;
using LightProto;

namespace AiNative.Client.Fantasy
{
    [Serializable]
    [ProtoContract]
    internal sealed partial class FantasyRealtimeEnvelope : AMessage, IMessage
    {
        internal const uint EnvelopeOpCode = 134217729U;

        [ProtoMember(1)]
        public uint ChannelId { get; set; }

        [ProtoMember(2)]
        public byte[] Payload { get; set; } = Array.Empty<byte>();

        [ProtoMember(3)]
        public ulong Sequence { get; set; }

        public uint OpCode() => EnvelopeOpCode;

        public void Dispose()
        {
            ChannelId = 0;
            Payload = Array.Empty<byte>();
            Sequence = 0;
        }
    }

    internal sealed class FantasyRealtimeEnvelopeHandler : Message<FantasyRealtimeEnvelope>
    {
        protected override async FTask Run(Session session, FantasyRealtimeEnvelope message)
        {
            FantasyClientSessionRouter.Deliver(
                session.RuntimeId,
                message.ChannelId,
                message.Payload,
                message.Sequence);
            await FTask.CompletedTask;
        }
    }
}
