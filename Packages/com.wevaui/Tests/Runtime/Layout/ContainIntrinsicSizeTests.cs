// CSS Sizing L4 §5 / CSS Containment L2 §4 — NUnit coverage for
// `contain-intrinsic-size`, `contain-intrinsic-width`, `contain-intrinsic-height`.
//
// These properties provide an EXPLICIT intrinsic size used INSTEAD of zero when
// the corresponding axis is size-contained (contain:size, contain:inline-size,
// contain:strict, or content-visibility:hidden).  Without containment the
// properties have no effect on layout.
//
// Spec grammar for each axis: `none | <length> | auto <length>`
//   none          → contained axis contributes 0 (current behaviour, regression pin)
//   <length>      → contained axis contributes that px value as content size
//   auto <length> → uses last-remembered size, falls back to <length> (v1: always
//                   uses the fallback — no last-remembered-size memo; Chrome parity
//                   gap documented in CSS_OPEN_GAPS.md B15)
//
// Shorthand (contain-intrinsic-size):
//   one value  = both axes share it
//   two values = first is width, second is height
//
// Percentages are invalid per spec grammar — treated as 0.
//
// Hook points:
//   Height axis: BlockLayout.FinalizeBlockSize HasSize branch
//   Width axis:  PositioningPass.MaxContentWidth/MinContentWidth HasInlineSize guard

using NUnit.Framework;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    public class ContainIntrinsicSizeTests {

        // ─── Helpers ──────────────────────────────────────────────────────────

        static BlockBox FirstById(Box root, string id) {
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element != null &&
                    bb.Element.GetAttribute("id") == id) return bb;
            }
            return null;
        }

        // ─── Regression pins: none = zero collapse (existing ContainmentTests) ─

        [Test]
        public void ContainIntrinsicHeight_none_is_zero_content_height() {
            // Baseline regression: contain-intrinsic-height:none (or unset) means
            // the contained axis contributes 0; height = padding+border only.
            var (root, _, _) = Build(
                "<div id=\"box\"><div style=\"height:100px\"></div></div>",
                "#box { width:200px; contain:size; contain-intrinsic-height:none; }",
                viewportWidth: 800);
            var box = FirstById(root, "box");
            Assert.That(box, Is.Not.Null);
            Assert.That(box.Height, Is.EqualTo(0).Within(0.001),
                "contain:size with none must collapse to 0 height");
        }

        [Test]
        public void ContainIntrinsicWidth_none_float_collapses_to_frame() {
            // Float with contain:inline-size and contain-intrinsic-width:none:
            // MaxContentWidth returns 0 content contribution; width = frame only.
            // No padding or border so frame = 0, fitted = 0.
            var (root, _, _) = Build(
                "<div style=\"width:500px\">" +
                "<div id=\"f\" style=\"float:left; contain:inline-size; contain-intrinsic-width:none; height:40px;\"></div>" +
                "</div>",
                null, viewportWidth: 800);
            var f = FirstById(root, "f");
            Assert.That(f, Is.Not.Null);
            double frame = f.PaddingLeft + f.PaddingRight + f.BorderLeft + f.BorderRight;
            Assert.That(f.Width, Is.EqualTo(frame).Within(0.001),
                "contain:inline-size with none collapses float to frame width only");
        }

        // ─── contain-intrinsic-height with contain:size ────────────────────────

        [Test]
        public void ContainIntrinsicHeight_length_used_as_content_height() {
            // contain-intrinsic-height:80px means the content contribution is 80px,
            // so total height = 80 + paddingTop + paddingBottom + borderTop + borderBottom.
            var (root, _, _) = Build(
                "<div id=\"box\"><div style=\"height:200px\"></div></div>",
                "#box { width:200px; contain:size; contain-intrinsic-height:80px; }",
                viewportWidth: 800);
            var box = FirstById(root, "box");
            Assert.That(box, Is.Not.Null);
            double frame = box.PaddingTop + box.PaddingBottom + box.BorderTop + box.BorderBottom;
            Assert.That(box.Height, Is.EqualTo(80 + frame).Within(0.001),
                "contain:size + contain-intrinsic-height:80px → height = 80 + frame");
        }

        [Test]
        public void ContainIntrinsicHeight_with_padding_adds_to_frame() {
            // When the box has padding, height = intrinsic + padding + border.
            const string css = "#box { width:200px; padding:10px; contain:size; contain-intrinsic-height:60px; }";
            var (root, _, _) = Build(
                "<div id=\"box\"><div style=\"height:200px\"></div></div>",
                css, viewportWidth: 800);
            var box = FirstById(root, "box");
            Assert.That(box, Is.Not.Null);
            // 60px content + 10 top + 10 bottom padding = 80px total (no border).
            Assert.That(box.Height, Is.EqualTo(80).Within(0.001),
                "height must equal intrinsic(60) + paddingTop(10) + paddingBottom(10)");
        }

        [Test]
        public void ContainIntrinsicHeight_auto_length_uses_length_fallback() {
            // `auto 120px` — no last-remembered-size in v1, so the <length> fallback
            // is used: height = 120 + frame.
            var (root, _, _) = Build(
                "<div id=\"box\"><div style=\"height:300px\"></div></div>",
                "#box { width:200px; contain:size; contain-intrinsic-height: auto 120px; }",
                viewportWidth: 800);
            var box = FirstById(root, "box");
            Assert.That(box, Is.Not.Null);
            double frame = box.PaddingTop + box.PaddingBottom + box.BorderTop + box.BorderBottom;
            Assert.That(box.Height, Is.EqualTo(120 + frame).Within(0.001),
                "auto <length> falls back to <length>; height = 120 + frame");
        }

        [Test]
        public void ContainIntrinsicHeight_no_containment_has_no_effect() {
            // Without containment the property must have NO effect — height follows
            // the actual content.
            var (root, _, _) = Build(
                "<div id=\"box\"><div style=\"height:50px\"></div></div>",
                "#box { width:200px; contain-intrinsic-height:999px; }",
                viewportWidth: 800);
            var box = FirstById(root, "box");
            Assert.That(box, Is.Not.Null);
            Assert.That(box.Height, Is.EqualTo(50).Within(0.001),
                "without containment, contain-intrinsic-height must not affect layout");
        }

        // ─── contain-intrinsic-width with inline-size containment ─────────────

        [Test]
        public void ContainIntrinsicWidth_length_used_for_float_shrink_to_fit() {
            // A float with contain:inline-size and contain-intrinsic-width:150px
            // must shrink-to-fit to 150px content width + frame.
            // (Float uses auto width by default so it goes through shrink-to-fit.)
            var (root, _, _) = Build(
                "<div style=\"width:500px\">" +
                "<div id=\"f\" style=\"float:left; contain:inline-size; contain-intrinsic-width:150px; height:40px;\"></div>" +
                "</div>",
                null, viewportWidth: 800);
            var f = FirstById(root, "f");
            Assert.That(f, Is.Not.Null);
            double frame = f.PaddingLeft + f.PaddingRight + f.BorderLeft + f.BorderRight;
            Assert.That(f.Width, Is.EqualTo(150 + frame).Within(0.001),
                "float with contain:inline-size + contain-intrinsic-width:150px → width = 150 + frame");
        }

        [Test]
        public void ContainIntrinsicWidth_no_containment_has_no_effect() {
            // Without inline-size containment, contain-intrinsic-width is a no-op.
            // A float with explicit width is unaffected.
            var (root, _, _) = Build(
                "<div style=\"width:500px\">" +
                "<div id=\"f\" style=\"float:left; width:200px; height:40px; contain-intrinsic-width:50px;\"></div>" +
                "</div>",
                null, viewportWidth: 800);
            var f = FirstById(root, "f");
            Assert.That(f, Is.Not.Null);
            Assert.That(f.Width, Is.EqualTo(200).Within(0.001),
                "without inline-size containment, contain-intrinsic-width must not affect float width");
        }

        // ─── Shorthand: contain-intrinsic-size one-value / two-value expansion ─

        [Test]
        public void ContainIntrinsicSize_one_value_applies_to_both_axes() {
            // contain-intrinsic-size:100px → both width and height hint = 100px.
            // Test height axis: contain:size makes height = 100 + frame.
            var (root, _, _) = Build(
                "<div id=\"box\"><div style=\"height:300px; width:300px\"></div></div>",
                "#box { contain:size; contain-intrinsic-size:100px; }",
                viewportWidth: 800);
            var box = FirstById(root, "box");
            Assert.That(box, Is.Not.Null);
            double frame = box.PaddingTop + box.PaddingBottom + box.BorderTop + box.BorderBottom;
            Assert.That(box.Height, Is.EqualTo(100 + frame).Within(0.001),
                "one-value contain-intrinsic-size:100px must apply 100px to height axis");
        }

        [Test]
        public void ContainIntrinsicSize_two_values_first_is_width_second_is_height() {
            // contain-intrinsic-size:80px 200px → width hint = 80px, height hint = 200px.
            // Test height axis only (width axis tested via float/inline-block).
            var (root, _, _) = Build(
                "<div id=\"box\"><div style=\"height:300px; width:300px\"></div></div>",
                "#box { contain:size; contain-intrinsic-size:80px 200px; }",
                viewportWidth: 800);
            var box = FirstById(root, "box");
            Assert.That(box, Is.Not.Null);
            double frame = box.PaddingTop + box.PaddingBottom + box.BorderTop + box.BorderBottom;
            Assert.That(box.Height, Is.EqualTo(200 + frame).Within(0.001),
                "two-value shorthand: second value is height");
        }

        // ─── em-unit resolution ───────────────────────────────────────────────

        [Test]
        public void ContainIntrinsicHeight_em_resolves_against_font_size() {
            // contain-intrinsic-height:5em with font-size:16px → 80px content height.
            var (root, _, _) = Build(
                "<div id=\"box\"><div style=\"height:300px\"></div></div>",
                "#box { width:200px; contain:size; font-size:16px; contain-intrinsic-height:5em; }",
                viewportWidth: 800);
            var box = FirstById(root, "box");
            Assert.That(box, Is.Not.Null);
            double frame = box.PaddingTop + box.PaddingBottom + box.BorderTop + box.BorderBottom;
            // 5em at 16px = 80px content.
            Assert.That(box.Height, Is.EqualTo(80 + frame).Within(0.5),
                "5em at font-size:16px = 80px; height = 80 + frame");
        }

        // ─── Invalid percentage rejected ───────────────────────────────────────

        [Test]
        public void ContainIntrinsicHeight_percentage_is_invalid_treated_as_zero() {
            // Percentages are invalid per spec grammar. Engine must treat them as
            // 0 (same as none), falling back to zero content height.
            var (root, _, _) = Build(
                "<div id=\"box\"><div style=\"height:300px\"></div></div>",
                "#box { width:200px; contain:size; contain-intrinsic-height:50%; }",
                viewportWidth: 800);
            var box = FirstById(root, "box");
            Assert.That(box, Is.Not.Null);
            double frame = box.PaddingTop + box.PaddingBottom + box.BorderTop + box.BorderBottom;
            Assert.That(box.Height, Is.EqualTo(frame).Within(0.001),
                "percentage in contain-intrinsic-height is invalid; treated as none/zero");
        }

        // ─── min-height / max-height still clamp after intrinsic ──────────────

        [Test]
        public void ContainIntrinsicHeight_min_height_floors_the_result() {
            // min-height:200px + contain-intrinsic-height:50px → height is 200px.
            var (root, _, _) = Build(
                "<div id=\"box\"><div style=\"height:300px\"></div></div>",
                "#box { width:200px; contain:size; contain-intrinsic-height:50px; min-height:200px; }",
                viewportWidth: 800);
            var box = FirstById(root, "box");
            Assert.That(box, Is.Not.Null);
            Assert.That(box.Height, Is.EqualTo(200).Within(0.001),
                "min-height still floors the result from contain-intrinsic-height");
        }

        [Test]
        public void ContainIntrinsicHeight_max_height_caps_the_result() {
            // max-height:40px + contain-intrinsic-height:150px → capped at 40px.
            var (root, _, _) = Build(
                "<div id=\"box\"><div style=\"height:300px\"></div></div>",
                "#box { width:200px; contain:size; contain-intrinsic-height:150px; max-height:40px; }",
                viewportWidth: 800);
            var box = FirstById(root, "box");
            Assert.That(box, Is.Not.Null);
            Assert.That(box.Height, Is.EqualTo(40).Within(0.001),
                "max-height still caps the result from contain-intrinsic-height");
        }

        // ─── contain:strict also honors contain-intrinsic-size ────────────────

        [Test]
        public void ContainStrict_with_contain_intrinsic_height() {
            // contain:strict = layout+paint+size+style. Height hint applies.
            var (root, _, _) = Build(
                "<div id=\"box\"><div style=\"height:300px\"></div></div>",
                "#box { width:200px; contain:strict; contain-intrinsic-height:70px; }",
                viewportWidth: 800);
            var box = FirstById(root, "box");
            Assert.That(box, Is.Not.Null);
            double frame = box.PaddingTop + box.PaddingBottom + box.BorderTop + box.BorderBottom;
            Assert.That(box.Height, Is.EqualTo(70 + frame).Within(0.001),
                "contain:strict also honors contain-intrinsic-height");
        }

        // ─── content-visibility:hidden also honors contain-intrinsic-size ────

        [Test]
        public void ContentVisibilityHidden_with_contain_intrinsic_height() {
            // content-visibility:hidden implies size containment; hint applies.
            var (root, _, _) = Build(
                "<div id=\"box\"><div style=\"height:300px\"></div></div>",
                "#box { width:200px; content-visibility:hidden; contain-intrinsic-height:90px; }",
                viewportWidth: 800);
            var box = FirstById(root, "box");
            Assert.That(box, Is.Not.Null);
            double frame = box.PaddingTop + box.PaddingBottom + box.BorderTop + box.BorderBottom;
            Assert.That(box.Height, Is.EqualTo(90 + frame).Within(0.001),
                "content-visibility:hidden + contain-intrinsic-height:90px → height = 90 + frame");
        }

        // ─── children still lay out despite contain-intrinsic-size ─────────────

        [Test]
        public void ContainIntrinsicSize_children_still_get_laid_out() {
            // Children still participate in layout (and can overflow), even when
            // contain-intrinsic-height is used as the hint.
            var (root, _, _) = Build(
                "<div id=\"box\"><div id=\"child\" style=\"height:80px; width:100px; margin-left:20px;\"></div></div>",
                "#box { width:200px; contain:size; contain-intrinsic-height:40px; }",
                viewportWidth: 800);
            var box = FirstById(root, "box");
            var child = FirstById(root, "child");
            Assert.That(box, Is.Not.Null);
            Assert.That(child, Is.Not.Null);
            double frame = box.PaddingTop + box.PaddingBottom + box.BorderTop + box.BorderBottom;
            Assert.That(box.Height, Is.EqualTo(40 + frame).Within(0.001),
                "container height equals intrinsic hint");
            Assert.That(child.Height, Is.EqualTo(80).Within(0.001),
                "child still lays out with its own height (can overflow the container)");
        }
    }
}
