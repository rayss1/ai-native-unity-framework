using System;
using System.IO;
using System.Reflection;
using System.Xml;
using AiNative.Client.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Build;

namespace AiNative.Client.Application.Tests
{
    public sealed class AndroidBattleClientBuildTests
    {
        [Test]
        public void ReleasePlan_BuildsOnlyBattleSceneAsNonDevelopmentArm64Apk()
        {
            var options = (BuildPlayerOptions)Builder().GetMethod("CreateBuildOptions", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { Path.Combine(Path.GetTempPath(), "battle.apk") });
            Assert.That(options.target, Is.EqualTo(BuildTarget.Android));
            Assert.That(options.targetGroup, Is.EqualTo(BuildTargetGroup.Android));
            Assert.That(options.options, Is.EqualTo(BuildOptions.None));
            Assert.That(options.scenes, Is.EqualTo(new[] { "Assets/AiNative.BattleClient/Scenes/BattleClient.unity" }));
            Assert.That(options.locationPathName, Does.EndWith("battle.apk"));
        }

        [Test]
        public void AndroidLinkerInput_PreservesPinnedTransportRegistrationAssemblies()
        {
            string path = (string)Builder().GetMethod("GeneratePreservationFile", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
            var xml = new XmlDocument(); xml.Load(path);
            Assert.That(xml.SelectSingleNode("/linker/assembly[@fullname='Fantasy.Unity' and @preserve='all']"), Is.Not.Null);
            Assert.That(xml.SelectSingleNode("/linker/assembly[@fullname='AiNative.Client.Fantasy' and @preserve='all']"), Is.Not.Null);
        }

        [Test]
        public void FailedReleaseBuild_RestoresPlayerAndToolchainSettings()
        {
            NamedBuildTarget android = NamedBuildTarget.Android;
            var before = (PlayerSettings.GetScriptingBackend(android), PlayerSettings.Android.targetArchitectures,
                PlayerSettings.Android.minSdkVersion, PlayerSettings.Android.targetSdkVersion,
                PlayerSettings.GetScriptingDefineSymbols(android), PlayerSettings.GetApplicationIdentifier(android), EditorUserBuildSettings.buildAppBundle);
            Type tools = Type.GetType("UnityEditor.Android.AndroidExternalToolsSettings, UnityEditor.Android.Extensions", true);
            string sdk = Tool(tools, "sdkRootPath"), ndk = Tool(tools, "ndkRootPath"), jdk = Tool(tools, "jdkRootPath");
            bool reachedBuild = false;
            var failure = new InvalidOperationException("fixture-build-failure");
            Exception observed = null;
            Action build = () => {
                reachedBuild = true;
                Assert.That(PlayerSettings.GetScriptingBackend(android), Is.EqualTo(ScriptingImplementation.IL2CPP));
                Assert.That(PlayerSettings.Android.targetArchitectures, Is.EqualTo(AndroidArchitecture.ARM64));
                Assert.That((int)PlayerSettings.Android.minSdkVersion, Is.EqualTo(25));
                Assert.That((int)PlayerSettings.Android.targetSdkVersion, Is.EqualTo(35));
                Assert.That(PlayerSettings.GetScriptingDefineSymbols(android).Split(';'), Does.Contain("FANTASY_UNITY"));
                Assert.That(PlayerSettings.GetApplicationIdentifier(android), Is.EqualTo("com.ainative.battleclient.validation"));
                Assert.That(EditorUserBuildSettings.buildAppBundle, Is.False);
                string bundled = Path.Combine(EditorApplication.applicationContentsPath, "PlaybackEngines", "AndroidPlayer");
                Assert.That(Path.GetFullPath(Tool(tools, "sdkRootPath")), Is.EqualTo(Path.GetFullPath(Path.Combine(bundled, "SDK"))));
                Assert.That(Path.GetFullPath(Tool(tools, "ndkRootPath")), Is.EqualTo(Path.GetFullPath(Path.Combine(bundled, "NDK"))));
                Assert.That(Path.GetFullPath(Tool(tools, "jdkRootPath")), Is.EqualTo(Path.GetFullPath(Path.Combine(bundled, "OpenJDK"))));
                throw failure;
            };
            try { Builder().GetMethod("WithReleaseSettings", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { "com.ainative.battleclient.validation", build }); }
            catch (TargetInvocationException exception) { observed = exception.InnerException; }
            Assert.That(reachedBuild, Is.True);
            Assert.That(observed, Is.SameAs(failure));
            Assert.That((PlayerSettings.GetScriptingBackend(android), PlayerSettings.Android.targetArchitectures,
                PlayerSettings.Android.minSdkVersion, PlayerSettings.Android.targetSdkVersion,
                PlayerSettings.GetScriptingDefineSymbols(android), PlayerSettings.GetApplicationIdentifier(android), EditorUserBuildSettings.buildAppBundle), Is.EqualTo(before));
            Assert.That((Tool(tools, "sdkRootPath"), Tool(tools, "ndkRootPath"), Tool(tools, "jdkRootPath")), Is.EqualTo((sdk, ndk, jdk)));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void FailedReleaseBuild_RestoresEmbeddedChoicesAndUnusedCustomPaths(bool preferencesExisted)
        {
            var previous = CapturePreferences();
            var failure = new InvalidOperationException("fixture-preference-build-failure");
            try
            {
                foreach (string key in EmbeddedKeys)
                    if (preferencesExisted) EditorPrefs.SetBool(key, true); else EditorPrefs.DeleteKey(key);
                foreach (string key in DirectoryKeys)
                    if (preferencesExisted) EditorPrefs.SetString(key, "unused-custom-tool-location"); else EditorPrefs.DeleteKey(key);
                SyncToolPreferences();
                var before = CapturePreferences();
                Exception observed = null;
                try { Builder().GetMethod("WithReleaseSettings", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { "com.ainative.battleclient.validation", (Action)(() => throw failure) }); }
                catch (TargetInvocationException exception) { observed = exception.InnerException; }
                Assert.That(observed, Is.SameAs(failure));
                Assert.That(CapturePreferences(), Is.EqualTo(before), "Restoring resolved paths must also retain embedded choices, unused custom paths and missing keys.");
            }
            finally { RestorePreferences(previous); SyncToolPreferences(); }
        }

        // Only the six non-secret toolchain preferences are inspected; signing configuration is excluded.
        private static readonly string[] EmbeddedKeys = { "SdkUseEmbedded", "NdkUseEmbedded", "JdkUseEmbedded" };
        private static readonly string[] DirectoryKeys = { "AndroidSdkRoot", "AndroidNdkRootR27C", "Jdk17Path" };
        private static (bool existed, bool embedded, string directory)[] CapturePreferences()
        {
            var values = new (bool, bool, string)[6];
            for (int i = 0; i < 3; i++) {
                values[i] = (EditorPrefs.HasKey(EmbeddedKeys[i]), EditorPrefs.GetBool(EmbeddedKeys[i], true), "");
                values[i + 3] = (EditorPrefs.HasKey(DirectoryKeys[i]), false, EditorPrefs.GetString(DirectoryKeys[i], ""));
            }
            return values;
        }
        private static void RestorePreferences((bool existed, bool embedded, string directory)[] values)
        {
            for (int i = 0; i < 3; i++) {
                if (values[i].existed) EditorPrefs.SetBool(EmbeddedKeys[i], values[i].embedded); else EditorPrefs.DeleteKey(EmbeddedKeys[i]);
                if (values[i + 3].existed) EditorPrefs.SetString(DirectoryKeys[i], values[i + 3].directory); else EditorPrefs.DeleteKey(DirectoryKeys[i]);
            }
        }
        private static void SyncToolPreferences()
        {
            foreach (string root in new[] { "AndroidSDKRoot", "AndroidNDKRoot", "AndroidJavaRoot" }) {
                Type type = Type.GetType("UnityEditor.Android." + root + ", UnityEditor.Android.Extensions", true);
                object instance = type.GetMethod("GetInstance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
                type.GetMethod("SyncPreferences", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Invoke(instance, null);
            }
        }

        [Test]
        public void AndroidBatchPreparation_PreservesSymbolsAndIsIdempotentWithoutSwitchingTarget()
        {
            MethodInfo prepare = Builder().GetMethod("PrepareAndroidBatchBuild", BindingFlags.Static | BindingFlags.Public);
            Assert.That(prepare, Is.Not.Null, "Android batch compilation needs a separate preparation entry.");
            NamedBuildTarget android = NamedBuildTarget.Android;
            string defines = PlayerSettings.GetScriptingDefineSymbols(android);
            BuildTarget active = EditorUserBuildSettings.activeBuildTarget;
            var settings = (PlayerSettings.GetScriptingBackend(android), PlayerSettings.Android.targetSdkVersion, PlayerSettings.Android.targetArchitectures);
            string path = Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath, "..", "ProjectSettings", "ProjectSettings.asset"));
            byte[] source = File.ReadAllBytes(path);
            try
            {
                PlayerSettings.SetScriptingDefineSymbols(android, defines.Length == 0 ? "OTHER;FANTASY_UNITY_CUSTOM" : defines + ";OTHER;FANTASY_UNITY_CUSTOM");
                prepare.Invoke(null, null); prepare.Invoke(null, null);
                string[] actual = PlayerSettings.GetScriptingDefineSymbols(android).Split(';');
                Assert.That(actual, Does.Contain("OTHER").And.Contain("FANTASY_UNITY_CUSTOM"));
                Assert.That(Array.FindAll(actual, value => value == "FANTASY_UNITY").Length, Is.EqualTo(1));
                Assert.That(EditorUserBuildSettings.activeBuildTarget, Is.EqualTo(active));
                Assert.That((PlayerSettings.GetScriptingBackend(android), PlayerSettings.Android.targetSdkVersion, PlayerSettings.Android.targetArchitectures), Is.EqualTo(settings));
            }
            finally { PlayerSettings.SetScriptingDefineSymbols(android, defines); AssetDatabase.SaveAssets(); File.WriteAllBytes(path, source); }
        }

        [TestCase(BuildTarget.StandaloneWindows64, false, false, false)]
        [TestCase(BuildTarget.Android, false, false, true)]
        [TestCase(BuildTarget.Android, true, false, false)]
        [TestCase(BuildTarget.Android, false, true, false)]
        public void AndroidReleaseInvocation_RequiresActiveAndroidAndCompletedCompilation(BuildTarget target, bool compiling, bool importing, bool accepted)
        {
            MethodInfo validate = Builder().GetMethod("ValidateBuildTarget", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(validate, Is.Not.Null, "Android release invocation must validate target before changing any build settings.");
            Exception rejection = null;
            try { validate.Invoke(null, new object[] { target, compiling, importing }); }
            catch (TargetInvocationException exception) { rejection = exception.InnerException; }
            Assert.That(rejection == null, Is.EqualTo(accepted));
            if (!accepted) Assert.That(rejection, Is.InstanceOf<InvalidOperationException>());
        }
        private static Type Builder()
        {
            Type type = typeof(BattleClientBuild).Assembly.GetType("AiNative.Client.Editor.AndroidBattleClientBuild");
            Assert.That(type, Is.Not.Null, "Android release build entry is not implemented."); return type;
        }
        private static string Tool(Type type, string property) => (string)type.GetProperty(property, BindingFlags.Public | BindingFlags.Static).GetValue(null);
    }
}
