using NUnit.Framework;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    // Tracker H5b — verifies that LayoutContext.ToLengthContext threads the
    // resolved line-height into the `lh` / `rlh` resolution path, so that
    // author-specified line-heights override the CssLength.ToPixels
    // `fontSize * 1.2` fallback at real layout call sites (not just synthetic
    // LengthContext.Default tests like CssLengthUnitsL4Tests).
    public class LineHeightUnitWiringTests {
        static BlockBox FindById(Box root, string id) {
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element?.GetAttribute("id") == id) return bb;
            }
            return null;
        }

        [Test]
        public void Padding_top_in_lh_uses_resolved_line_height_not_one_point_two_em_fallback() {
            // font-size: 20px, line-height: 30px. With H5b wired, padding-top:
            // 1lh resolves to 30px (the cascaded line-height). Pre-H5b it
            // resolved to fontSize * 1.2 = 24px from the CssLength.ToPixels
            // fallback.
            const string css = "html { font-size: 16px } #t { font-size: 20px; line-height: 30px; padding-top: 1lh; padding-bottom: 0 }";
            var (root, _, _) = Build("<div id=\"t\"></div>", css, viewportWidth: 800);
            var div = FindById(root, "t");
            Assert.That(div, Is.Not.Null);
            Assert.That(div.PaddingTop, Is.EqualTo(30.0).Within(0.001),
                "padding-top: 1lh must resolve to the cascaded line-height (30), NOT the fontSize*1.2 fallback (24)");
        }

        [Test]
        public void Padding_top_in_lh_falls_back_to_one_point_two_em_when_line_height_is_normal() {
            // With no explicit line-height (or `normal`), the engine's
            // line-height resolver returns the font's default leading. For
            // MonoFontMetrics (the test default), that's fontSize * 1.2 per
            // DefaultLineHeight. We confirm that path still works — `lh` then
            // resolves to that same value, mirroring the spec's "1lh = the
            // computed line-height" semantics even under the default.
            const string css = "#t { font-size: 20px; padding-top: 1lh; padding-bottom: 0 }";
            var (root, _, _) = Build("<div id=\"t\"></div>", css, viewportWidth: 800);
            var div = FindById(root, "t");
            Assert.That(div, Is.Not.Null);
            // MonoFontMetrics.LineHeight(20) == 20 * 1.2 == 24
            Assert.That(div.PaddingTop, Is.EqualTo(24.0).Within(0.001),
                "without explicit line-height, 1lh still resolves to the metric default (fontSize * 1.2)");
        }

        [Test]
        public void Padding_top_in_rlh_uses_root_line_height_not_local() {
            // `html` sets fs=16 and line-height=40px. The inner element has
            // its own fs=20 + line-height=30 — but 1rlh must bind to the
            // ROOT's line-height (40), not the local 30 or the
            // RootFontSizePx*1.2 (=19.2) fallback.
            const string css =
                "html { font-size: 16px; line-height: 40px }" +
                "#t { font-size: 20px; line-height: 30px; padding-top: 1rlh; padding-bottom: 0 }";
            var (root, _, _) = Build("<html><body><div id=\"t\"></div></body></html>", css, viewportWidth: 800);
            var div = FindById(root, "t");
            Assert.That(div, Is.Not.Null);
            Assert.That(div.PaddingTop, Is.EqualTo(40.0).Within(0.001),
                "padding-top: 1rlh must resolve against the root's line-height (40), not the local (30) or the RootFontSizePx*1.2 fallback");
        }

        [Test]
        public void Multiple_lh_units_scale_linearly_with_line_height() {
            // 2.5lh against line-height: 24 = 60. Guards against off-by-one
            // wiring that might accidentally always pass `0` and let the
            // fallback take over (which would produce 2.5 * 20 * 1.2 = 60
            // here too — so we set lh to a non-1.2-multiple to be sure).
            const string css = "#t { font-size: 20px; line-height: 50px; padding-top: 2lh; padding-bottom: 0 }";
            var (root, _, _) = Build("<div id=\"t\"></div>", css, viewportWidth: 800);
            var div = FindById(root, "t");
            Assert.That(div, Is.Not.Null);
            Assert.That(div.PaddingTop, Is.EqualTo(100.0).Within(0.001),
                "padding-top: 2lh against line-height 50 must equal 100 (= 2 * 50), not 2 * fs * 1.2 = 48");
        }
    }
}
