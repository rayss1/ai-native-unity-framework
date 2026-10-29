using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AiNative.Client.Assets.AssetBundle;
using AiNative.Client.Core.Mvvm;
using AiNative.Client.Fantasy;
using AiNative.Client.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Profiling;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace AiNative.Client.Application.PlayModeTests
{
    /// <summary>需要本机真实拓扑：Gate 明文 23001，双人匹配，1200 Tick 对局。</summary>
    public sealed class TopologyFrameworkEndToEndTests
    {
        private sealed class Client
        {
            public GameObject Owner;
            public TopologyClientFlow Flow;
            public FrameworkClientRoot Root;
            public TopologyClientView View;
            public LocalAssetBundleService Assets;
            public UiService Ui;
            public BindingScope Bindings;
            public BattleClientSession OwnedBattle;
            public string Username;
            public string Password;
            public string PlayerId;
            public string MatchId;
            public string RoomId;
            public long Played;
        }

        private readonly List<Client> _clients = new List<Client>();
        private readonly List<string> _unexpectedLogs = new List<string>();

        [SetUp]
        public void ObserveDiagnostics()
        {
            _unexpectedLogs.Clear();
            UnityEngine.Application.logMessageReceived += ObserveLog;
        }

        [UnityTearDown]
        public IEnumerator StopOwnedApplications()
        {
            try
            {
                foreach (Client client in _clients)
                {
                    if (client.Owner != null)
                    {
                        yield return Stop(client);
                    }
                }
            }
            finally
            {
                UnityEngine.Application.logMessageReceived -= ObserveLog;
                foreach (Client client in _clients)
                {
                    if (client.Owner != null)
                    {
                        Object.Destroy(client.Owner);
                    }
                }
                _clients.Clear();
            }
        }

        [UnityTest]
        public IEnumerator TwoInteractiveClientsUseBundlesUiReconnectSettlementAndReturnToLobby()
        {
            yield return RunMatchCycle();
        }

        [UnityTest]
        [Category("FrameworkSoak")]
        [Timeout(1200000)]
        public IEnumerator TwentyInteractiveMatchesReturnOwnedResourcesToBaseline()
        {
            int baselineViews = Object
                .FindObjectsByType<TopologyClientView>(
                    FindObjectsInactive.Include,
                    FindObjectsSortMode.None
                )
                .Length;
            int baselineEvents = Object
                .FindObjectsByType<EventSystem>(
                    FindObjectsInactive.Include,
                    FindObjectsSortMode.None
                )
                .Length;
            TestContext.Out.WriteLine(
                "FRAMEWORK_SOAK_CONFIG cycles=20; warmupMatches=1; players=2; matchTicks=1200; "
                    + "seed=server-defined; accountSuffix=random-guid; unity="
                    + UnityEngine.Application.unityVersion
                    + "; platform="
                    + UnityEngine.Application.platform
                    + "; OS="
                    + SystemInfo.operatingSystem
                    + "; CPU="
                    + SystemInfo.processorType
                    + "; RAM_MB="
                    + SystemInfo.systemMemorySize
                    + "; measurement=after-explicit-stop-and-destroy; GC=full-only-at-sample-boundary; "
                    + "not-a-frame-allocation-or-runtime-GC-performance-claim"
            );
            yield return RunMatchCycle();
            yield return null; // 完成 Destroy，并解除测试迭代器对上一轮局部对象的引用。
            RecordStoppedSample(0, baselineViews, baselineEvents);
            for (int cycle = 1; cycle <= 20; cycle++)
            {
                float started = Time.realtimeSinceStartup;
                yield return RunMatchCycle();
                yield return null;
                RecordStoppedSample(cycle, baselineViews, baselineEvents);
                TestContext.Out.WriteLine(
                    "FRAMEWORK_SOAK_DURATION cycle="
                        + cycle
                        + "; seconds="
                        + (Time.realtimeSinceStartup - started)
                );
            }
        }

        [UnityTest]
        [Category("FrameworkMemoryDiagnostics")]
        [Timeout(300000)]
        public IEnumerator TwoMatchesReportManagedMemoryAndDeferredFantasyCleanup()
        {
            // 独立短诊断；不改变生产释放时序，也不把 GC 估计值当作精确存活对象大小。
            RecordMemoryDiagnostic("idle-start", fullCollection: true);
            for (int frame = 0; frame < 60; frame++)
            {
                yield return null;
            }
            RecordMemoryDiagnostic("idle-after-60-frames", fullCollection: true);
            for (int cycle = 1; cycle <= 2; cycle++)
            {
                yield return RunMatchCycle();
                RecordMemoryDiagnostic("cycle-" + cycle + "-stopped", fullCollection: false);
                yield return null;
                yield return null;
                RecordMemoryDiagnostic("cycle-" + cycle + "-after-2-frames", fullCollection: true);
                for (int frame = 0; frame < 60; frame++)
                {
                    yield return null;
                }
                RecordMemoryDiagnostic("cycle-" + cycle + "-after-62-frames", fullCollection: true);
            }
        }

        [UnityTest]
        [Category("FrameworkMemoryIsolation")]
        [Timeout(120000)]
        public IEnumerator MemoryIsolationPrepareOnly()
        {
            // 首次确保目录已安装；采样窗口只覆盖复用安装目录的验证路径。
            yield return Await(PrepareDiagnosticContentAsync(), 30, "prepare diagnostic content");
            for (int round = 1; round <= 2; round++)
            {
                yield return SettleAndRecordMemory("prepare-only-" + round + "-before");
                yield return Await(
                    RepeatPrepareDiagnosticAsync(),
                    30,
                    "four installed validations"
                );
                yield return SettleAndRecordMemory("prepare-only-" + round + "-after");
            }
        }

        [UnityTest]
        [Category("FrameworkMemoryIsolation")]
        [Timeout(120000)]
        public IEnumerator MemoryIsolationUiOnly()
        {
            yield return Await(PrepareDiagnosticContentAsync(), 30, "prepare diagnostic content");
            for (int round = 1; round <= 2; round++)
            {
                yield return SettleAndRecordMemory("ui-only-" + round + "-before");
                Client first = Create("memory_ui_a", "unused");
                Client second = Create("memory_ui_b", "unused");
                yield return Initialize(first);
                yield return Initialize(second);
                // 不点击登录/注册，不匹配、不实例化角色，仅启动和关闭两份真实 Bundle UI。
                yield return Stop(first);
                yield return Stop(second);
                _clients.Clear();
                first = null;
                second = null;
                yield return SettleAndRecordMemory("ui-only-" + round + "-after");
            }
        }

        [UnityTest]
        [Category("FrameworkMemoryIsolation")]
        [Timeout(120000)]
        public IEnumerator MemoryIsolationCharacterOnly()
        {
            Task<string> preparation = PrepareDiagnosticContentAsync();
            yield return Await(preparation, 30, "prepare diagnostic content");
            string directory = preparation.GetAwaiter().GetResult();
            preparation = null;
            for (int round = 1; round <= 2; round++)
            {
                yield return SettleAndRecordMemory("character-only-" + round + "-before");
                yield return Await(
                    LoadDiagnosticCharactersAsync(directory),
                    30,
                    "two character loads"
                );
                yield return SettleAndRecordMemory("character-only-" + round + "-after");
            }
        }

        private static Task<string> PrepareDiagnosticContentAsync()
        {
            return LocalContentInstaller
                .PrepareAsync(
                    System.IO.Path.Combine(
                        UnityEngine.Application.streamingAssetsPath,
                        "AiNativeContent"
                    ),
                    System.IO.Path.Combine(
                        UnityEngine.Application.persistentDataPath,
                        "AiNativeContent"
                    ),
                    LocalAssetBundleService.CurrentPlatform,
                    CancellationToken.None
                )
                .AsTask();
        }

        private static async Task RepeatPrepareDiagnosticAsync()
        {
            for (int index = 0; index < 4; index++)
            {
                await PrepareDiagnosticContentAsync();
            }
        }

        private static async Task LoadDiagnosticCharactersAsync(string directory)
        {
            var first = new LocalAssetBundleService(
                directory,
                LocalAssetBundleService.CurrentPlatform
            );
            var second = new LocalAssetBundleService(
                directory,
                LocalAssetBundleService.CurrentPlatform
            );
            try
            {
                // 同时保留两个独立来源校验，与双客户端共享原生 Bundle 的真实路径一致。
                await Task.WhenAll(
                    first
                        .LoadAsync<GameObject>(
                            new AiNative.Client.Assets.AssetId("character.network")
                        )
                        .AsTask(),
                    second
                        .LoadAsync<GameObject>(
                            new AiNative.Client.Assets.AssetId("character.network")
                        )
                        .AsTask()
                );
            }
            finally
            {
                try
                {
                    await first.DisposeAsync();
                }
                finally
                {
                    await second.DisposeAsync();
                }
            }
            Assert.That(first.ActiveHandleCount + second.ActiveHandleCount, Is.Zero);
            Assert.That(first.LoadedBundleCount + second.LoadedBundleCount, Is.Zero);
        }

        private static IEnumerator SettleAndRecordMemory(string stage)
        {
            for (int frame = 0; frame < 62; frame++)
            {
                yield return null;
            }
            RecordMemoryDiagnostic(stage, fullCollection: true);
        }

        private static void RecordMemoryDiagnostic(string stage, bool fullCollection)
        {
            string ownership = ReadFantasyDiagnosticCounts();
            long estimateBefore = GC.GetTotalMemory(false);
            if (fullCollection)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            long estimateAfter = GC.GetTotalMemory(false);
            long monoUsed = Profiler.GetMonoUsedSizeLong();
            long monoHeap = Profiler.GetMonoHeapSizeLong();
            long unityAllocated = Profiler.GetTotalAllocatedMemoryLong();
            TestContext.Out.WriteLine(
                $"FRAMEWORK_MEMORY_DIAGNOSTIC stage={stage}; fullGc={fullCollection}; "
                    + $"gcEstimatedBefore={estimateBefore}; gcEstimatedAfter={estimateAfter}; "
                    + $"monoUsed={monoUsed}; monoHeapReserved={monoHeap}; unityAllocated={unityAllocated}; "
                    + ownership
            );
        }

        private static string ReadFantasyDiagnosticCounts()
        {
            // 仅测试反射观察固定版本内部计数；未发现字段必须报 unavailable，不伪装零。
            const BindingFlags staticFlags =
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags instanceFlags =
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type routerType = typeof(FantasyKcpRealtimeTransport).Assembly.GetType(
                "AiNative.Client.Fantasy.FantasyClientSessionRouter"
            );
            object routes = routerType?.GetField("_transports", staticFlags)?.GetValue(null);
            Type schedulerType = null;
            Type lifecycleType = null;
            Type sceneType = null;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                schedulerType ??= assembly.GetType("Fantasy.ThreadScheduler");
                lifecycleType ??= assembly.GetType("Fantasy.Assembly.AssemblyLifecycle");
                sceneType ??= assembly.GetType("Fantasy.Scene");
            }
            object scheduler = schedulerType
                ?.GetProperty("MainScheduler", staticFlags)
                ?.GetValue(null);
            object scenes = scheduler
                ?.GetType()
                .GetField("_queue", instanceFlags)
                ?.GetValue(scheduler);
            object context = scheduler
                ?.GetType()
                .GetField("ThreadSynchronizationContext", instanceFlags)
                ?.GetValue(scheduler);
            object queuedActions = context
                ?.GetType()
                .GetField("_queue", instanceFlags)
                ?.GetValue(context);
            object lifecycles = lifecycleType
                ?.GetField("AssemblyLifecycles", staticFlags)
                ?.GetValue(null);
            int disposedScheduled = 0;
            bool inspectedScenes = scenes is IEnumerable;
            if (scenes is IEnumerable sequence)
            {
                foreach (object scene in sequence)
                {
                    object disposed = scene
                        .GetType()
                        .GetProperty("IsDisposed", instanceFlags)
                        ?.GetValue(scene);
                    if (disposed is bool isDisposed && isDisposed)
                    {
                        disposedScheduled++;
                    }
                }
            }
            object sceneId = sceneType?.GetField("_unitySceneId", staticFlags)?.GetValue(null);
            return $"routerTransports={DiagnosticCount(routes)}; schedulerScenes={DiagnosticCount(scenes)}; "
                + $"disposedScheduled={(inspectedScenes ? disposedScheduled.ToString() : "unavailable")}; "
                + $"schedulerQueuedActions={DiagnosticCount(queuedActions)}; assemblyLifecycles={DiagnosticCount(lifecycles)}; "
                + $"unitySceneIdCounter={sceneId ?? "unavailable"}";
        }

        private static string DiagnosticCount(object collection)
        {
            if (collection == null)
            {
                return "unavailable";
            }
            object value = collection.GetType().GetProperty("Count")?.GetValue(collection);
            return value?.ToString() ?? "unavailable";
        }

        private IEnumerator RunMatchCycle()
        {
            string suffix = Guid.NewGuid().ToString("N").Substring(0, 16);
            Client first = Create("framework_a_" + suffix, Guid.NewGuid().ToString("N"));
            Client second = Create("framework_b_" + suffix, Guid.NewGuid().ToString("N"));
            yield return Initialize(first);
            yield return Initialize(second);
            FillCredentials(first);
            FillCredentials(second);
            Click(first.View.Register, "first registration");
            Click(second.View.Register, "second registration");
            yield return Until(
                () => InLobby(first) && InLobby(second),
                20,
                "registration",
                first,
                second
            );
            first.PlayerId = first.Flow.PlayerId;
            second.PlayerId = second.Flow.PlayerId;
            Assert.That(first.PlayerId, Is.Not.EqualTo(second.PlayerId));

            // 用全新应用根重新登录，覆盖真实账号持久化及关闭后 Bundle 重新加载。
            yield return Stop(first);
            yield return Stop(second);
            Client registeredFirst = first;
            Client registeredSecond = second;
            first = Create(registeredFirst.Username, registeredFirst.Password);
            second = Create(registeredSecond.Username, registeredSecond.Password);
            yield return Initialize(first);
            yield return Initialize(second);
            FillCredentials(first);
            FillCredentials(second);
            Click(first.View.Login, "first login");
            Click(second.View.Login, "second login");
            yield return Until(() => InLobby(first) && InLobby(second), 20, "login", first, second);
            Assert.That(first.Flow.PlayerId, Is.EqualTo(registeredFirst.PlayerId));
            Assert.That(second.Flow.PlayerId, Is.EqualTo(registeredSecond.PlayerId));

            Click(first.View.Create, "first create party");
            Click(second.View.Create, "second create party");
            yield return Until(
                () => PartyReadyForInput(first) && PartyReadyForInput(second),
                15,
                "create party",
                first,
                second
            );
            Click(first.View.Ready, "first ready");
            Click(second.View.Ready, "second ready");
            yield return Until(
                () => CanMatch(first) && CanMatch(second),
                15,
                "ready",
                first,
                second
            );
            Click(first.View.Match, "first match");
            Click(second.View.Match, "second match");
            yield return Until(
                () => Active(first) && Active(second),
                30,
                "battle admission",
                first,
                second
            );
            CaptureIdentity(first);
            CaptureIdentity(second);
            Assert.That(first.MatchId, Is.EqualTo(second.MatchId));
            Assert.That(first.RoomId, Is.EqualTo(second.RoomId));
            Assert.That(first.View.Pages[3].activeSelf && second.View.Pages[3].activeSelf, Is.True);

            // UI 按钮续签原分配并重建连接；原会话 RequestReconnect 路径由既有自动联机测试覆盖。
            Click(first.View.Reconnect, "first reconnect");
            Click(second.View.Reconnect, "second reconnect");
            yield return Until(
                () => Resumed(first) && Resumed(second),
                15,
                "original allocation reconnect",
                first,
                second
            );
            AssertIdentity(first);
            AssertIdentity(second);
            yield return Until(
                () => Settled(first) && Settled(second),
                60,
                "persisted settlement",
                first,
                second
            );
            AssertIdentity(first);
            AssertIdentity(second);
            Assert.That(first.Flow.Profile.Played, Is.EqualTo(first.Played + 1));
            Assert.That(second.Flow.Profile.Played, Is.EqualTo(second.Played + 1));
            Assert.That(first.View.Pages[4].activeSelf && second.View.Pages[4].activeSelf, Is.True);
            Assert.That(first.View.ResultTitle.text, Does.Contain("已保存"));
            Assert.That(second.View.ResultTitle.text, Does.Contain("已保存"));

            Click(first.View.Back, "first return to lobby");
            Click(second.View.Back, "second return to lobby");
            yield return Until(
                () => Returned(first) && Returned(second),
                20,
                "return to lobby",
                first,
                second
            );
            Assert.That(first.Flow.PlayerId, Is.EqualTo(first.PlayerId));
            Assert.That(second.Flow.PlayerId, Is.EqualTo(second.PlayerId));
            Assert.That(first.Flow.Profile.Played, Is.EqualTo(first.Played + 1));
            Assert.That(second.Flow.Profile.Played, Is.EqualTo(second.Played + 1));
            Assert.That(first.Flow.MatchId, Is.Empty);
            Assert.That(second.Flow.MatchId, Is.Empty);
            yield return Stop(first);
            yield return Stop(second);
            Assert.That(_unexpectedLogs, Is.Empty, string.Join("\n", _unexpectedLogs));
            // 每轮清掉测试自身保留的根、视图、服务和凭据；避免把测试持有误判成产品泄漏。
            _clients.Clear();
        }

        private void RecordStoppedSample(int cycle, int baselineViews, int baselineEvents)
        {
            int views = Object
                .FindObjectsByType<TopologyClientView>(
                    FindObjectsInactive.Include,
                    FindObjectsSortMode.None
                )
                .Length;
            int eventSystems = Object
                .FindObjectsByType<EventSystem>(
                    FindObjectsInactive.Include,
                    FindObjectsSortMode.None
                )
                .Length;
            Assert.That(
                views,
                Is.EqualTo(baselineViews),
                "Owned views must return to baseline after cycle " + cycle
            );
            Assert.That(
                eventSystems,
                Is.EqualTo(baselineEvents),
                "Owned EventSystems must return to baseline after cycle " + cycle
            );
            Assert.That(_clients, Is.Empty);
            // 只在完整停止后的测量边界执行完整 GC；这些趋势不代表 Tick 分配或产品运行时 GC 策略。
            long managedBeforeGc = GC.GetTotalMemory(false);
            long managedLiveAfterGc = GC.GetTotalMemory(true);
            long unityAllocated = Profiler.GetTotalAllocatedMemoryLong();
            TestContext.Out.WriteLine(
                "FRAMEWORK_SOAK_SAMPLE cycle="
                    + cycle
                    + "; managedBeforeGc="
                    + managedBeforeGc
                    + "; managedLiveAfterGc="
                    + managedLiveAfterGc
                    + "; unityAllocated="
                    + unityAllocated
                    + "; views="
                    + views
                    + "; eventSystems="
                    + eventSystems
                    + "; ownedHandles=0; ownedBundles=0; ownedPages=0; ownedBindings=0; ownedBattleSessions=0; retainedFixtureClients=0"
            );
        }

        private Client Create(string username, string password)
        {
            var owner = new GameObject("InteractiveFrameworkFixture");
            var flow = owner.AddComponent<TopologyClientFlow>();
            flow.Initialize(new GateConnectionOptions("127.0.0.1", 23001, false));
            var client = new Client
            {
                Owner = owner,
                Flow = flow,
                Root = FrameworkClientRoot.GetOrCreate(owner),
                Username = username,
                Password = password,
            };
            _clients.Add(client);
            return client;
        }

        private IEnumerator Initialize(Client client)
        {
            yield return Await(
                client.Root.StartAsync().AsTask(),
                30,
                "explicit application startup"
            );
            yield return Until(
                () => View(client.Flow) != null,
                15,
                "real Bundle UI initialization",
                client
            );
            client.View = View(client.Flow);
            client.Bindings = (BindingScope)
                typeof(TopologyClientView)
                    .GetField("_bindings", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(client.View);
            Task<AiNative.Client.Assets.IAssetService> assets = client
                .Root.AssetsAsync(CancellationToken.None)
                .AsTask();
            yield return Await(assets, 10, "resource service");
            client.Assets = assets.Result as LocalAssetBundleService;
            Assert.That(client.Assets, Is.Not.Null, "UI must use the real local Bundle service.");
            Task<UiService> ui = client.Root.UiAsync(CancellationToken.None).AsTask();
            yield return Await(ui, 10, "UI service");
            client.Ui = ui.Result;
            Assert.That(client.Assets.ActiveHandleCount, Is.GreaterThan(0));
            Assert.That(client.Assets.LoadedBundleCount, Is.GreaterThan(0));
            Assert.That(client.Ui.OpenCount, Is.EqualTo(1));
            Assert.That(client.View.Pages[0].activeSelf, Is.True);
        }

        private static void FillCredentials(Client client)
        {
            client.View.Username.text = client.Username;
            client.View.Password.text = client.Password;
            client.View.Host.text = "127.0.0.1";
            client.View.Port.text = "23001";
            client.View.Tls.isOn = false;
        }

        private static void Click(Button button, string operation)
        {
            Assert.That(
                button.gameObject.activeInHierarchy,
                Is.True,
                operation + " button must be visible."
            );
            Assert.That(button.IsInteractable(), Is.True, operation + " button must be enabled.");
            button.onClick.Invoke();
        }

        private static bool InLobby(Client client) =>
            client.Flow.State == "Lobby" && !client.Flow.UiBusy && client.View.Pages[1].activeSelf;

        private static bool PartyReadyForInput(Client client) =>
            client.Flow.Party != null
            && !client.Flow.UiBusy
            && client.View.Ready.gameObject.activeInHierarchy;

        private static bool CanMatch(Client client) =>
            client.Flow.Party.Members[0].Ready
            && !client.Flow.UiBusy
            && client.View.Match.IsInteractable();

        private static bool Active(Client client) =>
            client.Flow.Battle != null
            && client.Flow.Battle.State == BattleClientState.Active
            && client.Flow.Battle.IsPredictionInitialized
            && client.Flow.Battle.LastAcknowledgedSequence > 0
            && client.View.Pages[3].activeSelf
            && !client.Flow.UiBusy;

        private static bool Resumed(Client client) =>
            Active(client) && client.Flow.ResumeCompletedCount > 0;

        private static bool Settled(Client client) =>
            client.Flow.Completed
            && client.Flow.SettlementConfirmed
            && client.View.Pages[4].activeSelf
            && client.View.Back.IsInteractable();

        private static bool Returned(Client client) =>
            InLobby(client)
            && client.Flow.Battle == null
            && !client.Flow.Completed
            && !client.Flow.SettlementConfirmed;

        private static void CaptureIdentity(Client client)
        {
            client.OwnedBattle = client.Flow.Battle;
            client.PlayerId = client.Flow.PlayerId;
            client.MatchId = client.Flow.MatchId;
            client.RoomId = client.Flow.RoomId;
            client.Played = client.Flow.Profile.Played;
        }

        private static void AssertIdentity(Client client)
        {
            Assert.That(client.Flow.PlayerId, Is.EqualTo(client.PlayerId));
            Assert.That(client.Flow.MatchId, Is.EqualTo(client.MatchId));
            Assert.That(client.Flow.RoomId, Is.EqualTo(client.RoomId));
            // UI 续签重建传输会话；必须保持原业务分配，而非强制复用连接层标识。
            Assert.That(client.Flow.Battle.SessionId, Is.Not.Zero);
            Assert.That(client.Flow.Battle.EntityId, Is.Not.Zero);
        }

        private static IEnumerator Stop(Client client)
        {
            yield return Await(
                client.Root.StopAsync().AsTask(),
                15,
                "explicit application cleanup"
            );
            if (client.Assets != null)
            {
                Assert.That(client.Assets.ActiveHandleCount, Is.Zero);
                Assert.That(client.Assets.LoadedBundleCount, Is.Zero);
                Assert.That(client.Ui.OpenCount, Is.Zero);
                var bindings = (ICollection)
                    typeof(BindingScope)
                        .GetField("_bindings", BindingFlags.Instance | BindingFlags.NonPublic)
                        .GetValue(client.Bindings);
                Assert.That(
                    bindings.Count,
                    Is.Zero,
                    "View-owned binding subscriptions must be released."
                );
                if (client.OwnedBattle != null)
                {
                    Assert.That(client.OwnedBattle.State, Is.EqualTo(BattleClientState.Disposed));
                }
                Assert.That(
                    client.View == null,
                    Is.True,
                    "View must be destroyed before Bundle handles release."
                );
            }
            Object.Destroy(client.Owner);
            client.Owner = null;
        }

        private IEnumerator Until(
            Func<bool> completed,
            float timeoutSeconds,
            string phase,
            params Client[] clients
        )
        {
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (!completed() && Time.realtimeSinceStartup < deadline)
            {
                foreach (Client client in clients)
                {
                    Assert.That(client.Flow.Error, Is.Empty, Describe(client, phase));
                }
                Assert.That(_unexpectedLogs, Is.Empty, string.Join("\n", _unexpectedLogs));
                yield return null;
            }
            foreach (Client client in clients)
            {
                Assert.That(client.Flow.Error, Is.Empty, Describe(client, phase));
            }
            Assert.That(
                completed(),
                Is.True,
                phase
                    + " timed out: "
                    + string.Join("; ", Array.ConvertAll(clients, c => Describe(c, phase)))
            );
        }

        private static IEnumerator Await(Task operation, float timeoutSeconds, string phase)
        {
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (!operation.IsCompleted && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }
            Assert.That(operation.IsCompleted, Is.True, phase + " timed out.");
            operation.GetAwaiter().GetResult();
        }

        private static string Describe(Client client, string phase) =>
            phase
            + ": state="
            + client.Flow.State
            + ", error="
            + client.Flow.Error
            + ", backend="
            + client.Flow.BackendStatus
            + ", tick="
            + client.Flow.Battle?.LastReceivedTick;

        private static TopologyClientView View(TopologyClientFlow flow) =>
            (TopologyClientView)
                typeof(TopologyClientFlow)
                    .GetField("_view", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(flow);

        private void ObserveLog(string message, string stack, LogType type)
        {
            if (
                type == LogType.Warning
                || type == LogType.Error
                || type == LogType.Exception
                || type == LogType.Assert
            )
            {
                _unexpectedLogs.Add(type + ": " + message);
            }
        }
    }
}
