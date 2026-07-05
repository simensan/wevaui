using NUnit.Framework;
using Weva.Paint.Conversion.Incremental;

namespace Weva.Tests.Paint.Conversion.Incremental {
    public class PaintCacheKeyTests {
        [Test]
        public void Equal_when_all_three_versions_match() {
            var a = new PaintCacheKey(1, 2, 3);
            var b = new PaintCacheKey(1, 2, 3);
            Assert.That(a.Equals(b), Is.True);
            Assert.That(a == b, Is.True);
            Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));
        }

        [Test]
        public void Differ_when_box_version_differs() {
            var a = new PaintCacheKey(1, 2, 3);
            var b = new PaintCacheKey(99, 2, 3);
            Assert.That(a.Equals(b), Is.False);
            Assert.That(a != b, Is.True);
        }

        [Test]
        public void Differ_when_style_version_differs() {
            var a = new PaintCacheKey(1, 2, 3);
            var b = new PaintCacheKey(1, 99, 3);
            Assert.That(a.Equals(b), Is.False);
        }

        [Test]
        public void Differ_when_context_version_differs() {
            var a = new PaintCacheKey(1, 2, 3);
            var b = new PaintCacheKey(1, 2, 99);
            Assert.That(a.Equals(b), Is.False);
        }

        [Test]
        public void Equals_object_works_only_for_PaintCacheKey() {
            var a = new PaintCacheKey(1, 2, 3);
            Assert.That(a.Equals((object)new PaintCacheKey(1, 2, 3)), Is.True);
            Assert.That(a.Equals((object)"not a key"), Is.False);
            Assert.That(a.Equals((object)null), Is.False);
        }

        [Test]
        public void ToString_includes_all_three_versions() {
            var a = new PaintCacheKey(11, 22, 33);
            var s = a.ToString();
            Assert.That(s, Does.Contain("11"));
            Assert.That(s, Does.Contain("22"));
            Assert.That(s, Does.Contain("33"));
        }
    }
}
