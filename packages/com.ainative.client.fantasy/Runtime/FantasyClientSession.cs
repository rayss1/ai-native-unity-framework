using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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
        object RoutingIdentity { get; }

        bool IsClosed { get; }

        void Post(Action action);
        void Send(FantasyRealtimeEnvelope envelope);
    }

    internal interface IFantasyClientConnector
    {
        Task<IFantasyClientSession> ConnectAsync(
            FantasyKcpTransportOptions options,
            Action disconnected,
            CancellationToken cancellationToken
        );
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
        public object RoutingIdentity => _session;
        public bool IsClosed => Volatile.Read(ref _disposed) != 0 || _session.IsDisposed;

        public void Post(Action action) => _session.Scene.ThreadSynchronizationContext.Post(action);

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
        private static readonly SemaphoreSlim _initializationGate = new SemaphoreSlim(1, 1);
        private static int _initialized;

        private FantasyClientConnector() { }

        public Task<IFantasyClientSession> ConnectAsync(
            FantasyKcpTransportOptions options,
            Action disconnected,
            CancellationToken cancellationToken
        )
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
                    TaskCreationOptions.RunContinuationsAsynchronously
                );
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
                CancellationToken cancellationToken
            )
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
                    // Fantasy.Runtime 是进程级 Unity 单例；创建隔离客户端 Scene 前，
                    // 先释放旧 Session 及其所属 Scene。
                    // 隔离 Scene 直接接收回调，因此旧全局 Session 的迟到回调
                    //不得进入替换后的传输实例。
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
                        enableReceiveMessageJsonLog: false
                    );
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
                    new InvalidOperationException("Fantasy KCP connection failed.")
                );
            }

            private void CompleteConnected()
            {
                Volatile.Write(ref _connectCallbackReceived, 1);
                if (
                    _scene == null
                    || _session == null
                    || Interlocked.CompareExchange(ref _adapterCreated, 1, 0) != 0
                )
                {
                    return;
                }

                try
                {
                    // Scene.Connect 会绕过 FantasyRuntime 的自动心跳初始化。
                    // 连接回调和 Scene.Create 的继续执行都在所属 Scene 线程上运行。
                    // 将心跳绑定到此 Session，使释放会话时一并
                    // 移除心跳定时器；沿用 FantasyRuntime 固定默认值。
                    if (_session.GetComponent<SessionHeartbeatComponent>() == null)
                    {
                        _session
                            .AddComponent<SessionHeartbeatComponent>()
                            .Start(interval: 2000, timeOut: 30000, timeOutInterval: 5000);
                    }

                    var adapter = new FantasyClientSession(_scene, _session);
                    if (!_completion.TrySetResult(adapter))
                    {
                        adapter.Dispose();
                    }
                }
                catch (Exception exception)
                {
                    DisposeScene();
                    _completion.TrySetException(exception);
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

                await _initializationGate.WaitAsync();
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
                    _initializationGate.Release();
                }
            }
        }
    }

    internal static class FantasyClientSessionRouter
    {
        // RuntimeId 只在所属 Fantasy Scene 内唯一；对象身份避免独立客户端与重连场景冲突。
        private sealed class SessionIdentityComparer : IEqualityComparer<object>
        {
            public new bool Equals(object left, object right) => ReferenceEquals(left, right);

            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }

        private static readonly ConcurrentDictionary<
            object,
            FantasyKcpRealtimeTransport
        > _transports = new ConcurrentDictionary<object, FantasyKcpRealtimeTransport>(
            new SessionIdentityComparer()
        );

        internal static bool Register(
            object sessionIdentity,
            FantasyKcpRealtimeTransport transport
        ) => _transports.TryAdd(sessionIdentity, transport);

        internal static void Remove(object sessionIdentity, FantasyKcpRealtimeTransport transport)
        {
            if (
                _transports.TryGetValue(sessionIdentity, out FantasyKcpRealtimeTransport current)
                && ReferenceEquals(current, transport)
            )
            {
                _transports.TryRemove(sessionIdentity, out _);
            }
        }

        internal static bool Deliver(
            object sessionIdentity,
            uint channelId,
            ReadOnlySpan<byte> payload,
            ulong sequence
        )
        {
            return _transports.TryGetValue(
                    sessionIdentity,
                    out FantasyKcpRealtimeTransport transport
                ) && transport.TryEnqueueReceived(channelId, payload, sequence);
        }
    }
}
