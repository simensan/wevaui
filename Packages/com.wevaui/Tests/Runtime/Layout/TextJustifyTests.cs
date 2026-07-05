using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Layout;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    // CSS Text Module Level 3 §7.3: text-justify controls the spreading
    // algorithm when text-align:justify is active.
    //
    // MonoFontMetrics default: 0.5em/char → at 16px font-size = 8 px/char.
    // All tests use a fixed 200px container so arithmetic is precise.
    //
    // "ab cd ef" splits into 5 TextRun atoms: ["ab"(16), " "(8), "cd"(16), " "(8), "ef"(16)]
    //   natural line width = 64 px
    //   extra = 200 - 64 = 136 px
    public class TextJustifyTests {
        static BlockBox FindFirstBlock(Box root, string tag) {
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && !(b is AnonymousBlockBox) && bb.Element?.TagName == tag)
                    return bb;
            }
            return null;
        }

        static LineBox FirstLine(BlockBox block) {
            foreach (var c in block.Children)
                if (c is LineBox lb) return lb;
            return null;
        }

        // -------------------------------------------------------------------
        // auto / inter-word: existing inter-word spreading is preserved.
        // -------------------------------------------------------------------

        [Test]
        public void Auto_justify_distributes_extra_to_word_spaces() {
            // text-justify:auto is the initial value. Behaviour must be identical
            // to the baseline inter-word case (2 space gaps absorb 136 px / 2 = 68).
            var (root, _, _) = Build(
                "<p style=\"text-align:justify;text-align-last:justify;" +
                "text-justify:auto;width:200px\">ab cd ef</p>", null, 800);
            var p = FindFirstBlock(root, "p");
            var line = FirstLine(p);
            Assert.That(line, Is.Not.Null);
            Assert.That(line.Width, Is.EqualTo(200).Within(0.001),
                "auto justify must stretch line to container width");
            // Locate the space runs and verify they were widened, not the words.
            var runs = new List<TextRun>();
            foreach (var c in line.ChildList)
                if (c is TextRun tr) runs.Add(tr);
            // Two space runs should each be 8 + 68 = 76.
            int wideSpaces = 0;
            foreach (var r in runs)
                if (r.Text == " " && r.Width > 8.5) wideSpaces++;
            Assert.That(wideSpaces, Is.EqualTo(2),
                "both inter-word spaces must be widened by auto justify");
        }

        [Test]
        public void Inter_word_justify_same_as_auto() {
            // text-justify:inter-word is an explicit synonym for auto in this engine.
            var (root, _, _) = Build(
                "<p style=\"text-align:justify;text-align-last:justify;" +
                "text-justify:inter-word;width:200px\">ab cd ef</p>", null, 800);
            var p = FindFirstBlock(root, "p");
            var line = FirstLine(p);
            Assert.That(line.Width, Is.EqualTo(200).Within(0.001));
            // Words must NOT have extra spacing applied to them (only spaces).
            TextRun ab = null;
            foreach (var c in line.ChildList)
                if (c is TextRun tr && tr.Text == "ab") { ab = tr; break; }
            Assert.That(ab, Is.Not.Null);
            // "ab" is 16 px wide under inter-word (not widened).
            Assert.That(ab.Width, Is.EqualTo(16).Within(0.001),
                "inter-word justify must not widen word runs");
        }

        // -------------------------------------------------------------------
        // none: justify spreading is completely suppressed.
        // -------------------------------------------------------------------

        [Test]
        public void None_justify_leaves_line_at_natural_width() {
            // text-justify:none + text-align:justify → line must NOT be spread.
            // Natural width of "ab cd ef" = 8 chars × 8 px = 64 px.
            var (root, _, _) = Build(
                "<p style=\"text-align:justify;text-align-last:justify;" +
                "text-justify:none;width:200px\">ab cd ef</p>", null, 800);
            var p = FindFirstBlock(root, "p");
            var line = FirstLine(p);
            Assert.That(line, Is.Not.Null);
            Assert.That(line.Width, Is.EqualTo(64).Within(0.001),
                "text-justify:none must suppress all spreading; line stays at natural width");
        }

        [Test]
        public void None_justify_space_runs_are_not_widened() {
            var (root, _, _) = Build(
                "<p style=\"text-align:justify;text-align-last:justify;" +
                "text-justify:none;width:200px\">ab cd ef</p>", null, 800);
            var p = FindFirstBlock(root, "p");
            var line = FirstLine(p);
            // Space runs must be at their natural 8 px width (no justify increment).
            foreach (var c in line.ChildList) {
                if (c is TextRun tr && tr.Text == " ")
                    Assert.That(tr.Width, Is.EqualTo(8).Within(0.001),
                        "text-justify:none must leave space run widths at natural 8 px");
            }
        }

        [Test]
        public void None_justify_word_positions_are_unchanged() {
            // "ab" starts at X=0; with none-justify nothing shifts.
            var (root, _, _) = Build(
                "<p style=\"text-align:justify;text-align-last:justify;" +
                "text-justify:none;width:200px\">ab cd ef</p>", null, 800);
            var p = FindFirstBlock(root, "p");
            var line = FirstLine(p);
            TextRun ab = null, ef = null;
            foreach (var c in line.ChildList) {
                if (c is TextRun tr) {
                    if (tr.Text == "ab") ab = tr;
                    if (tr.Text == "ef") ef = tr;
                }
            }
            Assert.That(ab, Is.Not.Null);
            Assert.That(ef, Is.Not.Null);
            Assert.That(ab.X, Is.EqualTo(0).Within(0.001),
                "first word must not shift under none-justify");
            // "ef" natural position: "ab"(16) + " "(8) + "cd"(16) + " "(8) = 48.
            Assert.That(ef.X, Is.EqualTo(48).Within(0.001),
                "last word must stay at natural position under none-justify");
        }

        // -------------------------------------------------------------------
        // inter-character: extra distributed across every char gap.
        // -------------------------------------------------------------------
        //
        // Gap model (see JustifyLineInterCharacter doc):
        //   "ab cd ef" → runs ["ab","  ","cd"," ","ef"]
        //   intra-run gaps:  1 + 0 + 1 + 0 + 1 = 3
        //   boundary gaps:   4 (between every consecutive pair of text runs)
        //   total gaps = 7
        //   inc = 136 / 7
        //
        // Expected run states:
        //   "ab": X=0,   Width=16+inc,  JustifyLetterSpacingPx=inc
        //   " ": X=16+2*inc, Width=8,    JustifyLetterSpacingPx=inc
        //   "cd": X=24+3*inc, Width=16+inc, JustifyLetterSpacingPx=inc
        //   " ": X=40+5*inc, Width=8,    JustifyLetterSpacingPx=inc
        //   "ef": X=48+6*inc, Width=16+inc, JustifyLetterSpacingPx=inc

        [Test]
        public void Inter_character_line_width_equals_content_width() {
            var (root, _, _) = Build(
                "<p style=\"text-align:justify;text-align-last:justify;" +
                "text-justify:inter-character;width:200px\">ab cd ef</p>", null, 800);
            var p = FindFirstBlock(root, "p");
            var line = FirstLine(p);
            Assert.That(line, Is.Not.Null);
            Assert.That(line.Width, Is.EqualTo(200).Within(0.001),
                "inter-character justify must grow line to exactly the container width");
        }

        [Test]
        public void Inter_character_per_run_justify_spacing_equals_inc() {
            // Every run (word and space) gets JustifyLetterSpacingPx == inc.
            const double extra = 136.0;
            const int totalGaps = 7;
            double inc = extra / totalGaps;

            var (root, _, _) = Build(
                "<p style=\"text-align:justify;text-align-last:justify;" +
                "text-justify:inter-character;width:200px\">ab cd ef</p>", null, 800);
            var p = FindFirstBlock(root, "p");
            var line = FirstLine(p);
            var runs = new List<TextRun>();
            foreach (var c in line.ChildList)
                if (c is TextRun tr) runs.Add(tr);
            foreach (var r in runs)
                Assert.That(r.JustifyLetterSpacingPx, Is.EqualTo(inc).Within(0.001),
                    $"run '{r.Text}' must have JustifyLetterSpacingPx == inc ({inc:F4})");
        }

        [Test]
        public void Inter_character_word_runs_are_widened_by_intra_gaps_times_inc() {
            // "ab" has 1 intra-run gap → Width = 16 + 1*inc.
            const double extra = 136.0;
            const int totalGaps = 7;
            double inc = extra / totalGaps;

            var (root, _, _) = Build(
                "<p style=\"text-align:justify;text-align-last:justify;" +
                "text-justify:inter-character;width:200px\">ab cd ef</p>", null, 800);
            var p = FindFirstBlock(root, "p");
            var line = FirstLine(p);
            TextRun ab = null, cd = null, ef = null, sp1 = null;
            foreach (var c in line.ChildList) {
                if (c is TextRun tr) {
                    if (tr.Text == "ab") ab = tr;
                    else if (tr.Text == "cd") cd = tr;
                    else if (tr.Text == "ef") ef = tr;
                    else if (tr.Text == " " && sp1 == null) sp1 = tr;
                }
            }
            Assert.That(ab, Is.Not.Null);
            Assert.That(cd, Is.Not.Null);
            Assert.That(ef, Is.Not.Null);
            // 2-char word runs gain 1 intra gap each.
            Assert.That(ab.Width, Is.EqualTo(16 + inc).Within(0.001),
                "\"ab\" width must be 16 + 1*inc");
            Assert.That(cd.Width, Is.EqualTo(16 + inc).Within(0.001),
                "\"cd\" width must be 16 + 1*inc");
            Assert.That(ef.Width, Is.EqualTo(16 + inc).Within(0.001),
                "\"ef\" width must be 16 + 1*inc");
            // Space runs have 0 intra gaps → width unchanged.
            Assert.That(sp1, Is.Not.Null);
            Assert.That(sp1.Width, Is.EqualTo(8).Within(0.001),
                "space run width must not grow (0 intra gaps)");
        }

        [Test]
        public void Inter_character_run_x_positions_shifted_cumulatively() {
            const double extra = 136.0;
            const int totalGaps = 7;
            double inc = extra / totalGaps;

            var (root, _, _) = Build(
                "<p style=\"text-align:justify;text-align-last:justify;" +
                "text-justify:inter-character;width:200px\">ab cd ef</p>", null, 800);
            var p = FindFirstBlock(root, "p");
            var line = FirstLine(p);
            TextRun ab = null, sp1 = null, cd = null, sp2 = null, ef = null;
            foreach (var c in line.ChildList) {
                if (c is TextRun tr) {
                    if (tr.Text == "ab")      ab  = tr;
                    else if (tr.Text == " " && sp1 == null) sp1 = tr;
                    else if (tr.Text == "cd") cd  = tr;
                    else if (tr.Text == " " && sp2 == null) sp2 = tr;
                    else if (tr.Text == "ef") ef  = tr;
                }
            }
            // "ab" is the first text run — no cumulative shift.
            Assert.That(ab.X, Is.EqualTo(0).Within(0.001));
            // " " (first): cumulative was inc after "ab"'s intra gap, then +inc boundary = 2*inc.
            Assert.That(sp1.X, Is.EqualTo(16 + 2 * inc).Within(0.001));
            // "cd": cumulative = 2*inc (after sp1) + inc boundary = 3*inc.
            Assert.That(cd.X, Is.EqualTo(24 + 3 * inc).Within(0.001));
            // " " (second): cumulative = 3*inc + inc (cd intra) + inc boundary = 5*inc.
            Assert.That(sp2.X, Is.EqualTo(40 + 5 * inc).Within(0.001));
            // "ef": cumulative = 5*inc + inc boundary = 6*inc.
            Assert.That(ef.X, Is.EqualTo(48 + 6 * inc).Within(0.001));
        }

        // -------------------------------------------------------------------
        // Inheritance: text-justify is an inherited property.
        // -------------------------------------------------------------------

        [Test]
        public void Text_justify_none_is_inherited_by_span_children() {
            // A child <span> should inherit text-justify:none from the <p>.
            // Result: no spreading even though text-align:justify is active.
            var (root, styles, _) = Build(
                "<p style=\"text-align:justify;text-align-last:justify;" +
                "text-justify:none;width:200px\">ab cd ef</p>", null, 800);
            var p = FindFirstBlock(root, "p");
            var line = FirstLine(p);
            // The cascade's resolved text-justify for the p element must be "none".
            Assert.That(p.Style?.Get("text-justify"), Is.EqualTo("none"),
                "p element must carry text-justify:none in its cascaded style");
            // Suppressed spreading: line stays at natural width.
            Assert.That(line.Width, Is.EqualTo(64).Within(0.001),
                "inherited text-justify:none must suppress spreading");
        }

        [Test]
        public void Text_justify_inter_character_inheritance_from_parent() {
            // Parent <div> with text-justify:inter-character; child <p> inherits it.
            // Line width must reach container width via inter-character spreading.
            var (root, _, _) = Build(
                "<div style=\"text-justify:inter-character\">" +
                "<p style=\"text-align:justify;text-align-last:justify;" +
                "width:200px\">ab cd ef</p></div>", null, 800);
            var p = FindFirstBlock(root, "p");
            var line = FirstLine(p);
            Assert.That(line, Is.Not.Null);
            Assert.That(line.Width, Is.EqualTo(200).Within(0.001),
                "p must use inherited inter-character justify to fill 200 px");
        }

        // -------------------------------------------------------------------
        // Edge case: single-word line (no gaps) must not crash under any mode.
        // -------------------------------------------------------------------

        [Test]
        public void None_justify_single_word_is_safe() {
            var (root, _, _) = Build(
                "<p style=\"text-align:justify;text-align-last:justify;" +
                "text-justify:none;width:200px\">hello</p>", null, 800);
            var p = FindFirstBlock(root, "p");
            var line = FirstLine(p);
            Assert.That(line, Is.Not.Null);
            // Single word = 5 chars × 8 px = 40 px; none-justify must not spread.
            Assert.That(line.Width, Is.EqualTo(40).Within(0.001));
        }

        [Test]
        public void Inter_character_single_word_line_width_equals_content_width() {
            // "ab" has 1 gap (1 intra-run), total = 1. inc = (200-16)/1 = 184.
            var (root, _, _) = Build(
                "<p style=\"text-align:justify;text-align-last:justify;" +
                "text-justify:inter-character;width:200px\">ab</p>", null, 800);
            var p = FindFirstBlock(root, "p");
            var line = FirstLine(p);
            Assert.That(line, Is.Not.Null);
            Assert.That(line.Width, Is.EqualTo(200).Within(0.001));
        }

        [Test]
        public void Inter_character_single_char_no_gap_no_crash() {
            // A single-character run has 0 intra gaps and 0 boundary gaps.
            // JustifyLineInterCharacter must return early without crashing.
            // The line stays at its natural width (8 px).
            var (root, _, _) = Build(
                "<p style=\"text-align:justify;text-align-last:justify;" +
                "text-justify:inter-character;width:200px\">a</p>", null, 800);
            var p = FindFirstBlock(root, "p");
            var line = FirstLine(p);
            Assert.That(line, Is.Not.Null);
            Assert.That(line.Width, Is.EqualTo(8).Within(0.001),
                "single-char line: inter-character must not spread (0 gaps)");
        }
    }
}
