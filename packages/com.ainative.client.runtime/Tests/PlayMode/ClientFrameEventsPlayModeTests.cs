using System.Collections;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace AiNative.Client.Runtime.Tests
{
    public sealed class ClientFrameEventsPlayModeTests
    {
        [UnityTest]
        public IEnumerator FramesStayOnOwnerThreadAndUnsubscriptionStopsDelivery()
        {
            var root = new GameObject("FrameEventsTest");
            var relay = root.AddComponent<ClientFrameEvents>();
            int ownerThread = Thread.CurrentThread.ManagedThreadId;
            int updates = 0;
            int lateUpdates = 0;
            int fixedUpdates = 0;
            bool wrongThread = false;
            System.Action<float> update = seconds =>
            {
                wrongThread |= Thread.CurrentThread.ManagedThreadId != ownerThread;
                Assert.That(seconds, Is.GreaterThanOrEqualTo(0));
                updates++;
            };
            relay.Updating += update;
            relay.LateUpdating += seconds => lateUpdates++;
            relay.FixedUpdating += seconds => fixedUpdates++;
            try
            {
                float deadline = Time.realtimeSinceStartup + 3;
                while (fixedUpdates == 0 && Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                }
                Assert.That(updates, Is.GreaterThan(0));
                Assert.That(lateUpdates, Is.GreaterThan(0));
                Assert.That(fixedUpdates, Is.GreaterThan(0));
                Assert.That(wrongThread, Is.False);
                relay.Updating -= update;
                int stoppedCount = updates;
                yield return null;
                yield return null;
                Assert.That(updates, Is.EqualTo(stoppedCount));
            }
            finally
            {
                Object.Destroy(root);
            }
        }

        [UnityTest]
        public IEnumerator DestroyingOneRelayDoesNotAffectAnotherInstance()
        {
            var first = new GameObject("FirstFrameOwner");
            var second = new GameObject("SecondFrameOwner");
            int firstCalls = 0;
            int secondCalls = 0;
            first.AddComponent<ClientFrameEvents>().Updating += seconds => firstCalls++;
            second.AddComponent<ClientFrameEvents>().Updating += seconds => secondCalls++;
            try
            {
                yield return null;
                Object.Destroy(first);
                yield return null;
                int releasedCalls = firstCalls;
                int otherCalls = secondCalls;
                yield return null;
                yield return null;
                Assert.That(firstCalls, Is.EqualTo(releasedCalls));
                Assert.That(secondCalls, Is.GreaterThan(otherCalls));
            }
            finally
            {
                if (first != null)
                {
                    Object.Destroy(first);
                }
                Object.Destroy(second);
            }
        }
    }
}
