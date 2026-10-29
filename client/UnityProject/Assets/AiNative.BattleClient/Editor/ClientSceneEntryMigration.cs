using System;
using AiNative.Client.Application;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AiNative.Client.Editor
{
    /// <summary>通过 Editor 场景 API 补充显式入口；保留原场景 GUID、对象与序列化引用。</summary>
    public static class ClientSceneEntryMigration
    {
        /// <summary>幂等迁移产品场景；已打开且有未保存修改时拒绝，避免覆盖用户编辑。</summary>
        [MenuItem("AI Native/Framework/Migrate Client Scene Entry")]
        public static void Migrate()
        {
            string path = FrameworkBootstrap.ContentScenePath;
            Scene existing = SceneManager.GetSceneByPath(path);
            if (existing.IsValid() && existing.isLoaded && existing.isDirty)
            {
                throw new InvalidOperationException("client-scene-has-unsaved-changes");
            }
            Scene previous = SceneManager.GetActiveScene();
            bool opened = !existing.IsValid() || !existing.isLoaded;
            Scene scene = opened
                ? EditorSceneManager.OpenScene(path, OpenSceneMode.Additive)
                : existing;
            try
            {
                int count = 0;
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    foreach (
                        BattleClientCompositionRoot battle in root.GetComponentsInChildren<BattleClientCompositionRoot>(
                            true
                        )
                    )
                    {
                        count++;
                        if (battle.GetComponent<FrameworkClientRoot>() == null)
                        {
                            battle.gameObject.AddComponent<FrameworkClientRoot>();
                        }
                        if (battle.GetComponent<ClientSceneEntry>() == null)
                        {
                            battle.gameObject.AddComponent<ClientSceneEntry>();
                        }
                    }
                }
                if (count == 0)
                {
                    throw new InvalidOperationException("client-scene-battle-root-missing");
                }
                EditorSceneManager.MarkSceneDirty(scene);
                if (!EditorSceneManager.SaveScene(scene, path))
                {
                    throw new InvalidOperationException("client-scene-entry-save-failed");
                }
            }
            finally
            {
                if (opened)
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
                if (previous.IsValid() && previous.isLoaded)
                {
                    SceneManager.SetActiveScene(previous);
                }
            }
        }
    }
}
