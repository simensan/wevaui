using System;
using NUnit.Framework;
using Weva.Forms;
using Weva.Layout.Text;

namespace Weva.Tests.Forms {
    // TG-W4-TEM — TextEditModel word-boundary + metric-aware navigation tests.
    //
    // Verifies the W4 phase-1 wiring described in TextEditModel.cs:
    //
    //   Gap 1 — Word-boundary delegation (WordBoundary.cs):
    //     CJK codepoints are each treated as a single word unit; Ctrl+Arrow
    //     advances one codepoint at a time instead of skipping the whole run.
    //     (UAX #29 §3 word-boundary rules, browser parity: Chrome/Firefox).
    //
    //   Gap 2 — Metric-aware LineUpFrom / LineDownFrom (SetMeasureSubstring):
    //     When a pixel measurer is supplied, up/down navigation preserves
    //     visual X rather than character-column count.  Uses MonoFontMetrics
    //     at 16 px so each char = 8 px (deterministic, no Unity dependency).
    //
    // All tests are headless — no Unity types, no IFontMetrics production path.
    // MonoFontMetrics is the test shim (charWidthEm=0.5 => 8px at 16px font).
    [TestFixture]
    public class TextEditModelWordAndNavTests {

        // ---- helpers ----

        // Returns a measurer backed by MonoFontMetrics at 16 px.
        // Signature: (text, startIndex, charCount) => double pixels.
        static Func<string, int, int, double> MakeMeasurer() {
            var metrics = new MonoFontMetrics(); // 0.5em/char at 16px = 8px/char
            const double fontSize = 16.0;
            return (t, start, len) => metrics.Measure(t, start, len, fontSize);
        }

        static TextEditModel ModelWithMeasurer(string text, bool multiline = true) {
            var m = new TextEditModel(text, multiline: multiline);
            m.SetMeasureSubstring(MakeMeasurer());
            return m;
        }

        // ============================================================
        // Gap 1 — CJK word-boundary delegation
        // ============================================================

        // Ctrl+Right over a 3-char CJK string advances one codepoint per call.
        // Old IsWordSeparator treated CJK as word-chars and jumped the whole run.
        [Test]
        public void MoveWordRight_over_CJK_advances_one_codepoint_at_a_time() {
            // "日本語" — 3 Hiragana/Kanji chars, each one BMP codepoint (1 code unit).
            var m = new TextEditModel("日本語");
            m.SetCaret(0);

            m.MoveWordRight();
            Assert.That(m.Selection.Start, Is.EqualTo(1),
                "First CJK char should be one word unit (index 0→1)");

            m.MoveWordRight();
            Assert.That(m.Selection.Start, Is.EqualTo(2),
                "Second CJK char should be one word unit (index 1→2)");

            m.MoveWordRight();
            Assert.That(m.Selection.Start, Is.EqualTo(3),
                "Third CJK char should be one word unit (index 2→3)");
        }

        // Ctrl+Left over a CJK string retreats one codepoint per call.
        [Test]
        public void MoveWordLeft_over_CJK_retreats_one_codepoint_at_a_time() {
            const string text = "日本語";
            var m = new TextEditModel(text);
            m.SetCaret(3); // after "語"

            m.MoveWordLeft();
            Assert.That(m.Selection.Start, Is.EqualTo(2),
                "Prev from index 3 should land at 2 (before '語')");

            m.MoveWordLeft();
            Assert.That(m.Selection.Start, Is.EqualTo(1),
                "Prev from index 2 should land at 1 (before '本')");

            m.MoveWordLeft();
            Assert.That(m.Selection.Start, Is.EqualTo(0),
                "Prev from index 1 should land at 0 (before '日')");
        }

        // Ctrl+Right at Latin-CJK boundary stops at CJK boundary then
        // steps each CJK char individually.
        [Test]
        public void MoveWordRight_at_Latin_CJK_boundary() {
            // "test日本" — "test" is one Latin word, then two CJK one-word units.
            const string text = "test日本";
            var m = new TextEditModel(text);
            m.SetCaret(0);

            m.MoveWordRight();
            Assert.That(m.Selection.Start, Is.EqualTo(4),
                "Latin 'test' is one word unit → index 4");

            m.MoveWordRight();
            Assert.That(m.Selection.Start, Is.EqualTo(5),
                "First CJK '日' is one word unit → index 5");

            m.MoveWordRight();
            Assert.That(m.Selection.Start, Is.EqualTo(6),
                "Second CJK '本' is one word unit → index 6");
        }

        // Ctrl+Left at CJK-Latin boundary steps CJK chars individually then
        // jumps the whole Latin word.
        [Test]
        public void MoveWordLeft_at_CJK_Latin_boundary() {
            const string text = "test日本";
            var m = new TextEditModel(text);
            m.SetCaret(6); // after "本"

            m.MoveWordLeft();
            Assert.That(m.Selection.Start, Is.EqualTo(5), "Prev '本' → 5");

            m.MoveWordLeft();
            Assert.That(m.Selection.Start, Is.EqualTo(4), "Prev '日' → 4");

            m.MoveWordLeft();
            Assert.That(m.Selection.Start, Is.EqualTo(0), "Prev 'test' → 0");
        }

        // Shift+Ctrl+Right over CJK extends selection one codepoint at a time.
        [Test]
        public void MoveWordRight_extend_selection_over_CJK() {
            var m = new TextEditModel("日本語");
            m.SetCaret(0);

            m.MoveWordRight(extendSelection: true);
            Assert.That(m.Selection.Start, Is.EqualTo(0));
            Assert.That(m.Selection.End, Is.EqualTo(1));
            Assert.That(m.Selection.IsCollapsed, Is.False);

            m.MoveWordRight(extendSelection: true);
            Assert.That(m.Selection.End, Is.EqualTo(2));
        }

        // Surrogate-pair safety: MoveWordRight over a supplementary-plane CJK char
        // (encoded as 2 UTF-16 code units) advances by 2, not 1.
        [Test]
        public void MoveWordRight_over_supplementary_CJK_surrogate_pair_is_safe() {
            // U+20000 (𠀀) is CJK Extension B — encoded as surrogate pair (2 code units).
            // WordBoundary handles this via PeekCodepointRight which checks for surrogates.
            string supp = "\U00020000"; // length=2 in UTF-16
            Assert.That(supp.Length, Is.EqualTo(2), "Pre-condition: surrogate pair");

            var m = new TextEditModel(supp);
            m.SetCaret(0);
            m.MoveWordRight();
            Assert.That(m.Selection.Start, Is.EqualTo(2),
                "Supplementary CJK char (2 code units) must advance past both surrogates");
        }

        // Latin underscore joins word (existing contract, regression guard).
        [Test]
        public void MoveWordRight_underscore_joins_word_chars() {
            var m = new TextEditModel("foo_bar baz");
            m.SetCaret(0);
            m.MoveWordRight();
            Assert.That(m.Selection.Start, Is.EqualTo(7),
                "'foo_bar' is one word token (underscore is a word-char)");
        }

        // Existing Latin word-step behavior stays green (regression guard for
        // the delegation change).
        [Test]
        public void MoveWordRight_Latin_word_step_regression() {
            var m = new TextEditModel("hello world foo");
            m.SetCaret(0);
            m.MoveWordRight();
            Assert.That(m.Selection.Start, Is.EqualTo(5));
            m.MoveWordRight();
            Assert.That(m.Selection.Start, Is.EqualTo(11));
            m.MoveWordLeft();
            Assert.That(m.Selection.Start, Is.EqualTo(6));
        }

        // ============================================================
        // Gap 2 — metric-aware LineUpFrom / LineDownFrom
        // ============================================================

        // At 8px/char, "WWW" (24px) is wider than "iii" (24px at 8px/char).
        // With a proportional fake measurer (W=3x, i=1x) the goal X should
        // resolve to the right column.  MonoFontMetrics is 8px/char uniformly,
        // so we test the key invariant: caret at col 3 on "hello" (X=24px)
        // moves down to col 3 on "world" (X=24px).
        [Test]
        public void MoveLineDown_metric_aware_preserves_pixel_x() {
            // "hello\nworld" — each char 8px at default MonoFontMetrics.
            // Caret at index 3 of "hello" → col 3 → X=24px.
            // After MoveDown → col 3 of "world" → global index 6+3=9.
            var m = ModelWithMeasurer("hello\nworld");
            m.SetCaret(3); // "hel|lo"

            m.MoveLineDown();
            Assert.That(m.Selection.Start, Is.EqualTo(9),
                "col 3 on 'world' = offset(6)+col(3)=9");
        }

        // MoveLineUp preserves pixel X.
        [Test]
        public void MoveLineUp_metric_aware_preserves_pixel_x() {
            var m = ModelWithMeasurer("hello\nworld");
            m.SetCaret(9); // "wor|ld" col 3

            m.MoveLineUp();
            Assert.That(m.Selection.Start, Is.EqualTo(3),
                "col 3 on 'hello' = offset(0)+col(3)=3");
        }

        // When the target line is shorter than the goal X, clamp to end.
        [Test]
        public void MoveLineDown_metric_clamps_when_target_shorter() {
            // "hello\nhi\nworld"
            // Caret at col 4 of "hello" → X=32px.
            // "hi" is 2 chars = 16px < 32px → clamp to end → col 2 → index 8.
            var m = ModelWithMeasurer("hello\nhi\nworld");
            m.SetCaret(4); // "hell|o"

            m.MoveLineDown();
            Assert.That(m.Selection.Start, Is.EqualTo(8),
                "X=32px past 'hi'(16px) → end of 'hi' = offset(6)+len(2)=8");
        }

        // Proportional fake measurer: W is 3× wider than i.
        // Proves the goal column is preserved by pixel X, not char count.
        [Test]
        public void MoveLineDown_proportional_measurer_wide_W_vs_thin_i() {
            // Fake proportional measurer: 'W'=3px, everything else=1px.
            // Line 0: "WWW" (9px), caret after first W → goalX=3px.
            // Line 1: "iiiiii" (6px). goalX=3px lands at col 3 → index 3.
            Func<string, int, int, double> fakeMetrics = (t, start, len) => {
                double total = 0;
                for (int j = start; j < start + len && j < t.Length; j++) {
                    total += (t[j] == 'W') ? 3.0 : 1.0;
                }
                return total;
            };

            var m = new TextEditModel("WWW\niiiiii", multiline: true);
            m.SetMeasureSubstring(fakeMetrics);
            m.SetCaret(1); // after first 'W' → goalX = 3px

            m.MoveLineDown();
            // "iiiiii": each 'i' is 1px. goalX=3px → col 3 (mid-point of char 3
            // is at 3.5px; 3.0px < 3.5px → left side wins → col 3).
            Assert.That(m.Selection.Start, Is.EqualTo(4 + 3),
                "goalX=3px on 'iiiiii' at 1px/char → col 3 → global=offset(4)+3=7");
        }

        // Fallback: when no measurer is wired, LineUpFrom / LineDownFrom use
        // char-column (existing test behavior preserved).
        [Test]
        public void MoveLineDown_fallback_uses_char_column_when_no_measurer() {
            // No SetMeasureSubstring — char-column path.
            var m = new TextEditModel("hello\nworld\nfoo", multiline: true);
            m.SetCaret(2); // col 2 of "hello"

            m.MoveLineDown();
            Assert.That(m.Selection.Start, Is.EqualTo(8),
                "char-col fallback: col 2 on 'world' = offset(6)+2=8");

            m.MoveLineDown();
            Assert.That(m.Selection.Start, Is.EqualTo(14),
                "char-col fallback: col 2 on 'foo' = offset(12)+2=14");
        }

        // MoveLineUp fallback uses char-column when no measurer.
        [Test]
        public void MoveLineUp_fallback_uses_char_column_when_no_measurer() {
            var m = new TextEditModel("hello\nworld", multiline: true);
            m.SetCaret(9); // col 3 of "world" (offset 6 + 3)

            m.MoveLineUp();
            Assert.That(m.Selection.Start, Is.EqualTo(3),
                "char-col fallback: col 3 on 'hello' = offset(0)+3=3");
        }

        // Metric-aware up: caret at first line clamps to 0.
        [Test]
        public void MoveLineUp_metric_clamps_to_start_on_first_line() {
            var m = ModelWithMeasurer("hello\nworld");
            m.SetCaret(3);

            m.MoveLineUp();
            Assert.That(m.Selection.Start, Is.EqualTo(0),
                "Already on first line → clamp to 0");
        }

        // Metric-aware down: caret at last line clamps to text.Length.
        [Test]
        public void MoveLineDown_metric_clamps_to_end_on_last_line() {
            var m = ModelWithMeasurer("hello\nworld");
            m.SetCaret(9); // col 3 of "world"

            m.MoveLineDown();
            Assert.That(m.Selection.Start, Is.EqualTo(11),
                "Already on last line → clamp to text.Length=11");
        }

        // SetMeasureSubstring(null) reverts to char-column fallback.
        [Test]
        public void SetMeasureSubstring_null_reverts_to_char_column() {
            var m = ModelWithMeasurer("hello\nworld");
            m.SetCaret(3);

            // Remove measurer.
            m.SetMeasureSubstring(null);

            m.MoveLineDown();
            // Char-column: col 3 of "world" = offset(6)+3=9.
            Assert.That(m.Selection.Start, Is.EqualTo(9),
                "After SetMeasureSubstring(null), char-column fallback applies");
        }
    }
}
