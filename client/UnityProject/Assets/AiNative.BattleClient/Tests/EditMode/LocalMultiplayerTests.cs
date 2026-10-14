using System.Reflection;
using AiNative.Client.Editor;
using AiNative.Client.Fantasy;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AiNative.Client.Application.Tests
{
    public sealed class LocalMultiplayerTests
    {
        [TestCase(LocalMultiplayerSetup.TwoPlayersPath, 2)]
        [TestCase(LocalMultiplayerSetup.FourPlayersPath, 4)]
        public void AllPlayersUseLocalSceneAndClientRole(string path, int count)
        {
            var asset = AssetDatabase.LoadMainAssetAtPath(path);
            Assert.That(asset, Is.Not.Null);
            var serialized = new SerializedObject(asset);
            Assert.That(serialized.FindProperty("m_EnableEditors").boolValue, Is.True);
            AssertInstance(serialized.FindProperty("m_MainEditorInstance"));
            var editors = serialized.FindProperty("m_EditorInstances");
            Assert.That(editors.arraySize, Is.EqualTo(count - 1));
            for (int i = 0; i < editors.arraySize; i++) AssertInstance(editors.GetArrayElementAtIndex(i));
            Assert.That(serialized.FindProperty("m_LocalInstances").arraySize, Is.Zero);
            Assert.That(serialized.FindProperty("m_RemoteInstances").arraySize, Is.Zero);
        }

        [Test]
        public void LocalSceneStartsInteractivePlaintextGateWithoutLegacyBattleSession()
        {
            var scene = EditorSceneManager.OpenScene(LocalMultiplayerSetup.ScenePath, OpenSceneMode.Additive);
            try
            {
                var roots = scene.GetRootGameObjects();
                Assert.That(roots.Length, Is.EqualTo(1));
                var root = roots[0].GetComponent<BattleClientCompositionRoot>();
                Assert.That(new SerializedObject(root).FindProperty("editorLocalTopology").boolValue, Is.True);
                typeof(BattleClientCompositionRoot).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(root, null);
                Assert.That(root.Session, Is.Null, "The old standalone Battle transport must never be initialized.");
                var flow = root.GetComponent<TopologyClientFlow>();
                Assert.That(flow, Is.Not.Null);
                Assert.That(flow.Automated, Is.False);
                Assert.That(flow.AuthenticationRequired, Is.True);
                var options = (GateConnectionOptions)typeof(TopologyClientFlow).GetField("_options", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(flow);
                Assert.That((options.Host, options.Port, options.UseTls), Is.EqualTo(("127.0.0.1", 23001, false)));
            }
            finally { EditorSceneManager.CloseScene(scene, true); }
        }

        private static void AssertInstance(SerializedProperty instance)
        {
            Assert.That(instance.FindPropertyRelative("m_Role").intValue, Is.EqualTo(1));
            Assert.That(AssetDatabase.GetAssetPath(instance.FindPropertyRelative("m_InitialScene").objectReferenceValue), Is.EqualTo(LocalMultiplayerSetup.ScenePath));
        }
    }
}
