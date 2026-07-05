// Regression tests for CSS §10.6.7 FIXED-DIALOG-HEIGHT:
// a `position: fixed` (or absolute) element with `height: auto` (or
// `height: fit-content`) and at most one vertical pin must size to its
// content height, NOT stretch to fill the remaining viewport.
//
// Root cause (diagnosed in PositioningPass.cs): when both top+bottom are
// pinned (vertPinned=true) and height is a shrink-to-fit keyword
// (fit-content, min-content, max-content), the old code applied the CSS2
// §10.6.7 case (e) stretch formula (h = CB.height − top − bottom) instead
// of respecting the fit-content intent.  The Weva UA stylesheet gives
// `<dialog>` both `top:0; bottom:0` (for vertical auto-margin centering)
// AND `height:fit-content` (for content-based sizing).  The combination
// caused 520px (= 600 − 80) instead of ~86px for snippet 25.
using NUnit.Framework;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Positioning.PositioningTestHelpers;

namespace Weva.Tests.Layout.Positioning {
    // Tests use MonoFontMetrics (0.5em per char, lineHeight = fs * 1.2).
    // At 16px font: 8px/char, lineHeight ≈ 19.2px rounded to ~20px.
    public class FixedAutoHeightTests {

        // ------------------------------------------------------------------
        // 1. Single-pin (top only): content height.
        // CSS §10.6.7 case (b): height:auto, top is not auto, bottom is auto
        // → height is content-based.
        // ------------------------------------------------------------------
        [Test]
        public void Fixed_top_only_auto_height_equals_content_height() {
            // Dialog-like box: position:fixed; top:80px; width:100px.
            // No bottom pin, no explicit height. Should shrink to content.
            // "Hi" at 16px MonoFontMetrics: 1 line ≈ 19.2px.
            // height = padding(8)+border(0)+line+padding(8) ≈ 35px.
            const string css = @"
                .box { position: fixed; top: 80px; width: 100px;
                       padding: 8px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"box\">Hi</div>",
                css, viewportWidth: 800, viewportHeight: 600);
            var box = FirstByClass(root, "box");
            Assert.That(box, Is.Not.Null);
            // Content height must be well under viewport−top (600−80=520).
            Assert.That(box.Height, Is.LessThan(100),
                "top-only fixed box should shrink to content, not stretch to 520px");
            // And must be at least the one line's height plus padding.
            Assert.That(box.Height, Is.GreaterThan(20),
                "top-only fixed box must include its content line height");
        }

        // ------------------------------------------------------------------
        // 2. Single-pin (bottom only): content height, anchored at bottom.
        // CSS §10.6.7 case (a): height:auto, top auto, bottom is not auto
        // → height is content-based.
        // ------------------------------------------------------------------
        [Test]
        public void Absolute_bottom_only_auto_height_equals_content_height() {
            // Absolute box anchored at bottom:20px. Content = "OK" 1 line.
            const string css = @"
                .anchor { position: relative; width: 400px; height: 300px; }
                .box { position: absolute; bottom: 20px; width: 100px;
                       padding: 8px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"anchor\"><div class=\"box\">OK</div></div>",
                css, viewportWidth: 800, viewportHeight: 600);
            var box = FirstByClass(root, "box");
            Assert.That(box, Is.Not.Null);
            // Should not stretch to anchor.CBHeight − bottom = 280px.
            Assert.That(box.Height, Is.LessThan(100),
                "bottom-only abs box should shrink to content, not stretch to CB.h−bottom");
            Assert.That(box.Height, Is.GreaterThan(20),
                "bottom-only abs box must include its content line height");
            // Box should be positioned so its bottom edge is at CB.h − bottom.
            var (bx, by) = AbsoluteOriginOf(box);
            // CB is .anchor at y=0, h=300. Bottom pin=20: top of box = 300-20-h.
            double expectedY = 300 - 20 - box.Height;
            Assert.That(by, Is.EqualTo(expectedY).Within(1.0),
                "bottom-pinned box should be placed with its bottom edge at CB.bottom−20");
        }

        // ------------------------------------------------------------------
        // 3. Both pins set by AUTHOR + height:auto → stretch (per §10.6.7(e)).
        // This is the CONTROL case: explicit author top+bottom must stretch.
        // ------------------------------------------------------------------
        [Test]
        public void Fixed_both_pins_auto_height_stretches_to_inset_space() {
            // Author explicitly sets top AND bottom → stretch per §10.6.7(e).
            const string css = @"
                .box { position: fixed; top: 50px; bottom: 50px;
                       width: 100px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"box\">Text</div>",
                css, viewportWidth: 800, viewportHeight: 600);
            var box = FirstByClass(root, "box");
            Assert.That(box, Is.Not.Null);
            // Expected stretch: 600 − 50 − 50 = 500px.
            Assert.That(box.Height, Is.EqualTo(500).Within(1.0),
                "both-pinned auto-height fixed box must stretch between the pins");
        }

        // ------------------------------------------------------------------
        // 4. Explicit height overrides: must not change.
        // Even with both vertical pins, an explicit height stays.
        // ------------------------------------------------------------------
        [Test]
        public void Fixed_explicit_height_not_overridden_by_pins() {
            const string css = @"
                .box { position: fixed; top: 0; bottom: 0; width: 100px;
                       height: 200px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"box\">X</div>",
                css, viewportWidth: 800, viewportHeight: 600);
            var box = FirstByClass(root, "box");
            Assert.That(box, Is.Not.Null);
            Assert.That(box.Height, Is.EqualTo(200).Within(0.5),
                "explicit height must not be overridden by vertical inset pins");
        }

        // ------------------------------------------------------------------
        // 5. UA fit-content height + both pins set → fit-content wins.
        // This is the canonical FIXED-DIALOG-HEIGHT regression case:
        // height:fit-content + vertPinned must NOT stretch.
        // ------------------------------------------------------------------
        [Test]
        public void Fixed_fit_content_height_with_both_pins_uses_content_height() {
            // Simulate the dialog UA pattern: both vertical pins but
            // height:fit-content. Engine must not stretch.
            const string css = @"
                .box { position: fixed; top: 80px; bottom: 0;
                       width: 200px; padding: 16px;
                       height: fit-content; }
            ";
            var (root, _, _) = Build(
                "<div class=\"box\">Confirm action?</div>",
                css, viewportWidth: 800, viewportHeight: 600);
            var box = FirstByClass(root, "box");
            Assert.That(box, Is.Not.Null);
            // If stretched: 600 − 80 − 0 = 520px (the old bug).
            // Content: 1 line + 2*16px padding ≈ 51px.
            Assert.That(box.Height, Is.LessThan(100),
                "height:fit-content with both vertical pins must NOT stretch (was 520px, old bug)");
            Assert.That(box.Height, Is.GreaterThan(20),
                "height:fit-content must still include the content line height");
        }

        // ------------------------------------------------------------------
        // 6. fit-content box must never exceed available inset space.
        // If content is taller than the space between pins, cap at the space.
        // ------------------------------------------------------------------
        [Test]
        public void Fixed_fit_content_capped_at_available_inset_space() {
            // Tall content (10 lines) inside a very narrow inset space (40px).
            const string css = @"
                .box { position: fixed; top: 560px; bottom: 0;
                       width: 200px;
                       height: fit-content; }
            ";
            // 10 paragraphs of "Line N" text.
            var html = "";
            for (int i = 0; i < 10; i++) html += $"<p>Line {i}</p>";
            var (root, _, _) = Build(
                $"<div class=\"box\">{html}</div>",
                css, viewportWidth: 800, viewportHeight: 600);
            var box = FirstByClass(root, "box");
            Assert.That(box, Is.Not.Null);
            // Available space between pins: 600 − 560 − 0 = 40px.
            // Content is much taller than 40px; box must be capped at 40px.
            Assert.That(box.Height, Is.LessThanOrEqualTo(40 + 1.0),
                "height:fit-content must be capped at the available inset space (40px) when content is taller");
        }
    }
}
