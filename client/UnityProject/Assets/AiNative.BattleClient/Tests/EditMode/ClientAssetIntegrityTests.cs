using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AiNative.Client.Application.Tests
{
    public sealed class ClientAssetIntegrityTests
    {
        [Test]
        public void OwnedScenesAndPrefabsHaveScriptsAndPersistentEventTargets()
        {
            var failures = new List<string>();
            int inspectedCount = 0;
            foreach (string assetPath in AssetDatabase.GetAllAssetPaths())
            {
                if (!assetPath.StartsWith("Assets/AiNative.", StringComparison.Ordinal))
                {
                    continue;
                }
                if (assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    GameObject prefab = PrefabUtility.LoadPrefabContents(assetPath);
                    try
                    {
                        InspectHierarchy(prefab, assetPath, failures);
                        inspectedCount++;
                    }
                    finally
                    {
                        PrefabUtility.UnloadPrefabContents(prefab);
                    }
                }
                else if (assetPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                {
                    Scene scene = EditorSceneManager.OpenPreviewScene(assetPath);
                    try
                    {
                        foreach (GameObject root in scene.GetRootGameObjects())
                        {
                            InspectHierarchy(root, assetPath, failures);
                        }
                        inspectedCount++;
                    }
                    finally
                    {
                        EditorSceneManager.ClosePreviewScene(scene);
                    }
                }
            }
            Assert.That(
                inspectedCount,
                Is.GreaterThan(0),
                "没有发现项目自有场景或 Prefab，不能视为通过。"
            );
            Assert.That(failures, Is.Empty, string.Join("\n", failures));
            TestContext.WriteLine("Inspected owned scenes/prefabs: " + inspectedCount);
        }

        private static void InspectHierarchy(
            GameObject root,
            string assetPath,
            List<string> failures
        )
        {
            foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
            {
                int missing = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(
                    transform.gameObject
                );
                if (missing != 0)
                {
                    failures.Add(assetPath + ": " + transform.name + " missing scripts=" + missing);
                }
                foreach (Component component in transform.GetComponents<Component>())
                {
                    if (component == null)
                    {
                        continue;
                    }
                    InspectPersistentEvents(component, assetPath, failures);
                }
            }
        }

        private static void InspectPersistentEvents(
            Component component,
            string assetPath,
            List<string> failures
        )
        {
            using (var serialized = new SerializedObject(component))
            {
                SerializedProperty property = serialized.GetIterator();
                while (property.Next(true))
                {
                    // 只检查 UnityEvent 序列化的持久调用，不执行事件、不修改资源。
                    if (
                        !property.propertyPath.EndsWith(
                            "m_PersistentCalls.m_Calls",
                            StringComparison.Ordinal
                        ) || !property.isArray
                    )
                    {
                        continue;
                    }
                    for (int index = 0; index < property.arraySize; index++)
                    {
                        SerializedProperty call = property.GetArrayElementAtIndex(index);
                        UnityEngine.Object target = call.FindPropertyRelative(
                            "m_Target"
                        ).objectReferenceValue;
                        string methodName = call.FindPropertyRelative("m_MethodName").stringValue;
                        if (string.IsNullOrEmpty(methodName))
                        {
                            // Inspector 允许保留尚未配置的空槽，它不代表已有绑定丢失。
                            continue;
                        }
                        if (target == null || !HasMethod(target.GetType(), methodName))
                        {
                            failures.Add(
                                assetPath
                                    + ": "
                                    + component.name
                                    + "."
                                    + property.propertyPath
                                    + "["
                                    + index
                                    + "] invalid target/method="
                                    + methodName
                            );
                        }
                    }
                }
            }
        }

        private static bool HasMethod(Type type, string methodName)
        {
            while (type != null)
            {
                foreach (
                    MethodInfo method in type.GetMethods(
                        BindingFlags.Instance
                            | BindingFlags.Static
                            | BindingFlags.Public
                            | BindingFlags.NonPublic
                            | BindingFlags.DeclaredOnly
                    )
                )
                {
                    if (method.Name == methodName)
                    {
                        return true;
                    }
                }
                type = type.BaseType;
            }
            return false;
        }
    }
}
