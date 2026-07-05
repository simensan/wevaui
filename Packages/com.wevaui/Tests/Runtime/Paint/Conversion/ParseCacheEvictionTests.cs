using System.Collections.Generic;
using NUnit.Framework;
using Weva.Paint.Conversion;

namespace Weva.Tests.Paint.Conversion {
    // Audit P16: the gradient / filter parse caches were drop-new-on-overflow,
    // which permanently locked reusable values out of caching once an animated
    // value flooded the cap. ParseCacheEviction.EnsureRoom slice-evicts instead
    // so new entries can always land.
    public class ParseCacheEvictionTests {
        [Test]
        public void EnsureRoom_below_cap_is_a_noop() {
            var d = new Dictionary<int, int>();
            for (int i = 0; i < 10; i++) d[i] = i;
            ParseCacheEviction.EnsureRoom(d, 16);
            Assert.That(d.Count, Is.EqualTo(10), "below cap, nothing is evicted");
        }

        [Test]
        public void EnsureRoom_at_cap_evicts_a_quarter_slice() {
            const int cap = 16;
            var d = new Dictionary<int, int>();
            for (int i = 0; i < cap; i++) d[i] = i;
            ParseCacheEviction.EnsureRoom(d, cap);
            // cap/4 == 4 dropped, leaving room for the caller's next Add.
            Assert.That(d.Count, Is.EqualTo(cap - cap / 4));
            Assert.That(d.Count, Is.LessThan(cap), "must leave room for one more entry");
        }

        [Test]
        public void Repeated_overflow_never_locks_out_new_entries() {
            // The drop-new bug: once full, a NEW key could never be cached.
            // Simulate an animated value churning many novel keys through a
            // small cap and assert every Add lands (Count stays bounded, and
            // the most-recent key is always present).
            const int cap = 8;
            var d = new Dictionary<int, int>();
            for (int i = 0; i < 1000; i++) {
                ParseCacheEviction.EnsureRoom(d, cap);
                d[i] = i;
                Assert.That(d.ContainsKey(i), Is.True, "the freshly-added key must always be cached");
                Assert.That(d.Count, Is.LessThanOrEqualTo(cap), "cache stays bounded");
            }
        }

        [Test]
        public void HashSet_variant_evicts_a_slice() {
            const int cap = 12;
            var s = new HashSet<string>();
            for (int i = 0; i < cap; i++) s.Add("k" + i);
            ParseCacheEviction.EnsureRoom(s, cap);
            Assert.That(s.Count, Is.EqualTo(cap - cap / 4));
        }

        [Test]
        public void Tiny_cap_still_evicts_at_least_one() {
            var d = new Dictionary<int, int> { { 0, 0 }, { 1, 1 } };
            ParseCacheEviction.EnsureRoom(d, 2); // cap>>2 == 0 -> clamps to 1
            Assert.That(d.Count, Is.EqualTo(1));
        }
    }
}
