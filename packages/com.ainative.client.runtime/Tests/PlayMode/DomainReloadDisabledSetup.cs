#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace AiNative.Client.Runtime.Tests
{
    /// <summary>
    /// 独立测试批次的无域重载设置；测试类同时声明 PrebuildSetup 和 PostBuildCleanup。
    /// 崩溃后保留 Temp 中原值，下一次拒绝覆盖；人工检查后调用 Cleanup 恢复。
    /// </summary>
    public sealed class DomainReloadDisabledSetup : IPrebuildSetup, IPostBuildCleanup
    {
        private const string StatePath = "Temp/AiNative.Runtime.DomainReloadTests.json";
        private const string Owner = "AiNative.Runtime.DomainReloadTests.v1";

        [Serializable]
        private sealed class SavedState
        {
            public string OwnerId;
            public bool OriginalEnabled;
            public int OriginalOptions;
            public int AppliedOptions;
        }

        public void Setup()
        {
            if (File.Exists(StatePath))
            {
                throw new InvalidOperationException(
                    "domain-reload-fixture-state-exists: inspect Temp/AiNative.Runtime.DomainReloadTests.json and call DomainReloadDisabledSetup.Cleanup to recover"
                );
            }
            if (EditorApplication.isPlaying)
            {
                throw new InvalidOperationException("domain-reload-setup-requires-edit-mode");
            }
            var saved = new SavedState
            {
                OwnerId = Owner,
                OriginalEnabled = EditorSettings.enterPlayModeOptionsEnabled,
                OriginalOptions = (int)EditorSettings.enterPlayModeOptions,
                AppliedOptions = (int)(
                    EditorSettings.enterPlayModeOptions | EnterPlayModeOptions.DisableDomainReload
                ),
            };
            Directory.CreateDirectory("Temp");
            File.WriteAllText(StatePath, JsonUtility.ToJson(saved));
            EditorSettings.enterPlayModeOptions = (EnterPlayModeOptions)saved.AppliedOptions;
            EditorSettings.enterPlayModeOptionsEnabled = true;
        }

        public void Cleanup()
        {
            if (!File.Exists(StatePath))
            {
                return;
            }
            SavedState saved = JsonUtility.FromJson<SavedState>(File.ReadAllText(StatePath));
            if (saved == null || saved.OwnerId != Owner)
            {
                throw new InvalidOperationException("domain-reload-fixture-invalid-owner");
            }
            bool alreadyRestored =
                EditorSettings.enterPlayModeOptionsEnabled == saved.OriginalEnabled
                && (int)EditorSettings.enterPlayModeOptions == saved.OriginalOptions;
            bool stillOwned =
                EditorSettings.enterPlayModeOptionsEnabled
                && (int)EditorSettings.enterPlayModeOptions == saved.AppliedOptions;
            if (!alreadyRestored && !stillOwned)
            {
                throw new InvalidOperationException(
                    "domain-reload-options-changed-externally: preserve evidence and restore manually"
                );
            }
            EditorSettings.enterPlayModeOptions = (EnterPlayModeOptions)saved.OriginalOptions;
            EditorSettings.enterPlayModeOptionsEnabled = saved.OriginalEnabled;
            File.Delete(StatePath);
        }
    }
}
#endif
