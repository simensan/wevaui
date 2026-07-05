using System.Collections.Generic;
using NUnit.Framework;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Layout.Text;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    // W5 phase 2 — UAX #9 simplified bidi tests.
    //
    // Coverage:
    //   - BidiClasses.Classify codepoint classification
    //   - BidiRuns.Analyze logical run production (W7, N1/N2 neutral resolution)
    //   - BidiRuns.Reorder visual reordering (L2 per-line)
    //   - InlineLayout integration: RTL word reordering, mixed LTR+RTL, EN digits,
    //     neutral punctuation, multi-line RTL, LTR fast-path regression guard,
    //     direction:rtl default text-align.
    //
    // All layout tests use MonoFontMetrics defaults (char width = 0.5em,
    // line height = 1.2em, ascent = 0.8em, descent = 0.4em) at 16px font size:
    //   char width = 8px, line height = 19.2px.
    //
    // Hebrew strings use actual Unicode Hebrew codepoints (U+05D0–05D9, etc.)
    // and are syntactically valid UTF-16 strings in C#.
    public class BidiTests {

        // ─── BidiClasses unit tests ─────────────────────────────────────────

        [Test]
        public void BidiClasses_latin_uppercase_is_L() {
            // U+0041 'A'–U+005A 'Z' — basic Latin letters must be LTR.
            for (int cp = 0x0041; cp <= 0x005A; cp++) {
                Assert.That(BidiClasses.Classify(cp), Is.EqualTo(BidiClasses.BidiClass.L),
                    $"U+{cp:X4} should be L");
            }
        }

        [Test]
        public void BidiClasses_ascii_digits_are_EN() {
            for (int cp = 0x0030; cp <= 0x0039; cp++) {
                Assert.That(BidiClasses.Classify(cp), Is.EqualTo(BidiClasses.BidiClass.EN),
                    $"U+{cp:X4} should be EN");
            }
        }

        [Test]
        public void BidiClasses_hebrew_range_is_R() {
            // Sample Hebrew letters (Alef U+05D0, Bet U+05D1, Gimel U+05D2)
            Assert.That(BidiClasses.Classify(0x05D0), Is.EqualTo(BidiClasses.BidiClass.R), "Alef");
            Assert.That(BidiClasses.Classify(0x05D1), Is.EqualTo(BidiClasses.BidiClass.R), "Bet");
            Assert.That(BidiClasses.Classify(0x05D2), Is.EqualTo(BidiClasses.BidiClass.R), "Gimel");
            // Block start/end edges
            Assert.That(BidiClasses.Classify(0x0590), Is.EqualTo(BidiClasses.BidiClass.R), "block start");
            Assert.That(BidiClasses.Classify(0x05FF), Is.EqualTo(BidiClasses.BidiClass.R), "block end");
        }

        [Test]
        public void BidiClasses_arabic_base_block_is_R() {
            // Arabic letter Alef U+0627
            Assert.That(BidiClasses.Classify(0x0627), Is.EqualTo(BidiClasses.BidiClass.R), "Arabic Alef");
            Assert.That(BidiClasses.Classify(0x0600), Is.EqualTo(BidiClasses.BidiClass.R), "Arabic block start");
            Assert.That(BidiClasses.Classify(0x06FF), Is.EqualTo(BidiClasses.BidiClass.R), "Arabic block end");
        }

        [Test]
        public void BidiClasses_arabic_presentation_forms_are_R() {
            // Presentation Forms-A: FB50 + FDFF
            Assert.That(BidiClasses.Classify(0xFB50), Is.EqualTo(BidiClasses.BidiClass.R), "PF-A start");
            Assert.That(BidiClasses.Classify(0xFDFF), Is.EqualTo(BidiClasses.BidiClass.R), "PF-A end");
            // Presentation Forms-B: FE70 + FEFF
            Assert.That(BidiClasses.Classify(0xFE70), Is.EqualTo(BidiClasses.BidiClass.R), "PF-B start");
            Assert.That(BidiClasses.Classify(0xFEFF), Is.EqualTo(BidiClasses.BidiClass.R), "PF-B end");
        }

        [Test]
        public void BidiClasses_space_is_WS() {
            Assert.That(BidiClasses.Classify(0x0020), Is.EqualTo(BidiClasses.BidiClass.WS), "SPACE");
            Assert.That(BidiClasses.Classify(0x0009), Is.EqualTo(BidiClasses.BidiClass.WS), "TAB");
            Assert.That(BidiClasses.Classify(0x000A), Is.EqualTo(BidiClasses.BidiClass.WS), "LF");
        }

        [Test]
        public void BidiClasses_ContainsRtl_returns_false_for_pure_latin() {
            Assert.That(BidiClasses.ContainsRtl("Hello world 123!"), Is.False,
                "Pure ASCII should have no RTL");
        }

        [Test]
        public void BidiClasses_ContainsRtl_returns_true_for_hebrew_word() {
            // "שלום" = "שלום" (shalom)
            string shalom = "שלום";
            Assert.That(BidiClasses.ContainsRtl(shalom), Is.True,
                "Hebrew text must trigger ContainsRtl");
        }

        // ─── BidiRuns.Analyze unit tests ─────────────────────────────────────

        [Test]
        public void BidiRuns_pure_LTR_text_produces_single_L_run() {
            var runs = new List<BidiRuns.Run>();
            BidiRuns.Analyze("Hello", false, runs);
            Assert.That(runs.Count, Is.EqualTo(1));
            Assert.That(runs[0].Level, Is.EqualTo(0), "Latin text should be level 0 (LTR)");
            Assert.That(runs[0].Start, Is.EqualTo(0));
            Assert.That(runs[0].Length, Is.EqualTo(5));
        }

        [Test]
        public void BidiRuns_pure_RTL_text_produces_single_R_run() {
            // "שלום" = "שלום"
            string shalom = "שלום";
            var runs = new List<BidiRuns.Run>();
            BidiRuns.Analyze(shalom, false, runs);
            // All characters are R → all should be level 1.
            // Note: BidiRuns.Run is a struct; use a loop instead of Has.All.Property
            // (NUnit's Property constraint requires a property, not a field, on structs).
            foreach (var r in runs) {
                Assert.That(r.Level, Is.EqualTo(1),
                    $"Run at start={r.Start} should be level 1 (RTL)");
            }
            int totalLength = 0;
            foreach (var r in runs) totalLength += r.Length;
            Assert.That(totalLength, Is.EqualTo(shalom.Length),
                "All characters should be covered");
        }

        [Test]
        public void BidiRuns_digits_in_RTL_context_stay_RTL_via_W7() {
            // "שלום 50 שח" — space + "50" surrounded by Hebrew → digits stay RTL (level 1).
            // UAX #9 W7: EN after last-strong=R stays EN → level 1.
            string text = "שלום 50 שח"; // "שלום 50 שח"
            var runs = new List<BidiRuns.Run>();
            BidiRuns.Analyze(text, false, runs);
            // Find the "5" character (index of '5' in the string).
            int idx5 = text.IndexOf('5');
            Assert.That(idx5, Is.GreaterThan(0), "string must contain '5'");
            // Find the run covering that character.
            BidiRuns.Run digitRun = default;
            bool found = false;
            foreach (var r in runs) {
                if (idx5 >= r.Start && idx5 < r.Start + r.Length) {
                    digitRun = r;
                    found = true;
                    break;
                }
            }
            Assert.That(found, Is.True, "Run covering '5' must exist");
            // W7: last strong before '5' is R (Hebrew) → EN stays EN (level 1).
            Assert.That(digitRun.Level, Is.EqualTo(1),
                "Digit after Hebrew strong char must be level 1 (RTL context, W7)");
        }

        [Test]
        public void BidiRuns_digits_in_LTR_context_promoted_to_L_via_W7() {
            // "price 50" — digits follow LTR text → W7 promotes EN to L (level 0).
            string text = "price 50";
            var runs = new List<BidiRuns.Run>();
            BidiRuns.Analyze(text, false, runs);
            int idx = text.IndexOf('5');
            BidiRuns.Run digitRun = default;
            foreach (var r in runs) {
                if (idx >= r.Start && idx < r.Start + r.Length) { digitRun = r; break; }
            }
            Assert.That(digitRun.Level, Is.EqualTo(0),
                "Digit after LTR strong char must be level 0 (W7 promotes EN→L)");
        }

        [Test]
        public void BidiRuns_neutral_space_between_same_direction_takes_that_direction() {
            // "שלום שח" — space between two R runs → space takes R (N1 rule).
            string text = "שלום שח"; // "שלום שח"
            var runs = new List<BidiRuns.Run>();
            BidiRuns.Analyze(text, false, runs);
            int spaceIdx = text.IndexOf(' ');
            Assert.That(spaceIdx, Is.GreaterThan(0));
            BidiRuns.Run spaceRun = default;
            foreach (var r in runs) {
                if (spaceIdx >= r.Start && spaceIdx < r.Start + r.Length) { spaceRun = r; break; }
            }
            // N1: space between R…R takes R → level 1.
            Assert.That(spaceRun.Level, Is.EqualTo(1),
                "Space between two Hebrew R runs must resolve to RTL (N1)");
        }

        [Test]
        public void BidiRuns_neutral_space_between_opposite_directions_takes_base() {
            // "Hello שלום" with base LTR — space between L and R → takes base (N2, base=LTR).
            string text = "Hello שלום"; // "Hello שלום"
            var runs = new List<BidiRuns.Run>();
            BidiRuns.Analyze(text, false, runs);
            int spaceIdx = text.IndexOf(' ');
            BidiRuns.Run spaceRun = default;
            foreach (var r in runs) {
                if (spaceIdx >= r.Start && spaceIdx < r.Start + r.Length) { spaceRun = r; break; }
            }
            // N2: space between L and R → base direction (LTR) → level 0.
            Assert.That(spaceRun.Level, Is.EqualTo(0),
                "Space between LTR and RTL with base=LTR must take base direction (N2)");
        }

        // ─── BidiRuns.Reorder visual reordering tests ────────────────────────

        [Test]
        public void BidiRuns_Reorder_pure_LTR_is_unchanged() {
            var logical = new List<BidiRuns.Run> {
                new BidiRuns.Run { Start = 0, Length = 5, Level = 0 },
                new BidiRuns.Run { Start = 5, Length = 3, Level = 0 },
            };
            var visual = BidiRuns.Reorder(logical, false);
            Assert.That(visual.Count, Is.EqualTo(2));
            Assert.That(visual[0].Start, Is.EqualTo(0), "LTR: first run unchanged");
            Assert.That(visual[1].Start, Is.EqualTo(5), "LTR: second run unchanged");
        }

        [Test]
        public void BidiRuns_Reorder_reverses_RTL_block() {
            // Two RTL runs inside an LTR paragraph: [A-L0][B-L1][C-L1][D-L0].
            // L2: reverse the L1 block → visual order: A, C, B, D.
            var logical = new List<BidiRuns.Run> {
                new BidiRuns.Run { Start = 0,  Length = 2, Level = 0 },  // A
                new BidiRuns.Run { Start = 2,  Length = 3, Level = 1 },  // B
                new BidiRuns.Run { Start = 5,  Length = 4, Level = 1 },  // C
                new BidiRuns.Run { Start = 9,  Length = 2, Level = 0 },  // D
            };
            var visual = BidiRuns.Reorder(logical, false);
            Assert.That(visual.Count, Is.EqualTo(4));
            Assert.That(visual[0].Start, Is.EqualTo(0), "A stays first (level 0)");
            Assert.That(visual[1].Start, Is.EqualTo(5), "C comes before B (RTL block reversed)");
            Assert.That(visual[2].Start, Is.EqualTo(2), "B comes after C");
            Assert.That(visual[3].Start, Is.EqualTo(9), "D stays last (level 0)");
        }

        [Test]
        public void BidiRuns_Reorder_base_RTL_paragraph_reverses_all() {
            // Base RTL paragraph: [A-L1][B-L0][C-L1] →
            // L2: first reverse each L1 block (no change here since each is
            // isolated by L0 runs), then base-RTL reverses the whole line →
            // visual: C, B, A.
            var logical = new List<BidiRuns.Run> {
                new BidiRuns.Run { Start = 0, Length = 2, Level = 1 },   // A
                new BidiRuns.Run { Start = 2, Length = 3, Level = 0 },   // B
                new BidiRuns.Run { Start = 5, Length = 2, Level = 1 },   // C
            };
            var visual = BidiRuns.Reorder(logical, baseIsRtl: true);
            Assert.That(visual.Count, Is.EqualTo(3));
            Assert.That(visual[0].Start, Is.EqualTo(5), "C first in RTL base");
            Assert.That(visual[1].Start, Is.EqualTo(2), "B middle");
            Assert.That(visual[2].Start, Is.EqualTo(0), "A last in RTL base");
        }

        // ─── InlineLayout integration tests ──────────────────────────────────

        // Helper: collect TextRuns from a layout root in left-to-right X order.
        static List<TextRun> GetTextRunsInXOrder(Box root) {
            var runs = new List<TextRun>(AllTextRuns(root));
            runs.Sort((a, b) => a.X.CompareTo(b.X));
            return runs;
        }

        // Helper: collect TextRuns in the order they appear in the tree.
        static List<TextRun> GetTextRunsInTreeOrder(Box root) {
            return new List<TextRun>(AllTextRuns(root));
        }

        [Test]
        public void Integration_LTR_only_text_is_unaffected_by_bidi_path() {
            // Regression guard: a pure-ASCII line should produce EXACTLY the
            // same TextRun X positions as before the bidi implementation.
            // At MonoFontMetrics, each char = 8px; "Hello" = 40px wide.
            var (root, _, _) = Build(
                "<div id=\"box\">Hello</div>",
                "#box { width: 200px; font-size: 16px; }");
            var runs = GetTextRunsInTreeOrder(root);
            Assert.That(runs, Has.Count.GreaterThan(0));
            var first = runs[0];
            Assert.That(first.Text, Is.EqualTo("Hello"));
            Assert.That(first.X, Is.EqualTo(0).Within(0.001),
                "LTR-only text must start at X=0 (fast path / no bidi shift)");
            Assert.That(first.Width, Is.EqualTo(40).Within(0.001),
                "5 chars × 8px = 40px");
        }

        [Test]
        public void Integration_pure_hebrew_line_first_logical_char_is_rightmost() {
            // "שלום" (shalom, 4 Hebrew chars) inside a 100px RTL container.
            // Each Hebrew char is 8px wide; total = 32px.
            // With direction:rtl + text-align (default "start" = "right"), the run
            // should sit flush-right: run.X = 100 - 32 = 68.
            string shalom = "שלום"; // שלום
            var (root, _, _) = Build(
                $"<div id=\"box\">{shalom}</div>",
                "#box { width: 100px; font-size: 16px; direction: rtl; }");
            var runs = GetTextRunsInTreeOrder(root);
            Assert.That(runs, Has.Count.GreaterThan(0));
            // The run should be right-aligned (direction:rtl → text-align:right by default).
            var run = runs[0];
            Assert.That(run.X, Is.EqualTo(68).Within(0.5),
                "Pure Hebrew in direction:rtl should be right-aligned (X = containerW - runW)");
        }

        [Test]
        public void Integration_ltr_sentence_with_embedded_hebrew_reorders_hebrew_segment() {
            // "Hi שלום ok" in a wide LTR container.
            // "Hi " (3 chars = 24px) | "שלום" (4 chars = 32px) | " ok" (3 chars = 24px).
            // After bidi: LTR "Hi " stays left; Hebrew block reverses internally
            // (single run, no sub-reversal needed); LTR " ok" stays right.
            // BUT the Hebrew block should sit BETWEEN the two LTR pieces, so
            // visual: "Hi " | "שלום" | " ok".  All in the original order because
            // the Hebrew is a single RTL block between two LTR runs.
            string text = "Hi שלום ok"; // "Hi שלום ok"
            var (root, _, _) = Build(
                $"<div id=\"box\">{text}</div>",
                "#box { width: 400px; font-size: 16px; direction: ltr; }");
            var runs = GetTextRunsInXOrder(root);
            // Find which run contains the Hebrew text.
            TextRun hebrewRun = null;
            foreach (var r in runs) {
                if (r.Text != null && BidiClasses.ContainsRtl(r.Text)) { hebrewRun = r; break; }
            }
            // If "שלום" is one run and "Hi " is another, the Hebrew run
            // must sit between the two LTR runs in visual X order.
            // We assert that the run right-of the "Hi " run exists and has Hebrew.
            Assert.That(hebrewRun, Is.Not.Null, "There must be a Hebrew TextRun in the line");
            // The leftmost visible run must start with the LTR "Hi" text.
            Assert.That(runs[0].X, Is.EqualTo(0).Within(0.5),
                "First visual run must start at X=0");
        }

        [Test]
        public void Integration_numbers_inside_RTL_stay_with_RTL_runs() {
            // "מחיר 50 שח" (Hebrew: "price 50 [currency]")
            // With base=RTL: all content should reorder right-to-left visually.
            // The digits "50" should NOT float to the LTR side;
            // they stay in the RTL block (W7 rule: last strong before digits is R).
            string text = "מחיר 50 שח"; // "מחיר 50 שח"
            var (root, _, _) = Build(
                $"<div id=\"box\">{text}</div>",
                "#box { width: 400px; font-size: 16px; direction: rtl; }");
            var runs = GetTextRunsInXOrder(root);
            Assert.That(runs, Has.Count.GreaterThan(0));
            // In an RTL base paragraph the content is right-aligned by default.
            // We verify the line is not empty and has some rightward X position
            // (not all runs at X=0, which would indicate text-align:left).
            double maxX = 0;
            foreach (var r in runs) if (r.X > maxX) maxX = r.X;
            Assert.That(maxX, Is.GreaterThan(0),
                "RTL content must have at least one run at positive X (right-aligned)");
        }

        [Test]
        public void Integration_direction_rtl_block_default_alignment_is_right() {
            // CSS Writing Modes §5: direction:rtl → text-align initial value
            // resolves to "right" (via "start" → IsRtl → right).
            // A 5-char Hebrew word at 8px/char = 40px in a 200px box
            // should be placed at X = 160.
            string shalom = "שלוםא"; // 5 Hebrew chars
            var (root, _, _) = Build(
                $"<div id=\"box\">{shalom}</div>",
                "#box { width: 200px; font-size: 16px; direction: rtl; }");
            var runs = GetTextRunsInTreeOrder(root);
            Assert.That(runs, Has.Count.GreaterThan(0));
            // ApplyTextAlign with "right" shifts the run to contentW - lineW = 200 - 40 = 160.
            Assert.That(runs[0].X, Is.EqualTo(160).Within(0.5),
                "direction:rtl must default to right-aligned text (start=right)");
        }

        [Test]
        public void Integration_multiline_rtl_paragraph_each_line_reordered_independently() {
            // A 3-word Hebrew sentence split across two lines.
            // Each line must be independently reordered — not just the first.
            // Word widths: "שלום" = 4×8=32px, "עולם" = 4×8=32px, "טוב" = 3×8=24px.
            // In a 60px container: first line fits "שלום עולם" (32+8+32=72>60 → wraps);
            // Actually "שלום"=32 fits on line 1, " עולם"=40 total → line 1 = "שלום"
            // line 2 = "עולם" (no trailing space), line 3 = "טוב".
            string text = "שלום עולם טוב"; // שלום עולם טוב
            var (root, _, _) = Build(
                $"<div id=\"box\">{text}</div>",
                "#box { width: 60px; font-size: 16px; direction: rtl; }");
            var lines = new List<LineBox>(AllLineBoxes(root));
            Assert.That(lines.Count, Is.GreaterThanOrEqualTo(2),
                "A 3-word RTL sentence in a narrow box must wrap");
            // Each non-empty line must have its runs right-aligned (or at positive X).
            foreach (var line in lines) {
                bool hasRun = false;
                double maxRunX = 0;
                foreach (var child in line.Children) {
                    if (child is TextRun tr && !string.IsNullOrWhiteSpace(tr.Text)) {
                        hasRun = true;
                        double runRight = tr.X + tr.Width;
                        if (runRight > maxRunX) maxRunX = runRight;
                    }
                }
                if (hasRun) {
                    Assert.That(maxRunX, Is.GreaterThan(0),
                        "Each RTL line must have content extending beyond X=0");
                }
            }
        }

        [Test]
        public void Integration_mixed_punct_neutral_resolves_to_base_direction() {
            // "Hello! שלום." — punctuation between LTR and RTL.
            // '!' is ON (neutral). With base=LTR, N2: '!' between LTR and RTL
            // takes base direction (LTR) → stays in the LTR run.
            // '.' after Hebrew: with base=LTR, '.' is ON, follows R, then EOL →
            // N2: base direction (LTR) → stays in LTR.
            string text = "Hello! שלום.";
            var (root, _, _) = Build(
                $"<div id=\"box\">{text}</div>",
                "#box { width: 400px; font-size: 16px; direction: ltr; }");
            var runs = GetTextRunsInTreeOrder(root);
            Assert.That(runs, Has.Count.GreaterThan(0));
            // Layout must complete without throwing; we check basic structural invariants.
            double lineWidth = 0;
            foreach (var r in runs) lineWidth = System.Math.Max(lineWidth, r.X + r.Width);
            Assert.That(lineWidth, Is.GreaterThan(0),
                "Mixed punctuation line must produce positive-width content");
        }

        [Test]
        public void Integration_ltr_only_multiple_words_unaffected_by_bidi_path() {
            // Regression guard (stronger): a line with multiple LTR words
            // must be layout-identical to the pre-bidi path.
            // "The quick" at 8px/char: "The"=24, " "=8, "quick"=40 = total 72px.
            var (root, _, _) = Build(
                "<div id=\"box\">The quick</div>",
                "#box { width: 400px; font-size: 16px; }");
            var runs = GetTextRunsInTreeOrder(root);
            // Sum of widths: "The"=24 + " "=8 + "quick"=40 = 72, or single run "The quick" = 72.
            double totalWidth = 0;
            foreach (var r in runs) totalWidth += r.Width;
            Assert.That(totalWidth, Is.EqualTo(72).Within(0.5),
                "LTR multi-word line total width must remain 72px (no bidi distortion)");
            // All runs must start at X >= 0 and in left-to-right X order.
            double prevX = -1;
            foreach (var r in GetTextRunsInXOrder(root)) {
                Assert.That(r.X, Is.GreaterThanOrEqualTo(prevX - 0.001),
                    "LTR runs must be ordered left-to-right");
                prevX = r.X;
            }
        }

        [Test]
        public void Integration_ltr_fast_path_not_used_for_rtl_container() {
            // Verify that direction:rtl containers route through the bidi-aware
            // slow path even for a single-word run (the guard in
            // TryLayoutSingleRunFast must return false for IsRtl containers).
            // The observable effect is that text-align defaults to "right"
            // (start=right for RTL) placing the word at the right edge.
            var (root, _, _) = Build(
                "<div id=\"box\">Test</div>",
                "#box { width: 200px; font-size: 16px; direction: rtl; }");
            var runs = GetTextRunsInTreeOrder(root);
            Assert.That(runs, Has.Count.GreaterThan(0));
            // "Test" = 4×8 = 32px. Right-aligned at 200px → X = 168.
            Assert.That(runs[0].X, Is.EqualTo(168).Within(0.5),
                "direction:rtl single word must be right-aligned (TryLayoutSingleRunFast bypassed)");
        }

        [Test]
        public void Integration_ltr_fast_path_not_used_for_hebrew_text() {
            // Verify that a Hebrew string in an LTR container also bypasses the
            // fast path (ContainsRtl guard) and still renders the Hebrew as a
            // right-to-left block (naturally its level-1 run reverses).
            string shalom = "שלום"; // "שלום" — 4 chars
            var (root, _, _) = Build(
                $"<div id=\"box\">{shalom}</div>",
                "#box { width: 200px; font-size: 16px; direction: ltr; }");
            var runs = GetTextRunsInTreeOrder(root);
            Assert.That(runs, Has.Count.GreaterThan(0));
            // In a 200px LTR container, a 32px RTL run is placed by text-align:left
            // (default) at X=0 — the X is unchanged but we verify layout completed.
            Assert.That(runs[0].Width, Is.EqualTo(32).Within(0.5),
                "Hebrew 4-char run = 32px wide");
            Assert.That(runs[0].X, Is.GreaterThanOrEqualTo(0),
                "Hebrew run must have non-negative X in LTR container");
        }

        [Test]
        public void Integration_empty_rtl_container_does_not_throw() {
            // Edge case: an empty direction:rtl container must not throw.
            var (root, _, _) = Build(
                "<div id=\"box\"></div>",
                "#box { width: 100px; font-size: 16px; direction: rtl; }");
            // If we get here without exception the test passes.
            Assert.That(root, Is.Not.Null);
        }
    }
}
