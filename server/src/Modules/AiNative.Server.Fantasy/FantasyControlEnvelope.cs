using global::Fantasy;
using Fantasy.Async;
using Fantasy.Event;
using Fantasy.Network.Interface;
using LightProto;

namespace AiNative.Server.Fantasy;

// Application indices 177 are reserved for backend control; realtime index 1 remains unchanged.
[ProtoContract]
internal sealed partial class FantasyControlRequest : AMessage, IAddressRequest
{
    [ProtoIgnore] public FantasyControlResponse ResponseType { get; set; } = null!;
    [ProtoMember(1)] public byte[] Payload { get; set; } = [];
    public uint OpCode() => 1073742001u;
    public void Dispose() => Payload = [];
}
[ProtoContract]
internal sealed partial class FantasyControlResponse : AMessage, IAddressResponse
{
    [ProtoMember(1)] public uint ErrorCode { get; set; }
    [ProtoMember(2)] public byte[] Payload { get; set; } = [];
    public uint OpCode() => 1207959729u;
    public void Dispose() { ErrorCode = 0; Payload = []; }
}
[ProtoContract]
internal sealed partial class FantasyGateRequest : AMessage, IRequest
{
    [ProtoIgnore] public FantasyGateResponse ResponseType { get; set; } = null!;
    [ProtoMember(1)] public byte[] Payload { get; set; } = [];
    public uint OpCode() => 268435633u;
    public void Dispose() => Payload = [];
}
[ProtoContract]
internal sealed partial class FantasyGateResponse : AMessage, IResponse
{
    [ProtoMember(1)] public uint ErrorCode { get; set; }
    [ProtoMember(2)] public byte[] Payload { get; set; } = [];
    public uint OpCode() => 402653361u;
    public void Dispose() { ErrorCode = 0; Payload = []; }
}
internal sealed class FantasyControlHandler : AddressRPC<Scene, FantasyControlRequest, FantasyControlResponse>
{
    protected override async FTask Run(Scene scene, FantasyControlRequest request, FantasyControlResponse response, Action reply)
    { response.Payload = await FantasyServiceRuntime.DispatchAsync(request.Payload, "", false); }
}
internal sealed class FantasyGateHandler : MessageRPC<FantasyGateRequest, FantasyGateResponse>
{
    protected override async FTask Run(global::Fantasy.Network.Session session, FantasyGateRequest request, FantasyGateResponse response, Action reply)
    {
        if (!FantasyOuterSendBudget.Check(session)) return;
        FantasyServiceRuntime.RegisterOuter(session);
        response.Payload = await FantasyServiceRuntime.DispatchAsync(request.Payload, session.RuntimeId.ToString(System.Globalization.CultureInfo.InvariantCulture), true);
    }
}
[ProtoContract]
internal sealed partial class FantasyGateNotification : AMessage, IMessage
{
    [ProtoMember(1)] public byte[] Payload { get; set; } = [];
    public uint OpCode() => 134217905u;
    public void Dispose() => Payload = [];
}
internal sealed class FantasyGateNotificationHandler : Message<FantasyGateNotification>
{
    protected override async FTask Run(global::Fantasy.Network.Session session, FantasyGateNotification notification)
    { FantasyBackendProbe.Deliver(session.RuntimeId, notification.Payload); await FTask.CompletedTask; }
}
internal sealed class FantasyServiceSceneCreated : AsyncEventSystem<OnCreateScene>
{
    protected override async FTask Handler(OnCreateScene created)
    { FantasyServiceRuntime.MarkScene(created.Scene); await FTask.CompletedTask; }
}
