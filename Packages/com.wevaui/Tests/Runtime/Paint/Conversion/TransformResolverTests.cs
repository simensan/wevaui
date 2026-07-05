using NUnit.Framework;
using Weva.Css.Animation;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Dom;
using Weva.Paint;
using Weva.Paint.Conversion;

namespace Weva.Tests.Paint.Conversion {
    public class TransformResolverTests {
        const double Eps = 1e-4;
        static ComputedStyle Style() => new ComputedStyle(new Element("div"));

        [Test]
        public void None_returns_identity() {
            var s = Style();
            s.Set("transform", "none");
            var t = TransformResolver.ResolveTransform(s, 0, 0);
            Assert.That(t, Is.EqualTo(Transform2D.Identity));
        }

        [Test]
        public void Translate_xy_applies_offset() {
            var s = Style();
            s.Set("transform", "translate(10px, 20px)");
            var t = TransformResolver.ResolveTransform(s, 0, 0);
            var (x, y) = t.Apply(0, 0);
            Assert.That(x, Is.EqualTo(10).Within(Eps));
            Assert.That(y, Is.EqualTo(20).Within(Eps));
        }

        [Test]
        public void TranslateX_and_TranslateY() {
            var s1 = Style();
            s1.Set("transform", "translateX(5px)");
            var t1 = TransformResolver.ResolveTransform(s1, 0, 0);
            Assert.That(t1.Tx, Is.EqualTo(5).Within(Eps));
            Assert.That(t1.Ty, Is.EqualTo(0).Within(Eps));

            var s2 = Style();
            s2.Set("transform", "translateY(7px)");
            var t2 = TransformResolver.ResolveTransform(s2, 0, 0);
            Assert.That(t2.Tx, Is.EqualTo(0).Within(Eps));
            Assert.That(t2.Ty, Is.EqualTo(7).Within(Eps));
        }

        [Test]
        public void Scale_uniform_and_xy() {
            var s1 = Style();
            s1.Set("transform", "scale(2)");
            var t1 = TransformResolver.ResolveTransform(s1, 0, 0);
            Assert.That(t1.A, Is.EqualTo(2).Within(Eps));
            Assert.That(t1.D, Is.EqualTo(2).Within(Eps));

            var s2 = Style();
            s2.Set("transform", "scale(2, 3)");
            var t2 = TransformResolver.ResolveTransform(s2, 0, 0);
            Assert.That(t2.A, Is.EqualTo(2).Within(Eps));
            Assert.That(t2.D, Is.EqualTo(3).Within(Eps));
        }

        [Test]
        public void ScaleX_ScaleY() {
            var s1 = Style();
            s1.Set("transform", "scaleX(2)");
            var t1 = TransformResolver.ResolveTransform(s1, 0, 0);
            Assert.That(t1.A, Is.EqualTo(2).Within(Eps));
            Assert.That(t1.D, Is.EqualTo(1).Within(Eps));

            var s2 = Style();
            s2.Set("transform", "scaleY(3)");
            var t2 = TransformResolver.ResolveTransform(s2, 0, 0);
            Assert.That(t2.A, Is.EqualTo(1).Within(Eps));
            Assert.That(t2.D, Is.EqualTo(3).Within(Eps));
        }

        [Test]
        public void Rotate_degrees_turn_rad_grad() {
            var sDeg = Style();
            sDeg.Set("transform", "rotate(90deg)");
            var tDeg = TransformResolver.ResolveTransform(sDeg, 0, 0);
            var (x1, y1) = tDeg.Apply(1, 0);
            Assert.That(x1, Is.EqualTo(0).Within(Eps));
            Assert.That(y1, Is.EqualTo(1).Within(Eps));

            var sTurn = Style();
            sTurn.Set("transform", "rotate(0.25turn)");
            var tTurn = TransformResolver.ResolveTransform(sTurn, 0, 0);
            Assert.That(tTurn, Is.EqualTo(tDeg));

            var sRad = Style();
            sRad.Set("transform", "rotate(1.5707963267948966rad)");
            var tRad = TransformResolver.ResolveTransform(sRad, 0, 0);
            var (rx, ry) = tRad.Apply(1, 0);
            Assert.That(rx, Is.EqualTo(0).Within(Eps));
            Assert.That(ry, Is.EqualTo(1).Within(Eps));

            var sGrad = Style();
            sGrad.Set("transform", "rotate(100grad)");
            var tGrad = TransformResolver.ResolveTransform(sGrad, 0, 0);
            var (gx, gy) = tGrad.Apply(1, 0);
            Assert.That(gx, Is.EqualTo(0).Within(Eps));
            Assert.That(gy, Is.EqualTo(1).Within(Eps));
        }

        [Test]
        public void Skew_function() {
            var s = Style();
            s.Set("transform", "skew(45deg, 0deg)");
            var t = TransformResolver.ResolveTransform(s, 0, 0);
            // skewx(45deg) -> C = tan(45) = 1
            Assert.That(t.C, Is.EqualTo(1).Within(Eps));
            Assert.That(t.A, Is.EqualTo(1).Within(Eps));
        }

        [Test]
        public void Matrix_function_passes_through() {
            var s = Style();
            s.Set("transform", "matrix(1, 2, 3, 4, 5, 6)");
            var t = TransformResolver.ResolveTransform(s, 0, 0);
            Assert.That(t.A, Is.EqualTo(1).Within(Eps));
            Assert.That(t.B, Is.EqualTo(2).Within(Eps));
            Assert.That(t.C, Is.EqualTo(3).Within(Eps));
            Assert.That(t.D, Is.EqualTo(4).Within(Eps));
            Assert.That(t.Tx, Is.EqualTo(5).Within(Eps));
            Assert.That(t.Ty, Is.EqualTo(6).Within(Eps));
        }

        [Test]
        public void Compose_two_translate_then_scale() {
            var s = Style();
            s.Set("transform", "translate(10px, 20px) scale(2)");
            var t = TransformResolver.ResolveTransform(s, 0, 0);
            // CSS transform list order: rightmost function acts on the point
            // first. Origin is scaled first, then translated.
            var (x, y) = t.Apply(0, 0);
            Assert.That(x, Is.EqualTo(10).Within(Eps));
            Assert.That(y, Is.EqualTo(20).Within(Eps));
        }

        [Test]
        public void Compose_three_order_matters() {
            var sA = Style();
            sA.Set("transform", "scale(2) translate(10px, 0px) rotate(0deg)");
            var tA = TransformResolver.ResolveTransform(sA, 0, 0);
            var (xA, yA) = tA.Apply(0, 0);
            // Rightmost rotate(0) first, then translate, then scale.
            Assert.That(xA, Is.EqualTo(20).Within(Eps));
            Assert.That(yA, Is.EqualTo(0).Within(Eps));

            var sB = Style();
            sB.Set("transform", "translate(10px, 0px) scale(2) rotate(0deg)");
            var tB = TransformResolver.ResolveTransform(sB, 0, 0);
            var (xB, yB) = tB.Apply(0, 0);
            // Rightmost rotate(0), then scale, then translate.
            Assert.That(xB, Is.EqualTo(10).Within(Eps));
            Assert.That(yB, Is.EqualTo(0).Within(Eps));
        }

        [Test]
        public void Empty_or_null_yields_identity() {
            var s = Style();
            var t = TransformResolver.ResolveTransform(s, 0, 0);
            Assert.That(t, Is.EqualTo(Transform2D.Identity));
        }

        // Spec test (randhtml audit): the demo uses
        //   `.minimap-orb { transform: translate(-50%,-50%) rotate(45deg); }`
        // and similar compound transforms. Compound transforms must compose —
        // `rotate(...) translate(...)` is fundamentally different from
        // `translate(...) rotate(...)`. If our resolver dropped all but the
        // first function the two would coincidentally yield the same result
        // (just the rotate or just the translate), so this test guards against
        // that failure mode by asserting the compositions produce *different*
        // matrices.
        [Test]
        public void Compound_transform_composes_in_css_order() {
            var sA = Style();
            sA.Set("transform", "rotate(45deg) translate(10px, 0px)");
            var tA = TransformResolver.ResolveTransform(sA, 0, 0);

            var sB = Style();
            sB.Set("transform", "translate(10px, 0px) rotate(45deg)");
            var tB = TransformResolver.ResolveTransform(sB, 0, 0);

            Assert.That(tA, Is.Not.EqualTo(tB),
                "rotate(...) translate(...) must differ from translate(...) rotate(...)");

            // Sanity-check both compose (i.e. neither is a pure rotate or pure
            // translate). The pure rotate(45) has Tx=Ty=0; the pure translate
            // has A=D=1, B=C=0. Neither result should match those degenerate
            // forms.
            var pureRot = Transform2D.Rotate(45);
            var pureTrans = Transform2D.Translate(10, 0);
            Assert.That(tA, Is.Not.EqualTo(pureRot));
            Assert.That(tA, Is.Not.EqualTo(pureTrans));
            Assert.That(tB, Is.Not.EqualTo(pureRot));
            Assert.That(tB, Is.Not.EqualTo(pureTrans));
        }

        [Test]
        public void Translate_then_rotate_keeps_translation_unrotated() {
            var s = Style();
            s.Set("transform", "translateY(760px) rotate(260deg)");
            var t = TransformResolver.ResolveTransform(s, 0, 0);

            var (x, y) = t.Apply(0, 0);
            Assert.That(x, Is.EqualTo(0).Within(Eps));
            Assert.That(y, Is.EqualTo(760).Within(Eps));
        }

        // Bug G12 — transform-origin parsing gaps:
        //   1. 2-token vertical-then-horizontal keyword pair (`top left`)
        //      must swap to (X=left, Y=top).
        //   2. The Z component of a 3-token value (`50% 50% 10px`) must be
        //      consumed without error even though 2D paint ignores it.
        //   3. `calc()` per-axis must resolve against the axis basis.
        //   4. Regression: the canonical `50% 50%` pair still returns center.
        [Test]
        public void TransformOrigin_resolves_keywords_calc_and_z() {
            var ctx = LengthContext.Default;

            var sKw = Style();
            sKw.Set("transform-origin", "top left");
            var (kxA, kyA) = BoxToPaintConverter.ResolveTransformOrigin(sKw, 200, 100, ctx);
            Assert.That(kxA, Is.EqualTo(0).Within(Eps));
            Assert.That(kyA, Is.EqualTo(0).Within(Eps));

            var sKwBr = Style();
            sKwBr.Set("transform-origin", "bottom right");
            var (kxB, kyB) = BoxToPaintConverter.ResolveTransformOrigin(sKwBr, 200, 100, ctx);
            Assert.That(kxB, Is.EqualTo(200).Within(Eps));
            Assert.That(kyB, Is.EqualTo(100).Within(Eps));

            var sZ = Style();
            sZ.Set("transform-origin", "50% 50% 10px");
            (double zx, double zy) zResult = default;
            Assert.DoesNotThrow(() => {
                zResult = BoxToPaintConverter.ResolveTransformOrigin(sZ, 200, 100, ctx);
            });
            Assert.That(zResult.zx, Is.EqualTo(100).Within(Eps));
            Assert.That(zResult.zy, Is.EqualTo(50).Within(Eps));

            var sCalc = Style();
            sCalc.Set("transform-origin", "calc(50% + 10px) 0");
            var (cx, cy) = BoxToPaintConverter.ResolveTransformOrigin(sCalc, 200, 100, ctx);
            Assert.That(cx, Is.EqualTo(110).Within(Eps));
            Assert.That(cy, Is.EqualTo(0).Within(Eps));

            var sCanon = Style();
            sCanon.Set("transform-origin", "50% 50%");
            var (px, py) = BoxToPaintConverter.ResolveTransformOrigin(sCanon, 200, 100, ctx);
            Assert.That(px, Is.EqualTo(100).Within(Eps));
            Assert.That(py, Is.EqualTo(50).Within(Eps));
        }

        // CSS Transforms L2 §3 — individual `translate`/`rotate`/`scale`
        // properties compose with `transform` as
        // `translate * rotate * scale * transform`.
        [Test]
        public void Individual_translate_rotate_scale_register_and_compose() {
            var sTrans = Style();
            sTrans.Set("translate", "10px 20px");
            var tT = TransformResolver.ResolveTransform(sTrans, 0, 0);
            Assert.That(tT.Tx, Is.EqualTo(10).Within(Eps));
            Assert.That(tT.Ty, Is.EqualTo(20).Within(Eps));
            Assert.That(tT.A, Is.EqualTo(1).Within(Eps));
            Assert.That(tT.D, Is.EqualTo(1).Within(Eps));

            var sRot = Style();
            sRot.Set("rotate", "45deg");
            var tR = TransformResolver.ResolveTransform(sRot, 0, 0);
            var (rx, ry) = tR.Apply(1, 0);
            double cos45 = System.Math.Cos(System.Math.PI / 4);
            double sin45 = System.Math.Sin(System.Math.PI / 4);
            Assert.That(rx, Is.EqualTo(cos45).Within(Eps));
            Assert.That(ry, Is.EqualTo(sin45).Within(Eps));

            var sScale = Style();
            sScale.Set("scale", "2");
            var tS = TransformResolver.ResolveTransform(sScale, 0, 0);
            Assert.That(tS.A, Is.EqualTo(2).Within(Eps));
            Assert.That(tS.D, Is.EqualTo(2).Within(Eps));
            Assert.That(tS.Tx, Is.EqualTo(0).Within(Eps));
            Assert.That(tS.Ty, Is.EqualTo(0).Within(Eps));

            // Compose: `translate: 10px` then `transform: rotate(45deg)`.
            // Effective = translate * rotate. A point at origin first
            // rotates (no-op at origin), then translates by (10, 0).
            var sCompose = Style();
            sCompose.Set("translate", "10px 0");
            sCompose.Set("transform", "rotate(45deg)");
            var tC = TransformResolver.ResolveTransform(sCompose, 0, 0);
            var (cx, cy) = tC.Apply(0, 0);
            Assert.That(cx, Is.EqualTo(10).Within(Eps));
            Assert.That(cy, Is.EqualTo(0).Within(Eps));
            // A point at (1, 0) rotates to (cos45, sin45) then translates
            // by (10, 0): (10 + cos45, sin45). This distinguishes the
            // composition from raw translate-only or rotate-only.
            var (cx1, cy1) = tC.Apply(1, 0);
            Assert.That(cx1, Is.EqualTo(10 + cos45).Within(Eps));
            Assert.That(cy1, Is.EqualTo(sin45).Within(Eps));

            // Regression: `transform: rotate(45deg)` alone is unchanged by
            // the new composition path (individual props all `none`).
            var sLegacy = Style();
            sLegacy.Set("transform", "rotate(45deg)");
            var tL = TransformResolver.ResolveTransform(sLegacy, 0, 0);
            var (lx, ly) = tL.Apply(1, 0);
            Assert.That(lx, Is.EqualTo(cos45).Within(Eps));
            Assert.That(ly, Is.EqualTo(sin45).Within(Eps));
            Assert.That(tL.Tx, Is.EqualTo(0).Within(Eps));
            Assert.That(tL.Ty, Is.EqualTo(0).Within(Eps));
        }

        [Test]
        public void Typed_transform_function_names_are_case_insensitive() {
            var overlay = new ValueInterpolator.TransformTypedOverlay();
            Assert.That(ValueInterpolator.TryUpdateTransformTyped(
                "translateY(-28px) rotate(0deg)",
                "translateY(760px) rotate(260deg)",
                1.0,
                overlay), Is.True);

            var s = Style();
            s.SetParsed(CssProperties.TransformId, overlay.List);
            var t = TransformResolver.ResolveTransform(s, 0, 0);

            var (x, y) = t.Apply(0, 0);
            Assert.That(x, Is.EqualTo(0).Within(Eps));
            Assert.That(y, Is.EqualTo(760).Within(Eps));
        }
    }
}
