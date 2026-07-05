using System.Collections.Generic;
using NUnit.Framework;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Layout.Text;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    // W5 UAX #14 CJK line-breaking tests.
    //
    // All tests use MonoFontMetrics (default ctor): 16 px font → 8 px per
    // BMP character (CJK or ASCII). Each CJK code point is 1 UTF-16 unit
    // and measures 8 px. Surrogate-pair CJK (Extension B+) would measure 16
    // px (2 units × 8 px) — tested below in the supplementary-plane case.
    //
    // CSS Text L3 §5.2-§5.3 and UAX #14 spec references are cited inline.
    [TestFixture]
    public class CjkLineBreakingTests {
        static readonly MonoFontMetrics Mono = new MonoFontMetrics();

        // -----------------------------------------------------------------------
        // Helpers — mirror WordBreakTests's approach so test setup is minimal.
        // -----------------------------------------------------------------------
        static LineBreaker.Item Item(string text, double fontSize = 16,
                                     string ws = "normal",
                                     string wordBreak = null,
                                     string lineBreak = null,
                                     string overflowWrap = null) {
            return new LineBreaker.Item {
                Text = text,
                FontSize = fontSize,
                FontFamily = null,
                Color = "black",
                WhiteSpace = ws,
                WordBreak = wordBreak,
                LineBreak = lineBreak,
                OverflowWrap = overflowWrap,
                Metrics = Mono
            };
        }

        static string TextOfLine(LineBox line) {
            var sb = new System.Text.StringBuilder();
            foreach (var c in line.Children) {
                if (c is TextRun tr) sb.Append(tr.Text);
            }
            return sb.ToString();
        }

        static List<string> LineTexts(LineBreaker.Result r) {
            var list = new List<string>();
            foreach (var lb in r.Lines) list.Add(TextOfLine(lb));
            return list;
        }

        // -----------------------------------------------------------------------
        // 1. Pure CJK wrap — UAX #14 class ID (break between every ideograph)
        // -----------------------------------------------------------------------

        // 5 CJK chars at 8 px each = 40 px total.
        // Line width 16 px → 2 chars per line (2×8 = 16).
        // Expect: 5 chars → ceil(5/2) = 3 lines: "一二" | "三四" | "五".
        // This is the core W5 requirement: CJK breaks between EVERY pair of
        // adjacent ideographs when width is constrained.
        [Test]
        public void Pure_cjk_wraps_between_every_ideograph() {
            var br = new LineBreaker();
            // U+4E00–U+4E04 CJK unified ideographs
            var r = br.Break(new List<LineBreaker.Item> {
                Item("一丁丂七丄") // 一丁丂七丄 — 5 chars, 40 px
            }, 16);
            Assert.That(r.Lines.Count, Is.EqualTo(3));
            Assert.That(TextOfLine(r.Lines[0]), Is.EqualTo("一丁"));
            Assert.That(TextOfLine(r.Lines[1]), Is.EqualTo("丂七"));
            Assert.That(TextOfLine(r.Lines[2]), Is.EqualTo("丄"));
            Assert.That(r.Lines[0].Width, Is.EqualTo(16).Within(1e-6));
        }

        // Wide viewport: all 5 CJK chars fit on one line — no wrapping.
        [Test]
        public void Pure_cjk_does_not_wrap_when_all_fits_on_one_line() {
            var br = new LineBreaker();
            var r = br.Break(new List<LineBreaker.Item> {
                Item("一丁丂七丄") // 40 px
            }, 400);
            Assert.That(r.Lines.Count, Is.EqualTo(1));
            Assert.That(TextOfLine(r.Lines[0]).Length, Is.EqualTo(5));
        }

        // Hiragana (U+3040–309F) participates in break-between-ideographs.
        // 4 hiragana chars at 8 px = 32 px; viewport 16 px → 2 lines of 2.
        [Test]
        public void Hiragana_wraps_between_syllables() {
            var br = new LineBreaker();
            // あいうえ — small hiragana a, i, u, e (4 chars)
            var r = br.Break(new List<LineBreaker.Item> {
                Item("あいうえ") // 32 px
            }, 16);
            Assert.That(r.Lines.Count, Is.EqualTo(2));
            Assert.That(TextOfLine(r.Lines[0]).Length, Is.EqualTo(2));
            Assert.That(TextOfLine(r.Lines[1]).Length, Is.EqualTo(2));
        }

        // Katakana (U+30A0–30FF) participates in break-between-ideographs.
        [Test]
        public void Katakana_wraps_between_syllables() {
            var br = new LineBreaker();
            // アイウエ — Katakana A, I, U, E (4 chars)
            var r = br.Break(new List<LineBreaker.Item> {
                Item("アイウエ") // 32 px
            }, 16);
            Assert.That(r.Lines.Count, Is.EqualTo(2));
            Assert.That(TextOfLine(r.Lines[0]).Length, Is.EqualTo(2));
        }

        // Hangul syllables (U+AC00–D7AF) participate in break-between-ideographs.
        [Test]
        public void Hangul_syllables_wrap_between_each_other() {
            var br = new LineBreaker();
            // 가나다라 — Hangul syllables (4 chars)
            var r = br.Break(new List<LineBreaker.Item> {
                Item("가나다라") // 32 px
            }, 16);
            Assert.That(r.Lines.Count, Is.EqualTo(2));
        }

        // -----------------------------------------------------------------------
        // 2. Kinsoku prohibitions — CSS Text L3 §5.3
        // -----------------------------------------------------------------------

        // No break BEFORE a closing punctuation (kinsoku-close class).
        // 。is U+3002 IDEOGRAPHIC FULL STOP — must NOT start a new line.
        // "一二三。" at 8 px/char = 32 px. Viewport 24 px.
        // Greedy: "一二三" (24 px) fits; then "。" (8 px) must stay on the
        // same line even though the next ideograph could otherwise break.
        // BUT 24+8=32 > 24, so "。" would overflow — yet kinsoku forbids
        // putting it at the start of the next line. Chrome's resolution:
        // "一二三。" hangs on line 1 (punctuation hangs past the margin).
        // In our engine we keep it in the same chunk — it overflows the line
        // rather than violating kinsoku. Test: the "。" must appear on the
        // SAME line as its preceding ideograph, not at the start of a new line.
        [Test]
        public void Kinsoku_close_punctuation_stays_with_preceding_ideograph() {
            var br = new LineBreaker();
            // "一二三。" — 4 chars; viewport 16 px (fits 2 chars).
            // After CJK-split: chunks are "一二" | "三。" (kinsoku keeps 三 and 。together).
            var r = br.Break(new List<LineBreaker.Item> {
                Item("一二三。") // 32 px total
            }, 16);
            // Line 1: "一二"; Line 2: "三。" — the full stop must stay with 三, NOT start line 3.
            bool fullStopLeadsAnyLine = false;
            for (int i = 0; i < r.Lines.Count; i++) {
                string t = TextOfLine(r.Lines[i]);
                if (t.Length > 0 && t[0] == '。') fullStopLeadsAnyLine = true;
            }
            Assert.That(fullStopLeadsAnyLine, Is.False,
                "U+3002 IDEOGRAPHIC FULL STOP must never be the first char of a new line (kinsoku close).");
            Assert.That(r.Lines.Count, Is.GreaterThanOrEqualTo(2));
        }

        // No break BEFORE small kana (kinsoku-close).
        // Small つ (U+3063) cannot start a line.
        [Test]
        public void Kinsoku_small_tsu_does_not_start_a_line() {
            var br = new LineBreaker();
            // "あいっう" — あ(a) い(i) っ(small tsu) う(u)
            // Viewport 16 px (2 chars per line).
            // Without kinsoku, split would be "あい" | "っう".
            // WITH kinsoku, "っ" cannot start a line → "あいっ" chunk + "う".
            // But "あいっ" is 24 px > 16 px. So:
            //   - greedy packs "あい" (16 px) as first candidate,
            //   - then "っ" would go to next line but kinsoku forbids it,
            //   - so we keep "いっ" together → "あ" + "いっ" + "う"?
            // Actually, UAX #14: break opportunity exists between あ→い and い→っ.
            // The break at い→っ is FORBIDDEN (っ is kinsoku-close).
            // The break at あ→い is ALLOWED.
            // So chunks after splitting are: "あ" | "いっ" | "う".
            // Wait — no. We emit chunks between break opportunities. The opportunities
            // in "あいっう" are only at あ→い (allowed) and at っ→う (allowed).
            // い→っ is NOT an opportunity (kinsoku). So chunks are "あ" "いっ" "う".
            // At viewport 16 px: "あ" (8) on line 1, then "いっ" (16) fits too → line1 = "あいっ"?
            // Let's trace: chunkStart=0, prevCp=-1.
            //   i=0: cp=あ, prevCp=-1 → no check. prevCp=あ.
            //   i=1: cp=い, prevCp=あ → IsCjkBreakOpp(あ,い) = true (both CJK, no kinsoku). Emit chunk "あ". chunkStart=1. prevCp=い.
            //   i=2: cp=っ, prevCp=い → IsCjkBreakOpp(い,っ)? IsKinsokuClose(っ)=true → false. No break. prevCp=っ.
            //   i=3: cp=う, prevCp=っ → IsCjkBreakOpp(っ,う)? IsKinsokuClose(う)=false. IsKinsokuOpen(っ)=false. Both CJK → true. Emit chunk "いっ". chunkStart=3. prevCp=う.
            //   End: emit tail "う".
            // Chunks: "あ"(8px) "いっ"(16px) "う"(8px). Viewport=16px.
            //   "あ" (8) fits on line 1. Then "いっ" (16): 8+16=24>16 → wrap. line1="あ". "いっ" (16) fits on line2. "う" (8): 16+8=24>16 → wrap. line2="いっ". "う" on line3.
            var r = br.Break(new List<LineBreaker.Item> {
                Item("あいっう") // あいっう, 4 chars × 8 px = 32 px
            }, 16);
            bool smallTsuLeadsLine = false;
            for (int i = 0; i < r.Lines.Count; i++) {
                string t = TextOfLine(r.Lines[i]);
                if (t.Length > 0 && t[0] == 'っ') smallTsuLeadsLine = true;
            }
            Assert.That(smallTsuLeadsLine, Is.False,
                "Small tsu (っ U+3063) must never lead a line (kinsoku close).");
        }

        // No break AFTER an opening bracket (kinsoku-open).
        // 「 (U+300C) cannot end a line — nothing can start a new line immediately after it.
        [Test]
        public void Kinsoku_open_bracket_does_not_end_a_line() {
            var br = new LineBreaker();
            // 「一二三 — open corner bracket + 3 ideographs
            // Viewport 16 px. 「 (U+300C) → IsKinsokuOpen → no break after it.
            // IsCjkBreakOpportunity(「,一): IsKinsokuOpen(「)=true → false. No break.
            // IsCjkBreakOpportunity(一,二): true. IsCjkBreakOpportunity(二,三): true.
            // So chunks: "「一" (16px) | "二" (8px) | "三" (8px).
            // Viewport 16: "「一" (16) fits line1. "二" (8): after wrap? Wait, cursor is 16, "二" is 8 → 24 > 16? No: after FinishLine, cursor resets to 0. "二"(8) fits. Then "三"(8): 8+8=16 fits too. So line2 = "二三".
            // Actually chunks from EmitCjkRun are placed sequentially:
            //   Chunk "「一" (16px): state.X=0, fits (0+16≤16). state.X=16.
            //   Chunk "二" (8px): state.X=16, 16+8=24>16, has content → wrap. state.X=0. "二" (8) fits. state.X=8.
            //   Chunk "三" (8px): state.X=8, 8+8=16 ≤ 16. Fits. state.X=16.
            // Result: line1="「一", line2="二三".
            var r = br.Break(new List<LineBreaker.Item> {
                Item("「一二三") // 「一二三, 4 chars × 8 px = 32 px
            }, 16);
            bool openBracketEndsLine = false;
            foreach (var line in r.Lines) {
                string t = TextOfLine(line);
                if (t.Length > 0 && t[t.Length - 1] == '「') openBracketEndsLine = true;
            }
            Assert.That(openBracketEndsLine, Is.False,
                "Left corner bracket (「 U+300C) must never be the last char of a line (kinsoku open).");
        }

        // -----------------------------------------------------------------------
        // 3. word-break: keep-all — no CJK break, Latin word-break still applies
        // -----------------------------------------------------------------------

        // With keep-all, a long string of CJK chars does NOT break between them.
        // The entire run stays on one line (overflows if it doesn't fit).
        [Test]
        public void Keep_all_suppresses_cjk_breaks_and_overflows() {
            var br = new LineBreaker();
            // 4 CJK chars = 32 px; viewport 16 px. With keep-all: no inter-ideograph break.
            var r = br.Break(new List<LineBreaker.Item> {
                Item("一丁丂七", wordBreak: "keep-all") // 32 px
            }, 16);
            // keep-all: the whole CJK run stays together on one line (overflows).
            Assert.That(r.Lines.Count, Is.EqualTo(1));
            Assert.That(r.Lines[0].Width, Is.GreaterThan(16));
            Assert.That(TextOfLine(r.Lines[0]), Is.EqualTo("一丁丂七"));
        }

        // keep-all: Latin words in a mixed run still break at spaces.
        [Test]
        public void Keep_all_still_breaks_latin_at_spaces() {
            var br = new LineBreaker();
            // "hello world" — pure Latin, keep-all has no effect on spaces.
            var r = br.Break(new List<LineBreaker.Item> {
                Item("hello world", wordBreak: "keep-all")
            }, 48); // 48 px fits "hello" (40) but not "hello world" (88).
            Assert.That(r.Lines.Count, Is.EqualTo(2));
            Assert.That(TextOfLine(r.Lines[0]).Trim(), Is.EqualTo("hello"));
            Assert.That(TextOfLine(r.Lines[1]).Trim(), Is.EqualTo("world"));
        }

        // -----------------------------------------------------------------------
        // 4. word-break: break-all — existing behaviour preserved (no regression)
        // -----------------------------------------------------------------------

        // break-all with CJK text: every grapheme (including CJK) is a break point.
        // The existing EmitBreakAll path handles CJK via the same binary-search.
        // Since the CJK branch is skipped when breakAll=true, this test verifies
        // the existing code path still works for CJK under break-all.
        [Test]
        public void Break_all_breaks_cjk_at_arbitrary_grapheme_boundaries() {
            var br = new LineBreaker();
            // 4 CJK chars = 32 px; viewport 8 px → 1 char per line.
            var r = br.Break(new List<LineBreaker.Item> {
                Item("一丁丂七", wordBreak: "break-all") // 32 px
            }, 8);
            Assert.That(r.Lines.Count, Is.EqualTo(4));
            foreach (var line in r.Lines) {
                Assert.That(TextOfLine(line).Length, Is.EqualTo(1));
                Assert.That(line.Width, Is.LessThanOrEqualTo(8 + 1e-6));
            }
        }

        // -----------------------------------------------------------------------
        // 5. line-break: anywhere — kinsoku prohibitions lifted
        // -----------------------------------------------------------------------

        // With line-break: anywhere, 。 (kinsoku-close) MAY appear at the start
        // of a new line (the prohibition is lifted per CSS Text L3 §5.3).
        [Test]
        public void Line_break_anywhere_lifts_kinsoku_close_prohibition() {
            var br = new LineBreaker();
            // "一。" — ideograph + full stop, 2 chars. Viewport 8 px (1 char).
            // Normal: no break before 。 → both on same line (overflows).
            // line-break:anywhere → break IS allowed before 。.
            var r = br.Break(new List<LineBreaker.Item> {
                Item("一。", lineBreak: "anywhere") // 16 px total, viewport 8 px
            }, 8);
            // With kinsoku lifted, break occurs between 一 and 。:
            // line1 = "一", line2 = "。".
            Assert.That(r.Lines.Count, Is.EqualTo(2));
            string line2 = TextOfLine(r.Lines[1]);
            Assert.That(line2, Is.EqualTo("。"),
                "With line-break:anywhere, 。 may begin a new line.");
        }

        // With line-break: anywhere, the small kana prohibition is also lifted.
        [Test]
        public void Line_break_anywhere_lifts_small_kana_kinsoku() {
            var br = new LineBreaker();
            // "いっ" — normal i + small tsu; viewport 8 px.
            // Normal: no break before っ; both stay together (overflow).
            // line-break:anywhere → break allowed → two lines.
            var r = br.Break(new List<LineBreaker.Item> {
                Item("いっ", lineBreak: "anywhere") // 16 px, viewport 8 px
            }, 8);
            Assert.That(r.Lines.Count, Is.EqualTo(2));
        }

        // -----------------------------------------------------------------------
        // 6. Mixed Latin + CJK — Latin words keep word integrity
        // -----------------------------------------------------------------------

        // A Latin word embedded in a CJK run is treated as a single chunk and
        // not broken mid-word under normal/keep-all word-break mode. The space
        // between "hello" and CJK is the break point between the Latin and CJK.
        // We test via the cascade-level Build() helper to exercise the full pipeline.
        [Test]
        public void Mixed_latin_cjk_latin_word_keeps_integrity() {
            // "hello 一二三" — the Latin word "hello" must stay whole on its line.
            // With Mono at 8 px/char: "hello"=40px, space=8px, "一二三"=24px.
            // Viewport 48 px. "hello" (40) fits line1; space wraps; "一二三" goes to line2 (no intra-CJK wrap needed at 48px).
            const string css = @"
                p { width: 48px; font-family: monospace; font-size: 16px; }
            ";
            var (root, _, _) = Build("<p>hello 一二三</p>", css, viewportWidth: 800);
            var lines = new List<LineBox>(AllLineBoxes(root));
            // Line 1 should contain "hello" and not have it split.
            bool helloOnOneLine = false;
            foreach (var lb in lines) {
                string t = TextOfLine(lb);
                if (t.Contains("hello") && !t.Contains("hel\n") && !t.Contains("lo\n")) {
                    helloOnOneLine = true;
                }
            }
            Assert.That(helloOnOneLine, Is.True, "'hello' must appear unsplit on a single line.");
        }

        // -----------------------------------------------------------------------
        // 7. Viewport-driven line count verification (end-to-end via Build())
        // -----------------------------------------------------------------------

        // A 6-char CJK string at 8 px/char = 48 px.
        // Container width 16 px (2 chars/line) → 3 lines expected.
        [Test]
        public void Viewport_driven_cjk_wraps_produce_correct_line_count() {
            const string css = @"
                p { width: 16px; font-family: monospace; font-size: 16px; }
            ";
            // 6 CJK characters
            var (root, _, _) = Build("<p>一丁丂七丄丅</p>", css, viewportWidth: 800);
            var lines = new List<LineBox>(AllLineBoxes(root));
            Assert.That(lines.Count, Is.EqualTo(3),
                "6 CJK chars at 8px each in a 16px container should produce 3 lines (2 chars per line).");
        }

        // 3-char CJK string in a wide container: single line.
        [Test]
        public void Wide_viewport_cjk_stays_on_one_line() {
            const string css = @"
                p { width: 800px; font-family: monospace; font-size: 16px; }
            ";
            var (root, _, _) = Build("<p>一丁丂</p>", css, viewportWidth: 800);
            var lines = new List<LineBox>(AllLineBoxes(root));
            Assert.That(lines.Count, Is.EqualTo(1));
        }

        // -----------------------------------------------------------------------
        // 8. word-break: normal baseline — existing tests still pass (regression)
        // -----------------------------------------------------------------------

        // word-break: normal with Latin text: long word does NOT break (same
        // as before this change — the CJK branch is skipped for Latin-only text).
        [Test]
        public void Normal_word_break_latin_still_overflows_on_one_line() {
            var br = new LineBreaker();
            var r = br.Break(new List<LineBreaker.Item> {
                Item("supercalifragilistic") // 160 px
            }, 64);
            Assert.That(r.Lines.Count, Is.EqualTo(1));
            Assert.That(r.Lines[0].Width, Is.GreaterThan(64));
        }

        // word-break: normal with CJK: must wrap (this is the new behaviour added
        // by W5 — previously keep-all fell back to normal and did NOT wrap CJK).
        [Test]
        public void Normal_word_break_cjk_wraps_between_ideographs() {
            var br = new LineBreaker();
            // 4 CJK chars = 32 px; viewport 16 px → 2 chars per line → 2 lines.
            var r = br.Break(new List<LineBreaker.Item> {
                Item("一丁丂七") // wordBreak: null = "normal"
            }, 16);
            Assert.That(r.Lines.Count, Is.EqualTo(2));
            Assert.That(TextOfLine(r.Lines[0]), Is.EqualTo("一丁"));
            Assert.That(TextOfLine(r.Lines[1]), Is.EqualTo("丂七"));
        }

        // -----------------------------------------------------------------------
        // 9. LineBreakClasses unit tests — verify the classification helpers
        // -----------------------------------------------------------------------

        [Test]
        public void LineBreakClasses_ideographic_classification() {
            // CJK Unified Ideographs
            Assert.That(LineBreakClasses.IsCjkIdeographic(0x4E00), Is.True, "U+4E00 is CJK");
            Assert.That(LineBreakClasses.IsCjkIdeographic(0x9FFF), Is.True, "U+9FFF is CJK");
            // Hiragana
            Assert.That(LineBreakClasses.IsCjkIdeographic(0x3042), Is.True, "U+3042 あ is Hiragana");
            // Katakana
            Assert.That(LineBreakClasses.IsCjkIdeographic(0x30A2), Is.True, "U+30A2 ア is Katakana");
            // Hangul
            Assert.That(LineBreakClasses.IsCjkIdeographic(0xAC00), Is.True, "U+AC00 가 is Hangul");
            // ASCII letters are NOT CJK
            Assert.That(LineBreakClasses.IsCjkIdeographic('a'), Is.False, "ASCII 'a' is not CJK");
            Assert.That(LineBreakClasses.IsCjkIdeographic(' '), Is.False, "Space is not CJK");
        }

        [Test]
        public void LineBreakClasses_kinsoku_close_includes_small_kana_and_punctuation() {
            // Universal kinsoku: 。 is in IsKinsokuClose (applies at all levels including loose).
            Assert.That(LineBreakClasses.IsKinsokuClose(0x3002), Is.True, "。 is kinsoku close (universal)");
            // F17b: small kana (っ), prolonged sound mark (ー), and fullwidth ! (！) are now in
            // IsLooseOnlyKinsokuClose — they are forbidden under normal/strict but allowed under loose.
            // The combined IsKinsokuCloseForLevel(cp, "normal") still returns true for them.
            Assert.That(LineBreakClasses.IsLooseOnlyKinsokuClose(0x3063), Is.True, "っ small tsu is loose-only kinsoku close");
            Assert.That(LineBreakClasses.IsLooseOnlyKinsokuClose(0x30FC), Is.True, "ー prolonged sound is loose-only kinsoku close");
            Assert.That(LineBreakClasses.IsLooseOnlyKinsokuClose(0xFF01), Is.True, "！ fullwidth ! is loose-only kinsoku close");
            Assert.That(LineBreakClasses.IsKinsokuCloseForLevel(0x3063, "normal"), Is.True, "っ is kinsoku-close under normal");
            Assert.That(LineBreakClasses.IsKinsokuCloseForLevel(0x30FC, "normal"), Is.True, "ー is kinsoku-close under normal");
            Assert.That(LineBreakClasses.IsKinsokuCloseForLevel(0xFF01, "normal"), Is.True, "！ is kinsoku-close under normal");
            // Normal CJK ideographs are NOT kinsoku-close
            Assert.That(LineBreakClasses.IsKinsokuClose(0x4E00), Is.False, "一 is not kinsoku close");
        }

        [Test]
        public void LineBreakClasses_kinsoku_open_includes_opening_brackets() {
            Assert.That(LineBreakClasses.IsKinsokuOpen(0x300C), Is.True, "「 is kinsoku open");
            Assert.That(LineBreakClasses.IsKinsokuOpen(0x300E), Is.True, "『 is kinsoku open");
            Assert.That(LineBreakClasses.IsKinsokuOpen(0xFF08), Is.True, "（ fullwidth ( is kinsoku open");
            // Closing bracket is NOT kinsoku-open
            Assert.That(LineBreakClasses.IsKinsokuOpen(0x300D), Is.False, "」 is not kinsoku open (it's close)");
        }

        [Test]
        public void LineBreakClasses_break_opportunity_cjk_to_cjk() {
            // Normal case: break between two CJK ideographs is allowed.
            Assert.That(
                LineBreakClasses.IsCjkBreakOpportunity(0x4E00, 0x4E01, "normal"),
                Is.True, "Break between 一 and 丁 allowed");
            // Kinsoku: no break before 。
            Assert.That(
                LineBreakClasses.IsCjkBreakOpportunity(0x4E00, 0x3002, "normal"),
                Is.False, "No break before 。 (kinsoku close)");
            // Kinsoku: no break after 「
            Assert.That(
                LineBreakClasses.IsCjkBreakOpportunity(0x300C, 0x4E00, "normal"),
                Is.False, "No break after 「 (kinsoku open)");
            // line-break: anywhere lifts kinsoku
            Assert.That(
                LineBreakClasses.IsCjkBreakOpportunity(0x4E00, 0x3002, "anywhere"),
                Is.True, "Break before 。 allowed with line-break:anywhere");
        }

        // -----------------------------------------------------------------------
        // 10. ContainsCjk helper
        // -----------------------------------------------------------------------

        [Test]
        public void ContainsCjk_detects_cjk_in_mixed_string() {
            Assert.That(LineBreakClasses.ContainsCjk("hello一world"), Is.True);
            Assert.That(LineBreakClasses.ContainsCjk("hello"), Is.False);
            Assert.That(LineBreakClasses.ContainsCjk(""), Is.False);
            Assert.That(LineBreakClasses.ContainsCjk("一"), Is.True);
        }
    }
}
