using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace AiNative.Client.UI.Tests
{
    public sealed class KeyedViewListTests
    {
        [Test]
        public void StableKeysReuseViewsAndUpdateOrder()
        {
            int created = 0;
            var released = new List<int>();
            var rendered = new List<string>();
            using (
                var list = new KeyedViewList<int, int, int>(
                    value => value,
                    _ => ++created,
                    (view, value, index) => rendered.Add($"{view}:{value}:{index}"),
                    released.Add
                )
            )
            {
                list.Apply(new[] { 10, 20 });
                rendered.Clear();
                list.Apply(new[] { 20, 10, 30 });
                Assert.That(created, Is.EqualTo(3));
                Assert.That(rendered, Is.EqualTo(new[] { "2:20:0", "1:10:1", "3:30:2" }));
                list.Apply(new[] { 20 });
                Assert.That(released, Is.EquivalentTo(new[] { 1, 3 }));
            }
            Assert.That(released, Is.EquivalentTo(new[] { 1, 2, 3 }));
        }

        [Test]
        public void DuplicateKeysRejectBeforeAnyMutation()
        {
            int created = 0;
            int rendered = 0;
            int released = 0;
            using (
                var list = new KeyedViewList<int, int, int>(
                    value => value,
                    _ => ++created,
                    (_, __, ___) => rendered++,
                    _ => released++
                )
            )
            {
                list.Apply(new[] { 1 });
                Assert.Throws<ArgumentException>(() => list.Apply(new[] { 2, 2 }));
                Assert.That(created, Is.EqualTo(1));
                Assert.That(rendered, Is.EqualTo(1));
                Assert.That(released, Is.Zero);
                list.Apply(new[] { 1 });
                Assert.That(created, Is.EqualTo(1));
            }
        }

        [Test]
        public void DisposalContinuesAfterOneReleaseFails()
        {
            var released = new List<int>();
            var list = new KeyedViewList<int, int, int>(
                value => value,
                value => value,
                (_, __, ___) => { },
                value =>
                {
                    released.Add(value);
                    if (value == 1)
                    {
                        throw new InvalidOperationException("release-failed");
                    }
                }
            );
            list.Apply(new[] { 1, 2 });
            Assert.Throws<AggregateException>(list.Dispose);
            Assert.That(released, Is.EquivalentTo(new[] { 1, 2 }));
            Assert.DoesNotThrow(list.Dispose);
            Assert.Throws<ObjectDisposedException>(() => list.Apply(Array.Empty<int>()));
        }
    }
}
