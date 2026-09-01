using System;
using System.IO;
using AiNative.Client.Fantasy;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace AiNative.Client.Editor
{
    public static class BattleClientBuild
    {
        private const string ScenePath = "Assets/AiNative.BattleClient/Scenes/BattleClient.unity";
        private const string NoticePath = "Packages/com.ainative.client.fantasy/THIRD-PARTY-NOTICES.md";
        private const string RepositoryLicensePath = "../../server/vendor/Fantasy/LICENSE";

        public static void BuildWindowsSmoke()
        {
            string output = ReadRequiredAbsolutePath(
                Environment.GetCommandLineArgs(),
                "--ainative-build-output");
            if (!output.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("--ainative-build-output must name an .exe file.");
            }

            string outputDirectory = Path.GetDirectoryName(output);
            Directory.CreateDirectory(outputDirectory);
            PlayerSettings.SetScriptingBackend(
                NamedBuildTarget.Standalone,
                ScriptingImplementation.Mono2x);

            BuildPlayerOptions options = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = output,
                target = BuildTarget.StandaloneWindows64,
                targetGroup = BuildTargetGroup.Standalone,
                subtarget = (int)StandaloneBuildSubtarget.Player,
                options = BuildOptions.Development,
            };
            BuildReport report = BuildPipeline.BuildPlayer(options);
            if (report.summary.result != BuildResult.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Windows smoke Player build failed: {report.summary.result}, " +
                    $"errors={report.summary.totalErrors}.");
            }

            CopyThirdPartyNotice(outputDirectory);
            Debug.Log($"WS-26 Windows x64 Mono Player: {output}");
        }

        public static void BuildMacOsArm64Smoke()
        {
            string output = ReadRequiredAbsolutePath(
                Environment.GetCommandLineArgs(),
                "--ainative-build-output");
            if (!output.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("--ainative-build-output must name a .app bundle.");
            }

            string outputDirectory = Path.GetDirectoryName(output);
            Directory.CreateDirectory(outputDirectory);

            NamedBuildTarget standalone = NamedBuildTarget.Standalone;
            ScriptingImplementation previousBackend =
                PlayerSettings.GetScriptingBackend(standalone);
            int previousArchitecture = PlayerSettings.GetArchitecture(standalone);
            string projectSettingsPath = Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", "ProjectSettings", "ProjectSettings.asset"));
            byte[] previousProjectSettings = File.ReadAllBytes(projectSettingsPath);
            try
            {
                PlayerSettings.SetScriptingBackend(
                    standalone,
                    ScriptingImplementation.Mono2x);
                PlayerSettings.SetArchitecture(standalone, 1); // ARM64

                BuildPlayerOptions options = new BuildPlayerOptions
                {
                    scenes = new[] { ScenePath },
                    locationPathName = output,
                    target = BuildTarget.StandaloneOSX,
                    targetGroup = BuildTargetGroup.Standalone,
                    subtarget = (int)StandaloneBuildSubtarget.Player,
                    options = BuildOptions.Development,
                };
                BuildReport report = BuildPipeline.BuildPlayer(options);
                if (report.summary.result != BuildResult.Succeeded)
                {
                    throw new InvalidOperationException(
                        $"macOS ARM64 smoke Player build failed: {report.summary.result}, " +
                        $"errors={report.summary.totalErrors}.");
                }
            }
            finally
            {
                try
                {
                    PlayerSettings.SetArchitecture(standalone, previousArchitecture);
                    PlayerSettings.SetScriptingBackend(standalone, previousBackend);
                    AssetDatabase.SaveAssets();
                }
                finally
                {
                    File.WriteAllBytes(projectSettingsPath, previousProjectSettings);
                }
            }

            CopyThirdPartyNotice(outputDirectory);
            Debug.Log($"WS-26 macOS ARM64 Mono Player: {output}");
        }

        private static void CopyThirdPartyNotice(string outputDirectory)
        {
            UnityEditor.PackageManager.PackageInfo clientPackage =
                UnityEditor.PackageManager.PackageInfo.FindForAssembly(
                typeof(FantasyKcpRealtimeTransport).Assembly);
            string source = clientPackage is null
                ? Path.GetFullPath(NoticePath)
                : Path.Combine(clientPackage.resolvedPath, "THIRD-PARTY-NOTICES.md");
            if (!File.Exists(source))
            {
                throw new FileNotFoundException(
                    "Fantasy client third-party notice is required for distribution.",
                    source);
            }

            string notice = File.ReadAllText(source);
            if (notice.IndexOf("MIT", StringComparison.OrdinalIgnoreCase) < 0 ||
                notice.IndexOf("f8bed0d464924f159d46498f1311206ea0694be8", StringComparison.OrdinalIgnoreCase) < 0 ||
                notice.IndexOf("entity", StringComparison.OrdinalIgnoreCase) < 0)
            {
                throw new InvalidDataException(
                    "Fantasy notice must identify the MIT license, approved fork commit, and entity restriction.");
            }

            File.Copy(source, Path.Combine(outputDirectory, "THIRD-PARTY-NOTICES.md"), true);

            string licenseSource = FindFantasyUnityLicense();
            if (!File.Exists(licenseSource))
            {
                licenseSource = Path.GetFullPath(RepositoryLicensePath);
            }

            if (!File.Exists(licenseSource))
            {
                throw new FileNotFoundException("Fantasy restricted MIT license is required.", licenseSource);
            }

            string license = File.ReadAllText(licenseSource);
            if (license.IndexOf("MIT License", StringComparison.OrdinalIgnoreCase) < 0 ||
                license.IndexOf("explicitly prohibited", StringComparison.OrdinalIgnoreCase) < 0)
            {
                throw new InvalidDataException(
                    "Fantasy license must retain its MIT heading and entity-specific prohibition.");
            }

            File.Copy(licenseSource, Path.Combine(outputDirectory, "Fantasy-LICENSE.txt"), true);
        }

        private static string FindFantasyUnityLicense()
        {
            foreach (UnityEditor.PackageManager.PackageInfo package in
                     UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages())
            {
                if (!string.Equals(package.name, "com.fantasy.unity", StringComparison.Ordinal)) continue;
                string packageLicense = Path.Combine(package.resolvedPath, "LICENSE");
                if (File.Exists(packageLicense)) return packageLicense;

                // The Git package is rooted below the repository root; walk up only within
                // that resolved checkout to locate the pinned fork's license.
                DirectoryInfo directory = new DirectoryInfo(package.resolvedPath);
                for (int depth = 0; directory is not null && depth < 6; depth++)
                {
                    string candidate = Path.Combine(directory.FullName, "LICENSE");
                    string forkMarker = Path.Combine(directory.FullName, "AI_NATIVE_FORK.md");
                    if (File.Exists(candidate) && File.Exists(forkMarker)) return candidate;
                    directory = directory.Parent;
                }
            }

            return string.Empty;
        }

        private static string ReadRequiredAbsolutePath(string[] arguments, string option)
        {
            for (int index = 0; index + 1 < arguments.Length; index++)
            {
                if (!string.Equals(arguments[index], option, StringComparison.Ordinal)) continue;
                string path = arguments[index + 1];
                if (!Path.IsPathRooted(path))
                {
                    throw new ArgumentException(option + " must be an absolute path.");
                }

                return Path.GetFullPath(path);
            }

            throw new ArgumentException("Missing required command-line option " + option + ".");
        }
    }
}
