using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using global::Fantasy;
using Fantasy.Helper;
using Fantasy.Network;
using Fantasy.Network.KCP;
using Fantasy.Platform.Unity;

namespace AiNative.Client.Fantasy
{
    internal interface IFantasyClientSession : IDisposable
    {
        long RuntimeId { get; }

        bool IsClosed { get; }

        void Post(Action action);

        void Send(FantasyRealtimeEnvelope envelope);
    }

    internal interface IFantasyClientConnector
    {
        Task<IFantasyClientSession> ConnectAsync(
            FantasyKcpTransportOptions options,
            Action disconnected,
            CancellationToken cancellationToken);
    }

    internal sealed class FantasyClientSession : IFantasyClientSession
    {
        private readonly Session _session;
        private readonly Scene _scene;
        private int _disposed;

        internal FantasyClientSession(Scene scene, Session session)
        {
            _scene = scene ?? throw new ArgumentNullException(nameof(scene));
            _session = session ?? throw new ArgumentNullException(nameof(session));
        }

        public long RuntimeId => _session.RuntimeId;

        public bool IsClosed => Volatile.Read(ref _disposed) != 0 || _session.IsDisposed;

        public void Post(Action action) =>
            _session.Scene.ThreadSynchronizationContext.Post(action);

        public void Send(FantasyRealtimeEnvelope envelope) => _session.Send(envelope);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0 || _scene.IsDisposed)
            {
                return;
            }

            try
            {
                _scene.ThreadSynchronizationContext.Post(() =>
                {
                    if (!_scene.IsDisposed)
                    {
                        _scene.Dispose();
                    }
                });
            }
            catch
            {
                _scene.Dispose();
            }
        }
    }

    internal sealed class FantasyClientConnector : IFantasyClientConnector
    {
        internal static readonly FantasyClientConnector Instance = new FantasyClientConnector();
        private static readonly SemaphoreSlim InitializationGate = new SemaphoreSlim(1, 1);
        private static int _initialized;

        private FantasyClientConnector()
        {
        }

        public Task<IFantasyClientSession> ConnectAsync(
            FantasyKcpTransportOptions options,
            Action disconnected,
            CancellationToken cancellationToken)
        {
            KCPSettings.ConfigureOuterMtu(FantasyKcpRealtimeTransport.OuterKcpMtu);
            typeof(FantasyRealtimeEnvelope).Assembly.EnsureLoaded();
            var attempt = new ConnectAttempt(options, disconnected, cancellationToken);
            attempt.Start();
            return attempt.Task;
        }

        private sealed class ConnectAttempt
        {
            private readonly TaskCompletionSource<IFantasyClientSession> _completion =
                new TaskCompletionSource<IFantasyClientSession>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly Action _disconnected;
            private readonly FantasyKcpTransportOptions _options;
            private readonly CancellationToken _cancellationToken;
            private CancellationTokenRegistration _registration;
            private int _adapterCreated;
            private int _connectCallbackReceived;
            private Scene _scene;
            private Session _session;

            internal ConnectAttempt(
                FantasyKcpTransportOptions options,
                Action disconnected,
                CancellationToken cancellationToken)
            {
                _options = options;
                _disconnected = disconnected;
                _cancellationToken = cancellationToken;
            }

            internal Task<IFantasyClientSession> Task => AwaitAndReleaseRegistrationAsync();

            internal void Start()
            {
                _registration = _cancellationToken.Register(Cancel);
                BeginConnect();
            }

            private async void BeginConnect()
            {
                try
                {
                    // Fantasy.Runtime is a process-wide Unity singleton. Release the old
                    // Session and its owning Scene before creating an isolated client Scene.
                    // The isolated Scene receives direct callbacks, so a late callback from
                    // the old global Session cannot target the replacement transport.
                    global::Fantasy.Runtime.OnDestroy();
                    await EnsureInitializedAsync();
                    _scene = await Scene.Create();
                    _session = _scene.Connect(
                        string.Concat(_options.Host, ":", _options.Port),
                        NetworkProtocolType.KCP,
                        onConnectComplete: CompleteConnected,
                        onConnectFail: Fail,
                        onConnectDisconnect: _disconnected,
                        isHttps: false,
                        connectTimeout: _options.ConnectTimeoutMilliseconds,
                        enableReceiveMessageJsonLog: false);
                    if (Volatile.Read(ref _connectCallbackReceived) != 0)
                    {
                        CompleteConnected();
                    }

                    if (_cancellationToken.IsCancellationRequested || _completion.Task.IsFaulted)
                    {
                        DisposeScene();
                    }
                }
                catch (Exception exception)
                {
                    DisposeScene();
                    _completion.TrySetException(exception);
                }
            }

            private void Cancel()
            {
                DisposeScene();
                _completion.TrySetCanceled();
            }

            private void Fail()
            {
                DisposeScene();
                _completion.TrySetException(
                    new InvalidOperationException("Fantasy KCP connection failed."));
            }

            private void CompleteConnected()
            {
                Volatile.Write(ref _connectCallbackReceived, 1);
                if (_scene == null || _session == null ||
                    Interlocked.CompareExchange(ref _adapterCreated, 1, 0) != 0)
                {
                    return;
                }

                var adapter = new FantasyClientSession(_scene, _session);
                if (!_completion.TrySetResult(adapter))
                {
                    adapter.Dispose();
                }
            }

            private void DisposeScene()
            {
                Scene scene = _scene;
                if (scene == null || scene.IsDisposed)
                {
                    return;
                }

                try
                {
                    scene.ThreadSynchronizationContext.Post(() =>
                    {
                        if (!scene.IsDisposed)
                        {
                            scene.Dispose();
                        }
                    });
                }
                catch
                {
                    scene.Dispose();
                }
            }

            private async Task<IFantasyClientSession> AwaitAndReleaseRegistrationAsync()
            {
                try
                {
                    return await _completion.Task.ConfigureAwait(false);
                }
                finally
                {
                    _registration.Dispose();
                }
            }

            private static async Task EnsureInitializedAsync()
            {
                if (Volatile.Read(ref _initialized) != 0)
                {
                    return;
                }

                await InitializationGate.WaitAsync();
                try
                {
                    if (Volatile.Read(ref _initialized) == 0)
                    {
                        await Entry.Initialize();
                        Volatile.Write(ref _initialized, 1);
                    }
                }
                finally
                {
                    InitializationGate.Release();
                }
            }
        }
    }

    internal static class FantasyClientSessionRouter
    {
        private static readonly ConcurrentDictionary<long, FantasyKcpRealtimeTransport> Transports =
            new ConcurrentDictionary<long, FantasyKcpRealtimeTransport>();

        internal static bool Register(long sessionRuntimeId, FantasyKcpRealtimeTransport transport) =>
            Transports.TryAdd(sessionRuntimeId, transport);

        internal static void Remove(long sessionRuntimeId, FantasyKcpRealtimeTransport transport)
        {
            if (Transports.TryGetValue(sessionRuntimeId, out FantasyKcpRealtimeTransport current) &&
                ReferenceEquals(current, transport))
            {
                Transports.TryRemove(sessionRuntimeId, out _);
            }
        }

        internal static bool Deliver(
            long sessionRuntimeId,
            uint channelId,
            ReadOnlySpan<byte> payload,
            ulong sequence)
        {
            return Transports.TryGetValue(sessionRuntimeId, out FantasyKcpRealtimeTransport transport) &&
                transport.TryEnqueueReceived(channelId, payload, sequence);
        }
    }
}
