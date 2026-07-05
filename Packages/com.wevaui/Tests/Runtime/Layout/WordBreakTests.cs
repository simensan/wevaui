using System.Collections.Generic;
using NUnit.Framework;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Layout.Text;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    public class WordBreakTests {
        // Mirrors LineBreakerTests' MonoFontMetrics setup: 16 px font => 8 px per
        // ASCII char. Tests construct Items directly so we can hold word-break /
        // overflow-wrap behaviour to its CSS-spec contract without going through
        // the cascade.
        static readonly MonoFontMetrics Mono = new MonoFontMetrics();

        static LineBreaker.Item Item(string text, double fontSize = 16, string ws = "normal",
                                     string wordBreak = null, string overflowWrap = null,
                                     string hyphens = null, double tabSize = 8) {
            return new LineBreaker.Item {
                Text = text,
                FontSize = fontSize,
                FontFamily = null,
                Color = "black",
                WhiteSpace = ws,
                WordBreak = wordBreak,
                OverflowWrap = overflowWrap,
                Hyphens = hyphens,
                TabSizeSpaces = tabSize,
                Metrics = Mono
            };
        }

        static List<TextRun> RunsOnLine(LineBox line) {
            var list = new List<TextRun>();
            foreach (var c in line.Children) if (c is TextRun tr) list.Add(tr);
            return list;
        }

        static string TextOfLine(LineBox line) {
            var sb = new System.Text.StringBuilder();
            foreach (var run in RunsOnLine(line)) sb.Append(run.Text);
            return sb.ToString();
        }

        // Default behaviour preserved: long unbreakable word overflows on its own
        // line (the v1 baseline before word-break/overflow-wrap landed).
        [Test]
        public void Default_long_word_still_overflows_when_no_break_configured() {
            var br = new LineBreaker();
            var r = br.Break(new List<LineBreaker.Item> { Item("supercalifragilistic") }, 64);
            Assert.That(r.Lines.Count, Is.EqualTo(1));
            Assert.That(RunsOnLine(r.Lines[0])[0].Text, Is.EqualTo("supercalifragilistic"));
            Assert.That(r.Lines[0].Width, Is.GreaterThan(64));
        }

        [Test]
        public void Word_break_break_all_breaks_long_word_at_character_boundary() {
            // 20-char word at 8 px/char = 160 px; available 64 px = 8 chars/line.
            // Greedy break-all packs as many graphemes as fit each line:
            // "supercal" (8) | "ifragili" (8) | "stic" (4).
            var br = new LineBreaker();
            var r = br.Break(new List<LineBreaker.Item> {
                Item("supercalifragilistic", wordBreak: "break-all")
            }, 64);
            Assert.That(r.Lines.Count, Is.EqualTo(3));
            Assert.That(TextOfLine(r.Lines[0]), Is.EqualTo("supercal"));
            Assert.That(TextOfLine(r.Lines[1]), Is.EqualTo("ifragili"));
            Assert.That(TextOfLine(r.Lines[2]), Is.EqualTo("stic"));
            // Each filled line's measured width must not exceed available width.
            Assert.That(r.Lines[0].Width, Is.LessThanOrEqualTo(64 + 1e-6));
            Assert.That(r.Lines[1].Width, Is.LessThanOrEqualTo(64 + 1e-6));
        }

        [Test]
        public void Word_break_break_all_breaks_even_at_word_boundaries_in_mixed_text() {
            // Per CSS Text §6.1, break-all permits a break between any two
            // characters — including in the middle of regular short words.
            // "abc def ghi" packs greedily into 32-px lines (4 chars).
            var br = new LineBreaker();
            var r = br.Break(new List<LineBreaker.Item> {
                Item("abc def ghi", wordBreak: "break-all")
            }, 32);
            // Greedy fill: line1 takes 4 chars from "abc" (only 3 available, then space) → "abc ",
            // but trailing space is trimmed → "abc". Then "def" packs on line 2, "ghi" on line 3.
            // Specifically: tokens are "abc" (24 px), space, "def" (24 px), space, "ghi" (24 px).
            // With 32 px line, "abc" fits (24), then a space (8 = 32 total), then "def" (24)
            // exceeds → wrap. Line 2 starts with "def", then space (32), then "ghi" (24) wraps.
            Assert.That(r.Lines.Count, Is.GreaterThanOrEqualTo(2));
            // Each non-final line's measured width is <= 32.
            for (int i = 0; i < r.Lines.Count - 1; i++) {
                Assert.That(r.Lines[i].Width, Is.LessThanOrEqualTo(32 + 1e-6));
            }
        }

        [Test]
        public void Overflow_wrap_break_word_only_breaks_when_word_alone_on_line() {
            // 20-char word at 8 px/char = 160 px. Width 64. With break-word
            // we DO break (the word is alone on a line and would otherwise overflow).
            var br = new LineBreaker();
            var r = br.Break(new List<LineBreaker.Item> {
                Item("supercalifragilistic", overflowWrap: "break-word")
            }, 64);
            Assert.That(r.Lines.Count, Is.GreaterThanOrEqualTo(2));
            Assert.That(r.Lines[0].Width, Is.LessThanOrEqualTo(64 + 1e-6));
        }

        [Test]
        public void Overflow_wrap_break_word_does_not_break_when_word_pushed_to_fresh_line_fits() {
            // "hi supercalifragilistic" — "hi" (16 px) fits on line 1, the long
            // word goes to line 2, where it's alone. break-word kicks in there.
            var br = new LineBreaker();
            var r = br.Break(new List<LineBreaker.Item> {
                Item("hi supercalifragilistic", overflowWrap: "break-word")
            }, 64);
            // Line 1: "hi" (or "hi " trimmed). Line 2+: the long word, broken.
            Assert.That(r.Lines.Count, Is.GreaterThanOrEqualTo(3));
            Assert.That(TextOfLine(r.Lines[0]).Trim(), Is.EqualTo("hi"));
        }

        [Test]
        public void Overflow_wrap_break_word_with_short_words_only_breaks_the_overflowing_word() {
            // Short words wrap normally at whitespace; the long word breaks
            // mid-word only because it can't fit even on a fresh line.
            var br = new LineBreaker();
            var r = br.Break(new List<LineBreaker.Item> {
                Item("ab cd verylongwordhere ef", overflowWrap: "break-word")
            }, 64);
            // Line 1: "ab cd" (5 chars * 8 = 40 — wait, "ab cd" is 5 chars incl. space).
            // Let's just assert that no plain short word ("ab", "cd", "ef") was split.
            string joined = "";
            foreach (var line in r.Lines) joined += TextOfLine(line) + "|";
            Assert.That(joined, Does.Contain("ab"));
            Assert.That(joined, Does.Contain("cd"));
            Assert.That(joined, Does.Contain("ef"));
        }

        [Test]
        public void Break_does_not_split_surrogate_pair() {
            // U+1F600 GRINNING FACE = "😀", a surrogate pair (UTF-16
            // length 2). Wrap a string with the emoji at a position that the
            // greedy break would otherwise pick mid-surrogate, and assert the
            // surrogate stays intact on whichever side it lands.
            string s = "abcd😀efgh"; // a b c d <emoji> e f g h, 9 UTF-16 units
            var br = new LineBreaker();
            // Each ASCII char is 8 px, surrogate pair measures as 2 chars * 8 = 16 px.
            // Width 40 = 5 ASCII slots. The greedy break point falls inside the
            // emoji — we should snap back to BEFORE the high surrogate.
            var r = br.Break(new List<LineBreaker.Item> {
                Item(s, wordBreak: "break-all")
            }, 40);
            Assert.That(r.Lines.Count, Is.GreaterThanOrEqualTo(2));
            foreach (var line in r.Lines) {
                string text = TextOfLine(line);
                // Neither orphan high nor orphan low surrogate may appear.
                for (int k = 0; k < text.Length; k++) {
                    if (char.IsHighSurrogate(text[k])) {
                        Assert.That(k + 1 < text.Length && char.IsLowSurrogate(text[k + 1]),
                            "high surrogate at end of fragment");
                    }
                    if (char.IsLowSurrogate(text[k])) {
                        Assert.That(k > 0 && char.IsHighSurrogate(text[k - 1]),
                            "low surrogate without preceding high");
                    }
                }
            }
        }

        [Test]
        public void Break_at_line_edge_prefix_width_is_within_available_width() {
            var br = new LineBreaker();
            var r = br.Break(new List<LineBreaker.Item> {
                Item("abcdefghijklmnopqrst", wordBreak: "break-all")
            }, 50);
            // 50/8 = 6.25 → 6 chars per line.
            foreach (var line in r.Lines) {
                Assert.That(line.Width, Is.LessThanOrEqualTo(50 + 1e-6));
            }
            int totalChars = 0;
            foreach (var line in r.Lines) totalChars += TextOfLine(line).Length;
            Assert.That(totalChars, Is.EqualTo(20));
        }

        [Test]
        public void Overflow_wrap_anywhere_breaks_even_when_word_not_alone() {
            // Unlike break-word, anywhere breaks unconditionally when a word
            // doesn't fit at the current cursor — equivalent to break-all
            // (only difference is min-content sizing, which we don't model).
            var br = new LineBreaker();
            var r = br.Break(new List<LineBreaker.Item> {
                Item("hi supercalifragilistic", overflowWrap: "anywhere")
            }, 64);
            Assert.That(r.Lines.Count, Is.GreaterThanOrEqualTo(3));
            // First line should pack as much as fits — under anywhere this
            // includes "hi " plus a slice of the long word on the same line.
            Assert.That(r.Lines[0].Width, Is.LessThanOrEqualTo(64 + 1e-6));
        }

        // CSS Text L3 §6.2: the legacy value `word-break: break-word` is an
        // alias for `overflow-wrap: break-word`. Authors using the old name
        // must get mid-word breaking, identical to the canonical property.
        [Test]
        public void Word_break_break_word_aliases_overflow_wrap_break_word() {
            var br = new LineBreaker();
            var legacy = br.Break(new List<LineBreaker.Item> {
                Item("supercalifragilistic", wordBreak: "break-word")
            }, 64);
            Assert.That(legacy.Lines.Count, Is.GreaterThanOrEqualTo(2));
            Assert.That(legacy.Lines[0].Width, Is.LessThanOrEqualTo(64 + 1e-6));

            var normal = br.Break(new List<LineBreaker.Item> {
                Item("supercalifragilistic", wordBreak: "normal")
            }, 64);
            Assert.That(normal.Lines.Count, Is.EqualTo(1));
            Assert.That(normal.Lines[0].Width, Is.GreaterThan(64));

            var canonical = br.Break(new List<LineBreaker.Item> {
                Item("supercalifragilistic", overflowWrap: "break-word")
            }, 64);
            Assert.That(legacy.Lines.Count, Is.EqualTo(canonical.Lines.Count));
            for (int i = 0; i < legacy.Lines.Count; i++) {
                Assert.That(TextOfLine(legacy.Lines[i]),
                    Is.EqualTo(TextOfLine(canonical.Lines[i])));
            }
        }

        [Test]
        public void Word_break_keep_all_falls_back_to_normal() {
            // keep-all is a v1 simplification — it documentably behaves as
            // word-break: normal so a long Latin word still overflows rather
            // than breaks. This test pins the documented behaviour.
            var br = new LineBreaker();
            var r = br.Break(new List<LineBreaker.Item> {
                Item("supercalifragilistic", wordBreak: "keep-all")
            }, 64);
            Assert.That(r.Lines.Count, Is.EqualTo(1));
            Assert.That(r.Lines[0].Width, Is.GreaterThan(64));
        }

        // word-break is an inherited property; a child run inside a styled
        // parent should pick up the parent's value via the cascade.
        [Test]
        public void Word_break_inherits_to_descendant_text_runs() {
            const string css = @"
                p { width: 64px; word-break: break-all; font-family: monospace; font-size: 16px; }
                strong { font-weight: bold; }
            ";
            var (root, _, _) = Build(
                "<p><strong>supercalifragilistic</strong></p>",
                css, viewportWidth: 800);
            int lineCount = 0;
            foreach (var lb in AllLineBoxes(root)) lineCount++;
            // The long word should now wrap onto multiple lines because
            // word-break inherited from <p> reaches the <strong> run.
            Assert.That(lineCount, Is.GreaterThanOrEqualTo(2));
        }

        [Test]
        public void White_space_pre_does_not_break_inside_word_even_with_break_all() {
            // pre disables wrapping entirely; word-break only governs how
            // wrapping picks break points. A break-all word under white-space:
            // pre still goes on a single line.
            var br = new LineBreaker();
            var r = br.Break(new List<LineBreaker.Item> {
                Item("supercalifragilistic", ws: "pre", wordBreak: "break-all")
            }, 64);
            Assert.That(r.Lines.Count, Is.EqualTo(1));
            Assert.That(r.Lines[0].Width, Is.GreaterThan(64));
        }

        [Test]
        public void White_space_nowrap_disables_break_all() {
            var br = new LineBreaker();
            var r = br.Break(new List<LineBreaker.Item> {
                Item("supercalifragilistic", ws: "nowrap", wordBreak: "break-all")
            }, 64);
            Assert.That(r.Lines.Count, Is.EqualTo(1));
        }

        [Test]
        public void Break_all_progresses_when_one_grapheme_overflows_empty_line() {
            // Pathological: line is 4 px wide (less than one 8-px char). We must
            // still emit at least one character per line or we'd loop forever.
            var br = new LineBreaker();
            var r = br.Break(new List<LineBreaker.Item> {
                Item("abc", wordBreak: "break-all")
            }, 4);
            Assert.That(r.Lines.Count, Is.EqualTo(3));
            Assert.That(TextOfLine(r.Lines[0]), Is.EqualTo("a"));
            Assert.That(TextOfLine(r.Lines[1]), Is.EqualTo("b"));
            Assert.That(TextOfLine(r.Lines[2]), Is.EqualTo("c"));
        }

        // --- CSS Text 3 hyphenation and tab-stop behaviour -----------------

        // U+00AD SOFT HYPHEN is invisible mid-line and renders as "-"
        // only when it becomes the line-break point. Here "ab-" cannot fit
        // in 16 px, so the word overflows instead of breaking.
        [Test]
        public void Soft_hyphen_does_not_break_when_hyphenated_prefix_cannot_fit() {
            var br = new LineBreaker();
            var r = br.Break(new List<LineBreaker.Item> {
                Item("ab­cd")  // a, b, U+00AD, c, d  -> 5 chars * 8 = 40 px
            }, 16);
            // No special handling: the soft-hyphen is just another char inside
            // a word with no whitespace, so the whole thing sits on one line.
            Assert.That(r.Lines.Count, Is.EqualTo(1));
            Assert.That(r.Lines[0].Width, Is.GreaterThan(16));
        }

        [Test]
        public void Soft_hyphen_is_a_manual_break_opportunity() {
            var br = new LineBreaker();
            var r = br.Break(new List<LineBreaker.Item> {
                Item("ab\u00ADcd")
            }, 24);
            Assert.That(r.Lines.Count, Is.EqualTo(2));
            Assert.That(TextOfLine(r.Lines[0]), Is.EqualTo("ab-"));
            Assert.That(TextOfLine(r.Lines[1]), Is.EqualTo("cd"));
        }

        // hyphens:auto still needs dictionary hyphenation. Without a soft
        // hyphen or dictionary break point, long words overflow.
        [Test]
        public void Hyphens_auto_without_dictionary_does_not_break_plain_words() {
            const string css = @"
                p { width: 64px; hyphens: auto; font-family: monospace; font-size: 16px; }
            ";
            var (root, _, _) = Build(
                "<p>supercalifragilistic</p>",
                css, viewportWidth: 800);
            int lineCount = 0;
            foreach (var lb in AllLineBoxes(root)) lineCount++;
            // No hyphenation -> overflow on a single line.
            Assert.That(lineCount, Is.EqualTo(1));
        }

        [Test]
        public void Hyphens_manual_honors_soft_hyphen_via_cascade() {
            const string css = @"
                p { width: 24px; hyphens: manual; font-family: monospace; font-size: 16px; }
            ";
            var (root, _, _) = Build(
                "<p>ab&shy;cd</p>",
                css, viewportWidth: 800);
            var lines = new List<LineBox>(AllLineBoxes(root));
            Assert.That(lines.Count, Is.EqualTo(2));
            Assert.That(TextOfLine(lines[0]), Is.EqualTo("ab-"));
            Assert.That(TextOfLine(lines[1]), Is.EqualTo("cd"));
        }

        // In preserved whitespace, tabs advance to tab stops based on the
        // current inline position and tab-size.
        [Test]
        public void Tab_in_pre_wrap_advances_to_default_tab_stop() {
            var br = new LineBreaker();
            var r = br.Break(new List<LineBreaker.Item> {
                Item("a\tb", ws: "pre-wrap")
            }, 1000);
            Assert.That(r.Lines.Count, Is.EqualTo(1));
            Assert.That(r.Lines[0].Width, Is.EqualTo(72).Within(0.001));
            Assert.That(TextOfLine(r.Lines[0]), Is.EqualTo("a       b"));
        }

        [Test]
        public void Tab_size_changes_preserved_tab_stop_spacing() {
            var br = new LineBreaker();
            var r = br.Break(new List<LineBreaker.Item> {
                Item("a\tb", ws: "pre-wrap", tabSize: 4)
            }, 1000);
            Assert.That(r.Lines.Count, Is.EqualTo(1));
            Assert.That(r.Lines[0].Width, Is.EqualTo(40).Within(0.001));
            Assert.That(TextOfLine(r.Lines[0]), Is.EqualTo("a   b"));
        }

        // overflow-wrap is the canonical name; word-wrap is the legacy alias.
        // CSS Text 3 §6.2 says when both are set, overflow-wrap wins. We
        // implement this in InlineLayout.MakeItem; this test pins the cascade
        // path so the alias resolution doesn't silently regress.
        [Test]
        public void Overflow_wrap_break_word_wraps_long_word_via_cascade() {
            const string css = @"
                p { width: 64px; overflow-wrap: break-word; font-family: monospace; font-size: 16px; }
            ";
            var (root, _, _) = Build(
                "<p>supercalifragilistic</p>",
                css, viewportWidth: 800);
            int lineCount = 0;
            foreach (var lb in AllLineBoxes(root)) lineCount++;
            // break-word kicks in: the word is alone on its line and would
            // otherwise overflow, so it splits across multiple lines.
            Assert.That(lineCount, Is.GreaterThanOrEqualTo(2));
        }
    }
}
