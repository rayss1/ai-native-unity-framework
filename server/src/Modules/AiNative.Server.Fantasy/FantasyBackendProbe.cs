using System.Collections.Concurrent;
using System.Threading.Channels;
using AiNative.Protocol.Backend.V1;
using AiNative.Server.Control;
using global::Fantasy;
using Fantasy.Entitas;
using Fantasy.Network;
using Fantasy.Network.Interface;
using Fantasy.Network.TCP;
using Google.Protobuf;

namespace AiNative.Server.Fantasy;

// An adapter-owned backend connection also used by cross-process acceptance clients.
public sealed class FantasyBackendProbe : IAsyncDisposable
{
    private static readonly ConcurrentDictionary<long, FantasyBackendProbe> Clients = new();
    private readonly Session _session;
    private readonly Scene _ownerScene;
    private readonly long _sessionId;
    private readonly Channel<ControlNotification> _notifications = Channel.CreateBounded<ControlNotification>(64);
    private readonly SemaphoreSlim _calls = new(8, 8);
    private FantasyBackendProbe(Session session) { _session = session; _ownerScene = session.Scene; _sessionId = session.RuntimeId; Clients.TryAdd(_sessionId, this); }
    public bool TryReadNotification(out ControlNotification? notification) => _notifications.Reader.TryRead(out notification);
    internal static void Deliver(long id, byte[] payload)
    {
        if (Clients.TryGetValue(id, out FantasyBackendProbe? client))
        { try { client._notifications.Writer.TryWrite(ControlNotification.Parser.ParseFrom(payload)); } catch (InvalidProtocolBufferException) { } }
    }
    internal static Task<FantasyBackendProbe> ConnectAsync(Scene scene, string endpoint, CancellationToken ct)
    {
        TaskCompletionSource<FantasyBackendProbe> connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        scene.ThreadSynchronizationContext.Post(() =>
        {
            TCPClientNetwork network = Entity.Create<TCPClientNetwork>(scene, false, true);
            network.Initialize(NetworkTarget.Outer, enableMessageJsonLog: false);
            Session? session = null;
            try
            {
                session = network.Connect(endpoint,
                    onConnectComplete: () => connected.TrySetResult(new(session!)),
                    onConnectFail: () => connected.TrySetException(new ServiceException("unavailable")),
                    onConnectDisconnect: () => { }, isHttps: false, connectTimeout: 5000);
            }
            catch (Exception) { network.Dispose(); connected.TrySetException(new ServiceException("unavailable")); }
        });
        return connected.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
    }
    public async ValueTask<ServiceReply> CallAsync(string method, ReadOnlyMemory<byte> payload, string credential = "", CancellationToken ct = default)
    {
        if (_session.IsDisposed || _ownerScene.IsDisposed || payload.Length > 60000) return ServiceReply.Reject("unavailable");
        if (!_calls.Wait(0)) return ServiceReply.Reject("unavailable");
        TaskCompletionSource<ServiceReply> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ControlRequest request = new() { CorrelationId = Guid.NewGuid().ToString("N"), Method = method, Body = ByteString.CopyFrom(payload.Span), Credential = credential };
        _ownerScene.ThreadSynchronizationContext.Post(async () =>
        {
            try
            {
                using IResponse response = await _session.Call(new FantasyGateRequest { Payload = request.ToByteArray() });
                ControlResponse decoded = ControlResponse.Parser.ParseFrom(((FantasyGateResponse)response).Payload);
                if (decoded.CorrelationId != request.CorrelationId) throw new ServiceException("response-conflict");
                received.TrySetResult(new(decoded.Body.ToByteArray(), decoded.Error));
            }
            catch (Exception) { received.TrySetException(new ServiceException("unavailable")); }
            finally { _calls.Release(); }
        });
        return await received.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
    }
    public ValueTask DisposeAsync()
    {
        Clients.TryRemove(_sessionId, out _); _notifications.Writer.TryComplete();
        if (!_session.IsDisposed && !_ownerScene.IsDisposed) _ownerScene.ThreadSynchronizationContext.Post(() => { if (!_session.IsDisposed) _session.Dispose(); });
        return ValueTask.CompletedTask;
    }
}
