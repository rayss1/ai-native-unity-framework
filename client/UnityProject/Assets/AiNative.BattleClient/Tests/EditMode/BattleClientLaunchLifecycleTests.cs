using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace AiNative.Client.Application.Tests
{
    public sealed class BattleClientLaunchLifecycleTests
    {
        [Test]
        public void AwakeDoesNotConfigureLaunchOrCreateSession()
        {
            var owner = new GameObject("launch-awake-test");
            try
            {
                var battle = owner.AddComponent<BattleClientCompositionRoot>();
                typeof(BattleClientCompositionRoot)
                    .GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(battle, null);
                Assert.That(Read<bool>(battle, "_launchConfigured"), Is.False);
                Assert.That(battle.Session, Is.Null);
                Assert.That(owner.GetComponent<TopologyClientFlow>(), Is.Null);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(owner);
            }
        }

        [Test]
        public void InvalidConfigurationCanFailTwiceThenAcceptValidArguments()
        {
            var owner = new GameObject("launch-retry-test");
            owner.SetActive(false);
            bool background = global::UnityEngine.Application.runInBackground;
            int targetFrameRate = global::UnityEngine.Application.targetFrameRate;
            int syncCount = QualitySettings.vSyncCount;
            try
            {
                var battle = owner.AddComponent<BattleClientCompositionRoot>();
                string[] invalid = { "--ainative-smoke", "--ainative-regional-correction" };
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    Assert.Throws<ArgumentException>(() => battle.ConfigureLaunch(invalid));
                    Assert.That(Read<bool>(battle, "_launchConfigured"), Is.False);
                    Assert.That(battle.Session, Is.Null);
                    Assert.That(owner.GetComponent<TopologyClientFlow>(), Is.Null);
                }
                battle.ConfigureLaunch(
                    new[] { "--ainative-host", "127.0.0.2", "--ainative-port", "22400" }
                );
                Assert.That(Read<bool>(battle, "_launchConfigured"), Is.True);
                BattleClientLaunchOptions launch = Read<BattleClientLaunchOptions>(
                    battle,
                    "_launch"
                );
                Assert.That(launch.Host, Is.EqualTo("127.0.0.2"));
                Assert.That(launch.Port, Is.EqualTo(22400));
                Assert.That(battle.Session, Is.Null, "配置完成仍不得提前启动网络会话。");
            }
            finally
            {
                global::UnityEngine.Application.runInBackground = background;
                global::UnityEngine.Application.targetFrameRate = targetFrameRate;
                QualitySettings.vSyncCount = syncCount;
                UnityEngine.Object.DestroyImmediate(owner);
            }
        }

        private static T Read<T>(BattleClientCompositionRoot battle, string field)
        {
            return (T)
                typeof(BattleClientCompositionRoot)
                    .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(battle);
        }
    }
}
