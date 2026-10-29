using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AiNative.CharacterDemo.Editor
{
    /// <summary>使用已有控制器的完整状态图，包括同步层覆盖。</summary>
    public static class InvectorControllerAdapter
    {
        public const string Source =
            "Assets/Art/Animations/Shooter/Invector@ShooterMelee.controller";

        public static AnimatorController Build(string folder)
        {
            string path = folder + "/Invector@ShooterMelee.controller";
            // 这是应用自有工作副本；不得运行缺失的 Invector 脚本。
            // 保持原始美术控制器不变，重建时保留工作副本的 GUID。
            if (
                AssetDatabase.LoadAssetAtPath<AnimatorController>(path) == null
                && !AssetDatabase.CopyAsset(Source, path)
            )
            {
                throw new InvalidOperationException(
                    "Cannot copy Invector@ShooterMelee controller."
                );
            }

            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
            string clipsFolder = folder + "/InvectorClips";
            if (!AssetDatabase.IsValidFolder(clipsFolder))
            {
                AssetDatabase.CreateFolder(folder, "InvectorClips");
            }

            var motions = new Dictionary<Motion, Motion>();
            var states = new List<AnimatorState>();
            foreach (var layer in controller.layers)
            {
                Visit(layer.stateMachine, states);
            }

            foreach (var state in states)
            {
                state.motion = AdaptMotion(state.motion, clipsFolder, motions);
                // 动作生命周期由 Motor 持有；将已有动画片段匹配到对应时长。
                if (state.name == "Roll" && state.motion is AnimationClip roll)
                {
                    state.speed = roll.length / .65f;
                }

                if (
                    state.name == "C"
                    && state.motion is AnimationClip melee
                    && melee.name.StartsWith("WeakAttack_UnarmedC", StringComparison.Ordinal)
                )
                {
                    state.speed = melee.length;
                }

                EditorUtility.SetDirty(state);
            }

            var layers = controller.layers;
            foreach (var layer in layers)
            {
                if (layer.syncedLayerIndex >= 0)
                {
                    foreach (var state in states)
                    {
                        var motion = layer.GetOverrideMotion(state);
                        if (motion != null)
                        {
                            layer.SetOverrideMotion(
                                state,
                                AdaptMotion(motion, clipsFolder, motions)
                            );
                        }
                    }
                }
            }

            controller.layers = layers;
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            return controller;
        }

        private static void Visit(AnimatorStateMachine machine, List<AnimatorState> states)
        {
            if (machine == null)
            {
                return;
            }

            RemoveMissingBehaviours(machine);
            foreach (var child in machine.states)
            {
                RemoveMissingBehaviours(child.state);
                if (!states.Contains(child.state))
                {
                    states.Add(child.state);
                }
            }

            foreach (var child in machine.stateMachines)
            {
                Visit(child.stateMachine, states);
            }
        }

        private static void RemoveMissingBehaviours(Object owner)
        {
            var serialized = new SerializedObject(owner);
            var behaviours = serialized.FindProperty("m_StateMachineBehaviours");
            if (behaviours == null)
            {
                return;
            }

            for (int i = behaviours.arraySize - 1; i >= 0; i--)
            {
                if (behaviours.GetArrayElementAtIndex(i).objectReferenceValue == null)
                {
                    behaviours.DeleteArrayElementAtIndex(i);
                }
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static Motion AdaptMotion(
            Motion source,
            string folder,
            Dictionary<Motion, Motion> cache
        )
        {
            if (source == null)
            {
                return null; // Unused bow/throw/sword paths remain as supplied.
            }

            if (cache.TryGetValue(source, out var existing))
            {
                return existing;
            }

            if (source is BlendTree tree)
            {
                if (
                    !AssetDatabase
                        .GetAssetPath(tree)
                        .StartsWith(
                            Path.GetDirectoryName(folder).Replace('\\', '/') + "/",
                            StringComparison.Ordinal
                        )
                )
                {
                    throw new InvalidOperationException(
                        "Refusing to edit an external BlendTree: "
                            + AssetDatabase.GetAssetPath(tree)
                    );
                }

                cache[source] = tree;
                var children = tree.children;
                for (int i = 0; i < children.Length; i++)
                {
                    children[i].motion = AdaptMotion(children[i].motion, folder, cache);
                }

                tree.children = children;
                EditorUtility.SetDirty(tree);
                return tree;
            }

            if (!(source is AnimationClip clip))
            {
                return source;
            }

            string sourcePath = AssetDatabase.GetAssetPath(clip);
            AnimationClip copy;
            if (sourcePath.StartsWith(folder + "/", StringComparison.Ordinal))
            {
                copy = clip;
                if (Path.GetFileNameWithoutExtension(sourcePath) != copy.name)
                {
                    string error = AssetDatabase.RenameAsset(
                        sourcePath,
                        SafeName(copy.name) + "_" + Path.GetFileNameWithoutExtension(sourcePath)
                    );
                    if (!string.IsNullOrEmpty(error))
                    {
                        throw new InvalidOperationException(error);
                    }
                }
            }
            else
            {
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(clip, out string guid, out long id);
                string path = folder + "/" + SafeName(clip.name) + "_" + guid + "_" + id + ".anim";
                copy = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
                if (copy == null)
                {
                    copy = Object.Instantiate(clip);
                    AssetDatabase.CreateAsset(copy, path);
                }
                else
                {
                    EditorUtility.CopySerialized(clip, copy);
                }
            }

            copy.name = Path.GetFileNameWithoutExtension(AssetDatabase.GetAssetPath(copy));
            AnimationUtility.SetAnimationEvents(copy, Array.Empty<AnimationEvent>());
            var settings = AnimationUtility.GetAnimationClipSettings(copy);
            // 将水平位移提取为根运动，再由 Animator 丢弃。
            // 若将 XZ 位移烘焙进姿态，即使 applyRootMotion=false，网格仍会
            // 在每次循环中移动并跳回；保留源动画的 Y、旋转和循环语义。
            settings.loopBlendPositionXZ = false;
            AnimationUtility.SetAnimationClipSettings(copy, settings);
            EditorUtility.SetDirty(copy);
            cache[source] = copy;
            return copy;
        }

        private static string SafeName(string value)
        {
            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                value = value.Replace(invalid, '_');
            }

            return value;
        }
    }
}
