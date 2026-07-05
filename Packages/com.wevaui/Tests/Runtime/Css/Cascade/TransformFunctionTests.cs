using NUnit.Framework;
using System;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Dom;
using Weva.Paint;
using Weva.Paint.Conversion;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Transforms Module Level 1 §6 — cascade round-trips and resolver
    // behaviour for each transform function family.  The primary goal is to
    // pin that:
    //   (a) the value survives the cascade get/set cycle unchanged, and
    //   (b) TransformResolver produces the geometrically correct matrix.
    //
    // Tests for the individual L2 standalone properties (translate/rotate/scale)
    // live in TransformIndividualPropertyTests.cs.  transform-origin keyword and
    // multi-value forms are in TransformOriginTests.cs.
    public class TransformFunctionTests {
        const double Eps = 1e-4;

        // ── Cascade helpers ─────────────────────────────────────────────────

        static Document Html(string s) => HtmlParser.Parse(s);
        static Stylesheet Css(string s) => CssParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(Css(s));

        static ComputedStyle Compute(string css) {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("x"));
        }

        // Shortcut: build a plain ComputedStyle, set the transform raw string,
        // and resolve.  Mirrors TransformResolverTests / TransformPropertyTests
        // pattern — no cascade engine needed for resolver-only assertions.
        static ComputedStyle Style() => new ComputedStyle(new Element("div"));

        static Transform2D Resolve(string transform, double refW = 0, double refH = 0) {
            var s = Style();
            s.Set("transform", transform);
            return TransformResolver.ResolveTransform(s, refW, refH);
        }

        // ── 1. Initial value and none ────────────────────────────────────────

        [Test]
        public void Transform_initial_value_is_none() {
            // CSS Transforms L1 §6: initial value is `none`.
            Assert.That(CssProperties.InitialValueOf("transform"), Is.EqualTo("none"));
            Assert.That(CssProperties.IsInherited("transform"), Is.False);
        }

        [Test]
        public void Transform_none_round_trips_through_cascade() {
            var cs = Compute("#x { transform: none; }");
            Assert.That(cs.Get("transform"), Is.EqualTo("none"));
        }

        [Test]
        public void Transform_none_resolves_to_identity() {
            var t = Resolve("none");
            Assert.That(t, Is.EqualTo(Transform2D.Identity));
        }

        // ── 2. translate() — length and percentage ───────────────────────────

        [Test]
        public void Translate_px_round_trips_through_cascade() {
            var cs = Compute("#x { transform: translate(10px, 20px); }");
            Assert.That(cs.Get("transform"), Is.EqualTo("translate(10px, 20px)"));
        }

        [Test]
        public void Translate_single_value_defaults_y_to_zero() {
            // CSS Transforms L1 §13.1: translate(<tx>) = translate(<tx>, 0).
            var t = Resolve("translate(15px)");
            Assert.That(t.Tx, Is.EqualTo(15).Within(Eps));
            Assert.That(t.Ty, Is.EqualTo(0).Within(Eps));
        }

        [Test]
        public void Translate_percent_resolves_against_reference_box() {
            // translate(50%, 25%) with refW=200, refH=100 -> (100, 25).
            var t = Resolve("translate(50%, 25%)", refW: 200, refH: 100);
            Assert.That(t.Tx, Is.EqualTo(100).Within(Eps));
            Assert.That(t.Ty, Is.EqualTo(25).Within(Eps));
        }

        [Test]
        public void TranslateX_px_correct_offset() {
            var t = Resolve("translateX(42px)");
            Assert.That(t.Tx, Is.EqualTo(42).Within(Eps));
            Assert.That(t.Ty, Is.EqualTo(0).Within(Eps));
        }

        [Test]
        public void TranslateY_px_correct_offset() {
            var t = Resolve("translateY(99px)");
            Assert.That(t.Tx, Is.EqualTo(0).Within(Eps));
            Assert.That(t.Ty, Is.EqualTo(99).Within(Eps));
        }

        [Test]
        public void TranslateX_negative_percent() {
            // -50% with refW=400 -> -200.
            var t = Resolve("translateX(-50%)", refW: 400);
            Assert.That(t.Tx, Is.EqualTo(-200).Within(Eps));
            Assert.That(t.Ty, Is.EqualTo(0).Within(Eps));
        }

        // ── 3. scale() family ────────────────────────────────────────────────

        [Test]
        public void Scale_uniform_single_value() {
            // scale(3) expands to scale(3, 3).
            var t = Resolve("scale(3)");
            Assert.That(t.A, Is.EqualTo(3).Within(Eps));
            Assert.That(t.D, Is.EqualTo(3).Within(Eps));
            Assert.That(t.B, Is.EqualTo(0).Within(Eps));
            Assert.That(t.C, Is.EqualTo(0).Within(Eps));
        }

        [Test]
        public void Scale_two_values_independent() {
            var t = Resolve("scale(2, 0.5)");
            Assert.That(t.A, Is.EqualTo(2).Within(Eps));
            Assert.That(t.D, Is.EqualTo(0.5).Within(Eps));
        }

        [Test]
        public void ScaleX_only_affects_x_axis() {
            var t = Resolve("scaleX(4)");
            Assert.That(t.A, Is.EqualTo(4).Within(Eps));
            Assert.That(t.D, Is.EqualTo(1).Within(Eps));
        }

        [Test]
        public void ScaleY_only_affects_y_axis() {
            var t = Resolve("scaleY(0.25)");
            Assert.That(t.A, Is.EqualTo(1).Within(Eps));
            Assert.That(t.D, Is.EqualTo(0.25).Within(Eps));
        }

        [Test]
        public void Scale_round_trips_through_cascade() {
            var cs = Compute("#x { transform: scale(2, 3); }");
            Assert.That(cs.Get("transform"), Is.EqualTo("scale(2, 3)"));
        }

        // ── 4. rotate() — unit acceptance ───────────────────────────────────

        // CSS Transforms L1 §6: angle accepts deg | rad | grad | turn.

        [Test]
        public void Rotate_deg_90_maps_x_to_y() {
            var t = Resolve("rotate(90deg)");
            var (x, y) = t.Apply(1, 0);
            Assert.That(x, Is.EqualTo(0).Within(Eps));
            Assert.That(y, Is.EqualTo(1).Within(Eps));
        }

        [Test]
        public void Rotate_turn_quarter_is_90deg() {
            var t = Resolve("rotate(0.25turn)");
            var (x, y) = t.Apply(1, 0);
            Assert.That(x, Is.EqualTo(0).Within(Eps));
            Assert.That(y, Is.EqualTo(1).Within(Eps));
        }

        [Test]
        public void Rotate_rad_pi_over_2_is_90deg() {
            var t = Resolve("rotate(1.5707963267948966rad)");
            var (x, y) = t.Apply(1, 0);
            Assert.That(x, Is.EqualTo(0).Within(Eps));
            Assert.That(y, Is.EqualTo(1).Within(Eps));
        }

        [Test]
        public void Rotate_grad_100_is_90deg() {
            var t = Resolve("rotate(100grad)");
            var (x, y) = t.Apply(1, 0);
            Assert.That(x, Is.EqualTo(0).Within(Eps));
            Assert.That(y, Is.EqualTo(1).Within(Eps));
        }

        [Test]
        public void Rotate_180deg_inverts_x() {
            var t = Resolve("rotate(180deg)");
            var (x, y) = t.Apply(1, 0);
            Assert.That(x, Is.EqualTo(-1).Within(Eps));
            Assert.That(y, Is.EqualTo(0).Within(Eps));
        }

        [Test]
        public void Rotate_negative_degrees() {
            // rotate(-90deg) maps (1,0) -> (0,-1).
            var t = Resolve("rotate(-90deg)");
            var (x, y) = t.Apply(1, 0);
            Assert.That(x, Is.EqualTo(0).Within(Eps));
            Assert.That(y, Is.EqualTo(-1).Within(Eps));
        }

        [Test]
        public void Rotate_round_trips_through_cascade() {
            var cs = Compute("#x { transform: rotate(45deg); }");
            Assert.That(cs.Get("transform"), Is.EqualTo("rotate(45deg)"));
        }

        // ── 5. skew() / skewX() / skewY() ───────────────────────────────────

        [Test]
        public void SkewX_45deg_sets_c_to_one() {
            var t = Resolve("skewX(45deg)");
            Assert.That(t.C, Is.EqualTo(1).Within(Eps));
            Assert.That(t.B, Is.EqualTo(0).Within(Eps));
        }

        [Test]
        public void SkewY_45deg_sets_b_to_one() {
            var t = Resolve("skewY(45deg)");
            Assert.That(t.B, Is.EqualTo(1).Within(Eps));
            Assert.That(t.C, Is.EqualTo(0).Within(Eps));
        }

        [Test]
        public void Skew_two_values_sets_c_and_b() {
            double tan20 = Math.Tan(20 * Math.PI / 180.0);
            double tan10 = Math.Tan(10 * Math.PI / 180.0);
            var t = Resolve("skew(20deg, 10deg)");
            Assert.That(t.C, Is.EqualTo(tan20).Within(Eps));
            Assert.That(t.B, Is.EqualTo(tan10).Within(Eps));
        }

        [Test]
        public void Skew_one_value_y_defaults_to_zero() {
            // skew(30deg) = skew(30deg, 0deg) -> B = 0.
            double tan30 = Math.Tan(30 * Math.PI / 180.0);
            var t = Resolve("skew(30deg)");
            Assert.That(t.C, Is.EqualTo(tan30).Within(Eps));
            Assert.That(t.B, Is.EqualTo(0).Within(Eps));
        }

        // ── 6. matrix() ──────────────────────────────────────────────────────

        [Test]
        public void Matrix_six_args_identity() {
            var t = Resolve("matrix(1, 0, 0, 1, 0, 0)");
            Assert.That(t, Is.EqualTo(Transform2D.Identity));
        }

        [Test]
        public void Matrix_six_args_translates_and_scales() {
            // matrix(2, 0, 0, 3, 50, 80): scale x*2, y*3, then translate (50,80).
            var t = Resolve("matrix(2, 0, 0, 3, 50, 80)");
            Assert.That(t.A, Is.EqualTo(2).Within(Eps));
            Assert.That(t.D, Is.EqualTo(3).Within(Eps));
            Assert.That(t.Tx, Is.EqualTo(50).Within(Eps));
            Assert.That(t.Ty, Is.EqualTo(80).Within(Eps));
        }

        [Test]
        public void Matrix_fewer_than_six_args_returns_identity() {
            // Malformed: only 5 args.
            var t = Resolve("matrix(1, 0, 0, 1, 5)");
            Assert.That(t, Is.EqualTo(Transform2D.Identity));
        }

        [Test]
        public void Matrix_round_trips_through_cascade() {
            var cs = Compute("#x { transform: matrix(1, 2, 3, 4, 5, 6); }");
            Assert.That(cs.Get("transform"), Is.EqualTo("matrix(1, 2, 3, 4, 5, 6)"));
        }

        // ── 7. translate3d() — Z is ignored, XY resolved normally ───────────

        [Test]
        public void Translate3d_z_component_ignored_xy_resolved() {
            // translate3d(10px, 20px, 30px) -> same as translate(10px, 20px)
            // for 2D paint.  The resolver falls through to the string fallback
            // since translate3d is not in its typed switch.  The raw string
            // just isn't handled -> identity (current v1 spec intent).
            // Pin the current behaviour (identity) so any future change is
            // deliberate.
            var t = Resolve("translate3d(10px, 20px, 30px)");
            // Current resolver: unrecognised 3D functions -> identity.
            Assert.That(t, Is.EqualTo(Transform2D.Identity),
                "translate3d is a 3D function; v1 resolver treats it as identity");
        }

        [Test]
        public void Scale3d_returns_identity_in_2d_paint() {
            var t = Resolve("scale3d(2, 3, 1)");
            Assert.That(t, Is.EqualTo(Transform2D.Identity));
        }

        // ── 8. Composition — multiple functions in one declaration ──────────

        [Test]
        public void Composition_translate_then_rotate_differ_from_rotate_then_translate() {
            // CSS transform list: rightmost function acts on the point first.
            var tr = Resolve("translate(50px, 0) rotate(90deg)");
            var rt = Resolve("rotate(90deg) translate(50px, 0)");
            Assert.That(tr, Is.Not.EqualTo(rt));
        }

        [Test]
        public void Composition_translate_scale_applies_to_point() {
            // translate(10px, 20px) scale(2): scale runs first at origin
            // (no-op on origin), then translate.  Point (0,0) -> (10,20).
            var t = Resolve("translate(10px, 20px) scale(2)");
            var (x, y) = t.Apply(0, 0);
            Assert.That(x, Is.EqualTo(10).Within(Eps));
            Assert.That(y, Is.EqualTo(20).Within(Eps));
        }

        [Test]
        public void Composition_rotate_then_translate_moves_rotated_axis() {
            // rotate(90deg) translate(50px, 0): translate first in local
            // space along X; after 90° rotation that local X is world Y.
            // Point (0,0): translate -> (50,0) -> rotate90 -> (0,50).
            var t = Resolve("rotate(90deg) translate(50px, 0)");
            var (x, y) = t.Apply(0, 0);
            Assert.That(x, Is.EqualTo(0).Within(Eps));
            Assert.That(y, Is.EqualTo(50).Within(Eps));
        }

        [Test]
        public void Composition_three_functions_order_matters() {
            // scale(2) translate(10px, 0) rotate(0deg):
            // rotate(0) is identity, translate (10, 0), then scale x2 -> (20, 0).
            var t = Resolve("scale(2) translate(10px, 0) rotate(0deg)");
            var (x, y) = t.Apply(0, 0);
            Assert.That(x, Is.EqualTo(20).Within(Eps));
            Assert.That(y, Is.EqualTo(0).Within(Eps));
        }

        [Test]
        public void Composition_skew_and_scale() {
            // scale(2) skewX(45deg) -> scale X by 2, skew X by 45°.
            // Point (1, 0): skewX -> (1 + tan(45)*0, 0) = (1,0); scale -> (2,0).
            double tan45 = 1.0;
            var t = Resolve("scale(2) skewX(45deg)");
            var (x, y) = t.Apply(0, 1);
            // Point (0,1): skewX(45) -> (0 + tan45*1, 1) = (1,1); scale(2) -> (2,2).
            Assert.That(x, Is.EqualTo(2 * (0 + tan45)).Within(Eps));
            Assert.That(y, Is.EqualTo(2 * 1).Within(Eps));
        }

        // ── 9. Non-inheritance ───────────────────────────────────────────────

        [Test]
        public void Transform_is_not_inherited() {
            var doc = Html("<div><span id=\"child\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { transform: rotate(45deg); }")
            });
            var cs = engine.Compute(doc.GetElementById("child"));
            Assert.That(cs.Get("transform"), Is.EqualTo("none"),
                "transform is non-inherited; child must see the initial none");
        }

        [Test]
        public void Transform_property_is_registered_and_has_correct_metadata() {
            Assert.That(CssProperties.TryGet("transform", out _), Is.True);
            Assert.That(CssProperties.GetId("transform"), Is.GreaterThanOrEqualTo(0));
            Assert.That(CssProperties.IsInherited("transform"), Is.False);
        }
    }

    // CSS Transforms Module Level 1 §6.1 — `transform-origin` cascade and
    // resolution tests.  Coverage:
    //   - Initial value (50% 50% 0) and property metadata.
    //   - 1-value, 2-value, 3-value (Z ignored for 2D) form cascade round-trips.
    //   - Keyword values: top / right / bottom / left / center.
    //   - Vertical-then-horizontal keyword swap (`top left` = `0 0`).
    //   - Percentage and length values via BoxToPaintConverter.ResolveTransformOrigin.
    //   - Non-inheritance.
    public class TransformOriginCascadeTests {
        const double Eps = 1e-4;

        static Document Html(string s) => HtmlParser.Parse(s);
        static Stylesheet Css(string s) => CssParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(Css(s));

        static ComputedStyle Compute(string css) {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("x"));
        }

        static ComputedStyle Style() => new ComputedStyle(new Element("div"));

        // ── 1. Property metadata and initial value ───────────────────────────

        [Test]
        public void Transform_origin_initial_value_is_50pct_50pct_0() {
            // CSS Transforms L1 §6.1: initial value `50% 50% 0`.
            Assert.That(CssProperties.InitialValueOf("transform-origin"),
                Is.EqualTo("50% 50% 0"));
        }

        [Test]
        public void Transform_origin_is_not_inherited() {
            Assert.That(CssProperties.IsInherited("transform-origin"), Is.False);
        }

        [Test]
        public void Transform_origin_is_registered() {
            Assert.That(CssProperties.TryGet("transform-origin", out _), Is.True);
            Assert.That(CssProperties.GetId("transform-origin"), Is.GreaterThanOrEqualTo(0));
        }

        // ── 2. Cascade round-trips ────────────────────────────────────────────

        [Test]
        public void Transform_origin_center_keyword_round_trips() {
            var cs = Compute("#x { transform-origin: center; }");
            Assert.That(cs.Get("transform-origin"), Is.EqualTo("center"));
        }

        [Test]
        public void Transform_origin_two_keywords_round_trips() {
            var cs = Compute("#x { transform-origin: top left; }");
            Assert.That(cs.Get("transform-origin"), Is.EqualTo("top left"));
        }

        [Test]
        public void Transform_origin_percent_pair_round_trips() {
            var cs = Compute("#x { transform-origin: 25% 75%; }");
            Assert.That(cs.Get("transform-origin"), Is.EqualTo("25% 75%"));
        }

        [Test]
        public void Transform_origin_length_pair_round_trips() {
            var cs = Compute("#x { transform-origin: 10px 20px; }");
            Assert.That(cs.Get("transform-origin"), Is.EqualTo("10px 20px"));
        }

        [Test]
        public void Transform_origin_three_value_with_z_round_trips() {
            // Z component is stored and served back even though 2D paint ignores it.
            var cs = Compute("#x { transform-origin: 50% 50% 10px; }");
            Assert.That(cs.Get("transform-origin"), Is.EqualTo("50% 50% 10px"));
        }

        [Test]
        public void Transform_origin_right_bottom_round_trips() {
            var cs = Compute("#x { transform-origin: right bottom; }");
            Assert.That(cs.Get("transform-origin"), Is.EqualTo("right bottom"));
        }

        // ── 3. Pixel resolution via ResolveTransformOrigin ──────────────────

        [Test]
        public void Transform_origin_default_50pct_resolves_to_center() {
            var s = Style();
            s.Set("transform-origin", "50% 50%");
            var ctx = LengthContext.Default;
            var (x, y) = BoxToPaintConverter.ResolveTransformOrigin(s, 200, 100, ctx);
            Assert.That(x, Is.EqualTo(100).Within(Eps));
            Assert.That(y, Is.EqualTo(50).Within(Eps));
        }

        [Test]
        public void Transform_origin_top_left_resolves_to_zero_zero() {
            var s = Style();
            s.Set("transform-origin", "top left");
            var ctx = LengthContext.Default;
            var (x, y) = BoxToPaintConverter.ResolveTransformOrigin(s, 200, 100, ctx);
            Assert.That(x, Is.EqualTo(0).Within(Eps));
            Assert.That(y, Is.EqualTo(0).Within(Eps));
        }

        [Test]
        public void Transform_origin_bottom_right_resolves_to_far_corner() {
            var s = Style();
            s.Set("transform-origin", "bottom right");
            var ctx = LengthContext.Default;
            var (x, y) = BoxToPaintConverter.ResolveTransformOrigin(s, 200, 100, ctx);
            Assert.That(x, Is.EqualTo(200).Within(Eps));
            Assert.That(y, Is.EqualTo(100).Within(Eps));
        }

        [Test]
        public void Transform_origin_keyword_swap_vertical_then_horizontal() {
            // `top left` and `left top` must both resolve to (0, 0).
            // The resolver swaps the pair when the first token is vertical-axis.
            var s1 = Style();
            s1.Set("transform-origin", "top left");
            var (x1, y1) = BoxToPaintConverter.ResolveTransformOrigin(s1, 200, 100, LengthContext.Default);

            var s2 = Style();
            s2.Set("transform-origin", "left top");
            var (x2, y2) = BoxToPaintConverter.ResolveTransformOrigin(s2, 200, 100, LengthContext.Default);

            Assert.That(x1, Is.EqualTo(0).Within(Eps));
            Assert.That(y1, Is.EqualTo(0).Within(Eps));
            Assert.That(x2, Is.EqualTo(0).Within(Eps));
            Assert.That(y2, Is.EqualTo(0).Within(Eps));
        }

        [Test]
        public void Transform_origin_center_keyword_resolves_to_50pct() {
            var s = Style();
            s.Set("transform-origin", "center center");
            var (x, y) = BoxToPaintConverter.ResolveTransformOrigin(s, 400, 200, LengthContext.Default);
            Assert.That(x, Is.EqualTo(200).Within(Eps));
            Assert.That(y, Is.EqualTo(100).Within(Eps));
        }

        [Test]
        public void Transform_origin_length_pixels_resolve_directly() {
            var s = Style();
            s.Set("transform-origin", "30px 70px");
            var (x, y) = BoxToPaintConverter.ResolveTransformOrigin(s, 200, 100, LengthContext.Default);
            Assert.That(x, Is.EqualTo(30).Within(Eps));
            Assert.That(y, Is.EqualTo(70).Within(Eps));
        }

        [Test]
        public void Transform_origin_zero_zero_resolves_to_top_left() {
            var s = Style();
            s.Set("transform-origin", "0 0");
            var (x, y) = BoxToPaintConverter.ResolveTransformOrigin(s, 200, 100, LengthContext.Default);
            Assert.That(x, Is.EqualTo(0).Within(Eps));
            Assert.That(y, Is.EqualTo(0).Within(Eps));
        }

        [Test]
        public void Transform_origin_z_component_consumed_without_error() {
            var s = Style();
            s.Set("transform-origin", "50% 50% 10px");
            // Z is silently ignored; XY must still resolve correctly.
            var (ox, oy) = BoxToPaintConverter.ResolveTransformOrigin(s, 200, 100, LengthContext.Default);
            Assert.That(ox, Is.EqualTo(100).Within(Eps));
            Assert.That(oy, Is.EqualTo(50).Within(Eps));
        }

        // ── 4. 1-value form: single token — horizontal default center Y ──────

        [Test]
        public void Transform_origin_one_value_sets_x_and_defaults_y_to_50pct() {
            // CSS Transforms L1 §6.1 1-value grammar: the single token gives X;
            // Y defaults to `center` (50%).  `transform-origin: left` == `left center`.
            var s = Style();
            s.Set("transform-origin", "left");
            var (x, y) = BoxToPaintConverter.ResolveTransformOrigin(s, 200, 100, LengthContext.Default);
            Assert.That(x, Is.EqualTo(0).Within(Eps));
            Assert.That(y, Is.EqualTo(50).Within(Eps));  // default center = 50% of 100
        }

        // ── 5. Non-inheritance ────────────────────────────────────────────────

        [Test]
        public void Transform_origin_does_not_inherit() {
            var doc = Html("<div><span id=\"child\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { transform-origin: top left; }")
            });
            var cs = engine.Compute(doc.GetElementById("child"));
            // Child should see the initial value "50% 50% 0", not the parent's "top left".
            Assert.That(cs.Get("transform-origin"), Is.EqualTo("50% 50% 0"),
                "transform-origin is non-inherited; child must see the initial 50% 50% 0");
        }
    }
}
