using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Editor;

namespace AiNative.Client.Character.Editor
{
    public static class CharacterInputGenerator
    {
        [MenuItem("AiNative/Character Demo/Regenerate Input Wrapper")]
        public static void Generate()
        {
            const string source =
                "Packages/com.ainative.client.character/Runtime/Input/CharacterControls.inputactions";
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(source);
            string path = Path.Combine(
                package.resolvedPath,
                "Runtime/Input/CharacterControls.inputactions"
            );
            var asset = InputActionAsset.FromJson(File.ReadAllText(path));
            try
            {
                InputActionCodeGenerator.GenerateWrapperCode(
                    Path.ChangeExtension(path, "cs"),
                    asset,
                    new InputActionCodeGenerator.Options
                    {
                        className = "CharacterControls",
                        namespaceName = "AiNative.Client.Character",
                        sourceAssetPath = source,
                    }
                );
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
            AssetDatabase.Refresh();
        }
    }
}
