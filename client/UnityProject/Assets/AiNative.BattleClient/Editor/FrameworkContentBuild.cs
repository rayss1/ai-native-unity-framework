using System.IO;
using AiNative.Client.Assets.AssetBundle.Editor;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace AiNative.Client.Editor
{
    /// <summary>显式随包资源目录；构建与 Play 使用同一 Bundle 路径。</summary>
    [InitializeOnLoad]
    public sealed class FrameworkContentBuild : IPreprocessBuildWithReport
    {
        static FrameworkContentBuild()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.ExitingEditMode)
                {
                    try
                    {
                        Build();
                    }
                    catch (System.Exception exception)
                    {
                        Debug.LogException(exception);
                        EditorApplication.isPlaying = false;
                    }
                }
            };
        }

        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report) => BuildFor(report.summary.platform);

        [MenuItem("AI Native/Framework/Build Local Content")]
        public static void Build() => BuildFor(EditorUserBuildSettings.activeBuildTarget);

        private static void BuildFor(BuildTarget target)
        {
            const string resources = "Assets/AiNative.BattleClient/Resources/";
            LocalAssetBundleBuilder.BuildManaged(
                Path.Combine(UnityEngine.Application.streamingAssetsPath, "AiNativeContent"),
                target,
                new BundleAssetDefinition(
                    "ui.topology",
                    resources + "TopologyClientUI.prefab",
                    "ui",
                    "ui"
                ),
                new BundleAssetDefinition(
                    "character.network",
                    resources + "BattleClient/NetworkCharacter.prefab",
                    "character",
                    "battle"
                ),
                new BundleAssetDefinition(
                    "material.player",
                    resources + "BattleClient/Player.mat",
                    "character",
                    "battle"
                )
            );
        }
    }
}
