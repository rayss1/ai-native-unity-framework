using System;
using AiNative.Client.Application;
using AiNative.Client.Character;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AiNative.Client.Editor
{
    public static class NetworkCharacterBuilder
    {
        public const string PrefabPath = "Assets/AiNative.BattleClient/Resources/BattleClient/NetworkCharacter.prefab";
        [MenuItem("AiNative/Battle Client/Build Network Character")]
        public static void Build()
        {
            const string source = "Assets/AiNative.CharacterDemo/Generated/CharacterDemoRig.prefab";
            var rig = PrefabUtility.LoadPrefabContents(source);
            GameObject visual = null;
            try
            {
                var driver = rig.GetComponentInChildren<CharacterAnimationDriver>(true);
                if (driver == null) throw new InvalidOperationException("Build the Character Demo rig first.");
                // Copy only the authored visual subtree; never instantiate the local simulation scripts.
                visual = Object.Instantiate(driver.gameObject);
                visual.name = "NetworkCharacter";
                visual.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                foreach (var component in visual.GetComponentsInChildren<MonoBehaviour>(true))
                    if (!(component is CharacterAnimationDriver)) Object.DestroyImmediate(component);
                foreach (var collider in visual.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(collider);
                foreach (var body in visual.GetComponentsInChildren<Rigidbody>(true)) Object.DestroyImmediate(body);
                var animation = visual.GetComponent<CharacterAnimationDriver>();
                var serialized = new SerializedObject(animation);
                serialized.FindProperty("motor").objectReferenceValue = null;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                visual.AddComponent<NetworkCharacterPresenter>();
                PrefabUtility.SaveAsPrefabAsset(visual, PrefabPath);
                AssetDatabase.SaveAssets();
            }
            finally
            {
                if (visual != null) Object.DestroyImmediate(visual);
                PrefabUtility.UnloadPrefabContents(rig);
            }
        }
    }
}
