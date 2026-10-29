using System;
using System.IO;
using AiNative.Client.Application;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AiNative.Client.Editor
{
    /// <summary>只创建框架拥有的启动场景，不重写现有 BattleClient 场景或用户打开的场景。</summary>
    public static class FrameworkBootstrapBuild
    {
        /// <summary>生成的启动场景路径，Player 场景列表中应位于首项。</summary>
        public const string ScenePath =
            "Assets/AiNative.BattleClient/Scenes/FrameworkBootstrap.unity";

        /// <summary>返回启动场景与原产品场景，供显式 BuildPlayerOptions.scenes 使用。</summary>
        public static string[] GetPlayerScenes()
        {
            if (
                AssetDatabase.LoadAssetAtPath<SceneAsset>(FrameworkBootstrap.ContentScenePath)
                == null
            )
            {
                throw new InvalidOperationException("bootstrap-content-scene-missing");
            }
            return new[] { EnsureScene(), FrameworkBootstrap.ContentScenePath };
        }

        /// <summary>首次创建场景；已有场景仅校验所有权与结构，绝不覆盖未知修改。</summary>
        public static string EnsureScene()
        {
            if (File.Exists(ScenePath))
            {
                Scene preview = EditorSceneManager.OpenPreviewScene(ScenePath);
                try
                {
                    ValidateOwnedScene(preview);
                }
                finally
                {
                    EditorSceneManager.ClosePreviewScene(preview);
                }
                return ScenePath;
            }
            string directory = Path.GetDirectoryName(ScenePath).Replace('\\', '/');
            if (!AssetDatabase.IsValidFolder(directory))
            {
                throw new InvalidOperationException("bootstrap-scene-directory-missing");
            }
            bool replaceBatchDefault = CanReplaceBatchDefault();
            Scene previous = SceneManager.GetActiveScene();
            Scene created = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene,
                replaceBatchDefault ? NewSceneMode.Single : NewSceneMode.Additive
            );
            try
            {
                var owner = new GameObject("FrameworkBootstrap");
                SceneManager.MoveGameObjectToScene(owner, created);
                owner.AddComponent<FrameworkBootstrap>();
                ValidateOwnedScene(created);
                if (!EditorSceneManager.SaveScene(created, ScenePath))
                {
                    throw new IOException("bootstrap-scene-save-failed");
                }
            }
            finally
            {
                if (replaceBatchDefault)
                {
                    // 只恢复批处理默认空场景，不恢复或丢弃任何交互编辑内容。
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                }
                else
                {
                    EditorSceneManager.CloseScene(created, true);
                    if (previous.IsValid() && previous.isLoaded)
                    {
                        SceneManager.SetActiveScene(previous);
                    }
                }
            }
            AssetDatabase.ImportAsset(ScenePath);
            return ScenePath;
        }

        [MenuItem("AI Native/Framework/Ensure Bootstrap Scene")]
        private static void EnsureSceneFromMenu()
        {
            EnsureScene();
        }

        private static bool CanReplaceBatchDefault()
        {
            for (int index = 0; index < SceneManager.sceneCount; index++)
            {
                Scene scene = SceneManager.GetSceneAt(index);
                if (!string.IsNullOrEmpty(scene.path))
                {
                    continue;
                }
                // Unity 不允许在 untitled 场景旁 Additive 创建，也不允许保存 Preview Scene。
                // 只有批处理自身的干净默认场景可替换，用户未保存的场景必须明确保留。
                if (
                    global::UnityEngine.Application.isBatchMode
                    && SceneManager.sceneCount == 1
                    && !scene.isDirty
                )
                {
                    return true;
                }
                throw new InvalidOperationException(
                    "bootstrap-generation-requires-saved-scenes: 请先保存当前未命名场景，再生成启动场景；未保存内容未被修改。"
                );
            }
            return false;
        }

        private static void ValidateOwnedScene(Scene scene)
        {
            GameObject[] roots = scene.GetRootGameObjects();
            if (
                roots.Length != 1
                || roots[0].name != "FrameworkBootstrap"
                || roots[0].transform.childCount != 0
            )
            {
                throw new InvalidOperationException("bootstrap-scene-has-unknown-content");
            }
            Component[] components = roots[0].GetComponents<Component>();
            if (components.Length != 2 || roots[0].GetComponent<FrameworkBootstrap>() == null)
            {
                throw new InvalidOperationException("bootstrap-scene-not-owned");
            }
            if (!roots[0].activeSelf || !roots[0].GetComponent<FrameworkBootstrap>().enabled)
            {
                throw new InvalidOperationException("bootstrap-scene-disabled");
            }
        }
    }
}
