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
    private readonly object _ownership = new();
    private readonly HashSet<Task> _pendingCalls = [];
    private bool _closing;
    private Task? _disposeTask;
    private FantasyBackendProbe(Session session) { _session = session; _ownerScene = session.Scene; _sessionId = session.RuntimeId; Clients.TryAdd(_sessionId, this); }
    public bool TryReadNotification(out ControlNotification? notification) => _notifications.Reader.TryRead(out notification);
    internal static void Deliver(long id, byte[] payload)
    {
        if (Clients.TryGetValue(id, out FantasyBackendProbe? client))
        { try { client._notifications.Writer.TryWrite(ControlNotification.Parser.ParseFrom(payload)); } catch (InvalidProtocolBufferException) { } }
    }
    internal static Task<FantasyBackendProbe> ConnectAsync(Scene scene, string endpoint, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        TaskCompletionSource<FantasyBackendProbe> connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TCPClientNetwork? network = null;
        scene.ThreadSynchronizationContext.Post(() =>
        {
            if (connected.Task.IsCompleted || scene.IsDisposed) return;
            try
            {
                network = Entity.Create<TCPClientNetwork>(scene, false, true);
                network.Initialize(NetworkTarget.Outer, enableMessageJsonLog: false);
                FantasyProbeConnection.Begin<Session, FantasyBackendProbe>(complete => network.Connect(endpoint,
                    onConnectComplete: complete,
                    onConnectFail: () => connected.TrySetException(new ServiceException("unavailable")),
                    onConnectDisconnect: () => { }, isHttps: false, connectTimeout: 5000), session => new(session), connected,
                    probe => probe.DisposeAsync().GetAwaiter().GetResult());
            }
            catch (Exception) { network?.Dispose(); connected.TrySetException(new ServiceException("unavailable")); }
        });
        return FantasyProbeConnection.WaitOwnedAsync(connected, TimeSpan.FromSeconds(10), ct,
            () => FantasyProbeConnection.OnSceneAsync(scene, () => network?.Dispose()), probe => probe.DisposeAsync());
    }
    public async ValueTask<ServiceReply> CallAsync(string method, ReadOnlyMemory<byte> payload, string credential = "", CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (payload.Length > 60000) return ServiceReply.Reject("unavailable");
        TaskCompletionSource<ServiceReply> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ControlRequest request = new() { CorrelationId = Guid.NewGuid().ToString("N"), Method = method, Body = ByteString.CopyFrom(payload.Span), Credential = credential };
        lock (_ownership)
        {
            if (_closing || _session.IsDisposed || _ownerScene.IsDisposed || !_calls.Wait(0)) return ServiceReply.Reject("unavailable");
            _pendingCalls.Add(settled.Task);
            _ownerScene.ThreadSynchronizationContext.Post(async () =>
            {
                try
                {
                    lock (_ownership)
                        if (_closing || _session.IsDisposed) throw new ServiceException("unavailable");
                    using IResponse response = await _session.Call(new FantasyGateRequest { Payload = request.ToByteArray() });
                    ControlResponse decoded = ControlResponse.Parser.ParseFrom(((FantasyGateResponse)response).Payload);
                    if (decoded.CorrelationId != request.CorrelationId) throw new ServiceException("response-conflict");
                    received.TrySetResult(new(decoded.Body.ToByteArray(), decoded.Error));
                }
                catch (Exception)
                {
                    received.TrySetException(new ServiceException("unavailable"));
                    _ = received.Task.Exception;
                }
                finally
                {
                    _calls.Release();
                    // Complete before removing ownership, so Dispose cannot miss
                    // an operation whose vendor continuation is still running.
                    settled.TrySetResult();
                    lock (_ownership) _pendingCalls.Remove(settled.Task);
                }
            });
        }
        try { return await received.Task.WaitAsync(TimeSpan.FromSeconds(10), ct); }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException)
        {
            // Closing settles local vendor callbacks; it does not undo a request
            // already accepted by the server. Its mutation outcome remains unknown.
            await DisposeAsync();
            throw;
        }
    }
    public ValueTask DisposeAsync()
    {
        lock (_ownership)
        {
            if (_disposeTask is not null) return new(_disposeTask);
            _closing = true;
            Clients.TryRemove(_sessionId, out _); _notifications.Writer.TryComplete();
            _disposeTask = CloseAsync(_pendingCalls.ToArray());
            return new(_disposeTask);
        }
    }
    private async Task CloseAsync(Task[] pending)
    {
        await FantasyProbeConnection.OnSceneAsync(_ownerScene, () => { if (!_session.IsDisposed) _session.Dispose(); });
        // Session.Dispose fails its real pending RPC callbacks. Await their
        // adapter finally blocks too, including calls queued before close began.
        await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(10));
    }
}
