using System;
using AiNative.Client.Application;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AiNative.Client.Editor
{
    /// <summary>通过 Unity 序列化创建仅供本地使用的场景及 Multiplayer Play Mode 资产。</summary>
    [InitializeOnLoad]
    public static class LocalMultiplayerSetup
    {
        static LocalMultiplayerSetup()
        {
            TopologyClientFlow.EditorHasGameFocus = () =>
                UnityEditorInternal.InternalEditorUtility.isApplicationActive
                && EditorWindow.focusedWindow != null
                && EditorWindow.focusedWindow.GetType().Name == "GameView";
            EditorApplication.update += KeepLocalClientsRunning;
        }

        private static void KeepLocalClientsRunning()
        {
            if (EditorApplication.isPlaying && !UnityEngine.Application.runInBackground)
            {
                UnityEngine.Application.runInBackground = true;
            }
        }

        public const string ScenePath =
            "Assets/AiNative.BattleClient/Scenes/LocalMultiplayer.unity";
        public const string TwoPlayersPath = "Assets/Settings/PlayMode/LocalTwoClients.asset";
        public const string FourPlayersPath = "Assets/Settings/PlayMode/LocalFourClients.asset";

        [MenuItem("AI Native/Local Test/Open Two Clients")]
        public static void OpenTwoClients() => Open(TwoPlayersPath);

        [MenuItem("AI Native/Local Test/Open Four Clients")]
        public static void OpenFourClients() => Open(FourPlayersPath);

        private static void Open(string configurationPath)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                throw new InvalidOperationException("Stop Play Mode before choosing a local test.");
            }

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return;
            }

            EditorSceneManager.OpenScene(ScenePath);
            Selection.activeObject = AssetDatabase.LoadMainAssetAtPath(configurationPath);
            EditorGUIUtility.PingObject(Selection.activeObject);
            Debug.Log(
                "Select "
                    + Selection.activeObject.name
                    + " in Unity's Play Mode dropdown, then press Play. First run tools/topology/start-manual.cmd (Windows) or tools/topology/start-manual.sh (macOS); use a different account in each window."
            );
        }

        // 仅由显式编辑操作触发，不自动运行，也不覆盖用户场景或配置。
        [MenuItem("AI Native/Local Test/Create Missing Assets")]
        public static void CreateMissingAssets()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                throw new InvalidOperationException(
                    "Stop Play Mode before authoring local test assets."
                );
            }

            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) == null)
            {
                var previous = EditorSceneManager.GetActiveScene();
                var scene = EditorSceneManager.NewScene(
                    NewSceneSetup.EmptyScene,
                    NewSceneMode.Additive
                );
                var root = new GameObject("LocalMultiplayerClient");
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, scene);
                var composition = root.AddComponent<BattleClientCompositionRoot>();
                var serialized = new SerializedObject(composition);
                serialized.FindProperty("editorLocalTopology").boolValue = true;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                EditorSceneManager.SaveScene(scene, ScenePath);
                EditorSceneManager.CloseScene(scene, true);
                if (previous.IsValid())
                {
                    UnityEngine.SceneManagement.SceneManager.SetActiveScene(previous);
                }
            }

            EnsureFolder("Assets/Settings");
            EnsureFolder("Assets/Settings/PlayMode");
            CreateConfiguration(TwoPlayersPath, 2);
            CreateConfiguration(FourPlayersPath, 4);
            AssetDatabase.SaveAssets();
        }

        private static void CreateConfiguration(string path, int count)
        {
            if (AssetDatabase.LoadMainAssetAtPath(path) != null)
            {
                return;
            } // ScenarioConfig is provided by the Unity 6000.3 editor module; keep reflection here,

            // 此逻辑位于 Runtime 之外，避免多人编辑器实现进入 Player。
            Type type = Type.GetType(
                "Unity.Multiplayer.PlayMode.Editor.ScenarioConfig, UnityEditor.MultiplayerModule",
                true
            );
            var configuration = ScriptableObject.CreateInstance(type);
            var serialized = new SerializedObject(configuration);
            serialized.FindProperty("m_EnableEditors").boolValue = true;
            SceneAsset scene = AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath);
            ConfigureInstance(
                serialized.FindProperty("m_MainEditorInstance"),
                "Main Editor",
                scene
            );
            SerializedProperty editors = serialized.FindProperty("m_EditorInstances");
            editors.arraySize = count - 1;
            for (int i = 0; i < editors.arraySize; i++)
            {
                ConfigureInstance(editors.GetArrayElementAtIndex(i), "Player " + (i + 2), scene);
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.CreateAsset(configuration, path);
        }

        private static void ConfigureInstance(
            SerializedProperty instance,
            string name,
            SceneAsset scene
        )
        {
            instance.FindPropertyRelative("Name").stringValue = name;
            instance.FindPropertyRelative("m_Role").intValue = 1; // Unity MultiplayerRoleFlags.Client
            instance.FindPropertyRelative("m_InitialScene").objectReferenceValue = scene;
        }

        private static void EnsureFolder(string path)
        {
            if (!AssetDatabase.IsValidFolder(path))
            {
                AssetDatabase.CreateFolder(
                    System.IO.Path.GetDirectoryName(path).Replace('\\', '/'),
                    System.IO.Path.GetFileName(path)
                );
            }
        }
    }
}
