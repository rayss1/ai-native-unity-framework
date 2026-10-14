using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AiNative.Client.Character;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace AiNative.CharacterDemo.Editor
{
    /// <summary>Application composition only. Original art assets are never modified.</summary>
    public static class CharacterDemoBuilder
    {
        private const string Root = "Assets/AiNative.CharacterDemo";
        private const string Generated = Root + "/Generated";
        public const string ScenePath = Root + "/Scenes/CharacterDemo.unity";
        private const string Animations = "Assets/Art/Animations/";
        private static readonly Dictionary<Material, Material> Materials = new Dictionary<Material, Material>();

        [MenuItem("AiNative/Character Demo/Build")]
        public static void Build()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Exit Play Mode before building the demo.");
            // Do not discard unrelated dirty scenes or replace their in-memory objects.
            if (Enumerable.Range(0, SceneManager.sceneCount).Any(i => SceneManager.GetSceneAt(i).isDirty))
                throw new InvalidOperationException("Save the open scenes before building the demo.");
            EnsureFolder(Generated);
            EnsureFolder(Root + "/Scenes");
            Materials.Clear();
            var controller = CreateController();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            SceneManager.SetActiveScene(scene);
            var previousDemo = SceneManager.GetSceneByPath(ScenePath);
            if (previousDemo.IsValid() && previousDemo.isLoaded) EditorSceneManager.CloseScene(previousDemo, true);
            try
            {
                BuildCourse();
                var rig = new GameObject("Character Demo Rig");
                var player = new GameObject("Player");
                player.transform.SetParent(rig.transform, false);
                player.transform.position = new Vector3(0, 0.1f, -5);
                var capsule = player.AddComponent<CharacterController>();
                capsule.height = 1.8f;
                capsule.center = new Vector3(0, 0.9f, 0);
                capsule.radius = 0.3f;
                capsule.stepOffset = 0.3f;
                capsule.slopeLimit = 45;
                var input = player.AddComponent<CharacterInputReader>();
                var motor = player.AddComponent<CharacterMotor>();
                var visual = Object.Instantiate(Required<GameObject>("Assets/Art/Models/Characters/VBOT2.0_Custom.prefab"), player.transform);
                visual.name = "VBOT Visual";
                visual.transform.localPosition = Vector3.zero;
                visual.transform.localRotation = Quaternion.identity;
                StripBehavioursAndPhysics(visual);
                var animator = visual.GetComponentInChildren<Animator>(true);
                if (animator == null || animator.avatar == null || !animator.avatar.isValid || !animator.avatar.isHuman)
                    throw new InvalidOperationException("VBOT must have a valid Humanoid Avatar.");
                animator.applyRootMotion = false;
                animator.runtimeAnimatorController = controller;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                var rifle = Object.Instantiate(Required<GameObject>("Assets/Art/Models/Weapons/AssaultRifleModel.prefab"));
                rifle.name = "Rifle Visual";
                StripBehavioursAndPhysics(rifle);
                var hand = animator.GetBoneTransform(HumanBodyBones.RightHand);
                if (hand == null) throw new InvalidOperationException("VBOT has no mapped right hand.");
                // The imported skeleton is in centimetres. Preserve the weapon's metre scale,
                // and express attachment offsets in metres rather than bone-local centimetres.
                // Calibrated against Idle@Rifle on this Avatar, not against the T-pose.
                rifle.transform.position = hand.position + hand.rotation * new Vector3(-0.11598f, 0.00328f, -0.15439f);
                rifle.transform.rotation = hand.rotation * Quaternion.Euler(343.007f, 202.7292f, 222.5332f);
                rifle.transform.SetParent(hand, true);
                var grip = new GameObject("Left Hand Grip (adjust for rifle pose)").transform;
                grip.SetParent(rifle.transform, false);
                grip.localPosition = new Vector3(0, -0.04f, 0.18f);
                grip.localRotation = Quaternion.Euler(0, 0, 90);
                ConvertMaterials(visual);
                ConvertMaterials(rifle);
                foreach (var child in player.GetComponentsInChildren<Transform>(true)) child.gameObject.layer = 2;
                var cameraObject = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
                cameraObject.tag = "MainCamera";
                cameraObject.transform.SetParent(rig.transform, false);
                cameraObject.GetComponent<Camera>().nearClipPlane = 0.1f;
                cameraObject.GetComponent<Camera>().farClipPlane = 150;
                var cameraRig = cameraObject.AddComponent<CharacterOrbitCamera>();
                motor.Configure(input, cameraRig);
                cameraRig.Configure(motor, input);
                var animation = animator.gameObject.AddComponent<CharacterAnimationDriver>();
                animation.Configure(motor, animator, rifle, grip);
                animation.ConfigureAimPose(new Vector3(-7.19209f, 17.92332f, -0.16308f),
                    Quaternion.Euler(270.6801f, 292.9626f, 155.0211f));
                player.AddComponent<CharacterDemoHud>().Configure(motor, input);
                PrefabUtility.SaveAsPrefabAssetAndConnect(rig, Generated + "/CharacterDemoRig.prefab", InteractionMode.AutomatedAction);
                var events = new GameObject("EventSystem", typeof(EventSystem));
                input.ConfigureUi(events.AddComponent<InputSystemUIInputModule>());
                EditorSceneManager.SaveScene(scene, ScenePath);
                AssetDatabase.SaveAssets();
                Debug.Log("Character demo created: " + ScenePath);
            }
            catch
            {
                EditorSceneManager.CloseScene(scene, true);
                throw;
            }
        }

        [MenuItem("AiNative/Character Demo/Build Windows Player")]
        public static void BuildWindows()
        {
            if (!File.Exists(ScenePath)) throw new InvalidOperationException("Build the demo scene first.");
            var output = Path.GetFullPath(Path.Combine(Application.dataPath, "../../..", "artifacts/character-demo/CharacterDemo.exe"));
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath }, locationPathName = output,
                target = BuildTarget.StandaloneWindows64, options = BuildOptions.Development
            });
            if (report.summary.result != BuildResult.Succeeded)
                throw new InvalidOperationException("Character demo build failed: " + report.summary.result);
            Debug.Log("Character demo Player: " + output);
        }

        private static AnimatorController CreateController() => InvectorControllerAdapter.Build(Generated);

        private static void StripBehavioursAndPhysics(GameObject root)
        {
            foreach (var script in root.GetComponentsInChildren<MonoBehaviour>(true)) if (script != null) Object.DestroyImmediate(script);
            foreach (var transform in root.GetComponentsInChildren<Transform>(true)) GameObjectUtility.RemoveMonoBehavioursWithMissingScript(transform.gameObject);
            foreach (var collider in root.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(collider);
            foreach (var body in root.GetComponentsInChildren<Rigidbody>(true)) Object.DestroyImmediate(body);
        }

        private static void ConvertMaterials(GameObject root)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) return;
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                var materials = renderer.sharedMaterials;
                for (int i = 0; i < materials.Length; i++)
                {
                    var original = materials[i];
                    if (original == null || original.shader.name.StartsWith("Universal Render Pipeline/", StringComparison.Ordinal)) continue;
                    if (!Materials.TryGetValue(original, out var converted))
                    {
                        var path = Generated + "/Material_" + AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(original)) + ".mat";
                        converted = AssetDatabase.LoadAssetAtPath<Material>(path);
                        if (converted == null) { converted = new Material(shader); AssetDatabase.CreateAsset(converted, path); }
                        converted.shader = shader;
                        converted.name = original.name + " (Demo URP)";
                        if (original.HasProperty("_MainTex")) converted.SetTexture("_BaseMap", original.GetTexture("_MainTex"));
                        if (original.HasProperty("_Color")) converted.SetColor("_BaseColor", original.GetColor("_Color"));
                        if (original.HasProperty("_BumpMap")) { converted.SetTexture("_BumpMap", original.GetTexture("_BumpMap")); converted.EnableKeyword("_NORMALMAP"); }
                        if (original.HasProperty("_Metallic")) converted.SetFloat("_Metallic", original.GetFloat("_Metallic"));
                        if (original.HasProperty("_Glossiness")) converted.SetFloat("_Smoothness", original.GetFloat("_Glossiness"));
                        EditorUtility.SetDirty(converted);
                        Materials.Add(original, converted);
                    }
                    materials[i] = converted;
                }
                renderer.sharedMaterials = materials;
            }
        }

        private static void BuildCourse()
        {
            var floor = CourseMaterial("Floor", new Color(0.12f, 0.18f, 0.23f));
            var obstacles = CourseMaterial("Obstacles", new Color(0.18f, 0.55f, 0.65f));
            Box("Ground", new Vector3(0, -0.25f, 3), new Vector3(30, 0.5f, 30), floor);
            var ramp = Box("30 degree ramp", new Vector3(-7, 1.15f, 4), new Vector3(3, 0.3f, 5), obstacles);
            ramp.transform.rotation = Quaternion.Euler(-30, 0, 0);
            for (int i = 0; i < 5; i++) Box("Step " + i, new Vector3(-2, (i + 1) * 0.1f, 3 + i * 0.7f), new Vector3(2.5f, (i + 1) * 0.2f, 0.7f), obstacles);
            Box("Low ceiling - crouch clearance 1.25m", new Vector3(4, 1.4f, 3), new Vector3(3, 0.3f, 4), obstacles);
            Box("Low ceiling support L", new Vector3(2.6f, 0.6f, 3), new Vector3(0.2f, 1.2f, 4), obstacles);
            Box("Low ceiling support R", new Vector3(5.4f, 0.6f, 3), new Vector3(0.2f, 1.2f, 4), obstacles);
            Box("Camera wall", new Vector3(8, 1.7f, -1), new Vector3(0.5f, 3.4f, 8), obstacles);
            Box("Camera corner", new Vector3(6, 1.7f, -5), new Vector3(4.5f, 3.4f, 0.5f), obstacles);
            Box("Jump platform 0.6m", new Vector3(2, 0.3f, 9), new Vector3(2, 0.6f, 2), obstacles);
            Box("Jump platform 1.2m", new Vector3(5, 0.6f, 9), new Vector3(2, 1.2f, 2), obstacles);
            var light = new GameObject("Sun", typeof(Light)).GetComponent<Light>();
            light.type = LightType.Directional; light.intensity = 1.5f; light.shadows = LightShadows.Soft;
            light.transform.rotation = Quaternion.Euler(50, -30, 0);
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.65f, 0.7f, 0.8f);
        }

        private static Material CourseMaterial(string name, Color color)
        {
            string path = Generated + "/Course" + name + ".mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null) { material = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard")); AssetDatabase.CreateAsset(material, path); }
            material.color = color;
            EditorUtility.SetDirty(material);
            return material;
        }

        private static GameObject Box(string name, Vector3 position, Vector3 scale, Material material)
        {
            var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = name; box.transform.position = position; box.transform.localScale = scale;
            box.GetComponent<Renderer>().sharedMaterial = material;
            return box;
        }

        private static T Required<T>(string path) where T : Object =>
            AssetDatabase.LoadAssetAtPath<T>(path) ?? throw new InvalidOperationException("Missing required demo asset: " + path);

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = path.Substring(0, path.LastIndexOf('/'));
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, path.Substring(path.LastIndexOf('/') + 1));
        }
    }
}
