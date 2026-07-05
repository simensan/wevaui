using NUnit.Framework;
using Weva.Css.Cascade;

namespace Weva.Tests.Css.Cascade {
    public class CascadeCacheKeyTests {
        [Test]
        public void Equal_when_all_versions_match() {
            var a = new IncrementalCacheKey(1, 2, 3, 4, 5);
            var b = new IncrementalCacheKey(1, 2, 3, 4, 5);
            Assert.That(a.Equals(b), Is.True);
            Assert.That(a == b, Is.True);
            Assert.That(a != b, Is.False);
        }

        [Test]
        public void Differ_when_element_version_differs() {
            var a = new IncrementalCacheKey(1, 2, 3, 4, 5);
            var b = new IncrementalCacheKey(99, 2, 3, 4, 5);
            Assert.That(a.Equals(b), Is.False);
            Assert.That(a != b, Is.True);
        }

        [Test]
        public void Differ_when_parent_version_differs() {
            var a = new IncrementalCacheKey(1, 2, 3, 4, 5);
            var b = new IncrementalCacheKey(1, 99, 3, 4, 5);
            Assert.That(a.Equals(b), Is.False);
        }

        [Test]
        public void Differ_when_media_version_differs() {
            var a = new IncrementalCacheKey(1, 2, 3, 4, 5);
            var b = new IncrementalCacheKey(1, 2, 99, 4, 5);
            Assert.That(a.Equals(b), Is.False);
        }

        [Test]
        public void Differ_when_state_version_differs() {
            var a = new IncrementalCacheKey(1, 2, 3, 4, 5);
            var b = new IncrementalCacheKey(1, 2, 3, 99, 5);
            Assert.That(a.Equals(b), Is.False);
        }

        [Test]
        public void Differ_when_provider_id_differs() {
            var a = new IncrementalCacheKey(1, 2, 3, 4, 5);
            var b = new IncrementalCacheKey(1, 2, 3, 4, 99);
            Assert.That(a.Equals(b), Is.False);
        }

        [Test]
        public void Hashcodes_equal_for_equal_keys() {
            var a = new IncrementalCacheKey(7, 8, 9, 10, 11);
            var b = new IncrementalCacheKey(7, 8, 9, 10, 11);
            Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));
        }

        [Test]
        public void Hashcodes_typically_differ_for_unequal_keys() {
            var a = new IncrementalCacheKey(1, 2, 3, 4, 5);
            var b = new IncrementalCacheKey(5, 4, 3, 2, 1);
            Assert.That(a.GetHashCode(), Is.Not.EqualTo(b.GetHashCode()));
        }

        [Test]
        public void Equals_via_object_works() {
            var a = new IncrementalCacheKey(1, 2, 3, 4, 5);
            object b = new IncrementalCacheKey(1, 2, 3, 4, 5);
            Assert.That(a.Equals(b), Is.True);
            Assert.That(a.Equals("not a key"), Is.False);
            Assert.That(a.Equals(null), Is.False);
        }

        [Test]
        public void ToString_includes_all_versions() {
            var a = new IncrementalCacheKey(11, 22, 33, 44, 55);
            string s = a.ToString();
            Assert.That(s, Does.Contain("11"));
            Assert.That(s, Does.Contain("22"));
            Assert.That(s, Does.Contain("33"));
            Assert.That(s, Does.Contain("44"));
            Assert.That(s, Does.Contain("55"));
        }

        [Test]
        public void Default_struct_equals_zero_initialized() {
            var a = default(IncrementalCacheKey);
            var b = new IncrementalCacheKey(0, 0, 0, 0, 0);
            Assert.That(a.Equals(b), Is.True);
        }
    }
}
