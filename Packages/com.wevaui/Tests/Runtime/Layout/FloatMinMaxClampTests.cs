// CSS 2.1 §10.3.5 / §9.5.1 — Float shrink-to-fit min/max-width clamping.
//
// Chrome clamps a float's shrink-to-fit width by author min-width / max-width,
// but the engine did NOT apply this clamp for floats (only for abs-pos boxes).
// This file pins the newly-added clamping behavior.
//
// Hook: BlockLayout.LayoutFloatBox, after shrink-to-fit width computation.
// Mirrors the abs-pos clamping in PositioningPass.ApplyAbsoluteAgainst.
//
// Tests:
//   • min-width floors shrink-to-fit (content-box and border-box)
//   • max-width caps shrink-to-fit (content-box and border-box)
//   • float without min/max is unchanged (regression pin)
//   • float with explicit width is unaffected by these clamps
//     (explicit width goes through ApplyBoxModel clamping, not LayoutFloatBox)

using System.Collections.Generic;
using NUnit.Framework;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    public class FloatMinMaxClampTests {

        // ─── Helpers ──────────────────────────────────────────────────────────

        static IEnumerable<Box> Walk(Box root) {
            yield return root;
            foreach (var c in root.Children) foreach (var d in Walk(c)) yield return d;
        }

        static BlockBox FindById(Box root, string id) {
            foreach (var b in Walk(root)) {
                if (b is BlockBox bb && bb.Element != null
                    && bb.Element.GetAttribute("id") == id) return bb;
            }
            return null;
        }

        // ─── min-width floors shrink-to-fit ───────────────────────────────────

        [Test]
        public void Float_min_width_floors_shrink_to_fit() {
            // A float with auto width and no content would shrink to its frame
            // (0 px content + frame).  min-width:120px must raise it to 120px.
            // No padding or border so frame = 0, so width == 120px.
            var (root, _, _) = Build(
                "<div style=\"width:600px\">" +
                "<div id=\"f\" style=\"float:left; min-width:120px; height:40px;\"></div>" +
                "</div>",
                null, viewportWidth: 800);
            var f = FindById(root, "f");
            Assert.That(f, Is.Not.Null);
            Assert.That(f.IsFloat, Is.True);
            Assert.That(f.Width, Is.EqualTo(120).Within(0.001),
                "float shrink-to-fit must be floored by min-width:120px");
        }

        [Test]
        public void Float_min_width_border_box_floors_shrink_to_fit() {
            // With box-sizing:border-box, min-width:100px is the border-box floor.
            // Since the float has 5px padding each side, frame = 10, content = 90.
            // Shrink-to-fit of an empty float = frame only = 10px (no content).
            // min-width:100px border-box = min border-box = 100px → final width = 100.
            var (root, _, _) = Build(
                "<div style=\"width:600px\">" +
                "<div id=\"f\" style=\"float:left; padding:5px; box-sizing:border-box; min-width:100px; height:30px;\"></div>" +
                "</div>",
                null, viewportWidth: 800);
            var f = FindById(root, "f");
            Assert.That(f, Is.Not.Null);
            Assert.That(f.Width, Is.EqualTo(100).Within(0.001),
                "float border-box min-width must floor the shrink-to-fit border-box width");
        }

        // ─── max-width caps shrink-to-fit ─────────────────────────────────────

        [Test]
        public void Float_max_width_caps_shrink_to_fit() {
            // A float with long text would expand toward max-content.
            // max-width:50px must cap the result to 50px border-box.
            // Use a very long word so max-content > 50px.
            var (root, _, _) = Build(
                "<div style=\"width:600px\">" +
                "<div id=\"f\" style=\"float:left; max-width:50px; height:40px;\">" +
                "WWWWWWWWWWWWWWWWWWWWWWWWWWWW" +  // long token forces wide max-content
                "</div>" +
                "</div>",
                null, viewportWidth: 800);
            var f = FindById(root, "f");
            Assert.That(f, Is.Not.Null);
            Assert.That(f.Width, Is.EqualTo(50).Within(0.001),
                "float shrink-to-fit must be capped by max-width:50px");
        }

        [Test]
        public void Float_max_width_border_box_caps_shrink_to_fit() {
            // box-sizing:border-box max-width:80px — frame = 10px (5px padding each side).
            // Content max-content of a wide text sequence would exceed 80px.
            // Result border-box must be capped at 80px.
            var (root, _, _) = Build(
                "<div style=\"width:600px\">" +
                "<div id=\"f\" style=\"float:left; padding:5px; box-sizing:border-box; max-width:80px; height:30px;\">" +
                "ABCDEFGHIJKLMNOPQRSTUVWXYZABCDEF" +
                "</div>" +
                "</div>",
                null, viewportWidth: 800);
            var f = FindById(root, "f");
            Assert.That(f, Is.Not.Null);
            Assert.That(f.Width, Is.EqualTo(80).Within(0.001),
                "float border-box max-width:80px must cap the shrink-to-fit border-box width");
        }

        // ─── Regression: float without min/max is unchanged ───────────────────

        [Test]
        public void Float_without_min_max_uses_normal_shrink_to_fit() {
            // Existing shrink-to-fit (min(max-content, max(min-content, avail))).
            // An empty float has frame-only width; no min/max so result = 0 + frame.
            // No padding/border: width = 0.
            var (root, _, _) = Build(
                "<div style=\"width:600px\">" +
                "<div id=\"f\" style=\"float:left; height:40px;\"></div>" +
                "</div>",
                null, viewportWidth: 800);
            var f = FindById(root, "f");
            Assert.That(f, Is.Not.Null);
            double frame = f.PaddingLeft + f.PaddingRight + f.BorderLeft + f.BorderRight;
            Assert.That(f.Width, Is.EqualTo(frame).Within(0.001),
                "float with no min/max and no content shrinks to frame");
        }

        [Test]
        public void Float_without_min_max_shrink_to_fit_text_content() {
            // A float whose max-content is smaller than avail should use max-content.
            // "Hi" with MonoFontMetrics at 16px (8px/char) = 16px max-content.
            // No padding/border, avail = 600px, min-content = 16px (one word).
            // fitted = min(16, max(16, 600)) = 16px.
            var (root, _, _) = Build(
                "<div style=\"width:600px\">" +
                "<div id=\"f\" style=\"float:left; height:40px;\">Hi</div>" +
                "</div>",
                null, viewportWidth: 800);
            var f = FindById(root, "f");
            Assert.That(f, Is.Not.Null);
            // Width should be 16px (2 chars * 8px), with no min/max clamping.
            Assert.That(f.Width, Is.EqualTo(16).Within(0.5),
                "float shrink-to-fit without min/max = max-content of text");
        }

        // ─── Float with explicit width is unaffected ───────────────────────────

        [Test]
        public void Float_explicit_width_bypasses_shrink_to_fit_clamps() {
            // Explicit width on a float does NOT go through the shrink-to-fit path
            // in LayoutFloatBox (it returns early).  The min/max clamp there must
            // not affect it.  ApplyBoxModel handles min/max for explicit-width floats
            // separately (same as regular blocks).
            var (root, _, _) = Build(
                "<div style=\"width:600px\">" +
                "<div id=\"f\" style=\"float:left; width:200px; max-width:50px; height:40px;\"></div>" +
                "</div>",
                null, viewportWidth: 800);
            var f = FindById(root, "f");
            Assert.That(f, Is.Not.Null);
            // max-width:50px + explicit width:200px → ApplyBoxModel clamps to 50px.
            Assert.That(f.Width, Is.EqualTo(50).Within(0.001),
                "explicit float width is clamped by max-width via ApplyBoxModel");
        }

        // ─── min-width > max-width: min wins (CSS Sizing L3 §5.2) ─────────────

        [Test]
        public void Float_min_width_wins_when_greater_than_max_width() {
            // CSS §5.2: when min-width > max-width, min-width wins.
            // Apply max first then min: max clamps to 30, then min raises to 80.
            var (root, _, _) = Build(
                "<div style=\"width:600px\">" +
                "<div id=\"f\" style=\"float:left; min-width:80px; max-width:30px; height:40px;\">" +
                "Hi" +
                "</div>" +
                "</div>",
                null, viewportWidth: 800);
            var f = FindById(root, "f");
            Assert.That(f, Is.Not.Null);
            Assert.That(f.Width, Is.EqualTo(80).Within(0.001),
                "min-width:80px wins over max-width:30px per CSS §5.2");
        }
    }
}
