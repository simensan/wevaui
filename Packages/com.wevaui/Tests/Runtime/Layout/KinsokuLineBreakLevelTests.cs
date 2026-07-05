using System.Collections.Generic;
using NUnit.Framework;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Layout.Text;

// F17b: CSS Text L3 §5.3 `line-break` strict-set tests.
// These tests verify that the three `line-break` values (loose, normal, strict)
// produce the correct kinsoku prohibitions per the spec:
//   - line-break: normal / strict  → full kinsoku (universal + loose-only groups)
//   - line-break: loose             → only universal kinsoku; loose-only groups allowed
//   - line-break: anywhere          → all kinsoku lifted (regression: existing behaviour)
//
// All tests use MonoFontMetrics (16 px font → 8 px per BMP character).
// Character tables (all BMP, 1 UTF-16 unit, 8 px wide):
//   一 一  CJK ideograph
//   あ あ  Hiragana (full-size, normal flow)
//   ぁ ぁ  Small hiragana a  — Group 1 (loose-only)
//   っ っ  Small hiragana tsu — Group 1 (loose-only)
//   ッ ッ  Small katakana tsu ッ — Group 1 (loose-only)
//   ー ー  Prolonged sound mark — Group 1 (loose-only)
//   ‐ ‐  Hyphen — Group 2 (loose-only)
//   – –  En dash — Group 2 (loose-only)
//   〜 〜  Wave dash — Group 2 (loose-only)
//   ゠ ゠  Double hyphen — Group 2 (loose-only)
//   々 々  Ideographic iteration mark — Group 3 (loose-only)
//   ゝ ゝ  Hiragana iteration mark — Group 3 (loose-only)
//   ヽ ヽ  Katakana iteration mark ヽ — Group 3 (loose-only)
//   ・ ・  Katakana middle dot — Group 4 (loose-only, CJK context)
//   ： ：  Fullwidth colon — Group 4 (loose-only, CJK context)
//   ！ ！  Fullwidth ! — Group 4 (loose-only, CJK context)
//   ‼ ‼  Double exclamation — Group 4 (loose-only, CJK context)
//   。 。  Ideographic full stop — universal kinsoku (all levels forbid)
//   」 」  Right corner bracket — universal kinsoku (all levels forbid)
//   「 「  Left corner bracket — kinsoku open (cannot end a line)

namespace Weva.Tests.Layout {
    [TestFixture]
    public class KinsokuLineBreakLevelTests {
        static readonly MonoFontMetrics Mono = new MonoFontMetrics();

        static LineBreaker.Item Item(string text, double fontSize = 16,
                                     string ws = "normal",
                                     string wordBreak = null,
                                     string lineBreak = null) {
            return new LineBreaker.Item {
                Text     = text,
                FontSize = fontSize,
                FontFamily = null,
                Color    = "black",
                WhiteSpace = ws,
                WordBreak  = wordBreak,
                LineBreak  = lineBreak,
                Metrics  = Mono
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

        // Helper: does ANY line in the result start with the given char?
        static bool AnyLineStartsWith(LineBreaker.Result r, char c) {
            foreach (var lb in r.Lines) {
                string t = TextOfLine(lb);
                if (t.Length > 0 && t[0] == c) return true;
            }
            return false;
        }

        // ======================================================================
        // 1. Classification unit tests — IsKinsokuCloseForLevel
        // ======================================================================

        // Universal kinsoku (should be forbidden at every level except anywhere)
        [Test]
        public void IsKinsokuCloseForLevel_universal_close_forbidden_at_normal() {
            // 。 U+3002 IDEOGRAPHIC FULL STOP — universal kinsoku
            Assert.That(LineBreakClasses.IsKinsokuCloseForLevel(0x3002, "normal"), Is.True,
                "U+3002 must be kinsoku-close under normal");
        }

        [Test]
        public void IsKinsokuCloseForLevel_universal_close_forbidden_at_strict() {
            Assert.That(LineBreakClasses.IsKinsokuCloseForLevel(0x3002, "strict"), Is.True,
                "U+3002 must be kinsoku-close under strict");
        }

        [Test]
        public void IsKinsokuCloseForLevel_universal_close_forbidden_at_loose() {
            Assert.That(LineBreakClasses.IsKinsokuCloseForLevel(0x3002, "loose"), Is.True,
                "U+3002 universal kinsoku still applies under loose");
        }

        [Test]
        public void IsKinsokuCloseForLevel_anywhere_always_false() {
            // line-break: anywhere lifts ALL kinsoku
            Assert.That(LineBreakClasses.IsKinsokuCloseForLevel(0x3002, "anywhere"), Is.False,
                "Universal kinsoku is lifted by line-break: anywhere");
            Assert.That(LineBreakClasses.IsKinsokuCloseForLevel(0x3063, "anywhere"), Is.False,
                "Small tsu kinsoku is lifted by line-break: anywhere");
            Assert.That(LineBreakClasses.IsKinsokuCloseForLevel(0x30FC, "anywhere"), Is.False,
                "Prolonged sound mark kinsoku is lifted by line-break: anywhere");
        }

        // --- Group 1: Small kana ---
        [Test]
        public void IsKinsokuCloseForLevel_small_kana_normal_strict_forbidden_loose_allowed() {
            int[] smallKana = {
                0x3041, // ぁ small a
                0x3063, // っ small tsu
                0x3083, // ゃ small ya
                0x30A1, // ァ small A (katakana)
                0x30C3, // ッ small tsu (katakana)
                0x30FC, // ー prolonged sound mark
            };
            foreach (int cp in smallKana) {
                Assert.That(LineBreakClasses.IsKinsokuCloseForLevel(cp, "normal"), Is.True,
                    $"U+{cp:X4} should be kinsoku-close under normal");
                Assert.That(LineBreakClasses.IsKinsokuCloseForLevel(cp, "strict"), Is.True,
                    $"U+{cp:X4} should be kinsoku-close under strict");
                Assert.That(LineBreakClasses.IsKinsokuCloseForLevel(cp, "loose"), Is.False,
                    $"U+{cp:X4} should NOT be kinsoku-close under loose (Group 1)");
            }
        }

        [Test]
        public void IsKinsokuCloseForLevel_katakana_phonetic_extensions_loose_allowed() {
            // U+31F0-31FF Katakana Phonetic Extensions (small Ainu katakana)
            Assert.That(LineBreakClasses.IsKinsokuCloseForLevel(0x31F0, "normal"), Is.True,
                "U+31F0 is kinsoku-close under normal");
            Assert.That(LineBreakClasses.IsKinsokuCloseForLevel(0x31FF, "normal"), Is.True,
                "U+31FF is kinsoku-close under normal");
            Assert.That(LineBreakClasses.IsKinsokuCloseForLevel(0x31F0, "loose"), Is.False,
                "U+31F0 is allowed at line start under loose");
            Assert.That(LineBreakClasses.IsKinsokuCloseForLevel(0x31FF, "loose"), Is.False,
                "U+31FF is allowed at line start under loose");
        }

        // --- Group 2: Hyphen-like ---
        [Test]
        public void IsKinsokuCloseForLevel_hyphens_normal_strict_forbidden_loose_allowed() {
            int[] hyphens = {
                0x2010, // ‐ HYPHEN
                0x2013, // – EN DASH
                0x301C, // 〜 WAVE DASH
                0x30A0, // ゠ KATAKANA-HIRAGANA DOUBLE HYPHEN
            };
            foreach (int cp in hyphens) {
                Assert.That(LineBreakClasses.IsKinsokuCloseForLevel(cp, "normal"), Is.True,
                    $"U+{cp:X4} should be kinsoku-close under normal (Group 2)");
                Assert.That(LineBreakClasses.IsKinsokuCloseForLevel(cp, "strict"), Is.True,
                    $"U+{cp:X4} should be kinsoku-close under strict (Group 2)");
                Assert.That(LineBreakClasses.IsKinsokuCloseForLevel(cp, "loose"), Is.False,
                    $"U+{cp:X4} should NOT be kinsoku-close under loose (Group 2)");
            }
        }

        // --- Group 3: Iteration marks ---
        [Test]
        public void IsKinsokuCloseForLevel_iteration_marks_normal_strict_forbidden_loose_allowed() {
            int[] iterationMarks = {
                0x3005, // 々 IDEOGRAPHIC ITERATION MARK
                0x303B, // 〻 VERTICAL IDEOGRAPHIC ITERATION MARK
                0x309D, // ゝ HIRAGANA ITERATION MARK
                0x309E, // ゞ VOICED HIRAGANA ITERATION MARK
                0x30FD, // ヽ KATAKANA ITERATION MARK
                0x30FE, // ヾ VOICED KATAKANA ITERATION MARK
            };
            foreach (int cp in iterationMarks) {
                Assert.That(LineBreakClasses.IsKinsokuCloseForLevel(cp, "normal"), Is.True,
                    $"U+{cp:X4} should be kinsoku-close under normal (Group 3)");
                Assert.That(LineBreakClasses.IsKinsokuCloseForLevel(cp, "loose"), Is.False,
                    $"U+{cp:X4} should NOT be kinsoku-close under loose (Group 3)");
            }
        }

        // --- Group 4: Centered punctuation ---
        [Test]
        public void IsKinsokuCloseForLevel_centered_punctuation_normal_strict_forbidden_loose_allowed() {
            int[] centeredPunct = {
                0x30FB, // ・ KATAKANA MIDDLE DOT
                0xFF1A, // ： Fullwidth COLON
                0xFF1B, // ； Fullwidth SEMICOLON
                0x203C, // ‼ DOUBLE EXCLAMATION MARK
                0x2047, // ⁇ DOUBLE QUESTION MARK
                0x2048, // ⁈ QUESTION EXCLAMATION MARK
                0x2049, // ⁉ EXCLAMATION QUESTION MARK
                0xFF01, // ！ Fullwidth EXCLAMATION
                0xFF1F, // ？ Fullwidth QUESTION
            };
            foreach (int cp in centeredPunct) {
                Assert.That(LineBreakClasses.IsKinsokuCloseForLevel(cp, "normal"), Is.True,
                    $"U+{cp:X4} should be kinsoku-close under normal (Group 4)");
                Assert.That(LineBreakClasses.IsKinsokuCloseForLevel(cp, "strict"), Is.True,
                    $"U+{cp:X4} should be kinsoku-close under strict (Group 4)");
                Assert.That(LineBreakClasses.IsKinsokuCloseForLevel(cp, "loose"), Is.False,
                    $"U+{cp:X4} should NOT be kinsoku-close under loose (Group 4)");
            }
        }

        // ======================================================================
        // 2. IsCjkBreakOpportunity — per-level dispatch
        // ======================================================================

        // Normal case: break between two CJK ideographs — unchanged at all levels.
        [Test]
        public void BreakOpportunity_between_ideographs_allowed_at_all_levels() {
            // 一(U+4E00) → 丁(U+4E01)
            Assert.That(LineBreakClasses.IsCjkBreakOpportunity(0x4E00, 0x4E01, "normal"), Is.True);
            Assert.That(LineBreakClasses.IsCjkBreakOpportunity(0x4E00, 0x4E01, "strict"), Is.True);
            Assert.That(LineBreakClasses.IsCjkBreakOpportunity(0x4E00, 0x4E01, "loose"), Is.True);
            Assert.That(LineBreakClasses.IsCjkBreakOpportunity(0x4E00, 0x4E01, "anywhere"), Is.True);
        }

        // Universal kinsoku: no break before 。 at any level except anywhere.
        [Test]
        public void BreakOpportunity_no_break_before_ideographic_full_stop_normal_strict_loose() {
            Assert.That(LineBreakClasses.IsCjkBreakOpportunity(0x4E00, 0x3002, "normal"), Is.False,
                "No break before 。 under normal");
            Assert.That(LineBreakClasses.IsCjkBreakOpportunity(0x4E00, 0x3002, "strict"), Is.False,
                "No break before 。 under strict");
            Assert.That(LineBreakClasses.IsCjkBreakOpportunity(0x4E00, 0x3002, "loose"), Is.False,
                "No break before 。 under loose (universal kinsoku)");
        }

        // Group 1 small kana: forbidden under normal/strict, allowed under loose.
        [Test]
        public void BreakOpportunity_small_kana_forbidden_normal_allowed_loose() {
            // Break before small tsu っ (U+3063) — Group 1
            Assert.That(LineBreakClasses.IsCjkBreakOpportunity(0x3042, 0x3063, "normal"), Is.False,
                "No break before っ under normal");
            Assert.That(LineBreakClasses.IsCjkBreakOpportunity(0x3042, 0x3063, "strict"), Is.False,
                "No break before っ under strict");
            Assert.That(LineBreakClasses.IsCjkBreakOpportunity(0x3042, 0x3063, "loose"), Is.True,
                "Break before っ IS allowed under loose");
        }

        [Test]
        public void BreakOpportunity_prolonged_sound_mark_forbidden_normal_allowed_loose() {
            // Break before ー (U+30FC) — Group 1
            Assert.That(LineBreakClasses.IsCjkBreakOpportunity(0x30A2, 0x30FC, "normal"), Is.False,
                "No break before ー under normal");
            Assert.That(LineBreakClasses.IsCjkBreakOpportunity(0x30A2, 0x30FC, "loose"), Is.True,
                "Break before ー IS allowed under loose");
        }

        // Group 2 hyphen-like: forbidden under normal/strict, allowed under loose.
        [Test]
        public void BreakOpportunity_hyphen_forbidden_normal_allowed_loose() {
            // Break before ‐ (U+2010) — Group 2
            // For U+2010 to appear in IsCjkFlowChar, IsLooseOnlyKinsokuClose must return true.
            Assert.That(LineBreakClasses.IsCjkBreakOpportunity(0x4E00, 0x2010, "normal"), Is.False,
                "No break before U+2010 HYPHEN under normal");
            Assert.That(LineBreakClasses.IsCjkBreakOpportunity(0x4E00, 0x2010, "strict"), Is.False,
                "No break before U+2010 HYPHEN under strict");
            Assert.That(LineBreakClasses.IsCjkBreakOpportunity(0x4E00, 0x2010, "loose"), Is.True,
                "Break before U+2010 HYPHEN IS allowed under loose");
        }

        [Test]
        public void BreakOpportunity_wave_dash_forbidden_normal_allowed_loose() {
            // Break before 〜 (U+301C) — Group 2
            Assert.That(LineBreakClasses.IsCjkBreakOpportunity(0x4E00, 0x301C, "normal"), Is.False,
                "No break before 〜 under normal");
            Assert.That(LineBreakClasses.IsCjkBreakOpportunity(0x4E00, 0x301C, "loose"), Is.True,
                "Break before 〜 IS allowed under loose");
        }

        // Group 3 iteration marks: forbidden under normal/strict, allowed under loose.
        [Test]
        public void BreakOpportunity_iteration_mark_forbidden_normal_allowed_loose() {
            // Break before 々 (U+3005) — Group 3
            Assert.That(LineBreakClasses.IsCjkBreakOpportunity(0x4E00, 0x3005, "normal"), Is.False,
                "No break before 々 under normal");
            Assert.That(LineBreakClasses.IsCjkBreakOpportunity(0x4E00, 0x3005, "strict"), Is.False,
                "No break before 々 under strict");
            Assert.That(LineBreakClasses.IsCjkBreakOpportunity(0x4E00, 0x3005, "loose"), Is.True,
                "Break before 々 IS allowed under loose");
        }

        [Test]
        public void BreakOpportunity_katakana_iteration_mark_forbidden_normal_allowed_loose() {
            // Break before ヽ (U+30FD) — Group 3
            Assert.That(LineBreakClasses.IsCjkBreakOpportunity(0x30A2, 0x30FD, "normal"), Is.False,
                "No break before ヽ under normal");
            Assert.That(LineBreakClasses.IsCjkBreakOpportunity(0x30A2, 0x30FD, "loose"), Is.True,
                "Break before ヽ IS allowed under loose");
        }

        // Group 4 centered punctuation: forbidden under normal/strict, allowed under loose.
        [Test]
        public void BreakOpportunity_middle_dot_forbidden_normal_allowed_loose() {
            // Break before ・ (U+30FB) — Group 4
            Assert.That(LineBreakClasses.IsCjkBreakOpportunity(0x4E00, 0x30FB, "normal"), Is.False,
                "No break before ・ under normal");
            Assert.That(LineBreakClasses.IsCjkBreakOpportunity(0x4E00, 0x30FB, "strict"), Is.False,
                "No break before ・ under strict");
            Assert.That(LineBreakClasses.IsCjkBreakOpportunity(0x4E00, 0x30FB, "loose"), Is.True,
                "Break before ・ IS allowed under loose");
        }

        [Test]
        public void BreakOpportunity_fullwidth_exclamation_forbidden_normal_allowed_loose() {
            // Break before ！ (U+FF01) — Group 4
            Assert.That(LineBreakClasses.IsCjkBreakOpportunity(0x4E00, 0xFF01, "normal"), Is.False,
                "No break before ！ under normal");
            Assert.That(LineBreakClasses.IsCjkBreakOpportunity(0x4E00, 0xFF01, "loose"), Is.True,
                "Break before ！ IS allowed under loose");
        }

        [Test]
        public void BreakOpportunity_double_exclamation_forbidden_normal_allowed_loose() {
            // Break before ‼ (U+203C) — Group 4
            Assert.That(LineBreakClasses.IsCjkBreakOpportunity(0x4E00, 0x203C, "normal"), Is.False,
                "No break before ‼ under normal");
            Assert.That(LineBreakClasses.IsCjkBreakOpportunity(0x4E00, 0x203C, "loose"), Is.True,
                "Break before ‼ IS allowed under loose");
        }

        // anywhere lifts loose-only groups too.
        [Test]
        public void BreakOpportunity_anywhere_lifts_all_groups() {
            Assert.That(LineBreakClasses.IsCjkBreakOpportunity(0x4E00, 0x3063, "anywhere"), Is.True,
                "small tsu: anywhere lifts Group 1");
            Assert.That(LineBreakClasses.IsCjkBreakOpportunity(0x4E00, 0x2010, "anywhere"), Is.True,
                "hyphen: anywhere lifts Group 2");
            Assert.That(LineBreakClasses.IsCjkBreakOpportunity(0x4E00, 0x3005, "anywhere"), Is.True,
                "々: anywhere lifts Group 3");
            Assert.That(LineBreakClasses.IsCjkBreakOpportunity(0x4E00, 0xFF01, "anywhere"), Is.True,
                "！: anywhere lifts Group 4");
            Assert.That(LineBreakClasses.IsCjkBreakOpportunity(0x4E00, 0x3002, "anywhere"), Is.True,
                "。: anywhere lifts universal kinsoku");
        }

        // Open-class kinsoku is not relaxed by loose.
        [Test]
        public void BreakOpportunity_open_bracket_not_relaxed_by_loose() {
            // 「 (U+300C) is kinsoku-open — no break AFTER it, regardless of level.
            Assert.That(LineBreakClasses.IsCjkBreakOpportunity(0x300C, 0x4E00, "loose"), Is.False,
                "No break after 「 even under loose");
            Assert.That(LineBreakClasses.IsCjkBreakOpportunity(0x300C, 0x4E00, "normal"), Is.False,
                "No break after 「 under normal");
        }

        // ======================================================================
        // 3. Latin text is unaffected — LTR fast path regression guard
        // ======================================================================

        [Test]
        public void Latin_codepoints_never_produce_cjk_break_opportunity() {
            // 'a' → 'b' — neither is CJK flow
            Assert.That(LineBreakClasses.IsCjkBreakOpportunity('a', 'b', "normal"), Is.False);
            Assert.That(LineBreakClasses.IsCjkBreakOpportunity('a', 'b', "loose"), Is.False);
            Assert.That(LineBreakClasses.IsCjkBreakOpportunity('a', 'b', "anywhere"), Is.False);
        }

        [Test]
        public void IsLooseOnlyKinsokuClose_returns_false_for_latin() {
            Assert.That(LineBreakClasses.IsLooseOnlyKinsokuClose('a'), Is.False);
            Assert.That(LineBreakClasses.IsLooseOnlyKinsokuClose(' '), Is.False);
            Assert.That(LineBreakClasses.IsLooseOnlyKinsokuClose('!'), Is.False);
        }

        // ======================================================================
        // 4. End-to-end LineBreaker tests for loose vs normal
        // ======================================================================

        // Under normal: small kana っ cannot start a line.
        // Under loose: っ may start a line.
        //
        // Setup: "いっ" at 8px/char, viewport 8px (1 char per line).
        //   Normal: no break before っ → both chars forced onto one line (overflows).
        //   Loose: break IS allowed before っ → two lines: "い" | "っ".
        [Test]
        public void Small_kana_leads_line_under_loose_but_not_normal() {
            var br = new LineBreaker();

            // --- normal ---
            var rNormal = br.Break(new List<LineBreaker.Item> {
                // い = い, っ = っ
                Item("いっ", lineBreak: "normal")  // 16 px total, viewport 8 px
            }, 8);
            // Under normal, っ cannot start a line — both chars stay together.
            // Line count should be 1 (overflow on the same line).
            Assert.That(rNormal.Lines.Count, Is.EqualTo(1),
                "Under normal, no break before っ — chars stay on one line");

            // --- loose ---
            var rLoose = br.Break(new List<LineBreaker.Item> {
                Item("いっ", lineBreak: "loose")
            }, 8);
            // Under loose, break before っ is allowed → two lines.
            Assert.That(rLoose.Lines.Count, Is.EqualTo(2),
                "Under loose, break before っ is allowed → two lines");
            Assert.That(TextOfLine(rLoose.Lines[0]), Is.EqualTo("い"),
                "Line 1 = い");
            Assert.That(TextOfLine(rLoose.Lines[1]), Is.EqualTo("っ"),
                "Line 2 = っ (small kana may start line under loose)");
        }

        // Prolonged sound mark ー forbidden under normal, allowed under loose.
        [Test]
        public void Prolonged_sound_mark_leads_line_under_loose_but_not_normal() {
            var br = new LineBreaker();

            // ア = ア, ー = ー
            var rNormal = br.Break(new List<LineBreaker.Item> {
                Item("アー", lineBreak: "normal")
            }, 8);
            Assert.That(rNormal.Lines.Count, Is.EqualTo(1),
                "Under normal, no break before ー");

            var rLoose = br.Break(new List<LineBreaker.Item> {
                Item("アー", lineBreak: "loose")
            }, 8);
            Assert.That(rLoose.Lines.Count, Is.EqualTo(2),
                "Under loose, break before ー is allowed");
            Assert.That(TextOfLine(rLoose.Lines[1]), Is.EqualTo("ー"),
                "ー may start a line under loose");
        }

        // Iteration mark 々 forbidden under normal, allowed under loose.
        [Test]
        public void Iteration_mark_leads_line_under_loose_but_not_normal() {
            var br = new LineBreaker();

            // 一 = 一, 々 = 々
            var rNormal = br.Break(new List<LineBreaker.Item> {
                Item("一々", lineBreak: "normal")
            }, 8);
            Assert.That(rNormal.Lines.Count, Is.EqualTo(1),
                "Under normal, no break before 々");

            var rLoose = br.Break(new List<LineBreaker.Item> {
                Item("一々", lineBreak: "loose")
            }, 8);
            Assert.That(rLoose.Lines.Count, Is.EqualTo(2),
                "Under loose, break before 々 is allowed");
            Assert.That(TextOfLine(rLoose.Lines[1]), Is.EqualTo("々"),
                "々 may start a line under loose");
        }

        // Fullwidth ! forbidden under normal, allowed under loose.
        [Test]
        public void Fullwidth_exclamation_leads_line_under_loose_but_not_normal() {
            var br = new LineBreaker();

            // 一 = 一, ！ = ！
            var rNormal = br.Break(new List<LineBreaker.Item> {
                Item("一！", lineBreak: "normal")
            }, 8);
            Assert.That(rNormal.Lines.Count, Is.EqualTo(1),
                "Under normal, no break before ！");

            var rLoose = br.Break(new List<LineBreaker.Item> {
                Item("一！", lineBreak: "loose")
            }, 8);
            Assert.That(rLoose.Lines.Count, Is.EqualTo(2),
                "Under loose, break before ！ is allowed");
            Assert.That(TextOfLine(rLoose.Lines[1]), Is.EqualTo("！"),
                "！ may start a line under loose");
        }

        // Universal kinsoku (。) is forbidden at ALL levels including loose.
        [Test]
        public void Universal_kinsoku_still_applies_under_loose() {
            var br = new LineBreaker();

            // 一 = 一, 。 = 。
            var rLoose = br.Break(new List<LineBreaker.Item> {
                Item("一。", lineBreak: "loose")
            }, 8);
            // Even under loose, 。 cannot start a line.
            Assert.That(rLoose.Lines.Count, Is.EqualTo(1),
                "Universal kinsoku (。) still forbids line-start under loose");
            Assert.That(AnyLineStartsWith(rLoose, '。'), Is.False,
                "。 must never start a line (even loose)");
        }

        // Strict behaves the same as normal for all four groups (v1 simplification).
        [Test]
        public void Strict_same_as_normal_for_small_kana() {
            var br = new LineBreaker();

            // い = い, っ = っ
            var rStrict = br.Break(new List<LineBreaker.Item> {
                Item("いっ", lineBreak: "strict")
            }, 8);
            // Under strict, same as normal — no break before っ.
            Assert.That(rStrict.Lines.Count, Is.EqualTo(1),
                "Under strict (= normal for v1), no break before っ");
        }

        // Control: plain Latin text is completely unaffected by any line-break level.
        [Test]
        public void Latin_text_unaffected_by_line_break_level() {
            var br = new LineBreaker();

            // "hello world" — pure Latin, 48px viewport fits "hello"(40) not full string.
            var rLoose = br.Break(new List<LineBreaker.Item> {
                Item("hello world", lineBreak: "loose")
            }, 48);
            var rNormal = br.Break(new List<LineBreaker.Item> {
                Item("hello world", lineBreak: "normal")
            }, 48);
            var rStrict = br.Break(new List<LineBreaker.Item> {
                Item("hello world", lineBreak: "strict")
            }, 48);

            // All three should produce the same two-line result.
            Assert.That(rLoose.Lines.Count, Is.EqualTo(2),
                "loose: Latin breaks at space");
            Assert.That(rNormal.Lines.Count, Is.EqualTo(2),
                "normal: Latin breaks at space");
            Assert.That(rStrict.Lines.Count, Is.EqualTo(2),
                "strict: Latin breaks at space");
            Assert.That(TextOfLine(rLoose.Lines[0]).Trim(), Is.EqualTo("hello"));
            Assert.That(TextOfLine(rLoose.Lines[1]).Trim(), Is.EqualTo("world"));
        }

        // ======================================================================
        // 5. IsCjkFlowChar includes loose-only characters (so the CJK break
        //    path is entered when content consists of a CJK char + a Group 2-4
        //    char, and the lineBreak value controls the outcome).
        // ======================================================================

        [Test]
        public void IsCjkFlowChar_includes_hyphen_group2() {
            // U+2010 ‐ must be CJK-flow so the break algorithm sees it.
            Assert.That(LineBreakClasses.IsCjkFlowChar(0x2010), Is.True,
                "U+2010 HYPHEN must be in CJK flow (loose-only kinsoku)");
        }

        [Test]
        public void IsCjkFlowChar_includes_iteration_mark_group3() {
            // U+3005 々 must be in CJK flow.
            Assert.That(LineBreakClasses.IsCjkFlowChar(0x3005), Is.True,
                "U+3005 々 must be in CJK flow");
        }

        [Test]
        public void IsCjkFlowChar_includes_double_exclamation_group4() {
            // U+203C ‼ must be in CJK flow.
            Assert.That(LineBreakClasses.IsCjkFlowChar(0x203C), Is.True,
                "U+203C ‼ must be in CJK flow (loose-only kinsoku)");
        }

        [Test]
        public void IsCjkFlowChar_latin_chars_are_not_cjk_flow() {
            Assert.That(LineBreakClasses.IsCjkFlowChar('a'), Is.False);
            Assert.That(LineBreakClasses.IsCjkFlowChar('!'), Is.False);  // ASCII ! (not fullwidth)
            Assert.That(LineBreakClasses.IsCjkFlowChar(' '), Is.False);
        }
    }
}
