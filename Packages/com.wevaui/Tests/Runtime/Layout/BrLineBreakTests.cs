// Tests for <br> forced line break behaviour.
//
// HTML spec §4.5.27: The br element represents a line break.
// CSS rendering: <br> in inline formatting context forces a line break
// regardless of white-space mode. Consecutive <br><br> produce an empty
// intermediate line. <br> inside a <span> breaks the inline run.
// <br> with white-space:nowrap still breaks (break is unconditional per spec).
//
// MonoFontMetrics: fontSize=16, lineHeight=20, ascent=16, descent=4, measure=8px/char.
// LineHeight arithmetic: one 16px font-size line produces a lineBox.Height near 20
// (metric lineHeight). These tests use approximate checks.

using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Layout.Text;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    public class BrLineBreakTests {

        // Helper: collect all LineBox children of a box in order.
        static List<LineBox> GetLines(Box box) {
            var lines = new List<LineBox>();
            foreach (var c in box.Children) {
                if (c is LineBox lb) lines.Add(lb);
            }
            return lines;
        }

        // Helper: find the first BlockBox whose element has the given tag name.
        static BlockBox FindBlock(Box root, string tag) {
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && !(b is AnonymousBlockBox) && bb.Element?.TagName == tag)
                    return bb;
            }
            return null;
        }

        // Helper: collect all TextRuns inside a box.
        static List<TextRun> GetTextRuns(Box root) {
            var runs = new List<TextRun>();
            foreach (var b in AllBoxes(root)) {
                if (b is TextRun tr) runs.Add(tr);
            }
            return runs;
        }

        // -----------------------------------------------------------------------
        // BR-1: two text runs split by <br> produce two line boxes.
        //
        // Geometry: with MonoFontMetrics, a line has height ~20. The second
        // line box should start at a Y greater than or equal to the first line's height.
        // -----------------------------------------------------------------------
        [Test]
        public void Br_splits_two_text_runs_into_two_line_boxes() {
            var (root, _, _) = Build("<p>Line 1<br>Line 2</p>", null, viewportWidth: 800);
            var p = FindBlock(root, "p");
            Assert.That(p, Is.Not.Null, "p element not found");

            var lines = GetLines(p);
            Assert.That(lines.Count, Is.EqualTo(2), "Expected exactly 2 line boxes (one per br-split segment)");
        }

        // -----------------------------------------------------------------------
        // BR-2: second line's Y equals first line's height (lines stack consecutively).
        // -----------------------------------------------------------------------
        [Test]
        public void Br_second_line_Y_equals_first_line_height() {
            var (root, _, _) = Build("<p>Line 1<br>Line 2</p>", null, viewportWidth: 800);
            var p = FindBlock(root, "p");
            var lines = GetLines(p);
            Assert.That(lines.Count, Is.EqualTo(2));

            double firstH = lines[0].Height;
            double secondY = lines[1].Y;
            Assert.That(secondY, Is.EqualTo(firstH).Within(0.5),
                "Second line Y should equal first line height (lines stack flush)");
        }

        // -----------------------------------------------------------------------
        // BR-3: consecutive <br><br> produce three line boxes: "text", empty, "text".
        // The empty line has a positive height (font line height).
        // -----------------------------------------------------------------------
        [Test]
        public void Double_br_produces_three_line_boxes_with_empty_middle() {
            var (root, _, _) = Build("<p>First<br><br>Third</p>", null, viewportWidth: 800);
            var p = FindBlock(root, "p");
            var lines = GetLines(p);

            // Line 0: "First", Line 1: empty (produced by first <br> finalizing the
            // line and second <br> starting then finalizing a new empty line),
            // Line 2: "Third".
            Assert.That(lines.Count, Is.EqualTo(3), "Expected 3 line boxes: text, empty, text");

            // The middle line should have positive height (font metrics contribution).
            Assert.That(lines[1].Height, Is.GreaterThan(0),
                "Empty line from consecutive <br><br> must have positive height");
        }

        // -----------------------------------------------------------------------
        // BR-4: <br> inside a <span> still breaks the containing paragraph.
        // -----------------------------------------------------------------------
        [Test]
        public void Br_inside_span_breaks_paragraph() {
            var (root, _, _) = Build(
                "<p>Before <span>text<br>after</span> end</p>",
                null, viewportWidth: 800);
            var p = FindBlock(root, "p");
            var lines = GetLines(p);

            Assert.That(lines.Count, Is.GreaterThanOrEqualTo(2),
                "<br> inside a <span> must produce at least 2 line boxes in the paragraph");
        }

        // -----------------------------------------------------------------------
        // BR-5: <br> inside white-space:nowrap content still forces a line break.
        // The spec says <br> breaks regardless of white-space mode.
        // -----------------------------------------------------------------------
        [Test]
        public void Br_inside_nowrap_still_breaks() {
            var (root, _, _) = Build(
                "<p>Before<br>After</p>",
                "p { white-space: nowrap; }",
                viewportWidth: 800);
            var p = FindBlock(root, "p");
            var lines = GetLines(p);

            Assert.That(lines.Count, Is.EqualTo(2),
                "<br> must force a line break even when white-space:nowrap is active");
        }

        // -----------------------------------------------------------------------
        // BR-6: no-<br> regression pin — plain text without <br> stays on one line.
        // -----------------------------------------------------------------------
        [Test]
        public void No_br_short_text_stays_on_one_line() {
            var (root, _, _) = Build("<p>Hello world</p>", null, viewportWidth: 800);
            var p = FindBlock(root, "p");
            var lines = GetLines(p);
            Assert.That(lines.Count, Is.EqualTo(1),
                "Regression: short text with no <br> should produce exactly 1 line");
        }

        // -----------------------------------------------------------------------
        // BR-7: <br> at the start of a paragraph (before any text) produces an
        // empty first line and one content line.
        // -----------------------------------------------------------------------
        [Test]
        public void Br_at_start_before_text_produces_empty_first_line() {
            var (root, _, _) = Build("<p><br>Text after</p>", null, viewportWidth: 800);
            var p = FindBlock(root, "p");
            var lines = GetLines(p);

            Assert.That(lines.Count, Is.EqualTo(2),
                "<br> before any text should produce an empty first line, then a content line");
            Assert.That(lines[0].Height, Is.GreaterThan(0),
                "Empty first line must still have positive height from font metrics");
        }

        // -----------------------------------------------------------------------
        // BR-8: <br> inside an inline-block atom's content breaks within the atom.
        // -----------------------------------------------------------------------
        [Test]
        public void Br_inside_inline_block_atom_breaks_atom_content() {
            var (root, _, _) = Build(
                "<p><span style=\"display:inline-block\">Line A<br>Line B</span></p>",
                null, viewportWidth: 800);
            var p = FindBlock(root, "p");
            // The inline-block span becomes a BlockBox atom. Find it and check its lines.
            BlockBox atom = null;
            foreach (var b in AllBoxes(p)) {
                if (b is BlockBox bb && bb.IsInlineBlock && bb.Element?.TagName == "span") {
                    atom = bb; break;
                }
            }
            Assert.That(atom, Is.Not.Null, "Inline-block span not found in box tree");

            var atomLines = GetLines(atom);
            Assert.That(atomLines.Count, Is.EqualTo(2),
                "<br> inside inline-block atom should produce 2 lines within the atom");
        }

        // -----------------------------------------------------------------------
        // BR-9: three text segments separated by two <br>s produce three line boxes.
        // -----------------------------------------------------------------------
        [Test]
        public void Two_brs_produce_three_line_boxes() {
            var (root, _, _) = Build("<p>A<br>B<br>C</p>", null, viewportWidth: 800);
            var p = FindBlock(root, "p");
            var lines = GetLines(p);
            Assert.That(lines.Count, Is.EqualTo(3),
                "Two <br> elements should produce exactly 3 line boxes");
        }

        // -----------------------------------------------------------------------
        // BR-10: third line Y in A<br>B<br>C equals 2 * first line height.
        // -----------------------------------------------------------------------
        [Test]
        public void Br_third_line_Y_is_two_line_heights_down() {
            var (root, _, _) = Build("<p>A<br>B<br>C</p>", null, viewportWidth: 800);
            var p = FindBlock(root, "p");
            var lines = GetLines(p);
            Assert.That(lines.Count, Is.EqualTo(3));

            double lh = lines[0].Height;
            // All lines should have equal height since they use the same font.
            Assert.That(lines[1].Height, Is.EqualTo(lh).Within(0.5));
            Assert.That(lines[2].Y, Is.EqualTo(lh * 2).Within(1.0),
                "Third line Y must equal two line heights below first line");
        }
    }
}
