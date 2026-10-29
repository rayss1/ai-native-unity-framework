using System;
using System.IO;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace AiNative.Client.Editor
{
    /// <summary>
    /// Editor 测试证据捕获；跨域重载保存目标路径，完成后写出含原始 Output 的 NUnit XML。
    /// 只观察随后一次 Test Runner 运行，不启动测试，不访问网络，不进入 Player。
    /// </summary>
    [InitializeOnLoad]
    public static class ClientValidationCapture
    {
        private const string PathKey = "AiNative.Client.ValidationCapture.XmlPath";
        private static TestRunnerApi _api;
        private static CaptureCallbacks _callbacks;

        static ClientValidationCapture()
        {
            AssemblyReloadEvents.beforeAssemblyReload += Unregister;
            if (!string.IsNullOrEmpty(SessionState.GetString(PathKey, "")))
            {
                Register();
            }
        }

        /// <summary>
        /// 在 Editor 主线程、运行测试之前登记绝对 XML 路径；父目录自动创建。
        /// 已有捕获或已存在的证据文件会被拒绝，文件系统失败由调用方观察。
        /// 路径先存入 SessionState 再注册回调，域重载后恢复；Editor 退出不保证完成捕获。
        /// </summary>
        public static void Begin(string absoluteXmlPath)
        {
            if (string.IsNullOrWhiteSpace(absoluteXmlPath) || !Path.IsPathRooted(absoluteXmlPath))
            {
                throw new ArgumentException(
                    "validation-absolute-path-required",
                    nameof(absoluteXmlPath)
                );
            }
            if (!string.IsNullOrEmpty(SessionState.GetString(PathKey, "")))
            {
                throw new InvalidOperationException("validation-capture-already-pending");
            }
            string path = Path.GetFullPath(absoluteXmlPath);
            if (File.Exists(path) || File.Exists(path + ".progress.txt"))
            {
                throw new IOException("validation-evidence-already-exists");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            SessionState.SetString(PathKey, path);
            Register();
        }

        private static void Register()
        {
            if (_callbacks != null)
            {
                return;
            }
            _api = ScriptableObject.CreateInstance<TestRunnerApi>();
            _api.hideFlags = HideFlags.HideAndDontSave;
            _callbacks = new CaptureCallbacks();
            _api.RegisterCallbacks(_callbacks);
        }

        private static void Unregister()
        {
            if (_api != null)
            {
                if (_callbacks != null)
                {
                    _api.UnregisterCallbacks(_callbacks);
                }
                UnityEngine.Object.DestroyImmediate(_api);
            }
            _callbacks = null;
            _api = null;
        }

        private static void AppendProgress(string stage, string name)
        {
            string path = SessionState.GetString(PathKey, "");
            if (string.IsNullOrEmpty(path))
            {
                return;
            }
            File.AppendAllText(
                path + ".progress.txt",
                DateTime.UtcNow.ToString("O") + " " + stage + " " + name + Environment.NewLine
            );
        }

        private sealed class CaptureCallbacks : ICallbacks
        {
            public void RunStarted(ITestAdaptor testsToRun)
            {
                AppendProgress("RUN", testsToRun.FullName);
            }

            public void TestStarted(ITestAdaptor test)
            {
                AppendProgress("START", test.FullName);
            }

            public void TestFinished(ITestResultAdaptor result) { }

            public void RunFinished(ITestResultAdaptor result)
            {
                try
                {
                    string path = SessionState.GetString(PathKey, "");
                    if (!string.IsNullOrEmpty(path))
                    {
                        // 直接保存原始结果树，不能用仅含计数的 CLI JSON 重建 Output。
                        TestRunnerApi.SaveResultToFile(result, path);
                        AppendProgress("FINISHED", path);
                    }
                }
                finally
                {
                    SessionState.EraseString(PathKey);
                    Unregister();
                }
            }
        }
    }
}
