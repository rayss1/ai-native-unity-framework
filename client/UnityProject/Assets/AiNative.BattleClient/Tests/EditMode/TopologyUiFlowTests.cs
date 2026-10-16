using System;
using System.Reflection;
using System.Threading.Tasks;
using AiNative.Client.Fantasy;
using NUnit.Framework;
using UnityEngine;

namespace AiNative.Client.Application.Tests
{
    public sealed class TopologyUiFlowTests
    {
        private GameObject _owner;
        private TopologyClientFlow _flow;

        [SetUp]
        public void SetUp()
        {
            _owner = new GameObject("TopologyUiFlowFixture");
            _flow = _owner.AddComponent<TopologyClientFlow>();
            _flow.Initialize(new GateConnectionOptions("127.0.0.1", 23001, false));
        }

        [TearDown]
        public void TearDown()
        {
            if (_owner != null) UnityEngine.Object.DestroyImmediate(_owner);
        }

        [Test]
        public async Task BackgroundRefresh_DoesNotDisableUiOrClearForegroundError()
        {
            SetProperty("Error", "invalid_credentials");
            var refresh = new TaskCompletionSource<bool>();
            Run(() => refresh.Task);
            Assert.That(_flow.UiBusy, Is.False, "Polling must not make the player ID and input controls flicker disabled.");
            Assert.That(_flow.Error, Is.EqualTo("invalid_credentials"));
            refresh.SetResult(true);
            await Idle();
            Assert.That(_flow.UiBusy, Is.False);
            Assert.That(_flow.Error, Is.EqualTo("invalid_credentials"), "A successful refresh must not erase actionable feedback.");
        }

        [Test]
        public async Task ClickDuringRefresh_IsQueuedOnceAndExecutedAfterRefresh()
        {
            var refresh = new TaskCompletionSource<bool>();
            var operation = new TaskCompletionSource<bool>();
            int calls = 0;
            Run(() => refresh.Task);
            Submit(() => { calls++; return operation.Task; });
            Submit(() => { calls += 100; return Task.CompletedTask; });
            Assert.That(calls, Is.Zero, "Backend requests must remain serialized.");
            Assert.That(_flow.UiBusy, Is.True);
            refresh.SetResult(true);
            await Until(() => calls == 1);
            Assert.That(_flow.UiBusy, Is.True, "The accepted operation is still awaiting its own response.");
            operation.SetResult(true);
            await Idle();
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(_flow.UiBusy, Is.False);
        }

        [Test]
        public async Task FailedForegroundOperation_ReleasesControlsAndKeepsErrorThroughRefresh()
        {
            Submit(() => Task.FromException(new GateCallException("invalid_credentials")));
            await Idle();
            Assert.That(_flow.UiBusy, Is.False);
            Assert.That(_flow.Error, Is.EqualTo("invalid_credentials"));
            Run(() => Task.CompletedTask);
            await Idle();
            Assert.That(_flow.Error, Is.EqualTo("invalid_credentials"));
            Assert.That(_flow.DisplayError, Is.Not.EqualTo("invalid_credentials"));
        }

        [Test]
        public async Task FailedBackgroundRefresh_StillExecutesAcceptedForegroundAction()
        {
            var refresh = new TaskCompletionSource<bool>();
            int calls = 0;
            Run(() => refresh.Task);
            Submit(() => { calls++; return Task.CompletedTask; });
            refresh.SetException(new TimeoutException());
            await Idle();
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(_flow.UiBusy, Is.False);
        }

        [TestCase("ab", "long-enough-password")]
        [TestCase("valid_user", "short")]
        [TestCase("valid_user", "123456789012345678901234567890123")]
        [TestCase("bad name", "long-enough-password")]
        [TestCase("中文账号", "long-enough-password")]
        public async Task InvalidCredentials_AreRejectedBeforeCreatingNetworkSession(string user, string password)
        {
            _flow.Authenticate(true, user, password, "127.0.0.1", "23001", false, "");
            await Idle();
            Assert.That(_flow.Error, Is.EqualTo("invalid_credentials"));
            Assert.That(_flow.State, Is.EqualTo("Disconnected"));
            Assert.That(Field("_backend"), Is.Null);
            Assert.That(Field("_password"), Is.EqualTo(""), "Invalid input should not be retained as a reconnect credential.");
        }

        [TestCase(6)]
        [TestCase(32)]
        public async Task ValidPasswordBoundariesReachAddressValidation(int length)
        {
            _flow.Authenticate(true, "valid_user", new string('a', length), "", "23001", false, "");
            await Idle();
            Assert.That(_flow.Error, Is.EqualTo("invalid-address"));
            Assert.That(Field("_backend"), Is.Null);
        }

        [TestCase("", "23001")]
        [TestCase("127.0.0.1", "0")]
        [TestCase("127.0.0.1", "65536")]
        [TestCase("127.0.0.1", "not-a-port")]
        public async Task InvalidAddress_AreRejectedBeforeCreatingNetworkSession(string host, string port)
        {
            _flow.Authenticate(false, "valid_user", "long-enough-password", host, port, false, "");
            await Idle();
            Assert.That(_flow.Error, Is.EqualTo("invalid-address"));
            Assert.That(_flow.State, Is.EqualTo("Disconnected"));
            Assert.That(Field("_backend"), Is.Null);
            Assert.That(_flow.ConnectionHost, Is.EqualTo("127.0.0.1"));
            Assert.That(_flow.ConnectionPort, Is.EqualTo("23001"));
        }

        [TestCase(false, false, false)]
        [TestCase(false, true, false)]
        [TestCase(true, true, true)]
        public async Task ReturnToLobby_RequiresConfirmedInteractiveSettlement(bool automated, bool completed, bool confirmed)
        {
            SetField("_automated", automated);
            SetProperty("Completed", completed);
            SetProperty("SettlementConfirmed", confirmed);
            SetProperty("State", "Settling");
            var battle = new BattleClientSession("127.0.0.1", 22000,
                new BattleAdmissionInfo("old-room", "old-boot", "ticket", "old-node", "old-player"));
            SetProperty("Battle", battle);
            _flow.ReturnToLobby();
            await Idle();
            Assert.That(_flow.Error, Is.EqualTo("state-changed"));
            Assert.That(_flow.Battle, Is.SameAs(battle), "An early return must preserve the original battle and receipt identity.");
            Assert.That(_flow.SettlementConfirmed, Is.EqualTo(confirmed));
        }

        [Test]
        public async Task ReturnToLobby_WithoutBattleDoesNotDiscardExistingPageState()
        {
            SetProperty("Completed", true);
            SetProperty("SettlementConfirmed", true);
            SetProperty("State", "Finished");
            _flow.ReturnToLobby();
            await Idle();
            Assert.That(_flow.Error, Is.EqualTo("state-changed"));
            Assert.That(_flow.State, Is.EqualTo("Finished"));
            Assert.That(_flow.Completed && _flow.SettlementConfirmed, Is.True);
        }

        [Test]
        public async Task DestroyDuringRefresh_DiscardsQueuedClickAndClearsCredential()
        {
            var refresh = new TaskCompletionSource<bool>();
            int calls = 0;
            SetField("_password", "fixture-password");
            Run(() => refresh.Task);
            Submit(() => { calls++; return Task.CompletedTask; });
            // Plain MonoBehaviours do not receive OnDestroy in this EditMode fixture.
            // PlayMode coverage separately verifies Unity invokes the lifecycle itself.
            typeof(TopologyClientFlow).GetMethod("OnDestroy", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(_flow, null);
            UnityEngine.Object.DestroyImmediate(_owner);
            _owner = null;
            Assert.That(Field("_pendingAction"), Is.Null);
            Assert.That(Field("_password"), Is.EqualTo(""));
            refresh.SetResult(true);
            await Idle();
            Assert.That(calls, Is.Zero, "Completing an old refresh must not dispatch a click after its owner is destroyed.");
        }

        private void Run(Func<Task> action) => typeof(TopologyClientFlow).GetMethod("Run", BindingFlags.NonPublic | BindingFlags.Instance)
            .Invoke(_flow, new object[] { action, false });
        private void Submit(Func<Task> action) => typeof(TopologyClientFlow).GetMethod("Submit", BindingFlags.NonPublic | BindingFlags.Instance)
            .Invoke(_flow, new object[] { action });
        private object Field(string name) => typeof(TopologyClientFlow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(_flow);
        private void SetField(string name, object value) => typeof(TopologyClientFlow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(_flow, value);
        private void SetProperty(string name, object value) => typeof(TopologyClientFlow).GetProperty(name).GetSetMethod(true).Invoke(_flow, new[] { value });
        private Task Idle() => Until(() => !(bool)Field("_busy"));
        private static async Task Until(Func<bool> condition)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(3);
            while (!condition() && DateTime.UtcNow < deadline) await Task.Yield();
            Assert.That(condition(), Is.True, "The pending UI operation did not complete within three seconds.");
        }
    }
}
