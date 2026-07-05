using NUnit.Framework;
using Weva.Paint;

namespace Weva.Tests.Paint {
    public class RectTests {
        [Test]
        public void Empty_has_zero_size() {
            Assert.That(Rect.Empty.IsEmpty, Is.True);
        }

        [Test]
        public void IsEmpty_when_width_or_height_nonpositive() {
            Assert.That(new Rect(0, 0, 0, 10).IsEmpty, Is.True);
            Assert.That(new Rect(0, 0, 10, 0).IsEmpty, Is.True);
            Assert.That(new Rect(0, 0, 10, 10).IsEmpty, Is.False);
            Assert.That(new Rect(0, 0, -1, 10).IsEmpty, Is.True);
        }

        [Test]
        public void Right_and_Bottom_are_X_plus_W_and_Y_plus_H() {
            var r = new Rect(2, 3, 10, 20);
            Assert.That(r.Right, Is.EqualTo(12));
            Assert.That(r.Bottom, Is.EqualTo(23));
        }

        [Test]
        public void Contains_includes_top_left_excludes_bottom_right() {
            var r = new Rect(0, 0, 10, 10);
            Assert.That(r.Contains(0, 0), Is.True);
            Assert.That(r.Contains(5, 5), Is.True);
            Assert.That(r.Contains(9.999, 9.999), Is.True);
            Assert.That(r.Contains(10, 10), Is.False);
            Assert.That(r.Contains(-0.001, 0), Is.False);
            Assert.That(r.Contains(0, -0.001), Is.False);
        }

        [Test]
        public void Intersect_overlapping_rects_returns_overlap() {
            var a = new Rect(0, 0, 10, 10);
            var b = new Rect(5, 5, 10, 10);
            var i = a.Intersect(b);
            Assert.That(i.X, Is.EqualTo(5));
            Assert.That(i.Y, Is.EqualTo(5));
            Assert.That(i.Width, Is.EqualTo(5));
            Assert.That(i.Height, Is.EqualTo(5));
        }

        [Test]
        public void Intersect_disjoint_rects_returns_empty() {
            var a = new Rect(0, 0, 10, 10);
            var b = new Rect(20, 20, 5, 5);
            Assert.That(a.Intersect(b).IsEmpty, Is.True);
        }

        [Test]
        public void Intersect_self_is_self() {
            var a = new Rect(3, 4, 7, 8);
            Assert.That(a.Intersect(a), Is.EqualTo(a));
        }

        [Test]
        public void Equality_componentwise() {
            var a = new Rect(1, 2, 3, 4);
            var b = new Rect(1, 2, 3, 4);
            var c = new Rect(1, 2, 3, 5);
            Assert.That(a, Is.EqualTo(b));
            Assert.That(a == b, Is.True);
            Assert.That(a != c, Is.True);
            Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));
        }
    }
}
