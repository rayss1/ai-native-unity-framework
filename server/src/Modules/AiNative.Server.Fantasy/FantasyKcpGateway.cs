using System.Collections.Concurrent;
using AiNative.Realtime;
using global::Fantasy;
using Fantasy.Entitas;
using Fantasy.Helper;
using Fantasy.Network;
using Fantasy.Network.Interface;
using Fantasy.Network.KCP;
using Fantasy.Platform.Net;

namespace AiNative.Server.Fantasy;

internal sealed class FantasyKcpConnection : IAsyncDisposable
{
    internal FantasyKcpConnection(long connectionId, uint connectionEpoch, Session session, int maxInboundBytes)
    {
        ConnectionId = connectionId;
        ConnectionEpoch = connectionEpoch;
        Transport = new FantasyRealtimeTransport(new FantasySessionSender(session), maxInboundBytes);
    }

    public long ConnectionId { get; }

    public uint ConnectionEpoch { get; }

    public IRealtimeTransport Transport { get; }

    public ValueTask DisposeAsync() => Transport.DisposeAsync();
}

internal sealed class FantasyKcpProbe : IAsyncDisposable
{
    private readonly FantasyRealtimeTransport _transport;
    private readonly long _sessionRuntimeId;

    internal FantasyKcpProbe(Session session)
    {
        _sessionRuntimeId = session.RuntimeId;
        _transport = new FantasyRealtimeTransport(new FantasySessionSender(session));
        FantasyKcpGatewayBridge.RegisterProbe(_sessionRuntimeId, _transport);
    }

    public IRealtimeTransport Transport => _transport;

    public async ValueTask DisposeAsync()
    {
        FantasyKcpGatewayBridge.RemoveProbe(_sessionRuntimeId);
        await _transport.DisposeAsync();
    }
}

internal sealed class FantasyKcpGateway : IAsyncDisposable
{
    private readonly ConcurrentDictionary<long, FantasyKcpConnection> _connections = new();
    private readonly ConcurrentQueue<FantasyKcpConnection> _accepted = new();
    private readonly TaskCompletionSource<int> _listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly int _maxInboundBytesPerConnection;
    private readonly int _maxConnections;
    private int _accepting;
    private int _nextConnectionEpoch;
    private int _running;
    private Scene? _scene;

    public FantasyKcpGateway(
        int maxInboundBytesPerConnection = 256 * 1024,
        int maxConnections = 64)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxInboundBytesPerConnection);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConnections);
        _maxInboundBytesPerConnection = maxInboundBytesPerConnection;
        _maxConnections = maxConnections;
    }

    public int ListeningPort => _listening.Task.IsCompletedSuccessfully ? _listening.Task.Result : 0;

    public int ConnectionCount => _connections.Count;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _running, 1) != 0)
        {
            throw new InvalidOperationException("The Fantasy KCP gateway is already running.");
        }

        FantasyKcpGatewayBridge.Activate(this);
        Volatile.Write(ref _accepting, 1);
        typeof(FantasyKcpGateway).Assembly.EnsureLoaded();

        try
        {
            await Entry.Start(cancellationToken: cancellationToken);
        }
        finally
        {
            BeginDrain();
            FantasyKcpGatewayBridge.Deactivate(this);
            Volatile.Write(ref _running, 0);
        }
    }

    public Task WaitUntilListeningAsync(CancellationToken cancellationToken) =>
        _listening.Task.WaitAsync(cancellationToken);

    public bool TryAccept(out FantasyKcpConnection? connection) => _accepted.TryDequeue(out connection);

    public void Release(FantasyKcpConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (_connections.TryGetValue(connection.ConnectionId, out FantasyKcpConnection? current) &&
            ReferenceEquals(current, connection))
        {
            _connections.TryRemove(connection.ConnectionId, out _);
        }

        connection.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public async Task<FantasyKcpProbe> ConnectLoopbackProbeAsync(CancellationToken cancellationToken)
    {
        Scene scene = _scene ?? throw new InvalidOperationException("The Fantasy KCP scene is not ready.");
        TaskCompletionSource<FantasyKcpProbe> connected = new(TaskCreationOptions.RunContinuationsAsynchronously);

        scene.ThreadSynchronizationContext.Post(() =>
        {
            KCPClientNetwork network = Entity.Create<KCPClientNetwork>(scene, false, true);
            network.Initialize(NetworkTarget.Outer, enableReceiveMessageJsonLog: false);
            Session? clientSession = null;

            try
            {
                clientSession = network.Connect(
                    $"127.0.0.1:{ListeningPort}",
                    onConnectComplete: () => connected.TrySetResult(new FantasyKcpProbe(clientSession!)),
                    onConnectFail: () => connected.TrySetException(new InvalidOperationException("Fantasy KCP loopback connect failed.")),
                    onConnectDisconnect: () => { },
                    isHttps: false,
                    connectTimeout: 5000);
            }
            catch (Exception exception)
            {
                network.Dispose();
                connected.TrySetException(exception);
            }
        });

        return await connected.Task.WaitAsync(cancellationToken);
    }

    public void BeginDrain()
    {
        if (Interlocked.Exchange(ref _accepting, 0) == 0)
        {
            return;
        }

        foreach (FantasyKcpConnection connection in _connections.Values)
        {
            connection.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        _connections.Clear();
        while (_accepted.TryDequeue(out _))
        {
        }
    }

    internal void MarkListening(int port)
    {
        if (port > 0)
        {
            _listening.TrySetResult(port);
        }
    }

    internal void MarkListening(Scene scene, int port)
    {
        _scene = scene;
        MarkListening(port);
    }

    internal void Deliver(Session session, uint channelId, ReadOnlySpan<byte> payload, ulong sequence)
    {
        if (Volatile.Read(ref _accepting) == 0 || !TryMapChannel(channelId, out TransportChannel channel))
        {
            return;
        }

        long connectionId = session.RuntimeId;
        if (!_connections.ContainsKey(connectionId) && _connections.Count >= _maxConnections)
        {
            session.Dispose();
            return;
        }

        FantasyKcpConnection connection = _connections.GetOrAdd(connectionId, _ =>
        {
            uint epoch = unchecked((uint)Interlocked.Increment(ref _nextConnectionEpoch));
            FantasyKcpConnection created = new(connectionId, epoch, session, _maxInboundBytesPerConnection);
            _accepted.Enqueue(created);
            return created;
        });

        _ = ((FantasyRealtimeTransport)connection.Transport).TryEnqueueReceived(
            channel,
            payload,
            sequence,
            connection.ConnectionEpoch);
    }

    public ValueTask DisposeAsync()
    {
        BeginDrain();
        return ValueTask.CompletedTask;
    }

    internal static bool TryMapChannel(uint channelId, out TransportChannel channel)
    {
        channel = channelId switch
        {
            0 => new TransportChannel(0, TransportDelivery.Reliable, TransportOrdering.Ordered),
            1 => new TransportChannel(1, TransportDelivery.Unreliable, TransportOrdering.Sequenced),
            2 => new TransportChannel(2, TransportDelivery.Unreliable, TransportOrdering.Sequenced),
            3 => new TransportChannel(3, TransportDelivery.Reliable, TransportOrdering.Ordered),
            _ => default,
        };

        return channelId <= 3;
    }
}

internal static class FantasyKcpGatewayBridge
{
    private static readonly ConcurrentDictionary<long, FantasyRealtimeTransport> ProbeTransports = new();
    private static FantasyKcpGateway? _active;

    public static void Activate(FantasyKcpGateway gateway)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        if (Interlocked.CompareExchange(ref _active, gateway, null) is not null)
        {
            throw new InvalidOperationException("Only one Fantasy KCP gateway can be active in a process.");
        }
    }

    public static void Deactivate(FantasyKcpGateway gateway) =>
        Interlocked.CompareExchange(ref _active, null, gateway);

    public static void MarkListening(Scene scene, int port) => Volatile.Read(ref _active)?.MarkListening(scene, port);

    public static void RegisterProbe(long sessionRuntimeId, FantasyRealtimeTransport transport)
    {
        if (!ProbeTransports.TryAdd(sessionRuntimeId, transport))
        {
            throw new InvalidOperationException($"Fantasy KCP probe session {sessionRuntimeId} is already registered.");
        }
    }

    public static void RemoveProbe(long sessionRuntimeId) => ProbeTransports.TryRemove(sessionRuntimeId, out _);

    public static void Deliver(Session session, uint channelId, ReadOnlySpan<byte> payload, ulong sequence)
    {
        if (ProbeTransports.TryGetValue(session.RuntimeId, out FantasyRealtimeTransport? probe) &&
            FantasyKcpGateway.TryMapChannel(channelId, out TransportChannel probeChannel))
        {
            _ = probe.TryEnqueueReceived(probeChannel, payload, sequence, connectionEpoch: 1);
            return;
        }

        Volatile.Read(ref _active)?.Deliver(session, channelId, payload, sequence);
    }
}
