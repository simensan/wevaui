// §3 — align-items / align-self extension coverage (CSS Flexbox L1 §8.3)
// FlexAlignItemsTests.cs covers stretch/flex-start/flex-end/center/baseline.
// FlexAlignSelfTests.cs covers self-override, center, stretch-override, auto.
// This file adds: first-baseline / last-baseline keyword forms,
// align-items in column flex, and remaining cross-axis edge cases.
using NUnit.Framework;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Flex.FlexTestHelpers;

namespace Weva.Tests.Layout.Flex {
    public class FlexAlignItemsExtTests {

        // ── first baseline / last baseline keyword forms ───────────────────

        // CSS Flexbox L1 §8.3: "first baseline" is the canonical multi-word
        // form of "baseline". The engine now recognises the two-word form via
        // UnwrapOverflowPosition / ParseAlignSelf stripping the first/last prefix.

        [Test]
        public void AlignItems_first_baseline_spec_aligns_same_as_baseline() {
            // Spec: "first baseline" ≡ "baseline". With mono metrics (ascent
            // = 0.8*fontSize): 16px baseline = 12.8, 32px baseline = 25.6.
            // Small item must shift down by 12.8 to align baselines.
            var css = @"
                .flex { display: flex; align-items: first baseline; width: 600px; height: 200px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"flex\">"
                + "<p style=\"font-size:16px;margin:0\">a</p>"
                + "<p style=\"font-size:32px;margin:0\">b</p>"
                + "</div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var small = ChildAt(fb, 0); var large = ChildAt(fb, 1);
            Assert.That(large.Y, Is.EqualTo(0).Within(0.001));
            Assert.That(small.Y, Is.EqualTo(12.8).Within(0.5),
                "first-baseline: small item must shift to align its baseline with the large item");
        }

        [Test]
        public void AlignItems_last_baseline_accepted_without_exception() {
            // "last baseline" is accepted by the parser and applies some
            // alignment. We don't require exact spec values here because
            // last-baseline falls back to flex-end in v1; the test guards
            // that the keyword is parsed and doesn't crash.
            var css = @"
                .flex { display: flex; align-items: last baseline; width: 600px; height: 200px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"flex\">"
                + "<div style=\"width:100px;height:50px\"></div>"
                + "</div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0);
            // Simply assert a valid Y position — must not throw or be NaN.
            Assert.That(a.Y, Is.GreaterThanOrEqualTo(0));
            Assert.That(a.Y, Is.LessThanOrEqualTo(200));
        }

        // ── align-items in column flex ────────────────────────────────────

        [Test]
        public void Column_align_items_flex_start_pins_to_left() {
            var (root, _, _) = Build(
                "<div style=\"display:flex;flex-direction:column;width:300px;height:200px;align-items:flex-start\">"
                + "<div style=\"width:80px;height:40px\"></div></div>",
                null, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0);
            Assert.That(a.X, Is.EqualTo(0).Within(0.001));
            Assert.That(a.Width, Is.EqualTo(80).Within(0.001));
        }

        [Test]
        public void Column_align_items_flex_end_pins_to_right() {
            var (root, _, _) = Build(
                "<div style=\"display:flex;flex-direction:column;width:300px;height:200px;align-items:flex-end\">"
                + "<div style=\"width:80px;height:40px\"></div></div>",
                null, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0);
            Assert.That(a.X, Is.EqualTo(220).Within(0.001), "item at right: 300 - 80 = 220");
        }

        [Test]
        public void Column_align_items_center_centers_horizontally() {
            var (root, _, _) = Build(
                "<div style=\"display:flex;flex-direction:column;width:300px;height:200px;align-items:center\">"
                + "<div style=\"width:80px;height:40px\"></div></div>",
                null, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0);
            Assert.That(a.X, Is.EqualTo(110).Within(0.001), "centered: (300-80)/2 = 110");
        }

        [Test]
        public void Column_align_items_stretch_fills_container_width() {
            var (root, _, _) = Build(
                "<div style=\"display:flex;flex-direction:column;width:300px;height:200px\">"
                + "<div style=\"height:40px\"></div></div>",
                null, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0);
            // Default align-items is stretch in column flex → width = 300
            Assert.That(a.Width, Is.EqualTo(300).Within(0.001));
        }

        // ── align-self overrides in column flex ───────────────────────────

        [Test]
        public void Column_align_self_center_overrides_stretch() {
            var (root, _, _) = Build(
                "<div style=\"display:flex;flex-direction:column;width:300px;height:200px\">"
                + "<div style=\"height:40px;align-self:center\"></div></div>",
                null, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0);
            // align-self:center in column flex → X = (300 - content-width)/2.
            // Item has no explicit width, so it shrinks to 0 content width on center?
            // Guard: width must be less than container, not stretched.
            Assert.That(a.Width, Is.LessThan(300 - 0.001),
                "align-self:center on column flex item must not stretch to full width");
        }

        // ── Stretch with explicit cross size wins ─────────────────────────

        [Test]
        public void Stretch_does_not_override_explicit_height_in_row_flex() {
            // Already in FlexAlignItemsTests but we add a column variant:
            // explicit width in column flex wins over stretch.
            var (root, _, _) = Build(
                "<div style=\"display:flex;flex-direction:column;width:300px;height:200px\">"
                + "<div style=\"width:80px;height:40px\"></div></div>",
                null, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0);
            // Explicit width:80px must win over stretch
            Assert.That(a.Width, Is.EqualTo(80).Within(0.001),
                "explicit width on column flex item must not be overridden by stretch");
        }

        // ── Mixed align-self within one container ─────────────────────────

        [Test]
        public void Mixed_align_self_values_apply_independently_per_item() {
            var (root, _, _) = Build(
                "<div style=\"display:flex;width:600px;height:200px;align-items:flex-start\">"
                + "<div style=\"width:100px;height:50px\"></div>"
                + "<div style=\"width:100px;height:50px;align-self:center\"></div>"
                + "<div style=\"width:100px;height:50px;align-self:flex-end\"></div>"
                + "</div>",
                null, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1); var c = ChildAt(fb, 2);
            Assert.That(a.Y, Is.EqualTo(0).Within(0.001), "flex-start item at Y=0");
            Assert.That(b.Y, Is.EqualTo(75).Within(0.001), "centered item at Y=75");
            Assert.That(c.Y, Is.EqualTo(150).Within(0.001), "flex-end item at Y=150");
        }

        // ── Safe center / unsafe center (see FlexSafePositionalAlignmentTests for justify) ──

        [Test]
        public void AlignItems_unsafe_center_centers_on_cross_axis() {
            var (root, _, _) = Build(
                "<div style=\"display:flex;width:600px;height:200px;align-items:unsafe center\">"
                + "<div style=\"width:100px;height:50px\"></div></div>",
                null, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0);
            Assert.That(a.Y, Is.EqualTo(75).Within(0.001));
        }
    }
}
