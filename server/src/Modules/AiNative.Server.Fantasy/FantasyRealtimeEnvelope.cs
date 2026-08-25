using global::Fantasy;
using Fantasy.Async;
using Fantasy.Event;
using Fantasy.Network;
using Fantasy.Network.Interface;
using LightProto;

namespace AiNative.Server.Fantasy;

[Serializable]
[ProtoContract]
internal sealed partial class FantasyRealtimeEnvelope : AMessage, IMessage
{
    // ProtoBuf + OuterMessage + application-local index 1.
    private const uint EnvelopeOpCode = 134217729U;

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
        FantasyKcpGatewayBridge.Deliver(session, message.ChannelId, message.Payload, message.Sequence);
        await FTask.CompletedTask;
    }
}

internal sealed class FantasyGatewaySceneCreatedEvent : AsyncEventSystem<OnCreateScene>
{
    protected override async FTask Handler(OnCreateScene created)
    {
        if (string.Equals(
                created.Scene.SceneConfig.NetworkProtocol,
                nameof(NetworkProtocolType.KCP),
                StringComparison.Ordinal))
        {
            FantasyKcpGatewayBridge.MarkListening(created.Scene, created.Scene.SceneConfig.OuterPort);
        }

        await FTask.CompletedTask;
    }
}
