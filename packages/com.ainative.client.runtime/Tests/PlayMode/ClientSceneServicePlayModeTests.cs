#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace AiNative.Client.Runtime.Tests
{
    /// <summary>通过测试框架生成并登记自有临时场景；不修改或关闭用户场景。</summary>
    public sealed class SceneFixtureSetup : IPrebuildSetup, IPostBuildCleanup
    {
        internal const string StatePath = "Temp/AiNative.Runtime.SceneTests.json";
        private const string OwnedPrefix = "Assets/AiNative.Runtime.SceneTests-";

        [Serializable]
        public sealed class SceneRecord
        {
            public string Path;
            public bool Enabled;
        }

        [Serializable]
        public sealed class State
        {
            public string Folder;
            public SceneRecord[] Original;
            public string First;
            public string Second;
        }

        public void Setup()
        {
            if (File.Exists(StatePath))
            {
                throw new InvalidOperationException(
                    "Previous scene fixture evidence exists; inspect and run SceneFixtureSetup.Cleanup before retrying."
                );
            }
            string folder = OwnedPrefix + Guid.NewGuid().ToString("N");
            var original = new List<SceneRecord>();
            foreach (EditorBuildSettingsScene scene in EditorBuildSettings.scenes)
            {
                original.Add(new SceneRecord { Path = scene.path, Enabled = scene.enabled });
            }
            var state = new State
            {
                Folder = folder,
                Original = original.ToArray(),
                First = folder + "/First.unity",
                Second = folder + "/Second.unity",
            };
            Directory.CreateDirectory("Temp");
            File.WriteAllText(StatePath, JsonUtility.ToJson(state));
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
            Scene previouslyActive = SceneManager.GetActiveScene();
            try
            {
                CreateScene(state.First);
                CreateScene(state.Second);
                var entries = OriginalEntries(state);
                entries.Add(new EditorBuildSettingsScene(state.First, true));
                entries.Add(new EditorBuildSettingsScene(state.Second, true));
                EditorBuildSettings.scenes = entries.ToArray();
            }
            finally
            {
                if (previouslyActive.IsValid() && previouslyActive.isLoaded)
                {
                    SceneManager.SetActiveScene(previouslyActive);
                }
            }
        }

        public void Cleanup()
        {
            if (!File.Exists(StatePath))
            {
                return;
            }
            State state = ReadState();
            if (
                !state.Folder.StartsWith(OwnedPrefix, StringComparison.Ordinal)
                || state.Folder.Length != OwnedPrefix.Length + 32
                || !Guid.TryParseExact(state.Folder.Substring(OwnedPrefix.Length), "N", out _)
            )
            {
                throw new InvalidOperationException("Invalid owned scene fixture identity.");
            }
            // 恢复前确认除自有场景之外没有发生新的配置变化，避免覆盖外部工作。
            var remaining = new List<EditorBuildSettingsScene>();
            foreach (EditorBuildSettingsScene scene in EditorBuildSettings.scenes)
            {
                if (scene.path != state.First && scene.path != state.Second)
                {
                    remaining.Add(scene);
                }
            }
            if (remaining.Count != state.Original.Length)
            {
                throw new InvalidOperationException(
                    "Build settings changed outside scene fixture; original inventory retained in Temp."
                );
            }
            for (int index = 0; index < remaining.Count; index++)
            {
                if (
                    remaining[index].path != state.Original[index].Path
                    || remaining[index].enabled != state.Original[index].Enabled
                )
                {
                    throw new InvalidOperationException(
                        "Build settings changed outside scene fixture; refusing overwrite."
                    );
                }
            }
            EditorBuildSettings.scenes = OriginalEntries(state).ToArray();
            if (
                AssetDatabase.IsValidFolder(state.Folder)
                && !AssetDatabase.DeleteAsset(state.Folder)
            )
            {
                throw new IOException("Could not delete owned scene fixture folder.");
            }
            File.Delete(StatePath);
        }

        internal static State ReadState()
        {
            return JsonUtility.FromJson<State>(File.ReadAllText(StatePath));
        }

        private static List<EditorBuildSettingsScene> OriginalEntries(State state)
        {
            var entries = new List<EditorBuildSettingsScene>();
            foreach (SceneRecord record in state.Original)
            {
                entries.Add(new EditorBuildSettingsScene(record.Path, record.Enabled));
            }
            return entries;
        }

        private static void CreateScene(string path)
        {
            Scene scene = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene,
                NewSceneMode.Additive
            );
            try
            {
                if (!EditorSceneManager.SaveScene(scene, path))
                {
                    throw new IOException("Could not save owned test scene.");
                }
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }
    }

    [PrebuildSetup(typeof(SceneFixtureSetup))]
    [PostBuildCleanup(typeof(SceneFixtureSetup))]
    public sealed class ClientSceneServicePlayModeTests
    {
        private ClientSceneService _service;
        private SceneFixtureSetup.State _state;
        private Scene _initialActive;

        [SetUp]
        public void SetUp()
        {
            _state = SceneFixtureSetup.ReadState();
            _initialActive = SceneManager.GetActiveScene();
            _service = new ClientSceneService(id =>
            {
                if (id.Value == "first")
                {
                    return _state.First;
                }
                if (id.Value == "second")
                {
                    return _state.Second;
                }
                return string.Empty;
            });
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_initialActive.IsValid() && _initialActive.isLoaded)
            {
                SceneManager.SetActiveScene(_initialActive);
            }
            Task cleanup = _service.UnloadAsync().AsTask();
            yield return WaitFor(cleanup);
            cleanup.GetAwaiter().GetResult();
            Assert.That(SceneManager.GetSceneByPath(_state.First).isLoaded, Is.False);
            Assert.That(SceneManager.GetSceneByPath(_state.Second).isLoaded, Is.False);
        }

        [UnityTest]
        public IEnumerator SwitchLoadsTargetBeforeUnloadingPrevious()
        {
            Task first = _service.SwitchAsync(new SceneId("first")).AsTask();
            yield return WaitFor(first);
            first.GetAwaiter().GetResult();
            Assert.That(SceneManager.GetSceneByPath(_state.First).isLoaded, Is.True);
            Task second = _service.SwitchAsync(new SceneId("second")).AsTask();
            yield return WaitFor(second);
            second.GetAwaiter().GetResult();
            Assert.That(SceneManager.GetSceneByPath(_state.First).isLoaded, Is.False);
            Assert.That(SceneManager.GetSceneByPath(_state.Second).isLoaded, Is.True);
            Assert.That(SceneManager.GetActiveScene().path, Is.EqualTo(_state.Second));
            Assert.That(
                _initialActive.isLoaded,
                Is.True,
                "Bootstrap/test runner scene must survive content transitions."
            );
        }

        [UnityTest]
        public IEnumerator FailedResolutionKeepsExistingSceneUsable()
        {
            Task first = _service.SwitchAsync(new SceneId("first")).AsTask();
            yield return WaitFor(first);
            first.GetAwaiter().GetResult();
            Task failed = _service.SwitchAsync(new SceneId("unregistered")).AsTask();
            yield return WaitFor(failed);
            Assert.That(failed.IsFaulted, Is.True);
            Assert.That(failed.Exception.GetBaseException(), Is.TypeOf<ArgumentException>());
            Assert.That(SceneManager.GetSceneByPath(_state.First).isLoaded, Is.True);
            Assert.That(SceneManager.GetActiveScene().path, Is.EqualTo(_state.First));
        }

        [UnityTest]
        public IEnumerator CancellationDuringNativeLoadUnloadsLateTargetAndPreservesPrevious()
        {
            Task first = _service.SwitchAsync(new SceneId("first")).AsTask();
            yield return WaitFor(first);
            first.GetAwaiter().GetResult();
            using (var cancellation = new CancellationTokenSource())
            {
                Task transition = _service
                    .SwitchAsync(new SceneId("second"), cancellation.Token)
                    .AsTask();
                Assert.That(
                    transition.IsCompleted,
                    Is.False,
                    "Cancellation must exercise an in-flight native load."
                );
                cancellation.Cancel();
                yield return WaitFor(transition);
                Assert.That(transition.IsCanceled, Is.True);
                Assert.That(SceneManager.GetSceneByPath(_state.Second).isLoaded, Is.False);
                Assert.That(SceneManager.GetSceneByPath(_state.First).isLoaded, Is.True);
                Assert.That(SceneManager.GetActiveScene().path, Is.EqualTo(_state.First));
            }
        }

        [UnityTest]
        public IEnumerator ShortSceneNameIsRejectedBeforeNativeLoad()
        {
            int initialSceneCount = SceneManager.sceneCount;
            var invalid = new ClientSceneService(_ =>
                Path.GetFileNameWithoutExtension(_state.First)
            );
            Task failed = invalid.SwitchAsync(new SceneId("first")).AsTask();
            Assert.That(failed.IsCompleted, Is.True, "短名称应在原生加载前立即拒绝。");
            Assert.That(failed.IsFaulted, Is.True);
            Assert.That(failed.Exception.GetBaseException(), Is.TypeOf<ArgumentException>());
            Task cleanup = invalid.UnloadAsync().AsTask();
            yield return WaitFor(cleanup);
            cleanup.GetAwaiter().GetResult();
            Assert.That(SceneManager.sceneCount, Is.EqualTo(initialSceneCount));
            Assert.That(SceneManager.GetSceneByPath(_state.First).isLoaded, Is.False);
        }

        [UnityTest]
        public IEnumerator PendingPathRejectsRivalWithoutSharingCancellationOrUnload()
        {
            var rival = new ClientSceneService(_ => _state.First);
            Task original = _service.SwitchAsync(new SceneId("first")).AsTask();
            Assert.That(original.IsCompleted, Is.False, "必须覆盖正在加载的原生场景。");
            using (var cancellation = new CancellationTokenSource())
            {
                Task rejected = rival
                    .SwitchAsync(new SceneId("first"), cancellation.Token)
                    .AsTask();
                Assert.That(rejected.IsCompleted, Is.True, "预留冲突应在发起原生加载前立即拒绝。");
                Assert.That(rejected.IsFaulted, Is.True);
                Assert.That(
                    rejected.Exception.GetBaseException(),
                    Is.TypeOf<InvalidOperationException>()
                );
                cancellation.Cancel();
                Task rivalCleanup = rival.UnloadAsync().AsTask();
                yield return WaitFor(rivalCleanup);
                rivalCleanup.GetAwaiter().GetResult();
                yield return WaitFor(original);
                original.GetAwaiter().GetResult();
                Scene owned = SceneManager.GetSceneByPath(_state.First);
                Assert.That(owned.isLoaded, Is.True);
                Task repeated = _service.SwitchAsync(new SceneId("first")).AsTask();
                yield return WaitFor(repeated);
                repeated.GetAwaiter().GetResult();
                Assert.That(
                    SceneManager.GetSceneByPath(_state.First).handle,
                    Is.EqualTo(owned.handle)
                );
                int matchingScenes = 0;
                for (int index = 0; index < SceneManager.sceneCount; index++)
                {
                    if (SceneManager.GetSceneAt(index).path == _state.First)
                    {
                        matchingScenes++;
                    }
                }
                Assert.That(matchingScenes, Is.EqualTo(1), "同一路径只能存在一个原生场景实例。");
            }
        }

        private static IEnumerator WaitFor(Task task)
        {
            float deadline = Time.realtimeSinceStartup + 30f;
            while (!task.IsCompleted)
            {
                Assert.That(
                    Time.realtimeSinceStartup,
                    Is.LessThan(deadline),
                    "Scene operation did not complete within 30 seconds."
                );
                yield return null;
            }
        }
    }
}
#endif
