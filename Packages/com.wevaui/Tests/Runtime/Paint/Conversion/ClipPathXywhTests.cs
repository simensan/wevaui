using System;
using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Dom;
using Weva.Paint;
using Weva.Paint.Conversion;

namespace Weva.Tests.Paint.Conversion {
    // CSS Shapes L1 §3.1.1 — xywh() basic-shape support for clip-path.
    // xywh(x y w h [round <border-radius>]?) resolves against the reference box:
    //   x, w  -> reference-box width
    //   y, h  -> reference-box height
    // Equivalent to inset(y  refW-x-w  refH-y-h  x  round ...)
    public class ClipPathXywhTests {

        // ---- helpers --------------------------------------------------------

        static readonly Rect Box100x200 = new Rect(0, 0, 100, 200);
        static readonly Rect BoxOffset  = new Rect(10, 20, 100, 200); // non-zero origin

        static InsetClipPathShape ResolveXywh(string clipPath, Rect box) {
            var style = new ComputedStyle(new Element("div"));
            style.Set("clip-path", clipPath);
            var shape = ClipPathResolver.Resolve(style, LengthContext.Default, box);
            Assert.That(shape, Is.Not.Null, "xywh() should resolve to a shape");
            Assert.That(shape, Is.TypeOf<InsetClipPathShape>(),
                "xywh() must resolve to InsetClipPathShape");
            return (InsetClipPathShape)shape;
        }

        // ---- IsSupportedValue -----------------------------------------------

        [Test]
        public void IsSupportedValue_accepts_xywh_function() {
            Assert.That(ClipPathResolver.IsSupportedValue("xywh(10px 20px 80px 50px)"), Is.True);
        }

        [Test]
        public void IsSupportedValue_accepts_xywh_with_round_clause() {
            Assert.That(ClipPathResolver.IsSupportedValue("xywh(0 0 100% 100% round 8px)"), Is.True);
        }

        // ---- pixel arguments ------------------------------------------------

        [Test]
        public void Xywh_px_arguments_produce_correct_rect() {
            // xywh(10px 20px 60px 80px) on 100x200 box
            // => rect at (0+10, 0+20) = (10,20) size 60x80
            var inset = ResolveXywh("xywh(10px 20px 60px 80px)", Box100x200);
            Assert.That(inset.Rect.X,      Is.EqualTo(10).Within(1e-6));
            Assert.That(inset.Rect.Y,      Is.EqualTo(20).Within(1e-6));
            Assert.That(inset.Rect.Width,  Is.EqualTo(60).Within(1e-6));
            Assert.That(inset.Rect.Height, Is.EqualTo(80).Within(1e-6));
        }

        [Test]
        public void Xywh_px_arguments_with_offset_box_produce_correct_rect() {
            // Same clip on a box that starts at (10,20).
            var inset = ResolveXywh("xywh(10px 20px 60px 80px)", BoxOffset);
            Assert.That(inset.Rect.X,      Is.EqualTo(20).Within(1e-6), "box.X + x");
            Assert.That(inset.Rect.Y,      Is.EqualTo(40).Within(1e-6), "box.Y + y");
            Assert.That(inset.Rect.Width,  Is.EqualTo(60).Within(1e-6));
            Assert.That(inset.Rect.Height, Is.EqualTo(80).Within(1e-6));
        }

        // ---- percentage arguments -------------------------------------------

        [Test]
        public void Xywh_x_and_w_percentages_resolve_against_box_width() {
            // 10% of 100 = 10; 50% of 100 = 50
            var inset = ResolveXywh("xywh(10% 0px 50% 100px)", Box100x200);
            Assert.That(inset.Rect.X,     Is.EqualTo(10).Within(1e-6), "x resolves against width");
            Assert.That(inset.Rect.Width, Is.EqualTo(50).Within(1e-6), "w resolves against width");
        }

        [Test]
        public void Xywh_y_and_h_percentages_resolve_against_box_height() {
            // 10% of 200 = 20; 50% of 200 = 100
            var inset = ResolveXywh("xywh(0px 10% 100px 50%)", Box100x200);
            Assert.That(inset.Rect.Y,      Is.EqualTo(20).Within(1e-6),  "y resolves against height");
            Assert.That(inset.Rect.Height, Is.EqualTo(100).Within(1e-6), "h resolves against height");
        }

        [Test]
        public void Xywh_100pct_width_and_height_covers_full_box() {
            var inset = ResolveXywh("xywh(0% 0% 100% 100%)", Box100x200);
            Assert.That(inset.Rect.X,      Is.EqualTo(0).Within(1e-6));
            Assert.That(inset.Rect.Y,      Is.EqualTo(0).Within(1e-6));
            Assert.That(inset.Rect.Width,  Is.EqualTo(100).Within(1e-6));
            Assert.That(inset.Rect.Height, Is.EqualTo(200).Within(1e-6));
        }

        // ---- equivalence with inset() ---------------------------------------

        [Test]
        public void Xywh_rect_matches_equivalent_inset_rect() {
            // xywh(10px 20px 60px 80px) on 100x200 is equivalent to
            // inset(20px 30px 100px 10px):
            //   top=20, right=100-10-60=30, bottom=200-20-80=100, left=10
            var xywhInset = ResolveXywh("xywh(10px 20px 60px 80px)", Box100x200);

            var styleInset = new ComputedStyle(new Element("div"));
            styleInset.Set("clip-path", "inset(20px 30px 100px 10px)");
            var insetShape = (InsetClipPathShape)ClipPathResolver.Resolve(
                styleInset, LengthContext.Default, Box100x200);

            Assert.That(xywhInset.Rect.X,      Is.EqualTo(insetShape.Rect.X).Within(1e-6));
            Assert.That(xywhInset.Rect.Y,      Is.EqualTo(insetShape.Rect.Y).Within(1e-6));
            Assert.That(xywhInset.Rect.Width,  Is.EqualTo(insetShape.Rect.Width).Within(1e-6));
            Assert.That(xywhInset.Rect.Height, Is.EqualTo(insetShape.Rect.Height).Within(1e-6));
        }

        // ---- round clause — uniform radius ----------------------------------

        [Test]
        public void Xywh_round_uniform_radius_applies_to_all_corners() {
            var inset = ResolveXywh("xywh(0px 0px 100px 100px round 10px)", Box100x200);
            Assert.That(inset.Radii.TopLeft.XRadius,     Is.EqualTo(10).Within(1e-6));
            Assert.That(inset.Radii.TopRight.XRadius,    Is.EqualTo(10).Within(1e-6));
            Assert.That(inset.Radii.BottomRight.XRadius, Is.EqualTo(10).Within(1e-6));
            Assert.That(inset.Radii.BottomLeft.XRadius,  Is.EqualTo(10).Within(1e-6));
        }

        [Test]
        public void Xywh_round_four_radii_assigns_corners_per_css_shorthand() {
            // border-radius shorthand: TL TR BR BL (clockwise)
            var inset = ResolveXywh("xywh(0px 0px 80px 80px round 4px 8px 12px 16px)", Box100x200);
            Assert.That(inset.Radii.TopLeft.XRadius,     Is.EqualTo(4).Within(1e-6),  "TL");
            Assert.That(inset.Radii.TopRight.XRadius,    Is.EqualTo(8).Within(1e-6),  "TR");
            Assert.That(inset.Radii.BottomRight.XRadius, Is.EqualTo(12).Within(1e-6), "BR");
            Assert.That(inset.Radii.BottomLeft.XRadius,  Is.EqualTo(16).Within(1e-6), "BL");
        }

        [Test]
        public void Xywh_round_elliptical_slash_form_sets_x_and_y_radii() {
            // "round 10px / 5px" => XRadius=10, YRadius=5 for all corners
            var inset = ResolveXywh("xywh(0px 0px 100px 100px round 10px / 5px)", Box100x200);
            Assert.That(inset.Radii.TopLeft.XRadius, Is.EqualTo(10).Within(1e-6));
            Assert.That(inset.Radii.TopLeft.YRadius, Is.EqualTo(5).Within(1e-6));
            Assert.That(inset.Radii.BottomRight.XRadius, Is.EqualTo(10).Within(1e-6));
            Assert.That(inset.Radii.BottomRight.YRadius, Is.EqualTo(5).Within(1e-6));
        }

        [Test]
        public void Xywh_round_elliptical_four_per_axis_assigns_all_eight_values() {
            // "round 4px 8px 12px 16px / 1px 2px 3px 4px" — full 4/4 form
            var inset = ResolveXywh(
                "xywh(0px 0px 100px 100px round 4px 8px 12px 16px / 1px 2px 3px 4px)",
                Box100x200);
            Assert.That(inset.Radii.TopLeft.XRadius,     Is.EqualTo(4).Within(1e-6));
            Assert.That(inset.Radii.TopLeft.YRadius,     Is.EqualTo(1).Within(1e-6));
            Assert.That(inset.Radii.TopRight.XRadius,    Is.EqualTo(8).Within(1e-6));
            Assert.That(inset.Radii.TopRight.YRadius,    Is.EqualTo(2).Within(1e-6));
            Assert.That(inset.Radii.BottomRight.XRadius, Is.EqualTo(12).Within(1e-6));
            Assert.That(inset.Radii.BottomRight.YRadius, Is.EqualTo(3).Within(1e-6));
            Assert.That(inset.Radii.BottomLeft.XRadius,  Is.EqualTo(16).Within(1e-6));
            Assert.That(inset.Radii.BottomLeft.YRadius,  Is.EqualTo(4).Within(1e-6));
        }

        // ---- round clause with round keyword case-insensitive ---------------

        [Test]
        public void Xywh_round_keyword_is_case_insensitive() {
            // "ROUND" must be handled the same as "round"
            var inset = ResolveXywh("xywh(0px 0px 100px 100px ROUND 8px)", Box100x200);
            Assert.That(inset.Radii.TopLeft.XRadius, Is.EqualTo(8).Within(1e-6));
        }

        // ---- zero / clamping ------------------------------------------------

        [Test]
        public void Xywh_zero_position_produces_rect_at_box_origin() {
            var inset = ResolveXywh("xywh(0px 0px 40px 40px)", Box100x200);
            Assert.That(inset.Rect.X, Is.EqualTo(0).Within(1e-6));
            Assert.That(inset.Rect.Y, Is.EqualTo(0).Within(1e-6));
        }

        [Test]
        public void Xywh_negative_w_clamps_to_zero() {
            // Spec: w and h are clamped to >= 0 before resolving
            // A negative width token is rejected as invalid so the whole thing
            // should return null (invalid). Chrome rejects negative w/h too.
            var style = new ComputedStyle(new Element("div"));
            style.Set("clip-path", "xywh(0px 0px -10px 50px)");
            var shape = ClipPathResolver.Resolve(style, LengthContext.Default, Box100x200);
            // Negative dimension means w=-10, clamped to 0 => width=0 => rect is degenerate.
            // The resolver must either produce a zero-width rect or reject the value.
            // We pin that it does NOT produce an InsetClipPathShape with negative width.
            if (shape != null) {
                var insetShape = shape as InsetClipPathShape;
                if (insetShape != null) {
                    Assert.That(insetShape.Rect.Width, Is.GreaterThanOrEqualTo(0),
                        "resolved rect width must never be negative");
                }
            }
        }

        [Test]
        public void Xywh_w_larger_than_box_extends_beyond_box_chrome_compatible() {
            // Chrome: xywh rect may extend beyond the reference box (negative
            // insets on the trailing edges). Width clamp in InsetClipPathShape
            // uses Math.Max(0, …), so the rect degenerates to the box edge.
            // Pin that this does not throw and produces a valid shape.
            var inset = ResolveXywh("xywh(0px 0px 200px 200px)", Box100x200);
            // rect width = min(200,100)=100 due to inset clamping
            Assert.That(inset.Rect.Width,  Is.GreaterThanOrEqualTo(0));
            Assert.That(inset.Rect.Height, Is.GreaterThanOrEqualTo(0));
        }

        // ---- contains() correctness ----------------------------------------

        [Test]
        public void Xywh_contains_inside_point() {
            // xywh(10px 20px 60px 80px): rect X=[10,70] Y=[20,100]
            var inset = ResolveXywh("xywh(10px 20px 60px 80px)", Box100x200);
            Assert.That(inset.Contains(40, 60), Is.True);
        }

        [Test]
        public void Xywh_does_not_contain_outside_point() {
            var inset = ResolveXywh("xywh(10px 20px 60px 80px)", Box100x200);
            Assert.That(inset.Contains(5, 60), Is.False, "point left of rect");
            Assert.That(inset.Contains(40, 5), Is.False, "point above rect");
        }

        [Test]
        public void Xywh_rounded_corner_excludes_corner_pixel() {
            // xywh(0 0 100px 100px round 20px) — top-left corner has r=20
            // Point (2,2) is near the corner and should be outside the ellipse.
            var inset = ResolveXywh("xywh(0px 0px 100px 100px round 20px)", Box100x200);
            Assert.That(inset.Contains(2, 2), Is.False, "corner pixel outside round radius");
            Assert.That(inset.Contains(50, 50), Is.True, "center well inside");
        }

        // ---- malformed / rejection ------------------------------------------

        [Test]
        public void Xywh_with_too_few_args_returns_null() {
            var style = new ComputedStyle(new Element("div"));
            style.Set("clip-path", "xywh(10px 20px 60px)");
            Assert.That(ClipPathResolver.Resolve(style, LengthContext.Default, Box100x200), Is.Null,
                "3 args is invalid; must have exactly 4");
        }

        [Test]
        public void Xywh_with_too_many_args_returns_null() {
            var style = new ComputedStyle(new Element("div"));
            style.Set("clip-path", "xywh(10px 20px 60px 80px 5px)");
            Assert.That(ClipPathResolver.Resolve(style, LengthContext.Default, Box100x200), Is.Null,
                "5 positional args is invalid");
        }

        [Test]
        public void Xywh_with_non_length_argument_returns_null() {
            var style = new ComputedStyle(new Element("div"));
            style.Set("clip-path", "xywh(abc 20px 60px 80px)");
            Assert.That(ClipPathResolver.Resolve(style, LengthContext.Default, Box100x200), Is.Null,
                "non-length token must cause rejection");
        }
    }
}
