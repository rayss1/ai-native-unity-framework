using AiNative.Protocol.Backend.V1;
using AiNative.Server.Control;
using global::Fantasy;
using Fantasy.Helper;
using Fantasy.Network.Interface;
using Fantasy.Platform.Net;
using Google.Protobuf;
using System.Collections.Concurrent;
using global::Fantasy.Network;
using System.Diagnostics;

namespace AiNative.Server.Fantasy;

public sealed class FantasyServiceRuntime : IServiceRpc, IClientNotifier, IAsyncDisposable
{
    private static FantasyServiceRuntime? _current;
    private readonly int _sourceSceneId;
    private readonly ServiceRole _role;
    private readonly PeerAuthentication _auth;
    private readonly SemaphoreSlim _requests = new(128, 128), _handlers = new(128, 128);
    private readonly SemaphoreSlim _notifications = new(128, 128);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IServiceHandler? _handler;
    private Scene? _scene;
    private readonly ConcurrentDictionary<string, Session> _outer = new(StringComparer.Ordinal);
    private readonly FantasyBattleProbeGroups _probeGroups = new();
    public FantasyServiceRuntime(int sourceSceneId, ServiceRole role, PeerAuthentication auth)
    {
        // Independent acceptance probes do not construct a Battle gateway.
        // Configure vendor-global settings before the first outer KCP network.
        global::Fantasy.Network.KCP.KCPSettings.ConfigureOuterMtu(FantasyKcpGateway.DefaultOuterKcpMtu);
        _sourceSceneId = sourceSceneId; _role = role; _auth = auth;
    }
    public bool IsReady => _ready.Task.IsCompletedSuccessfully;
    public void SetHandler(IServiceHandler handler) => _handler = handler;
    public Task WaitUntilReadyAsync(CancellationToken ct = default) => _ready.Task.WaitAsync(ct);
    internal static void RegisterOuter(Session session)
    {
        FantasyServiceRuntime? runtime = Volatile.Read(ref _current);
        if (runtime is not null && runtime._role == ServiceRole.Gate && runtime._outer.Count < 1024)
            runtime._outer.TryAdd(session.RuntimeId.ToString(System.Globalization.CultureInfo.InvariantCulture), session);
    }
    public async ValueTask SweepDisconnectedAsync(CancellationToken ct = default)
    {
        foreach (var pair in _outer)
            if (pair.Value.IsDisposed && _outer.TryRemove(pair.Key, out _) && _handler is IServiceConnectionObserver observer)
                await observer.DisconnectedAsync(pair.Key, ct);
    }
    public ValueTask NotifyAsync(string connectionId, string method, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        if (!_outer.TryGetValue(connectionId, out Session? session) || session.IsDisposed || payload.Length > 60000) return ValueTask.CompletedTask;
        if (!_notifications.Wait(0)) throw new ServiceException("unavailable");
        byte[] packet = new ControlNotification { Method = method, Body = ByteString.CopyFrom(payload.Span) }.ToByteArray();
        session.Scene.ThreadSynchronizationContext.Post(() =>
        { try { if (FantasyOuterSendBudget.Check(session)) session.Send(new FantasyGateNotification { Payload = packet }); } finally { _notifications.Release(); } });
        return ValueTask.CompletedTask;
    }
    public Task<FantasyBackendProbe> ConnectGateProbeAsync(string endpoint, CancellationToken ct = default) =>
        FantasyBackendProbe.ConnectAsync(_scene ?? throw new InvalidOperationException("runtime-not-ready"), endpoint, ct);
    internal Task<FantasyBattleProbeGroup> CreateBattleProbeGroupAsync(CancellationToken ct = default)
    {
        if (_role != ServiceRole.Client) throw new InvalidOperationException("probe-group-requires-client-runtime");
        return _probeGroups.CreateAsync(_scene ?? throw new InvalidOperationException("runtime-not-ready"), ct);
    }
    internal Task<FantasyKcpProbe> ConnectBattleProbeAsync(string endpoint, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Scene scene = _scene ?? throw new InvalidOperationException("runtime-not-ready");
        TaskCompletionSource<FantasyKcpProbe> connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        global::Fantasy.Network.KCP.KCPClientNetwork? network = null;
        scene.ThreadSynchronizationContext.Post(() =>
        {
            if (connected.Task.IsCompleted || scene.IsDisposed) return;
            try
            {
                network = global::Fantasy.Entitas.Entity.Create<global::Fantasy.Network.KCP.KCPClientNetwork>(scene, false, true);
                network.Initialize(NetworkTarget.Outer, enableReceiveMessageJsonLog: false);
                FantasyProbeConnection.Begin<Session, FantasyKcpProbe>(complete => network.Connect(endpoint, onConnectComplete: complete,
                    onConnectFail: () => connected.TrySetException(new ServiceException("unavailable")), onConnectDisconnect: () => { },
                    isHttps: false, connectTimeout: 5000), session => new(session), connected,
                    probe => probe.DisposeAsync().GetAwaiter().GetResult());
            }
            catch (Exception) { network?.Dispose(); connected.TrySetException(new ServiceException("unavailable")); }
        });
        return FantasyProbeConnection.WaitOwnedAsync(connected, TimeSpan.FromSeconds(10), ct,
            () => FantasyProbeConnection.OnSceneAsync(scene, () => network?.Dispose()), probe => probe.DisposeAsync());
    }
    public async Task RunAsync(CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _current, this, null) is not null) throw new InvalidOperationException("one-fantasy-runtime-per-process");
        typeof(FantasyServiceRuntime).Assembly.EnsureLoaded();
        using CancellationTokenRegistration cancelled = ct.Register(() => _lifetime.Cancel());
        try { await Entry.Start(cancellationToken: ct); }
        finally { Interlocked.CompareExchange(ref _current, null, this); }
    }
    internal static void MarkScene(Scene scene)
    {
        FantasyServiceRuntime? runtime = Volatile.Read(ref _current);
        if (runtime is not null && scene.SceneConfigId == runtime._sourceSceneId)
        { runtime._scene = scene; runtime._ready.TrySetResult(true); }
    }
    public async ValueTask<ServiceReply> CallAsync(ServiceTarget target, string method, ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default, string playerId = "")
    {
        using Activity? activity = ServiceDiagnostics.Activities.StartActivity(ServiceDiagnostics.Method(method), ActivityKind.Client);
        if (payload.Length > 60000) return ServiceReply.Reject("request-too-large");
        Scene? source = _scene;
        ServicePeer? peer = _auth.Peers.FirstOrDefault(x => x.Role == target.Role && (target.NodeId.Length == 0 || target.NodeId == x.Id));
        if (source is null || source.IsDisposed || peer is null || _lifetime.IsCancellationRequested) return ServiceReply.Reject("unavailable");
        if (!_requests.Wait(0)) return ServiceReply.Reject("unavailable");
        TaskCompletionSource<ServiceReply> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ControlRequest request = new() { CorrelationId = Guid.NewGuid().ToString("N"), Method = method,
            Body = ByteString.CopyFrom(payload.Span), DelegatedPlayerId = playerId,
            TraceParent = Activity.Current?.Id ?? "", TraceState = Activity.Current?.TraceStateString ?? "" };
        _auth.Sign(request);
        source.ThreadSynchronizationContext.Post(async () =>
        {
            try
            {
                long address = SceneConfigData.Instance.Get((uint)peer.SceneId).Address;
                using IResponse response = await source.NetworkMessagingComponent.Call(address, new FantasyControlRequest { Payload = request.ToByteArray() });
                if (response is not FantasyControlResponse control || control.ErrorCode != 0) throw new ServiceException("unavailable");
                ControlResponse result = ControlResponse.Parser.ParseFrom(control.Payload);
                if (result.CorrelationId != request.CorrelationId) throw new ServiceException("response-conflict");
                received.TrySetResult(new(result.Body.ToByteArray(), result.Error));
            }
            catch (Exception exception) { received.TrySetException(new ServiceException(exception is TimeoutException ? "timeout" : "unavailable")); }
            finally { _requests.Release(); }
        });
        try { return await received.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken); }
        catch (TimeoutException) { throw new ServiceException("timeout"); }
    }
    internal static async Task<byte[]> DispatchAsync(byte[] payload, string connectionId, bool outer)
    {
        FantasyServiceRuntime? runtime = Volatile.Read(ref _current);
        ControlResponse response = new() { Error = "unavailable" };
        if (runtime is null || runtime._lifetime.IsCancellationRequested || payload.Length > 65536) return response.ToByteArray();
        ControlRequest request;
        try { request = ControlRequest.Parser.ParseFrom(payload); }
        catch (InvalidProtocolBufferException) { return response.ToByteArray(); }
        response.CorrelationId = request.CorrelationId;
        ServiceCallContext context;
        if (outer)
        {
            if (runtime._role != ServiceRole.Gate) { response.Error = "forbidden"; return response.ToByteArray(); }
            context = new(ServiceRole.Client, connectionId);
            // Gate receives the entire envelope, including the client's opaque login credential.
        }
        else if (!runtime._auth.TryAuthenticate(request, out context))
        { response.Error = "forbidden"; return response.ToByteArray(); }
        if (!runtime._handlers.Wait(0)) return response.ToByteArray();
        ActivityContext.TryParse(request.TraceParent, request.TraceState, true, out ActivityContext parent);
        using Activity? activity = ServiceDiagnostics.Activities.StartActivity(ServiceDiagnostics.Method(request.Method), ActivityKind.Server, parent);
        long started = Stopwatch.GetTimestamp();
        try
        {
            if (runtime._handler is not null)
            {
                ServiceReply reply = await runtime._handler.HandleAsync(context, request.Method,
                    outer ? request.ToByteArray() : request.Body.Memory, runtime._lifetime.Token);
                response.Error = reply.Error; response.Body = ByteString.CopyFrom(reply.Payload);
            }
        }
        catch (Exception) { response.Error = "unavailable"; }
        finally { runtime._handlers.Release(); ServiceDiagnostics.RecordRpc(request.Method, runtime._role, started, response.Error.Length == 0); }
        return response.ToByteArray();
    }
    public async ValueTask DisposeAsync() { await _probeGroups.DisposeAsync(); _lifetime.Cancel(); _auth.Dispose(); }
}
