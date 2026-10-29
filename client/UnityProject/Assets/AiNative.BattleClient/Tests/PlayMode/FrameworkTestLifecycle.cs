using System.Collections;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;

namespace AiNative.Client.Application.PlayModeTests
{
    internal static class FrameworkTestLifecycle
    {
        internal static IEnumerator Start(GameObject owner)
        {
            Task starting = FrameworkClientRoot.GetOrCreate(owner).StartAsync().AsTask();
            float deadline = Time.realtimeSinceStartup + 30f;
            while (!starting.IsCompleted && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }
            Assert.That(starting.IsCompleted, Is.True, "显式应用启动必须有界完成。");
            starting.GetAwaiter().GetResult();
        }
    }
}
