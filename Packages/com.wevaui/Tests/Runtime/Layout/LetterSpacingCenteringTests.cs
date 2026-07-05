using System.Collections.Generic;
using NUnit.Framework;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    // Centering regression tests for letter-spacing + text-indent interaction.
    //
    // Background (CSS Text Module Level 3 §10.1 / §7.1):
    //   Chrome centers text using the run's measured glyph width (which uses the
    //   (n-1) letter-spacing formula per §10.1 — no trailing spacing on the last
    //   character). text-indent then shifts the content START within the centered
    //   region without affecting the centering calculation itself. The standard
    //   author workaround for the visual asymmetry of trailing letter-spacing is:
    //       text-indent: <letter-spacing>
    //   which adds one letter-spacing of lead space to balance the trailing space.
    //
    // Hypotheses under test:
    //   H1. LineBreaker.BreakInto starts state.X at `firstLineIndent`, so
    //       FinishLine emits line.Width = textWidth + firstLineIndent.
    //       InlineLayout.ApplyTextAlign then computes:
    //           extra = contentW - line.Width = contentW - textWidth - indent
    //           centerShift = extra / 2
    //       and calls OffsetLine(line, centerShift), yielding:
    //           first run X = indent + (contentW - textWidth - indent) / 2
    //       instead of the correct:
    //           first run X = indent + (contentW - textWidth) / 2
    //       The first run is indent/2 px to the LEFT of its spec position.
    //   H2. TryLayoutSingleRunFast (fast path for single-token paragraphs) does
    //       NOT call TextIndentPx, so text-indent is silently ignored on that
    //       path. The fast path runs whenever the container has exactly one child
    //       TextRun AND the content fits in the available width.
    //
    // MonoFontMetrics defaults: charWidthEm = 0.5 → 8 px/char @ 16 px.
    // "PAUSED" = 6 chars × 8 = 48 px natural; + 5 gaps × 4 px LS = 68 px total.
    // "AB" = 2 chars × 8 = 16 px natural; + 1 gap × 4 px LS = 20 px total.
    public class LetterSpacingCenteringTests {
        const double ViewW = 200;

        static BlockBox FindFirstBlock(Box root, string tag) {
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && !(b is AnonymousBlockBox) && bb.Element?.TagName == tag) return bb;
            }
            return null;
        }

        static List<LineBox> LinesOf(BlockBox p) {
            var list = new List<LineBox>();
            foreach (var c in p.Children) if (c is LineBox lb) list.Add(lb);
            return list;
        }

        static TextRun FirstRunOf(Box container) {
            foreach (var b in AllBoxes(container)) if (b is TextRun tr) return tr;
            return null;
        }

        // ------------------------------------------------------------------
        // H2: fast path silently ignores text-indent
        // ------------------------------------------------------------------

        // The fast path (TryLayoutSingleRunFast) handles single-TextRun
        // containers where text fits in the available width. It does NOT apply
        // text-indent. The run should sit at the centered position regardless
        // of what text-indent is set to (both values produce identical X).
        //
        // "AB" (2 chars × 8 = 16 px) + letter-spacing:4px (1 gap) = 20 px.
        // Centered in 200 px: shift = (200 - 20) / 2 = 90. Run at X = 90.
        // With text-indent:4px the run should be at 4 + 90 = 94.
        // Bug: fast path ignores text-indent, so run is still at X = 90.
        [Test]
        public void Fast_path_centers_run_without_text_indent() {
            var (root, _, _) = Build(
                "<p style=\"text-align:center;letter-spacing:4px;width:200px;font-size:16px\">AB</p>",
                null, ViewW);
            var p = FindFirstBlock(root, "p");
            var run = FirstRunOf(p);
            Assert.That(run, Is.Not.Null);
            // Natural: 2*8=16; LS: 1*4=4; total=20. Center shift=(200-20)/2=90.
            Assert.That(run.X, Is.EqualTo(90).Within(0.001),
                "fast path: centered run at (contentW - glyphWidth) / 2");
        }

        [Test]
        public void Fast_path_ignores_text_indent_silently() {
            // With text-indent:4px the fast path is still taken (single-run, fits).
            // Expected correct: 4 + (200-20)/2 = 4 + 90 = 94.
            // Actual (bug H2): fast path ignores text-indent → run at 90 (same as above).
            var (root, _, _) = Build(
                "<p style=\"text-align:center;letter-spacing:4px;text-indent:4px;width:200px;font-size:16px\">AB</p>",
                null, ViewW);
            var p = FindFirstBlock(root, "p");
            var run = FirstRunOf(p);
            Assert.That(run, Is.Not.Null);
            // Before fix: run at 90 (text-indent ignored). After fix: run at 94.
            Assert.That(run.X, Is.EqualTo(94).Within(0.001),
                "text-indent:4px must shift centered run from 90 to 94 on first line");
        }

        // ------------------------------------------------------------------
        // H1: slow path includes text-indent in line.Width, undercorrects centering
        // ------------------------------------------------------------------

        // The slow path is triggered when `white-space:pre` prevents the fast
        // path from accepting the run (TryLayoutSingleRunFast bails on ws!=normal
        // and ws!=nowrap). Single word "AB" with white-space:pre; letter-spacing:4px.
        // Glyph width = 20 px. Without indent, line.Width = 20. Center shift = 90.
        [Test]
        public void Slow_path_centers_run_correctly_without_text_indent() {
            var (root, _, _) = Build(
                "<p style=\"white-space:pre;text-align:center;letter-spacing:4px;width:200px;font-size:16px\">AB</p>",
                null, ViewW);
            var p = FindFirstBlock(root, "p");
            var run = FirstRunOf(p);
            Assert.That(run, Is.Not.Null);
            // Glyph width = 20. Center shift = (200 - 20) / 2 = 90.
            Assert.That(run.X, Is.EqualTo(90).Within(0.001),
                "slow path (pre): centered run without indent at (200-20)/2 = 90");
        }

        // Bug H1: slow path includes text-indent in line.Width.
        // line.Width = 4 + 20 = 24. extra = 176. shift = 88.
        // first run at X = 4 + 88 = 92.
        // Correct: line.Width = 20. extra = 180. shift = 90. first run at X = 4 + 90 = 94.
        [Test]
        public void Slow_path_text_indent_does_not_affect_centering_calculation() {
            var (root, _, _) = Build(
                "<p style=\"white-space:pre;text-align:center;letter-spacing:4px;text-indent:4px;width:200px;font-size:16px\">AB</p>",
                null, ViewW);
            var p = FindFirstBlock(root, "p");
            var run = FirstRunOf(p);
            Assert.That(run, Is.Not.Null);
            // Correct: glyph width 20, center shift 90, plus indent 4 → run at 94.
            // Bug (H1): line.Width includes indent → shift is (200-24)/2=88, run at 4+88=92.
            Assert.That(run.X, Is.EqualTo(94).Within(0.001),
                "text-indent:4px on centered slow-path: run must sit at indent + (contentW - glyphWidth) / 2 = 94, not 92");
        }

        // ------------------------------------------------------------------
        // Regression: letter-spacing alone still centers correctly (no text-indent)
        // ------------------------------------------------------------------

        // This verifies the baseline: without text-indent, both fast and slow paths
        // center "AB" (20px) in 200px at X = 90. Guards against an over-eager fix.
        [Test]
        public void Centering_without_indent_is_unaffected_by_the_fix() {
            // Fast path
            var (rootFast, _, _) = Build(
                "<p style=\"text-align:center;letter-spacing:4px;width:200px;font-size:16px\">AB</p>",
                null, ViewW);
            var pFast = FindFirstBlock(rootFast, "p");
            var runFast = FirstRunOf(pFast);
            Assert.That(runFast, Is.Not.Null);
            Assert.That(runFast.X, Is.EqualTo(90).Within(0.001), "fast path: no-indent centering unchanged");

            // Slow path (white-space:pre)
            var (rootSlow, _, _) = Build(
                "<p style=\"white-space:pre;text-align:center;letter-spacing:4px;width:200px;font-size:16px\">AB</p>",
                null, ViewW);
            var pSlow = FindFirstBlock(rootSlow, "p");
            var runSlow = FirstRunOf(pSlow);
            Assert.That(runSlow, Is.Not.Null);
            Assert.That(runSlow.X, Is.EqualTo(90).Within(0.001), "slow path: no-indent centering unchanged");
        }

        // ------------------------------------------------------------------
        // Full user scenario: "PAUSED" (6 chars) centered with letter-spacing + indent
        // ------------------------------------------------------------------

        // "PAUSED" natural: 6 × 8 = 48 px. + 5 gaps × 4 px LS = 68 px.
        // Center in 200 px: shift = (200 - 68) / 2 = 66. Run at X = 66.
        // With text-indent:4px: run should be at 4 + 66 = 70.
        [Test]
        public void Full_scenario_PAUSED_centered_with_letter_spacing_only() {
            var (root, _, _) = Build(
                "<p style=\"white-space:pre;text-align:center;letter-spacing:4px;width:200px;font-size:16px\">PAUSED</p>",
                null, ViewW);
            var p = FindFirstBlock(root, "p");
            var run = FirstRunOf(p);
            Assert.That(run, Is.Not.Null);
            // 6 chars × 8 = 48 natural; + 5×4 = 20 LS = 68 total.
            Assert.That(run.Width, Is.EqualTo(68).Within(0.001), "run width includes (n-1) letter-spacing gaps");
            Assert.That(run.X, Is.EqualTo(66).Within(0.001),
                "center shift = (200 - 68) / 2 = 66");
        }

        [Test]
        public void Full_scenario_PAUSED_centered_with_letter_spacing_and_text_indent() {
            var (root, _, _) = Build(
                "<p style=\"white-space:pre;text-align:center;letter-spacing:4px;text-indent:4px;width:200px;font-size:16px\">PAUSED</p>",
                null, ViewW);
            var p = FindFirstBlock(root, "p");
            var run = FirstRunOf(p);
            Assert.That(run, Is.Not.Null);
            // Glyph width = 68. Center shift = (200 - 68) / 2 = 66. Indent = 4.
            // Correct run X = 4 + 66 = 70.
            // Bug: line.Width = 4 + 68 = 72, shift = (200-72)/2 = 64, run at 4+64=68.
            Assert.That(run.Width, Is.EqualTo(68).Within(0.001), "run width unchanged by text-indent");
            Assert.That(run.X, Is.EqualTo(70).Within(0.001),
                "text-indent:4px must sit at indent + (contentW - glyphWidth)/2 = 4 + 66 = 70");
        }

        // ------------------------------------------------------------------
        // line.Width after centering must not include text-indent
        // ------------------------------------------------------------------

        // OffsetLine shifts run positions but also bumps line.Width. After centering,
        // line.Width should equal contentW (the entire content width, not contentW + indent).
        // Currently (bug) with indent: line.Width = indent + glyph + extra/2 = contentW - extra/2.
        // After fix, same as without indent: line.Width = contentW - extra/2 still (OffsetLine
        // adds extra/2 to line.Width). What matters is that the RUN POSITION is correct.
        // This test specifically guards the invariant that line.Width after centering
        // equals contentW (= glyph width + centering delta that fills the container)
        // when text-indent is not present.
        [Test]
        public void Line_width_after_centering_equals_content_width() {
            var (root, _, _) = Build(
                "<p style=\"white-space:pre;text-align:center;letter-spacing:4px;width:200px;font-size:16px\">AB</p>",
                null, ViewW);
            var p = FindFirstBlock(root, "p");
            var lines = LinesOf(p);
            Assert.That(lines.Count, Is.EqualTo(1));
            // OffsetLine increments line.Width by the centering shift:
            // line.Width starts at 20 (glyph), shift = 90, after = 20 + 90 = 110.
            // Not 200 — this is by design (line.Width reflects the right edge of content).
            // The IMPORTANT invariant: line.Width WITHOUT indent == line.Width WITH indent
            // (since indent should not change the centering calculation or line.Width).
            var (rootIndent, _, _) = Build(
                "<p style=\"white-space:pre;text-align:center;letter-spacing:4px;text-indent:4px;width:200px;font-size:16px\">AB</p>",
                null, ViewW);
            var pIndent = FindFirstBlock(rootIndent, "p");
            var linesIndent = LinesOf(pIndent);
            Assert.That(linesIndent.Count, Is.EqualTo(1));
            // After fix: same glyph width, same centering, so line.Width should be the same value.
            Assert.That(linesIndent[0].Width, Is.EqualTo(lines[0].Width).Within(0.001),
                "line.Width after centering must not differ due to text-indent alone");
        }
    }
}
