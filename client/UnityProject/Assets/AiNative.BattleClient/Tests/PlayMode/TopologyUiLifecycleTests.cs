using System;
using System.Collections;
using System.Reflection;
using System.Threading.Tasks;
using AiNative.Client.Fantasy;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace AiNative.Client.Application.PlayModeTests
{
    public sealed class TopologyUiLifecycleTests
    {
        [UnityTest]
        public IEnumerator DestroyAndRecreateInteractiveFlow_CleansUiAndDiscardsQueuedOperations()
        {
            // 不发送账号请求；此处验证真实 MonoBehaviour/UI 生命周期，
            // 用刻意保持未完成的后台任务代替网络 I/O。
            for (int round = 0; round < 2; round++)
            {
                var owner = new GameObject("InteractiveUiLifecycle" + round);
                var refresh = new TaskCompletionSource<bool>();
                try
                {
                    var flow = owner.AddComponent<TopologyClientFlow>();
                    flow.Initialize(new GateConnectionOptions("127.0.0.1", 23001, false));
                    yield return WaitForView(flow);
                    var view = (TopologyClientView)Field(flow, "_view");
                    Assert.That(
                        view != null,
                        Is.True,
                        "Interactive flow must load and bind ui.topology through the asset service."
                    );
                    Canvas canvas = view.GetComponentInChildren<Canvas>(true);
                    Assert.That(canvas != null, Is.True);
                    GameObject ownedEventSystem = (GameObject)Field(view, "_ownedEventSystem");
                    int calls = 0;
                    Invoke(flow, "Run", new Func<Task>(() => refresh.Task), false);
                    Invoke(
                        flow,
                        "Submit",
                        new Func<Task>(() =>
                        {
                            calls++;
                            return Task.CompletedTask;
                        })
                    );
                    Assert.That(flow.UiBusy, Is.True);
                    UnityEngine.Object.Destroy(owner);
                    yield return null;
                    yield return null; // The view schedules its owned EventSystem for destruction.
                    yield return null;
                    Assert.That(
                        view == null,
                        Is.True,
                        "No UI from the old flow may survive a Play lifecycle."
                    );
                    Assert.That(canvas == null, Is.True);
                    Assert.That(
                        ownedEventSystem == null,
                        Is.True,
                        "Only an EventSystem created by this view should be cleaned up."
                    );
                    refresh.TrySetResult(true);
                    yield return null;
                    Assert.That(
                        calls,
                        Is.Zero,
                        "A late refresh completion must not execute an old queued button action."
                    );
                }
                finally
                {
                    if (owner != null)
                    {
                        UnityEngine.Object.Destroy(owner);
                    }

                    refresh.TrySetResult(true);
                }
            }
        }

        private static IEnumerator WaitForView(TopologyClientFlow flow)
        {
            float deadline = Time.realtimeSinceStartup + 10f;
            while (
                Field(flow, "_view") == null
                && Time.realtimeSinceStartup < deadline
                && flow.Error != "ui-load-failed"
            )
            {
                yield return null;
            }
            Assert.That(
                Field(flow, "_view"),
                Is.Not.Null,
                "UI initialization must complete within 10 seconds; error=" + flow.Error
            );
        }

        private static object Field(object instance, string name) =>
            instance
                .GetType()
                .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
                .GetValue(instance);

        private static void Invoke(object instance, string name, params object[] arguments) =>
            instance
                .GetType()
                .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(instance, arguments);
    }
}
