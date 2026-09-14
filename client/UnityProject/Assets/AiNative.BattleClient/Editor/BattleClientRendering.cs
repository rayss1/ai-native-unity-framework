using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace AiNative.Client.Editor
{
    public static class BattleClientRendering
    {
        private const string SettingsFolder = "Assets/AiNative.BattleClient/Rendering";
        private const string PipelinePath = SettingsFolder + "/BattleClientPipeline.asset";
        private const string RendererPath = SettingsFolder + "/BattleClientRenderer.asset";
        private const string ResourcesFolder = "Assets/AiNative.BattleClient/Resources";
        private const string MaterialFolder = ResourcesFolder + "/BattleClient";

        [MenuItem("AI Native/Configure URP")]
        public static void ConfigureUrp()
        {
            EnsureFolder(SettingsFolder);
            EnsureFolder(ResourcesFolder);
            EnsureFolder(MaterialFolder);
            UniversalRendererData renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath);
            if (renderer == null)
            {
                renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
                AssetDatabase.CreateAsset(renderer, RendererPath);
            }

            UniversalRenderPipelineAsset pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelinePath);
            if (pipeline == null)
            {
                pipeline = UniversalRenderPipelineAsset.Create(renderer);
                pipeline.msaaSampleCount = 4;
                AssetDatabase.CreateAsset(pipeline, PipelinePath);
            }

            GraphicsSettings.defaultRenderPipeline = pipeline;
            int selectedQuality = QualitySettings.GetQualityLevel();
            try
            {
                for (int index = 0; index < QualitySettings.names.Length; index++)
                {
                    QualitySettings.SetQualityLevel(index, false);
                    QualitySettings.renderPipeline = pipeline;
                }
            }
            finally { QualitySettings.SetQualityLevel(selectedQuality, false); }

            CreateMaterial("Player", new Color(0.15f, 0.65f, 1f));
            CreateMaterial("Floor", new Color(0.08f, 0.1f, 0.14f));

            // Supersedes the old built-in greybox shader-retention workaround.
            SerializedObject graphics = new SerializedObject(
                AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset")[0]);
            SerializedProperty included = graphics.FindProperty("m_AlwaysIncludedShaders");
            for (int index = included.arraySize - 1; index >= 0; index--)
            {
                if (included.GetArrayElementAtIndex(index).objectReferenceValue is Shader shader && shader.name == "Standard")
                {
                    included.GetArrayElementAtIndex(index).objectReferenceValue = null;
                    included.DeleteArrayElementAtIndex(index);
                }
            }
            graphics.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssets();
            ValidateUrpConfiguration();
            Debug.Log("Battle Client URP configuration and material dependencies validated.");
        }

        public static void ValidateUrpConfiguration()
        {
            UniversalRenderPipelineAsset pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelinePath);
            if (pipeline == null || GraphicsSettings.defaultRenderPipeline != pipeline)
                throw new InvalidOperationException("Configure the Battle Client URP asset before building.");
            if (pipeline.rendererDataList.Length == 0 || pipeline.rendererDataList[0] == null)
                throw new InvalidOperationException("The URP asset requires a renderer.");
            for (int index = 0; index < QualitySettings.names.Length; index++)
            {
                if (QualitySettings.GetRenderPipelineAssetAt(index) != pipeline)
                    throw new InvalidOperationException("Every quality level must select the Battle Client URP asset.");
            }
            foreach (string name in new[] { "Player", "Floor" })
            {
                Material material = AssetDatabase.LoadAssetAtPath<Material>(MaterialFolder + "/" + name + ".mat");
                if (material == null || material.shader == null || material.shader.name != "Universal Render Pipeline/Lit")
                    throw new InvalidOperationException("Missing URP Lit greybox material: " + name);
            }
        }

        private static void CreateMaterial(string name, Color color)
        {
            string path = MaterialFolder + "/" + name + ".mat";
            if (AssetDatabase.LoadAssetAtPath<Material>(path) != null) return;
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) throw new InvalidOperationException("URP Lit shader is unavailable.");
            Material material = new Material(shader) { name = name };
            material.SetColor("_BaseColor", color);
            material.SetFloat("_Smoothness", 0.2f);
            AssetDatabase.CreateAsset(material, path);
        }

        private static void EnsureFolder(string path)
        {
            if (!AssetDatabase.IsValidFolder(path))
                AssetDatabase.CreateFolder(System.IO.Path.GetDirectoryName(path).Replace('\\', '/'), System.IO.Path.GetFileName(path));
        }
    }
}
