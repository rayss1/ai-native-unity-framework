using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.UnityLinker;
using UnityEngine;

namespace AiNative.Client.Editor
{
    /// <summary>构建设备验收 APK；结束时始终恢复临时发布和工具链设置。</summary>
    public static class AndroidBattleClientBuild
    {
        /// <summary>在以 -buildTarget Android 重启 Unity 前执行；外层构建脚本负责恢复项目设置。</summary>
        public static void PrepareAndroidBatchBuild()
        {
            NamedBuildTarget android = NamedBuildTarget.Android;
            string defines = PlayerSettings.GetScriptingDefineSymbols(android);
            if (Array.IndexOf(defines.Split(';'), "FANTASY_UNITY") < 0)
            {
                PlayerSettings.SetScriptingDefineSymbols(
                    android,
                    defines.Length == 0 ? "FANTASY_UNITY" : defines + ";FANTASY_UNITY"
                );
            }

            AssetDatabase.SaveAssets();
        }

        public static void BuildAndroidRelease()
        {
            string[] arguments = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(arguments, "--ainative-build-output");
            if (index < 0 || index + 1 == arguments.Length)
            {
                throw new ArgumentException("Missing --ainative-build-output.");
            }

            BuildAndroidReleaseAtPath(arguments[index + 1]);
        }

        public static void BuildAndroidReleaseAtPath(
            string outputPath,
            string applicationId = "com.ainative.battleclient.validation"
        )
        {
            ValidateBuildTarget(
                EditorUserBuildSettings.activeBuildTarget,
                EditorApplication.isCompiling,
                EditorApplication.isUpdating
            );
            if (
                !Path.IsPathRooted(outputPath)
                || !outputPath.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)
            )
            {
                throw new ArgumentException(
                    "Android release output must be an absolute .apk path.",
                    nameof(outputPath)
                );
            }

            if (string.IsNullOrWhiteSpace(applicationId) || applicationId.Split('.').Length < 2)
            {
                throw new ArgumentException(
                    "A reverse-domain Android application ID is required.",
                    nameof(applicationId)
                );
            }

            foreach (char c in applicationId)
            {
                if (!(char.IsLetterOrDigit(c) || c == '_' || c == '.'))
                {
                    throw new ArgumentException(
                        "Invalid Android application ID.",
                        nameof(applicationId)
                    );
                }
            }

            outputPath = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            BattleClientRendering.ValidateUrpConfiguration();
            WithReleaseSettings(
                applicationId,
                () =>
                {
                    BuildReport report = BuildPipeline.BuildPlayer(CreateBuildOptions(outputPath));
                    if (report.summary.result != BuildResult.Succeeded)
                    {
                        throw new InvalidOperationException(
                            $"Android IL2CPP build failed: {report.summary.result}, errors={report.summary.totalErrors}."
                        );
                    }

                    BattleClientBuild.CopyThirdPartyNotice(Path.GetDirectoryName(outputPath));
                    WriteBuildIdentity(outputPath);
                }
            );
            Debug.Log("Android ARM64 IL2CPP device-validation APK: " + outputPath);
        }

        private static void ValidateBuildTarget(BuildTarget target, bool compiling, bool importing)
        {
            if (target != BuildTarget.Android)
            {
                throw new InvalidOperationException(
                    "Activate Android and complete compilation before building. For batch builds, run PrepareAndroidBatchBuild first, then restart Unity with -buildTarget Android."
                );
            }

            if (compiling || importing)
            {
                throw new InvalidOperationException(
                    "Android scripts and assets must finish compiling and importing before building."
                );
            }
        }

        private static BuildPlayerOptions CreateBuildOptions(string outputPath) =>
            new BuildPlayerOptions
            {
                scenes = FrameworkBootstrapBuild.GetPlayerScenes(),
                locationPathName = outputPath,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = BuildOptions.None,
            };

        private static void WithReleaseSettings(string applicationId, Action build)
        {
            NamedBuildTarget target = NamedBuildTarget.Android;
            ScriptingImplementation backend = PlayerSettings.GetScriptingBackend(target);
            AndroidArchitecture architecture = PlayerSettings.Android.targetArchitectures;
            AndroidSdkVersions min = PlayerSettings.Android.minSdkVersion,
                max = PlayerSettings.Android.targetSdkVersion;
            ManagedStrippingLevel stripping = PlayerSettings.GetManagedStrippingLevel(target);
            string defines = PlayerSettings.GetScriptingDefineSymbols(target),
                priorId = PlayerSettings.GetApplicationIdentifier(target);
            bool bundle = EditorUserBuildSettings.buildAppBundle,
                export = EditorUserBuildSettings.exportAsGoogleAndroidProject,
                development = EditorUserBuildSettings.development;
            bool customKeystore = PlayerSettings.Android.useCustomKeystore;
            Type tools = Type.GetType(
                "UnityEditor.Android.AndroidExternalToolsSettings, UnityEditor.Android.Extensions",
                true
            );
            var toolPreferences = new ToolchainPreferences();
            string settingsPath = Path.GetFullPath(
                Path.Combine(
                    global::UnityEngine.Application.dataPath,
                    "..",
                    "ProjectSettings",
                    "ProjectSettings.asset"
                )
            );
            byte[] settings = File.ReadAllBytes(settingsPath);
            string bundled = BundledToolchainRoot(
                EditorApplication.applicationContentsPath,
                global::UnityEngine.Application.platform
            );
            ValidateToolchain(bundled, global::UnityEngine.Application.platform);
            try
            {
                PlayerSettings.SetScriptingBackend(target, ScriptingImplementation.IL2CPP);
                PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
                PlayerSettings.Android.minSdkVersion = (AndroidSdkVersions)25;
                PlayerSettings.Android.targetSdkVersion = (AndroidSdkVersions)35;
                PlayerSettings.SetManagedStrippingLevel(target, ManagedStrippingLevel.Minimal);
                PlayerSettings.SetApplicationIdentifier(target, applicationId);
                if (Array.IndexOf(defines.Split(';'), "FANTASY_UNITY") < 0)
                {
                    PlayerSettings.SetScriptingDefineSymbols(
                        target,
                        defines.Length == 0 ? "FANTASY_UNITY" : defines + ";FANTASY_UNITY"
                    );
                }

                EditorUserBuildSettings.buildAppBundle = false;
                EditorUserBuildSettings.exportAsGoogleAndroidProject = false;
                EditorUserBuildSettings.development = false;
                PlayerSettings.Android.useCustomKeystore = false;
                SetTool(tools, "sdkRootPath", Path.Combine(bundled, "SDK"));
                SetTool(tools, "ndkRootPath", Path.Combine(bundled, "NDK"));
                SetTool(tools, "jdkRootPath", Path.Combine(bundled, "OpenJDK"));
                AssetDatabase.SaveAssets();
                build();
            }
            finally
            {
                try
                {
                    PlayerSettings.SetScriptingBackend(target, backend);
                    PlayerSettings.Android.targetArchitectures = architecture;
                    PlayerSettings.Android.minSdkVersion = min;
                    PlayerSettings.Android.targetSdkVersion = max;
                    PlayerSettings.SetManagedStrippingLevel(target, stripping);
                    PlayerSettings.SetApplicationIdentifier(target, priorId);
                    PlayerSettings.SetScriptingDefineSymbols(target, defines);
                    EditorUserBuildSettings.buildAppBundle = bundle;
                    EditorUserBuildSettings.exportAsGoogleAndroidProject = export;
                    EditorUserBuildSettings.development = development;
                    PlayerSettings.Android.useCustomKeystore = customKeystore;
                    AssetDatabase.SaveAssets();
                }
                finally
                {
                    try
                    {
                        toolPreferences.Restore();
                    }
                    finally
                    {
                        File.WriteAllBytes(settingsPath, settings);
                    }
                }
            }
        }

        private static string BundledToolchainRoot(string contents, RuntimePlatform platform) =>
            Path.GetFullPath(
                Path.Combine(
                    contents,
                    platform == RuntimePlatform.OSXEditor ? "../.." : ".",
                    "PlaybackEngines",
                    "AndroidPlayer"
                )
            );

        private static void ValidateToolchain(string bundled, RuntimePlatform platform)
        {
            string suffix,
                host;
            switch (platform)
            {
                case RuntimePlatform.WindowsEditor:
                    suffix = ".exe";
                    host = "windows-x86_64";
                    break;
                case RuntimePlatform.OSXEditor:
                    suffix = "";
                    host = "darwin-x86_64";
                    break;
                case RuntimePlatform.LinuxEditor:
                    suffix = "";
                    host = "linux-x86_64";
                    break;
                default:
                    throw new InvalidOperationException("Unsupported Android build host platform.");
            }

            if (
                !File.Exists(Path.Combine(bundled, "SDK", "platforms", "android-35", "android.jar"))
                || !File.Exists(Path.Combine(bundled, "SDK", "platform-tools", "adb" + suffix))
                || !File.Exists(
                    Path.Combine(
                        bundled,
                        "NDK",
                        "toolchains",
                        "llvm",
                        "prebuilt",
                        host,
                        "bin",
                        "clang" + suffix
                    )
                )
                || !File.Exists(Path.Combine(bundled, "OpenJDK", "bin", "java" + suffix))
                || !File.Exists(Path.Combine(bundled, "NDK", "source.properties"))
                || !File.Exists(Path.Combine(bundled, "OpenJDK", "release"))
            )
            {
                throw new InvalidOperationException(
                    "The current Unity Editor's bundled Android API35/SDK/NDK/JDK toolchain is incomplete."
                );
            }

            if (
                !File.ReadAllText(Path.Combine(bundled, "NDK", "source.properties"))
                    .Contains("27.2.12479018")
                || !File.ReadAllText(Path.Combine(bundled, "OpenJDK", "release"))
                    .Contains("JAVA_VERSION=\"17.")
            )
            {
                throw new InvalidOperationException(
                    "The Android validation toolchain requires NDK r27c and JDK17."
                );
            }
        }

        private static void SetTool(Type type, string property, string value) =>
            type.GetProperty(property, BindingFlags.Static | BindingFlags.Public)
                .SetValue(null, value);

        private sealed class ToolchainPreferences
        {
            // 设置根路径会关闭 UseEmbedded，并覆盖未使用的自定义路径。
            // 仅保存这些非敏感工具链偏好，包括键缺失时的默认值。
            private static readonly string[] _embeddedKeys =
            {
                "SdkUseEmbedded",
                "NdkUseEmbedded",
                "JdkUseEmbedded",
            };
            private static readonly string[] _directoryKeys =
            {
                "AndroidSdkRoot",
                "AndroidNdkRootR27C",
                "Jdk17Path",
            };
            private readonly bool[] _hasEmbedded = new bool[3];
            private readonly bool[] _embedded = new bool[3];
            private readonly bool[] _hasDirectory = new bool[3];
            private readonly string[] _directory = new string[3];

            internal ToolchainPreferences()
            {
                for (int i = 0; i < 3; i++)
                {
                    _hasEmbedded[i] = EditorPrefs.HasKey(_embeddedKeys[i]);
                    _embedded[i] = EditorPrefs.GetBool(_embeddedKeys[i], true);
                    _hasDirectory[i] = EditorPrefs.HasKey(_directoryKeys[i]);
                    _directory[i] = EditorPrefs.GetString(_directoryKeys[i], "");
                }
            }

            internal void Restore()
            {
                for (int i = 0; i < 3; i++)
                {
                    if (_hasEmbedded[i])
                    {
                        EditorPrefs.SetBool(_embeddedKeys[i], _embedded[i]);
                    }
                    else
                    {
                        EditorPrefs.DeleteKey(_embeddedKeys[i]);
                    }

                    if (_hasDirectory[i])
                    {
                        EditorPrefs.SetString(_directoryKeys[i], _directory[i]);
                    }
                    else
                    {
                        EditorPrefs.DeleteKey(_directoryKeys[i]);
                    }
                }

                // Unity 6000 的 AndroidRoot 单例同时缓存工具链选择和目录。
                foreach (
                    string name in new[] { "AndroidSDKRoot", "AndroidNDKRoot", "AndroidJavaRoot" }
                )
                {
                    Type type = Type.GetType(
                        "UnityEditor.Android." + name + ", UnityEditor.Android.Extensions",
                        true
                    );
                    object root = type.GetMethod(
                            "GetInstance",
                            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic
                        )
                        .Invoke(null, null);
                    type.GetMethod(
                            "SyncPreferences",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                        )
                        .Invoke(root, null);
                }
            }
        }

        internal static string GeneratePreservationFile()
        {
            string path = Path.GetFullPath("Library/AiNativeBuild/android-link.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            // 两个固定程序集均使用 Fantasy 源生成注册；实体系统、
            // 心跳定时器、信封处理器及序列化器必须在 IL2CPP 裁剪后保留。
            File.WriteAllText(
                path,
                "<linker><assembly fullname=\"Fantasy.Unity\" preserve=\"all\"/><assembly fullname=\"AiNative.Client.Fantasy\" preserve=\"all\"/></linker>"
            );
            return path;
        }

        private static void WriteBuildIdentity(string output)
        {
            using var hash = SHA256.Create();
            using var file = File.OpenRead(output);
            string digest = BitConverter
                .ToString(hash.ComputeHash(file))
                .Replace("-", "")
                .ToLowerInvariant();
            File.WriteAllText(
                Path.Combine(Path.GetDirectoryName(output), "ANDROID-BUILD-INFO.json"),
                JsonUtility.ToJson(
                    new BuildIdentity
                    {
                        unityVersion = global::UnityEngine.Application.unityVersion,
                        applicationId = PlayerSettings.GetApplicationIdentifier(
                            NamedBuildTarget.Android
                        ),
                        apkSha256 = digest,
                    },
                    true
                )
            );
        }

        [Serializable]
        private sealed class BuildIdentity
        {
            public string unityVersion;
            public string applicationId;
            public string apkSha256;
            public string backend = "IL2CPP";
            public string abi = "arm64-v8a";
            public string ndk = "27.2.12479018";
            public string jdk = "17";
            public int minApi = 25;
            public int targetApi = 35;
            public bool development = false;
        }
    }

    public sealed class AndroidBattleClientLinker : IUnityLinkerProcessor
    {
        public int callbackOrder => 0;

        public string GenerateAdditionalLinkXmlFile(
            BuildReport report,
            UnityLinkerBuildPipelineData data
        ) =>
            report.summary.platform == BuildTarget.Android
                ? AndroidBattleClientBuild.GeneratePreservationFile()
                : null;
    }
}
