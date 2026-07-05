using System;
using NUnit.Framework;
using Weva.Paint;

namespace Weva.Tests.Paint {
    public class Transform2DTests {
        const double Eps = 1e-5;

        static void AssertPointEqual(Transform2D t, double x, double y, double ex, double ey) {
            var (rx, ry) = t.Apply(x, y);
            Assert.That(rx, Is.EqualTo(ex).Within(Eps), "x mismatch");
            Assert.That(ry, Is.EqualTo(ey).Within(Eps), "y mismatch");
        }

        [Test]
        public void Identity_leaves_points_unchanged() {
            AssertPointEqual(Transform2D.Identity, 1, 2, 1, 2);
            AssertPointEqual(Transform2D.Identity, -3, 4, -3, 4);
        }

        [Test]
        public void Translate_adds_offset() {
            var t = Transform2D.Translate(5, 7);
            AssertPointEqual(t, 0, 0, 5, 7);
            AssertPointEqual(t, 1, -2, 6, 5);
        }

        [Test]
        public void Scale_multiplies_axes_independently() {
            var t = Transform2D.Scale(2, 3);
            AssertPointEqual(t, 1, 1, 2, 3);
            AssertPointEqual(t, -1, 4, -2, 12);
        }

        [Test]
        public void Rotate_90_degrees_about_origin_sends_x_axis_to_y_axis() {
            var t = Transform2D.Rotate(90);
            AssertPointEqual(t, 1, 0, 0, 1);
            AssertPointEqual(t, 0, 1, -1, 0);
        }

        [Test]
        public void Rotate_180_degrees_negates_both_axes() {
            var t = Transform2D.Rotate(180);
            AssertPointEqual(t, 1, 0, -1, 0);
            AssertPointEqual(t, 0, 1, 0, -1);
        }

        [Test]
        public void Composition_is_non_commutative() {
            var t = Transform2D.Translate(10, 0);
            var s = Transform2D.Scale(2, 2);
            // translate then scale: (1,1) -> (11,1) -> (22,2)
            var ts = t.Multiply(s);
            // scale then translate: (1,1) -> (2,2) -> (12,2)
            var st = s.Multiply(t);
            AssertPointEqual(ts, 1, 1, 22, 2);
            AssertPointEqual(st, 1, 1, 12, 2);
            Assert.That(ts == st, Is.False);
        }

        [Test]
        public void Identity_is_multiply_neutral() {
            var t = Transform2D.Translate(3, 4).Multiply(Transform2D.Scale(2, 3));
            var ti = t.Multiply(Transform2D.Identity);
            var it = Transform2D.Identity.Multiply(t);
            AssertPointEqual(ti, 1, 1, t.Apply(1, 1).X, t.Apply(1, 1).Y);
            AssertPointEqual(it, 1, 1, t.Apply(1, 1).X, t.Apply(1, 1).Y);
        }

        [Test]
        public void Equality_and_hashcode() {
            var a = Transform2D.Translate(1, 2);
            var b = Transform2D.Translate(1, 2);
            var c = Transform2D.Translate(1, 3);
            Assert.That(a, Is.EqualTo(b));
            Assert.That(a == b, Is.True);
            Assert.That(a != c, Is.True);
            Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));
        }

        [Test]
        public void Rotate_then_translate_applies_in_order() {
            // Rotate 90 first, then translate by (5, 0): (1,0) -> (0,1) -> (5,1)
            var combined = Transform2D.Rotate(90).Multiply(Transform2D.Translate(5, 0));
            AssertPointEqual(combined, 1, 0, 5, 1);
        }
    }
}
