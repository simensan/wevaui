using System.Collections.Generic;
using NUnit.Framework;
using Weva.Layout;

namespace Weva.Tests.Layout {
    // Audit L15: the layout measurement caches (fastMeasureCache,
    // measureCache, measureWindowCache) full-Clear()-ed at the cap, flipping a
    // high-text-variety layout from a warm cache to cold and re-measuring
    // everything. LayoutCacheEviction.EnsureRoom slice-evicts instead.
    public class LayoutCacheEvictionTests {
        [Test]
        public void Below_cap_is_a_noop() {
            var d = new Dictionary<int, double>();
            for (int i = 0; i < 10; i++) d[i] = i;
            LayoutCacheEviction.EnsureRoom(d, 16);
            Assert.That(d.Count, Is.EqualTo(10));
        }

        [Test]
        public void At_cap_evicts_a_quarter_and_leaves_room() {
            const int cap = 16;
            var d = new Dictionary<int, double>();
            for (int i = 0; i < cap; i++) d[i] = i;
            LayoutCacheEviction.EnsureRoom(d, cap);
            Assert.That(d.Count, Is.EqualTo(cap - cap / 4));
            Assert.That(d.Count, Is.LessThan(cap));
        }

        [Test]
        public void Churn_keeps_cache_bounded_and_never_drops_the_new_entry() {
            const int cap = 8;
            var d = new Dictionary<int, double>();
            for (int i = 0; i < 500; i++) {
                LayoutCacheEviction.EnsureRoom(d, cap);
                d[i] = i;
                Assert.That(d.ContainsKey(i), Is.True);
                Assert.That(d.Count, Is.LessThanOrEqualTo(cap));
            }
            // After heavy churn the cache is still near-full (warm), not cold.
            Assert.That(d.Count, Is.GreaterThan(cap / 2));
        }

        [Test]
        public void Tiny_cap_clamps_batch_to_one() {
            var d = new Dictionary<int, double> { { 0, 0 }, { 1, 1 } };
            LayoutCacheEviction.EnsureRoom(d, 2);
            Assert.That(d.Count, Is.EqualTo(1));
        }
    }
}
