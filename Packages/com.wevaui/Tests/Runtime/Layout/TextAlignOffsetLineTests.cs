using System.Collections.Generic;
using NUnit.Framework;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    // Tests for InlineLayout.OffsetLine fix:
    // OffsetLine (called for text-align:center and text-align:right) must NOT
    // add dx to line.Width. The translation only moves content positions; it
    // does not widen the content span. Compare with JustifyLine which DOES
    // add `extra` to line.Width because spaces are physically stretched.
    //
    // Bug: line.Width += dx in OffsetLine inflated the width by the centering/
    // right shift. MakeAtomItem picks up lb.Width to compute max-content for
    // inline-block atoms, so a centered inline-block was wider than its text.
    //
    // MonoFontMetrics defaults: charWidthEm=0.5 → 8 px/char @ 16 px.
    // "AB" = 2 × 8 = 16 px natural.
    // "HI" = 2 × 8 = 16 px natural.
    // "A B" = "A"(8) + " "(8) + "B"(8) = 24 px natural.
    public class TextAlignOffsetLineTests {
        // -----------------------------------------------------------------------
        // Helper: find the first LineBox child of a named block element.
        // -----------------------------------------------------------------------

        static BlockBox FindFirstBlock(Box root, string tag) {
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && !(b is AnonymousBlockBox) && bb.Element?.TagName == tag)
                    return bb;
            }
            return null;
        }

        static List<LineBox> LinesOf(BlockBox container) {
            var list = new List<LineBox>();
            foreach (var c in container.Children)
                if (c is LineBox lb) list.Add(lb);
            return list;
        }

        static BlockBox FirstInlineBlock(Box root) {
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.IsInlineBlock) return bb;
            }
            return null;
        }

        // -----------------------------------------------------------------------
        // Test 1: center-align — line.Width after OffsetLine equals content span
        //
        // Container 200 px, text-align:center, white-space:pre (force slow path).
        // "AB" glyph width = 16 px. extra = 200-16 = 184. dx = 92 (center shift).
        // Before fix: line.Width = 16 + 92 = 108.
        // After  fix: line.Width = 16 (content span only, NOT right-edge).
        // -----------------------------------------------------------------------
        [Test]
        public void Center_align_OffsetLine_does_not_inflate_line_Width() {
            var (root, _, _) = Build(
                "<p style=\"white-space:pre;text-align:center;width:200px;font-size:16px\">AB</p>",
                null, 400);
            var p = FindFirstBlock(root, "p");
            var lines = LinesOf(p);
            Assert.That(lines.Count, Is.EqualTo(1), "single line expected");

            // After the fix, line.Width must equal the glyph content width,
            // not be inflated by the centering shift.
            double glyphWidth = 16; // "AB" @ 16 px mono: 2 × 8 = 16
            Assert.That(lines[0].Width, Is.EqualTo(glyphWidth).Within(0.001),
                "center-align: line.Width must stay at content span (not += centering shift)");
        }

        // -----------------------------------------------------------------------
        // Test 2: inline-block shrink-to-fit with center-aligned text
        //
        // The inline-block contains centered text. MakeAtomItem runs a first
        // layout pass at availableWidth, reads lb.Width to get maxContent, then
        // re-layouts at that width.  If OffsetLine inflated lb.Width by the
        // centering shift, the atom would be wider than its text.
        //
        // Setup: parent 400 px, inline-block no explicit width, text-align:center.
        // "HI" glyph = 2 × 8 = 16 px.
        // Correct atom.Width = 16 px (content only, no padding/border).
        // Buggy  atom.Width ≈ (400+16)/2 = 208 px (half of available-width as fake width).
        //
        // white-space:pre forces the slow-path layout inside the atom so
        // ApplyTextAlign fires on the atom's lines.
        // -----------------------------------------------------------------------
        [Test]
        public void Inline_block_centered_text_intrinsic_width_is_content_not_inflated() {
            var (root, _, _) = Build(
                "<p style=\"width:400px;font-size:16px\">" +
                "<span style=\"display:inline-block;text-align:center;white-space:pre\">HI</span>" +
                "</p>",
                null, 800);
            var atom = FirstInlineBlock(root);
            Assert.That(atom, Is.Not.Null, "inline-block span must be found");

            // "HI" at 16 px mono = 2 × 8 = 16 px. No padding/border, so atom.Width == 16.
            // Before fix: atom.Width ≈ 208 px (inflated by centering of 16 in 400 px space).
            Assert.That(atom.Width, Is.EqualTo(16).Within(0.001),
                "inline-block shrink-to-fit must equal content max-content (16 px), not inflated by centering shift");
        }

        // -----------------------------------------------------------------------
        // Test 3: right-align — line.Width after OffsetLine equals content span
        //
        // Container 200 px, text-align:right, white-space:pre.
        // "AB" glyph width = 16 px. extra = 200-16 = 184. dx = 184 (full right shift).
        // Before fix: line.Width = 16 + 184 = 200.
        // After  fix: line.Width = 16 (content span only).
        // Also verify the text run's X was shifted to the right edge correctly.
        // -----------------------------------------------------------------------
        [Test]
        public void Right_align_OffsetLine_does_not_inflate_line_Width() {
            var (root, _, _) = Build(
                "<p style=\"white-space:pre;text-align:right;width:200px;font-size:16px\">AB</p>",
                null, 400);
            var p = FindFirstBlock(root, "p");
            var lines = LinesOf(p);
            Assert.That(lines.Count, Is.EqualTo(1), "single line expected");

            double glyphWidth = 16; // "AB" @ 16 px mono
            Assert.That(lines[0].Width, Is.EqualTo(glyphWidth).Within(0.001),
                "right-align: line.Width must stay at content span (not += right-align shift)");

            // Also verify the run was shifted to sit at the right edge of the 200 px container.
            TextRun run = null;
            foreach (var b in AllBoxes(p))
                if (b is TextRun tr) { run = tr; break; }
            Assert.That(run, Is.Not.Null, "text run must exist");
            // Run X should be at container_width - glyph_width = 200 - 16 = 184.
            Assert.That(run.X, Is.EqualTo(184).Within(0.001),
                "right-align: run must shift to right edge of container (184 px)");
        }

        // -----------------------------------------------------------------------
        // Test 4: justify regression-guard — JustifyLine DOES add extra to line.Width
        //
        // For text-align:justify, inter-word gaps are physically stretched, so
        // line.Width genuinely grows. This test ensures JustifyLine's
        // `line.Width += extra` is NOT affected by the OffsetLine fix and
        // still reports the physically-widened line width.
        //
        // Setup: 30 px container, "A B C". Forces wrap: "A B" on line 1 (IsFinalLine=false),
        // "C" on line 2 (IsFinalLine=true, alignLast=left → no justify).
        // Line 1: "A"(8) + " "(8) + "B"(8) = 24 px. extra = 30-24 = 6.
        // After JustifyLine: space widens by 6, line.Width = 24+6 = 30.
        // -----------------------------------------------------------------------
        [Test]
        public void Justify_line_Width_includes_extra_space_growth() {
            // white-space:normal (default) allows wrapping.
            var (root, _, _) = Build(
                "<p style=\"text-align:justify;width:30px;font-size:16px\">A B C</p>",
                null, 200);
            var p = FindFirstBlock(root, "p");
            var lines = LinesOf(p);
            // Must have at least 2 lines: "A B" wraps from "A B C".
            Assert.That(lines.Count, Is.GreaterThanOrEqualTo(2),
                "text must wrap to at least 2 lines in a 30 px container");

            // The first line is a non-final line and gets justified.
            var firstLine = lines[0];
            Assert.That(firstLine.IsFinalLine, Is.False,
                "first line must be non-final so justify applies");

            // After JustifyLine, line.Width = contentW (fully spans the container).
            // This verifies JustifyLine's `line.Width += extra` is intact.
            Assert.That(firstLine.Width, Is.EqualTo(30).Within(0.001),
                "justified line.Width must equal container width (spaces physically stretched)");
        }
    }
}
