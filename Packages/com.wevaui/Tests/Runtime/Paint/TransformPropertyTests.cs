using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Paint;
using Weva.Paint.Conversion;

namespace Weva.Tests.Paint {
    // Broad CSS `transform` coverage: each of the 8 function families plus
    // transform-origin and composition. Complements Transform2DTests
    // (matrix math) and TransformResolverTests (resolver smoke). The helpers
    // mirror the resolver-test convention — build a ComputedStyle, set the
    // raw CSS string, call ResolveTransform, then inspect Transform2D matrix
    // components or pump points through Apply().
    public class TransformPropertyTests {
        const double Eps = 1e-4;
        static ComputedStyle Style() => new ComputedStyle(new Element("div"));

        static Transform2D Resolve(string transform, double refW = 0, double refH = 0) {
            var s = Style();
            s.Set("transform", transform);
            return TransformResolver.ResolveTransform(s, refW, refH);
        }

        // ---- translate family ----

        [Test]
        public void Translate_single_value_treats_y_as_zero() {
            var t = Resolve("translate(40px)");
            Assert.That(t.Tx, Is.EqualTo(40).Within(Eps));
            Assert.That(t.Ty, Is.EqualTo(0).Within(Eps));
        }

        [Test]
        public void Translate_two_values_sets_x_and_y() {
            var t = Resolve("translate(40px, 20px)");
            Assert.That(t.Tx, Is.EqualTo(40).Within(Eps));
            Assert.That(t.Ty, Is.EqualTo(20).Within(Eps));
        }

        [Test]
        public void Translate_percent_resolves_against_box_dimensions() {
            // CSS Transforms L1 §13.4: translate percentages resolve against
            // the reference box width (X) and height (Y) respectively.
            var t = Resolve("translate(50%, 50%)", refW: 200, refH: 100);
            Assert.That(t.Tx, Is.EqualTo(100).Within(Eps));
            Assert.That(t.Ty, Is.EqualTo(50).Within(Eps));
        }

        [Test]
        public void TranslateX_only_affects_x() {
            var t = Resolve("translateX(30px)");
            Assert.That(t.Tx, Is.EqualTo(30).Within(Eps));
            Assert.That(t.Ty, Is.EqualTo(0).Within(Eps));
        }

        [Test]
        public void TranslateY_only_affects_y() {
            var t = Resolve("translateY(30px)");
            Assert.That(t.Tx, Is.EqualTo(0).Within(Eps));
            Assert.That(t.Ty, Is.EqualTo(30).Within(Eps));
        }

        // ---- scale family ----

        [Test]
        public void Scale_uniform_with_one_value() {
            var t = Resolve("scale(2)");
            Assert.That(t.A, Is.EqualTo(2).Within(Eps));
            Assert.That(t.D, Is.EqualTo(2).Within(Eps));
            Assert.That(t.B, Is.EqualTo(0).Within(Eps));
            Assert.That(t.C, Is.EqualTo(0).Within(Eps));
        }

        [Test]
        public void Scale_separate_x_y() {
            var t = Resolve("scale(2, 0.5)");
            Assert.That(t.A, Is.EqualTo(2).Within(Eps));
            Assert.That(t.D, Is.EqualTo(0.5).Within(Eps));
        }

        // ---- rotate family ----

        [Test]
        public void Rotate_degrees() {
            // rotate(90deg) sends (1, 0) to (0, 1) about the origin.
            // Transform2D.Apply uses column-vector semantics:
            //   x' = A*x + C*y + Tx,  y' = B*x + D*y + Ty.
            var t = Resolve("rotate(90deg)");
            var (x, y) = t.Apply(1, 0);
            Assert.That(x, Is.EqualTo(0).Within(Eps));
            Assert.That(y, Is.EqualTo(1).Within(Eps));
        }

        [Test]
        public void Rotate_radians() {
            // π/2 rad ≈ 1.5708 ≈ 90°.
            var t = Resolve("rotate(1.5707963267948966rad)");
            var (x, y) = t.Apply(1, 0);
            Assert.That(x, Is.EqualTo(0).Within(Eps));
            Assert.That(y, Is.EqualTo(1).Within(Eps));
        }

        [Test]
        public void Rotate_turn() {
            // 0.25 turn = 90°.
            var t = Resolve("rotate(0.25turn)");
            var (x, y) = t.Apply(1, 0);
            Assert.That(x, Is.EqualTo(0).Within(Eps));
            Assert.That(y, Is.EqualTo(1).Within(Eps));
        }

        // ---- skew family ----

        [Test]
        public void Skew_one_value_treats_y_as_zero() {
            // skew(20deg) = skew(20deg, 0). Resolver builds matrix
            //   [ 1       tan(20°) ]   <- the resolver's row-major encoding
            //   [ tan(0)  1        ]
            // i.e. C = tan(20°), B = tan(0) = 0.
            var t = Resolve("skew(20deg)");
            double tan20 = System.Math.Tan(20 * System.Math.PI / 180.0);
            Assert.That(t.C, Is.EqualTo(tan20).Within(Eps));
            Assert.That(t.B, Is.EqualTo(0).Within(Eps));
            Assert.That(t.A, Is.EqualTo(1).Within(Eps));
            Assert.That(t.D, Is.EqualTo(1).Within(Eps));
        }

        [Test]
        public void Skew_two_values() {
            var t = Resolve("skew(20deg, 10deg)");
            double tan20 = System.Math.Tan(20 * System.Math.PI / 180.0);
            double tan10 = System.Math.Tan(10 * System.Math.PI / 180.0);
            Assert.That(t.C, Is.EqualTo(tan20).Within(Eps));
            Assert.That(t.B, Is.EqualTo(tan10).Within(Eps));
            Assert.That(t.A, Is.EqualTo(1).Within(Eps));
            Assert.That(t.D, Is.EqualTo(1).Within(Eps));
        }

        // ---- matrix ----

        [Test]
        public void Matrix_six_args() {
            // matrix(1, 0, 0, 1, 50, 30) is identity rotation/scale + a
            // (50, 30) translation. Equivalent to translate(50px, 30px).
            var t = Resolve("matrix(1, 0, 0, 1, 50, 30)");
            Assert.That(t.A, Is.EqualTo(1).Within(Eps));
            Assert.That(t.B, Is.EqualTo(0).Within(Eps));
            Assert.That(t.C, Is.EqualTo(0).Within(Eps));
            Assert.That(t.D, Is.EqualTo(1).Within(Eps));
            Assert.That(t.Tx, Is.EqualTo(50).Within(Eps));
            Assert.That(t.Ty, Is.EqualTo(30).Within(Eps));
        }

        // ---- transform-origin ----
        // v1: the engine registers `transform-origin` as a cascadeable
        // property (CssProperties.TransformOriginId, default "50% 50% 0") and
        // tracks it as a wrapper property so paint reflows pick up changes,
        // but TransformResolver does NOT apply the origin to the resolved
        // matrix — rotate/scale always pivot at (0, 0) in local space.
        // Compositing of transform-origin into the matrix is a future task.
        // These tests therefore assert the cascade-stored raw string only.

        [Test]
        public void Transform_origin_default_is_center() {
            // Default is "50% 50% 0" per CSS Transforms L1 §6.1, but on a
            // raw `new ComputedStyle(Element)` (no CascadeEngine compute)
            // the slot is empty and Get returns null. Cascade-computed
            // styles fill in the initial value.
            var s = Style();
            Assert.That(s.Get(CssProperties.TransformOriginId), Is.Null);
            // Round-trip the initial value to confirm it's the documented
            // "50% 50% 0" two-axis-plus-z form (i.e. center).
            Assert.That(CssProperties.InitialValueOf("transform-origin"),
                Is.EqualTo("50% 50% 0"));
        }

        [Test]
        public void Transform_origin_keyword_top_left() {
            var s = Style();
            s.Set("transform-origin", "top left");
            Assert.That(s.Get(CssProperties.TransformOriginId),
                Is.EqualTo("top left"));
            // v1: keywords pass through as raw strings — the engine does
            // not yet normalise them to "0% 0%". A future origin-compositor
            // pass will need to interpret these.
        }

        [Test]
        public void Transform_origin_percent() {
            var s = Style();
            s.Set("transform-origin", "25% 75%");
            Assert.That(s.Get(CssProperties.TransformOriginId),
                Is.EqualTo("25% 75%"));
        }

        // ---- composition ----

        [Test]
        public void Transform_composition_translate_then_rotate() {
            // CSS transform list order: the rightmost function acts on the
            // point first. translate(50px) rotate(45deg) rotates the local
            // box, then translates it. Reversing the functions rotates the
            // translated point around the origin.
            var tr = Resolve("translate(50px) rotate(45deg)");
            var rt = Resolve("rotate(45deg) translate(50px)");

            Assert.That(tr, Is.Not.EqualTo(rt),
                "translate then rotate must differ from rotate then translate");

            var (trx, tryp) = tr.Apply(0, 0);
            Assert.That(trx, Is.EqualTo(50).Within(Eps));
            Assert.That(tryp, Is.EqualTo(0).Within(Eps));

            var (rtx, rty) = rt.Apply(0, 0);
            double s45 = System.Math.Sqrt(0.5) * 50;
            Assert.That(rtx, Is.EqualTo(s45).Within(Eps));
            Assert.That(rty, Is.EqualTo(s45).Within(Eps));
        }

        [Test]
        public void Transform_none_clears_existing() {
            var s = Style();
            s.Set("transform", "translate(50px, 50px)");
            var pre = TransformResolver.ResolveTransform(s, 0, 0);
            Assert.That(pre, Is.Not.EqualTo(Transform2D.Identity));

            s.Set("transform", "none");
            var post = TransformResolver.ResolveTransform(s, 0, 0);
            Assert.That(post, Is.EqualTo(Transform2D.Identity));
        }
    }
}
