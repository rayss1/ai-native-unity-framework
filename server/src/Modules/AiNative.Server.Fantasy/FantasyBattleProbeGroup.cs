using System.Xml;
using System.Xml.Linq;
using global::Fantasy;
using Fantasy.Entitas;
using Fantasy.IdFactory;
using Fantasy.Network;
using Fantasy.Network.Interface;
using Fantasy.Network.KCP;
using Fantasy.Platform.Net;
using AiNative.Server.Control;

namespace AiNative.Server.Fantasy;

// Qualification clients only. A room's eight clients share this context; another
// room's socket initialization cannot stop their input/message pump.
internal sealed class FantasyBattleProbeGroup : IAsyncDisposable
{
    private readonly Scene _scene;
    private readonly Task<Thread> _thread;
    private readonly Action<FantasyBattleProbeGroup> _released;
    private readonly object _ownership = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly HashSet<Task<FantasyKcpProbe>> _connecting = [];
    private readonly List<FantasyKcpProbe> _probes = [];
    private Task? _disposeTask;
    private int _attempts;
    private bool _closing;

    internal FantasyBattleProbeGroup(Scene scene, Action<FantasyBattleProbeGroup> released)
    {
        _scene = scene; _released = released;
        var thread = new TaskCompletionSource<Thread>(TaskCreationOptions.RunContinuationsAsynchronously);
        scene.ThreadSynchronizationContext.Post(() => thread.TrySetResult(Thread.CurrentThread));
        _thread = thread.Task;
    }

    internal Task<FantasyKcpProbe> ConnectAsync(string endpoint, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_ownership)
        {
            if (_closing) throw new ObjectDisposedException(nameof(FantasyBattleProbeGroup));
            if (_attempts >= 8) throw new InvalidOperationException("probe-group-client-limit");
            _attempts++;
            Task<FantasyKcpProbe> connecting = ConnectCoreAsync(endpoint, ct);
            _connecting.Add(connecting);
            _ = connecting.ContinueWith(completed =>
            {
                lock (_ownership) _connecting.Remove(completed);
                if (completed.IsFaulted) _ = completed.Exception;
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return connecting;
        }
    }

    private async Task<FantasyKcpProbe> ConnectCoreAsync(string endpoint, CancellationToken ct)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        cancellation.Token.ThrowIfCancellationRequested();
        var connected = new TaskCompletionSource<FantasyKcpProbe>(TaskCreationOptions.RunContinuationsAsynchronously);
        KCPClientNetwork? network = null;
        _scene.ThreadSynchronizationContext.Post(() =>
        {
            if (connected.Task.IsCompleted || _scene.IsDisposed) return;
            try
            {
                network = Entity.Create<KCPClientNetwork>(_scene, false, true);
                network.Initialize(NetworkTarget.Outer, enableReceiveMessageJsonLog: false);
                FantasyProbeConnection.Begin<Session, FantasyKcpProbe>(complete => network.Connect(endpoint,
                    onConnectComplete: complete,
                    onConnectFail: () => connected.TrySetException(new ServiceException("unavailable")),
                    onConnectDisconnect: () => { }, isHttps: false, connectTimeout: 5000),
                    session => new(session), connected, probe => probe.DisposeAsync().GetAwaiter().GetResult());
            }
            catch (Exception) { network?.Dispose(); connected.TrySetException(new ServiceException("unavailable")); }
        });
        FantasyKcpProbe probe = await FantasyProbeConnection.WaitOwnedAsync(connected, TimeSpan.FromSeconds(10), cancellation.Token,
            () => FantasyProbeConnection.OnSceneAsync(_scene, () => network?.Dispose()), owner => owner.DisposeAsync());
        lock (_ownership) _probes.Add(probe);
        return probe;
    }

    public ValueTask DisposeAsync()
    {
        lock (_ownership)
        {
            _closing = true;
            // Close may be requested on this Scene's own thread. Its Join must
            // execute elsewhere, and every caller shares the complete close.
            return new(_disposeTask ??= Task.Run(CloseAsync));
        }
    }

    private async Task CloseAsync()
    {
        _stop.Cancel();
        Task<FantasyKcpProbe>[] connecting;
        lock (_ownership) connecting = _connecting.ToArray();
        Task allConnections = Task.WhenAll(connecting);
        try { await allConnections.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false); }
        catch (Exception) when (allConnections.IsCompleted)
        {
            // Failed/cancelled connects retain network cleanup through WaitOwned.
            // A failed caller cannot prevent the group's remaining close.
            _ = allConnections.Exception;
        }
        FantasyKcpProbe[] probes;
        lock (_ownership) probes = _probes.ToArray();
        await Task.WhenAll(probes.Select(probe => probe.DisposeAsync().AsTask())).ConfigureAwait(false);
        Thread owner = await _thread.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await CloseSceneAsync(_scene).WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        if (!_scene.IsDisposed) throw new InvalidOperationException("probe-group-scene-not-closed");
        await Task.Run(() =>
        {
            if (owner.IsAlive && !owner.Join(TimeSpan.FromSeconds(10))) throw new TimeoutException("probe-group-thread-not-closed");
        }).ConfigureAwait(false);
        _stop.Dispose();
        // Do not release the bound until both the Scene and its actual thread end.
        _released(this);
    }

    private static async Task CloseSceneAsync(Scene scene) { if (!scene.IsDisposed) await scene.Close(); }
}

internal sealed class FantasyBattleProbeGroups : IAsyncDisposable
{
    private const int MaximumGroups = 32;
    private static int _nextSceneId = 19999;
    private readonly object _ownership = new();
    private readonly HashSet<FantasyBattleProbeGroup> _groups = [];
    private readonly HashSet<Task<FantasyBattleProbeGroup>> _creating = [];
    private bool _closing;
    private Task? _disposeTask;

    internal Task<FantasyBattleProbeGroup> CreateAsync(Scene root, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_ownership)
        {
            if (_closing) throw new ObjectDisposedException(nameof(FantasyBattleProbeGroups));
            if (_groups.Count + _creating.Count >= MaximumGroups) throw new InvalidOperationException("probe-group-limit");
            Task<FantasyBattleProbeGroup> creation = CreateCoreAsync(root, ct);
            _creating.Add(creation);
            _ = creation.ContinueWith(completed =>
            {
                lock (_ownership) _creating.Remove(completed);
                if (completed.IsFaulted) _ = completed.Exception;
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return creation;
        }
    }

    private async Task<FantasyBattleProbeGroup> CreateCoreAsync(Scene root, CancellationToken ct)
    {
        if (root.IsDisposed) throw new InvalidOperationException("runtime-not-ready");
        if (IdFactoryHelper.GetIdFactoryType() != IdFactoryType.Default) throw new InvalidOperationException("probe-group-requires-default-ids");
        // Dynamic transport-only Scenes must never join service discovery.
        using (XmlReader reader = XmlReader.Create(Path.Combine(AppContext.BaseDirectory, "Fantasy.config"),
            new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
        {
            XElement config = XElement.Load(reader);
            XElement? discovery = config.Element(config.Name.Namespace + "controlCenter");
            if (bool.Parse(discovery?.Attribute("enabled")?.Value ?? "false"))
                throw new InvalidOperationException("probe-group-requires-local-discovery-disabled");
        }
        SceneConfig source = SceneConfigData.Instance.Get(root.SceneConfigId);
        if (source.SceneTypeString != "AcceptanceClient") throw new InvalidOperationException("probe-group-requires-acceptance-client");
        uint id;
        do
        {
            int next = Interlocked.Increment(ref _nextSceneId);
            if (next > ushort.MaxValue) throw new InvalidOperationException("probe-group-scene-ids-exhausted");
            id = (uint)next;
        } while (SceneConfigData.Instance.TryGet(id, out _));
        SceneConfig sceneConfig = new()
        {
            Id = id, ProcessConfigId = root.Process.Id, WorldConfigId = 0,
            SceneRuntimeMode = SceneRuntimeMode.MultiThread, SceneType = source.SceneType,
            SceneTypeString = source.SceneTypeString, NetworkProtocol = "", InnerPort = 0, OuterPort = 0
        };
        sceneConfig.Initialize();
        // Await creation itself, including cancellation races. WaitAsync(ct)
        // here would lose a Scene still being initialized by the vendor.
        Scene scene = await Scene.Create(root.Process, MachineConfigData.Instance.Get(root.Process.MachineId), sceneConfig);
        FantasyBattleProbeGroup group = new(scene, Released);
        bool abandoned;
        lock (_ownership) { _groups.Add(group); abandoned = _closing || ct.IsCancellationRequested; }
        if (abandoned)
        {
            await group.DisposeAsync();
            ct.ThrowIfCancellationRequested();
            throw new ObjectDisposedException(nameof(FantasyBattleProbeGroups));
        }
        return group;
    }

    private void Released(FantasyBattleProbeGroup group) { lock (_ownership) _groups.Remove(group); }

    public ValueTask DisposeAsync()
    {
        lock (_ownership)
        {
            _closing = true;
            return new(_disposeTask ??= Task.Run(CloseAsync));
        }
    }

    private async Task CloseAsync()
    {
        Task<FantasyBattleProbeGroup>[] creating;
        lock (_ownership) creating = _creating.ToArray();
        Task allCreations = Task.WhenAll(creating);
        try { await allCreations.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false); }
        catch (Exception) when (allCreations.IsCompleted) { _ = allCreations.Exception; }
        FantasyBattleProbeGroup[] groups;
        lock (_ownership) groups = _groups.ToArray();
        await Task.WhenAll(groups.Select(group => group.DisposeAsync().AsTask())).ConfigureAwait(false);
    }
}
