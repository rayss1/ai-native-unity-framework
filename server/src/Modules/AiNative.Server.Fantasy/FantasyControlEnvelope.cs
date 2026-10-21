using global::Fantasy;
using AiNative.Realtime;
using Fantasy.Async;
using Fantasy.Event;
using Fantasy.Network.Interface;
using LightProto;

namespace AiNative.Server.Fantasy;

// Application indices 177 are reserved for backend control; realtime index 1 remains unchanged.
[ProtoContract]
internal sealed partial class S2S_FantasyControlRequest : AMessage, IAddressRequest
{
    [ProtoIgnore]
    public S2S_FantasyControlResponse ResponseType { get; set; } = null!;

    [ProtoMember(1)]
    public byte[] Payload { get; set; } = [];

    public uint OpCode() => ServiceControlTransportOpCodes.Request;

    public void Dispose() => Payload = [];
}

[ProtoContract]
internal sealed partial class S2S_FantasyControlResponse : AMessage, IAddressResponse
{
    [ProtoMember(1)]
    public uint ErrorCode { get; set; }

    [ProtoMember(2)]
    public byte[] Payload { get; set; } = [];

    public uint OpCode() => ServiceControlTransportOpCodes.Response;

    public void Dispose()
    {
        ErrorCode = 0;
        Payload = [];
    }
}

[ProtoContract]
internal sealed partial class C2S_FantasyGateRequest : AMessage, IRequest
{
    [ProtoIgnore]
    public S2C_FantasyGateResponse ResponseType { get; set; } = null!;

    [ProtoMember(1)]
    public byte[] Payload { get; set; } = [];

    public uint OpCode() => GateTransportOpCodes.Request;

    public void Dispose() => Payload = [];
}

[ProtoContract]
internal sealed partial class S2C_FantasyGateResponse : AMessage, IResponse
{
    [ProtoMember(1)]
    public uint ErrorCode { get; set; }

    [ProtoMember(2)]
    public byte[] Payload { get; set; } = [];

    public uint OpCode() => GateTransportOpCodes.Response;

    public void Dispose()
    {
        ErrorCode = 0;
        Payload = [];
    }
}

internal sealed class FantasyControlHandler
    : AddressRPC<Scene, S2S_FantasyControlRequest, S2S_FantasyControlResponse>
{
    protected override async FTask Run(
        Scene scene,
        S2S_FantasyControlRequest request,
        S2S_FantasyControlResponse response,
        Action reply
    )
    {
        response.Payload = await FantasyServiceRuntime.DispatchAsync(request.Payload, "", false);
    }
}

internal sealed class FantasyGateHandler
    : MessageRPC<C2S_FantasyGateRequest, S2C_FantasyGateResponse>
{
    protected override async FTask Run(
        global::Fantasy.Network.Session session,
        C2S_FantasyGateRequest request,
        S2C_FantasyGateResponse response,
        Action reply
    )
    {
        if (!FantasyOuterSendBudget.Check(session))
            return;
        FantasyServiceRuntime.RegisterOuter(session);
        response.Payload = await FantasyServiceRuntime.DispatchAsync(
            request.Payload,
            session.RuntimeId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            true
        );
    }
}

[ProtoContract]
internal sealed partial class S2C_FantasyGatePush : AMessage, IMessage
{
    [ProtoMember(1)]
    public byte[] Payload { get; set; } = [];

    public uint OpCode() => GateTransportOpCodes.Push;

    public void Dispose() => Payload = [];
}

internal sealed class FantasyGateNotificationHandler : Message<S2C_FantasyGatePush>
{
    protected override async FTask Run(
        global::Fantasy.Network.Session session,
        S2C_FantasyGatePush notification
    )
    {
        FantasyBackendProbe.Deliver(session.RuntimeId, notification.Payload);
        await FTask.CompletedTask;
    }
}

internal sealed class FantasyServiceSceneCreated : AsyncEventSystem<OnCreateScene>
{
    protected override async FTask Handler(OnCreateScene created)
    {
        FantasyServiceRuntime.MarkScene(created.Scene);
        await FTask.CompletedTask;
    }
}
