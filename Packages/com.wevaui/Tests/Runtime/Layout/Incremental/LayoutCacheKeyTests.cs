using NUnit.Framework;
using Weva.Layout.Incremental;

namespace Weva.Tests.Layout.Incremental {
    public class LayoutCacheKeyTests {
        [Test]
        public void Keys_with_same_versions_are_equal() {
            var a = new LayoutCacheKey(1, 2, 3, 4, 5, 6);
            var b = new LayoutCacheKey(1, 2, 3, 4, 5, 6);
            Assert.That(a.Equals(b), Is.True);
            Assert.That(a == b, Is.True);
            Assert.That(a != b, Is.False);
            Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));
        }

        [Test]
        public void Keys_differ_when_element_version_differs() {
            var a = new LayoutCacheKey(1, 2, 3, 4, 5, 6);
            var b = new LayoutCacheKey(99, 2, 3, 4, 5, 6);
            Assert.That(a.Equals(b), Is.False);
            Assert.That(a != b, Is.True);
        }

        [Test]
        public void Keys_differ_when_computed_style_version_differs() {
            var a = new LayoutCacheKey(1, 2, 3, 4, 5, 6);
            var b = new LayoutCacheKey(1, 99, 3, 4, 5, 6);
            Assert.That(a.Equals(b), Is.False);
        }

        [Test]
        public void Keys_differ_when_container_width_differs() {
            var a = new LayoutCacheKey(1, 2, 3, 4, 5, 6);
            var b = new LayoutCacheKey(1, 2, 99, 4, 5, 6);
            Assert.That(a.Equals(b), Is.False);
        }

        [Test]
        public void Keys_differ_when_container_height_differs() {
            var a = new LayoutCacheKey(1, 2, 3, 4, 5, 6);
            var b = new LayoutCacheKey(1, 2, 3, 99, 5, 6);
            Assert.That(a.Equals(b), Is.False);
        }

        [Test]
        public void Keys_differ_when_layout_context_version_differs() {
            var a = new LayoutCacheKey(1, 2, 3, 4, 5, 6);
            var b = new LayoutCacheKey(1, 2, 3, 4, 99, 6);
            Assert.That(a.Equals(b), Is.False);
        }

        [Test]
        public void Keys_differ_when_child_aggregate_version_differs() {
            var a = new LayoutCacheKey(1, 2, 3, 4, 5, 6);
            var b = new LayoutCacheKey(1, 2, 3, 4, 5, 99);
            Assert.That(a.Equals(b), Is.False);
        }

        [Test]
        public void Hashcodes_equal_for_equal_keys() {
            var a = new LayoutCacheKey(7, 7, 7, 7, 7, 7);
            var b = new LayoutCacheKey(7, 7, 7, 7, 7, 7);
            Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));
        }

        [Test]
        public void ToString_is_diagnostic() {
            var a = new LayoutCacheKey(11, 22, 33, 44, 55, 66);
            string s = a.ToString();
            Assert.That(s, Does.Contain("11"));
            Assert.That(s, Does.Contain("22"));
            Assert.That(s, Does.Contain("33"));
            Assert.That(s, Does.Contain("44"));
            Assert.That(s, Does.Contain("55"));
            Assert.That(s, Does.Contain("66"));
        }

        [Test]
        public void Boxed_equals_works() {
            var a = new LayoutCacheKey(1, 2, 3, 4, 5, 6);
            object b = new LayoutCacheKey(1, 2, 3, 4, 5, 6);
            Assert.That(a.Equals(b), Is.True);
            Assert.That(a.Equals("not a key"), Is.False);
        }
    }
}
