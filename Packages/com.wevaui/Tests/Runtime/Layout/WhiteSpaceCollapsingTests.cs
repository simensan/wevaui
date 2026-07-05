// CSS Text Module Level 3 §4 — White-space processing rules
// https://www.w3.org/TR/css-text-3/#white-space-processing
//
// Tests assert the post-collapse / post-segment-break text content emitted by
// the engine's LineBreaker for each `white-space` value.  All tests use
// MonoFontMetrics (8 px/char at 16 px font) with a wide line (9999 px) so
// wrapping does not interfere with collapsing assertions unless explicitly
// testing wrap behaviour.
//
// Spec matrix:
//   value        | collapse | segment-break | wrap
//   -------------|----------|---------------|-----
//   normal       | collapse | collapse      | wrap
//   pre          | preserve | preserve      | no-wrap
//   nowrap       | collapse | collapse      | no-wrap
//   pre-wrap     | preserve | preserve      | wrap
//   pre-line     | collapse | preserve      | wrap
//   break-spaces | preserve | preserve      | wrap  (every space is its own wrap opportunity)
//
// §4.1.1: runs of collapsible whitespace collapse to single U+0020.
// §4.1.1.1: U+00A0 (non-breaking space) is NEVER collapsible.
// §4.1.2: leading/trailing collapsible whitespace at line start/end is removed
//         when wrap+collapse mode (normal); in preserve mode it "hangs".
// §4.1.3: tabs collapse with other whitespace when collapse=collapse;
//         expanded to tab stops when collapse=preserve.
// Inheritance: `white-space` IS inherited per spec (CSS Text L3 §3).
//
// Engine divergence note (A14/A14b/A15) — FIXED:
//   A14: FinishLine now calls TrimTrailingSpace when CollapseTrailingSpace=true
//        (normal/nowrap/pre-line), stripping trailing collapsible spaces from the
//        final line per §4.1.2.
//   A14b: AppendPreserving (pre-line path) now has the leading-space skip guard so
//         a leading collapsed single-space token is dropped at the start of a line.
//   A15: break-spaces routes through AppendPreserving; every space is a soft-wrap
//        opportunity (wraps rather than hanging).

using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Layout.Text;
using Weva.Parsing;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    public class WhiteSpaceCollapsingTests {
        static readonly MonoFontMetrics Mono = new MonoFontMetrics();
        // MonoFontMetrics: Measure(text, fontSize) = text.Length * fontSize * 0.5
        // At fontSize=16: 8 px/char
        const double FontSize = 16;
        // Wide line so no word-wrap occurs in collapsing tests
        const double WideLine = 9999;
        // U+00A0 -- non-breaking space, never collapsible per CSS Text L3 §4.1.1.1
        const char Nbsp = (char)0x00A0;

        // -----------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------

        static LineBreaker.Item Item(string text, string ws = "normal", double tabSize = 8) {
            return new LineBreaker.Item {
                Text = text,
                FontSize = FontSize,
                FontFamily = null,
                Color = "black",
                WhiteSpace = ws,
                TabSizeSpaces = tabSize,
                Metrics = Mono,
            };
        }

        // Collect all text from every line in order; no inter-line separator.
        static string FlatText(LineBreaker.Result r) {
            var sb = new StringBuilder();
            foreach (var line in r.Lines) {
                foreach (var c in line.Children) {
                    if (c is TextRun tr && tr.Text != null) sb.Append(tr.Text);
                }
            }
            return sb.ToString();
        }

        static LineBreaker.Result Break(string text, string ws, double availableWidth = WideLine) {
            var br = new LineBreaker();
            return br.Break(new List<LineBreaker.Item> { Item(text, ws) }, availableWidth);
        }

        static string LineText(LineBox line) {
            var sb = new StringBuilder();
            foreach (var c in line.Children) if (c is TextRun tr && tr.Text != null) sb.Append(tr.Text);
            return sb.ToString();
        }

        // -----------------------------------------------------------------------
        // §4.1.1 -- Internal whitespace collapsing
        // -----------------------------------------------------------------------

        [Test]
        public void Normal_collapses_internal_space_runs_and_newlines_to_single_spaces() {
            // "  a  b\n  c  ": leading spaces stripped (AppendCollapsing skips leading space
            // token when no fragments exist yet); internal runs and \n collapse to one space;
            // CSS Text L3 §4.1.2: trailing collapsible space stripped from the final line.
            var r = Break("  a  b\n  c  ", "normal");
            Assert.That(r.Lines.Count, Is.EqualTo(1), "normal: no forced line breaks from \\n");
            Assert.That(FlatText(r), Is.EqualTo("a b c"),
                "normal collapses internal runs; trailing space stripped on final line per §4.1.2");
        }

        [Test]
        public void Nowrap_collapses_whitespace_same_as_normal() {
            // nowrap: same collapse rules as normal, just no line wrapping
            // CSS Text L3 §4.1.2: trailing collapsible space stripped from the final line.
            var r = Break("  a  b\n  c  ", "nowrap");
            Assert.That(r.Lines.Count, Is.EqualTo(1), "nowrap: must not produce line breaks");
            Assert.That(FlatText(r), Is.EqualTo("a b c"),
                "nowrap collapse identical to normal; trailing space stripped on final line per §4.1.2");
        }

        [Test]
        public void Pre_preserves_all_whitespace_and_breaks_on_newline() {
            // pre: collapse=preserve, segment-break=preserve, wrap=no-wrap
            var r = Break("  a  b\n  c  ", "pre");
            Assert.That(r.Lines.Count, Is.EqualTo(2),
                "pre: \\n must force a hard line break");
            Assert.That(LineText(r.Lines[0]), Is.EqualTo("  a  b"),
                "pre: leading spaces and internal spaces preserved on first line");
            Assert.That(LineText(r.Lines[1]), Is.EqualTo("  c  "),
                "pre: leading and trailing spaces preserved on second line");
        }

        [Test]
        public void Pre_wrap_preserves_all_whitespace_and_breaks_on_newline() {
            // pre-wrap: collapse=preserve, segment-break=preserve, wrap=wrap
            var r = Break("  a  b\n  c  ", "pre-wrap");
            Assert.That(r.Lines.Count, Is.EqualTo(2),
                "pre-wrap: \\n must force a hard line break");
            Assert.That(LineText(r.Lines[0]), Is.EqualTo("  a  b"),
                "pre-wrap: leading and internal spaces preserved on first segment");
            Assert.That(LineText(r.Lines[1]), Is.EqualTo("  c  "),
                "pre-wrap: trailing spaces preserved on second segment");
        }

        [Test]
        public void Pre_line_collapses_spaces_but_preserves_newlines_as_breaks() {
            // pre-line: collapse=collapse, segment-break=preserve, wrap=wrap
            // CSS Text L3 §4.1.2: leading AND trailing collapsed spaces stripped per §4.1.2.
            // A14b fix: AppendPreserving now has the leading-space skip guard for pre-line.
            // A14 fix: FinishLine strips trailing space on each segment line.
            var r = Break("  a  b\n  c  ", "pre-line");
            Assert.That(r.Lines.Count, Is.EqualTo(2),
                "pre-line: \\n must force a hard line break (segment-break=preserve)");
            Assert.That(LineText(r.Lines[0]), Is.EqualTo("a b"),
                "pre-line: leading and trailing collapsed spaces stripped per §4.1.2");
            Assert.That(LineText(r.Lines[1]), Is.EqualTo("c"),
                "pre-line: second segment leading and trailing collapsed spaces stripped");
        }

        // -----------------------------------------------------------------------
        // Spec-correct assertions for §4.1.2 trailing/leading strip (Ignored)
        // -----------------------------------------------------------------------

        [Test]
        public void Spec_normal_strips_trailing_whitespace_on_final_line() {
            // A14 fixed: FinishLine now calls TrimTrailingSpace when CollapseTrailingSpace=true.
            var r = Break("  a  b\n  c  ", "normal");
            Assert.That(FlatText(r), Is.EqualTo("a b c"),
                "CSS Text L3 §4.1.2: trailing collapsible ws must be stripped at end of line");
        }

        [Test]
        public void Spec_pre_line_strips_leading_and_trailing_collapsed_spaces() {
            // A14b fixed: AppendPreserving now has the leading-space skip guard for pre-line.
            // A14 fixed: FinishLine strips trailing collapsed space for pre-line mode too.
            var r = Break("  a  b\n  c  ", "pre-line");
            Assert.That(r.Lines.Count, Is.EqualTo(2));
            Assert.That(LineText(r.Lines[0]), Is.EqualTo("a b"),
                "CSS Text L3 §4.1.2: leading collapsed space must be stripped from pre-line line");
            Assert.That(LineText(r.Lines[1]), Is.EqualTo("c"),
                "CSS Text L3 §4.1.2: trailing collapsed space must be stripped from pre-line line");
        }

        // -----------------------------------------------------------------------
        // §4.1.2 -- Leading whitespace IS stripped at start of a NEW line (wrap break)
        // -----------------------------------------------------------------------

        [Test]
        public void Normal_strips_leading_space_from_wrapped_second_line() {
            // 8px/char; available 16px. "aa bbb": "aa" (16px) fills line 1;
            // then " bbb" -- the leading space must NOT appear on line 2.
            var r = Break("aa bbb", "normal", 16);
            Assert.That(r.Lines.Count, Is.EqualTo(2), "wrap produces two lines");
            Assert.That(LineText(r.Lines[1]), Is.EqualTo("bbb"),
                "normal: leading space before 'bbb' on new wrapped line must be stripped");
        }

        [Test]
        public void Normal_strips_trailing_space_from_non_final_wrapped_line() {
            // "aa   bbb" at 16px: "aa" fills line 1; spaces trimmed before wrap;
            // line 1 text must be "aa" without trailing spaces.
            var r = Break("aa   bbb", "normal", 16);
            Assert.That(r.Lines.Count, Is.EqualTo(2), "wrap produces two lines");
            Assert.That(LineText(r.Lines[0]), Is.EqualTo("aa"),
                "normal: trailing spaces stripped from non-final wrapped line");
        }

        // -----------------------------------------------------------------------
        // §4.1.1 -- Leading whitespace before first word (normal/nowrap)
        // -----------------------------------------------------------------------

        [Test]
        public void Normal_strips_leading_whitespace_before_first_word() {
            // AppendCollapsing skips space token when state.FragStart == count (no fragments)
            var r = Break("   hello", "normal");
            Assert.That(FlatText(r)[0], Is.EqualTo('h'),
                "normal: first character must be 'h' (leading spaces stripped)");
        }

        [Test]
        public void Nowrap_strips_leading_whitespace_before_first_word() {
            var r = Break("   hello", "nowrap");
            Assert.That(FlatText(r)[0], Is.EqualTo('h'),
                "nowrap: first character must be 'h' (leading spaces stripped same as normal)");
        }

        [Test]
        public void Pre_preserves_leading_whitespace() {
            var r = Break("   hello   ", "pre");
            Assert.That(FlatText(r), Is.EqualTo("   hello   "),
                "pre: leading and trailing spaces are preserved");
        }

        [Test]
        public void Pre_wrap_preserves_leading_whitespace() {
            var r = Break("   hello   ", "pre-wrap");
            Assert.That(FlatText(r), Is.EqualTo("   hello   "),
                "pre-wrap: leading and trailing spaces preserved when line does not wrap");
        }

        // -----------------------------------------------------------------------
        // §4.1.3 -- Tab handling per mode
        // -----------------------------------------------------------------------

        [Test]
        public void Normal_collapses_tab_with_other_whitespace() {
            // IsCollapsibleWs includes '\t', so tab is treated as collapsible whitespace
            var r = Break("a\tb", "normal");
            Assert.That(FlatText(r), Is.EqualTo("a b"),
                "normal: tab collapses into a single space between words");
        }

        [Test]
        public void Nowrap_collapses_tab_same_as_normal() {
            var r = Break("a\tb", "nowrap");
            Assert.That(FlatText(r), Is.EqualTo("a b"),
                "nowrap: tab collapses with other whitespace same as normal");
        }

        [Test]
        public void Pre_preserves_tab_as_tab_stop_expansion() {
            // NormalizePreservedText expands tabs to tab stops.
            // tab-size=8 spaces, 8 px/char. "a\tb":
            //   cursor at 0; "a" -> cursor=8px; tab: next stop at 64px -> delta=56px = 7 spaces
            //   result: "a       b" (7-space gap)
            var r = Break("a\tb", "pre");
            Assert.That(FlatText(r), Is.EqualTo("a       b"),
                "pre: tab expands to fill to next tab stop (8-space default at 8px/char)");
        }

        [Test]
        public void Pre_wrap_expands_tab_to_tab_stop() {
            var r = Break("a\tb", "pre-wrap");
            Assert.That(FlatText(r), Is.EqualTo("a       b"),
                "pre-wrap: tab expanded to tab stop same as pre");
        }

        [Test]
        public void Pre_line_collapses_tab_with_other_whitespace() {
            // CollapseSpacesPreserveSpaces: only ' ' and '\t' are collapsed, not '\n'
            var r = Break("a\tb", "pre-line");
            Assert.That(FlatText(r), Is.EqualTo("a b"),
                "pre-line: tab collapses to single space (space-collapse applies to tabs too)");
        }

        // -----------------------------------------------------------------------
        // §4.1.1.1 -- U+00A0 (non-breaking space) is NEVER collapsed
        // -----------------------------------------------------------------------

        [Test]
        public void Normal_nbsp_is_not_treated_as_collapsible_whitespace() {
            // IsCollapsibleWs does NOT include U+00A0.
            // Tokenizer sees NBSP as a word character (not whitespace).
            // Input "a " + NBSP + " b":
            //   tokens: space, word("a"), space, word(NBSP), space, word("b")
            //   leading space skipped; "a" added; space added; NBSP (word) added;
            //   space added; "b" added.  ENGINE trailing space omitted (no trailing ws here).
            string input = "a " + Nbsp + " b";
            var r = Break(input, "normal");
            string text = FlatText(r);
            Assert.That(text.Contains(Nbsp), Is.True,
                "§4.1.1.1: U+00A0 must NOT be collapsed even in normal mode");
            Assert.That(text, Is.EqualTo("a " + Nbsp + " b"),
                "normal: surrounding ASCII spaces collapse to single spaces; NBSP survives");
        }

        [Test]
        public void Nowrap_nbsp_survives_collapsing() {
            string input = "a " + Nbsp + " b";
            var r = Break(input, "nowrap");
            Assert.That(FlatText(r).Contains(Nbsp), Is.True,
                "§4.1.1.1: U+00A0 must not be collapsed in nowrap mode");
        }

        [Test]
        public void Pre_line_nbsp_survives_space_collapsing() {
            // CollapseSpacesPreserveSpaces only collapses ' ' and '\t'; U+00A0 passes through
            string input = Nbsp.ToString() + "text";
            var r = Break(input, "pre-line");
            string text = FlatText(r);
            Assert.That(text[0], Is.EqualTo(Nbsp),
                "§4.1.1.1: U+00A0 must survive pre-line space collapsing");
        }

        [Test]
        public void Multiple_consecutive_nbsp_all_survive() {
            // Three consecutive NBSPs with no ASCII whitespace around them
            string input = "a" + Nbsp + Nbsp + Nbsp + "b";
            var r = Break(input, "normal");
            Assert.That(FlatText(r), Is.EqualTo("a" + Nbsp + Nbsp + Nbsp + "b"),
                "Three consecutive U+00A0 chars must all survive; nothing to collapse");
        }

        // -----------------------------------------------------------------------
        // Segment-break (newline) -- collapse vs preserve
        // -----------------------------------------------------------------------

        [Test]
        public void Normal_collapses_newline_to_single_space() {
            // '\n' is in IsCollapsibleWs, so it is treated as whitespace in collapse mode
            var r = Break("a\nb", "normal");
            Assert.That(r.Lines.Count, Is.EqualTo(1),
                "normal: \\n must NOT produce a forced line break");
            Assert.That(FlatText(r), Is.EqualTo("a b"),
                "normal: \\n collapses to a single space");
        }

        [Test]
        public void Nowrap_collapses_newline_to_space() {
            var r = Break("a\nb", "nowrap");
            Assert.That(r.Lines.Count, Is.EqualTo(1),
                "nowrap: \\n must NOT produce a forced line break");
            Assert.That(FlatText(r), Is.EqualTo("a b"),
                "nowrap: \\n collapses to single space (same as normal)");
        }

        [Test]
        public void Pre_newline_forces_a_line_break() {
            var r = Break("a\nb", "pre");
            Assert.That(r.Lines.Count, Is.EqualTo(2),
                "pre: \\n must force a hard line break (segment-break=preserve)");
        }

        [Test]
        public void Pre_wrap_newline_forces_a_line_break() {
            var r = Break("a\nb", "pre-wrap");
            Assert.That(r.Lines.Count, Is.EqualTo(2),
                "pre-wrap: \\n must force a hard line break (segment-break=preserve)");
        }

        [Test]
        public void Pre_line_newline_forces_a_line_break() {
            var r = Break("a\nb", "pre-line");
            Assert.That(r.Lines.Count, Is.EqualTo(2),
                "pre-line: \\n must force a hard line break (segment-break=preserve)");
        }

        // -----------------------------------------------------------------------
        // Wrap behaviour -- normal vs nowrap vs pre vs pre-wrap vs pre-line
        // -----------------------------------------------------------------------

        [Test]
        public void Normal_wraps_when_content_overflows() {
            // "hello world" at 8px/char = 88px; available 64px -> wrap after "hello"
            var r = Break("hello world", "normal", 64);
            Assert.That(r.Lines.Count, Is.EqualTo(2),
                "normal: content wraps when it overflows the available width");
        }

        [Test]
        public void Nowrap_does_not_wrap_even_when_content_overflows() {
            var r = Break("hello world", "nowrap", 64);
            Assert.That(r.Lines.Count, Is.EqualTo(1),
                "nowrap: no line wrapping regardless of overflow");
            Assert.That(r.Lines[0].Width, Is.GreaterThan(64),
                "nowrap: line width can exceed container width");
        }

        [Test]
        public void Pre_does_not_wrap_long_line() {
            var r = Break("aaaaaaaaaaaaaaaa", "pre", 64);
            Assert.That(r.Lines.Count, Is.EqualTo(1),
                "pre: long lines must not be wrapped (no-wrap mode)");
        }

        [Test]
        public void Pre_wrap_wraps_when_content_overflows() {
            var r = Break("hello world", "pre-wrap", 64);
            Assert.That(r.Lines.Count, Is.GreaterThanOrEqualTo(2),
                "pre-wrap: long content must wrap (wrap=wrap mode)");
        }

        [Test]
        public void Pre_line_wraps_when_content_overflows() {
            var r = Break("hello world", "pre-line", 64);
            Assert.That(r.Lines.Count, Is.GreaterThanOrEqualTo(2),
                "pre-line: content wraps on overflow (wrap=wrap mode)");
        }

        // -----------------------------------------------------------------------
        // Cascade: `white-space` IS inherited per CSS Text L3 §3
        // -----------------------------------------------------------------------

        [Test]
        public void White_space_is_inherited_by_child_elements() {
            // Parent sets white-space:pre; child <span> must inherit it.
            var doc = HtmlParser.Parse(
                "<div style=\"white-space:pre\"><span id=\"s\">hello</span></div>");
            var sheets = new List<OriginatedStylesheet> {
                OriginatedStylesheet.UserAgent(CssParser.Parse(BuiltinUserAgent))
            };
            var engine = new CascadeEngine(sheets, true);
            var computedMap = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engine.ComputeAll(doc)) computedMap[kv.Key] = kv.Value;
            Element spanEl = null;
            foreach (var kv in computedMap) {
                if (kv.Key.TagName == "span") { spanEl = kv.Key; break; }
            }
            Assert.That(spanEl, Is.Not.Null, "span element must appear in computed styles");
            string ws = computedMap[spanEl].Get(CssProperties.WhiteSpaceId);
            Assert.That(ws, Is.EqualTo("pre"),
                "white-space must be inherited: span inside pre-parent receives ws=pre");
        }

        [Test]
        public void White_space_initial_value_is_normal() {
            // CSS Text L3 §3: initial value of white-space is `normal`
            var doc = HtmlParser.Parse("<p>text</p>");
            var sheets = new List<OriginatedStylesheet> {
                OriginatedStylesheet.UserAgent(CssParser.Parse(BuiltinUserAgent))
            };
            var engine = new CascadeEngine(sheets, true);
            var computedMap = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engine.ComputeAll(doc)) computedMap[kv.Key] = kv.Value;
            Element pEl = null;
            foreach (var kv in computedMap) {
                if (kv.Key.TagName == "p") { pEl = kv.Key; break; }
            }
            Assert.That(pEl, Is.Not.Null, "p element must appear in computed styles");
            string ws = computedMap[pEl].Get(CssProperties.WhiteSpaceId);
            Assert.That(ws, Is.EqualTo("normal"),
                "white-space initial value must be 'normal' per CSS Text L3 §3");
        }

        [Test]
        public void White_space_child_explicit_overrides_inherited_value() {
            // Parent: pre; span: nowrap (explicit override must win over inheritance)
            var doc = HtmlParser.Parse(
                "<div style=\"white-space:pre\"><span style=\"white-space:nowrap\">text</span></div>");
            var sheets = new List<OriginatedStylesheet> {
                OriginatedStylesheet.UserAgent(CssParser.Parse(BuiltinUserAgent))
            };
            var engine = new CascadeEngine(sheets, true);
            var computedMap = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engine.ComputeAll(doc)) computedMap[kv.Key] = kv.Value;
            Element spanEl = null;
            foreach (var kv in computedMap) {
                if (kv.Key.TagName == "span") { spanEl = kv.Key; break; }
            }
            Assert.That(spanEl, Is.Not.Null);
            string ws = computedMap[spanEl].Get(CssProperties.WhiteSpaceId);
            Assert.That(ws, Is.EqualTo("nowrap"),
                "explicit white-space:nowrap on child must override inherited pre");
        }

        // -----------------------------------------------------------------------
        // break-spaces: engine gap -- engine falls into AppendCollapsing (Ignored)
        // -----------------------------------------------------------------------

        [Test]
        public void Break_spaces_preserves_spaces_and_forces_break_on_newline() {
            // A15 fixed: break-spaces now routes through AppendPreserving.
            // With wide line (9999px), no wrapping occurs — all spaces are preserved.
            var r = Break("  a  b\n  c  ", "break-spaces");
            Assert.That(r.Lines.Count, Is.EqualTo(2),
                "break-spaces: \\n must force a hard line break");
            Assert.That(LineText(r.Lines[0]), Is.EqualTo("  a  b"),
                "break-spaces: leading + internal spaces preserved on first line");
            Assert.That(LineText(r.Lines[1]), Is.EqualTo("  c  "),
                "break-spaces: leading + trailing spaces preserved on second line");
        }

        [Test]
        public void Break_spaces_every_space_is_a_wrap_opportunity() {
            // A15 fixed: every preserved space is a wrap opportunity in break-spaces.
            // "a b" at 16px (2 chars = 16px): "a" (8px) fits line; after "a" the space
            // wraps — space goes on line 1 (hang/wrap) and "b" starts line 2.
            // At 16px available: "a" = 8px fits; then space triggers wrap; "b" is line 2.
            var r = Break("a b", "break-spaces", 16);
            Assert.That(r.Lines.Count, Is.EqualTo(2),
                "break-spaces: space at EOL wraps to next line instead of being stripped");
        }

        // -----------------------------------------------------------------------
        // Extra edge-case tests (A14 / A14b / A15 regressions)
        // -----------------------------------------------------------------------

        [Test]
        public void Normal_single_trailing_space_on_single_line_stripped() {
            // Single-line input — no wrap, one trailing space.
            // A14: FinishLine(finalLine:true) must strip trailing collapsed space.
            var r = Break("hello ", "normal");
            Assert.That(r.Lines.Count, Is.EqualTo(1), "no wrap on wide line");
            Assert.That(FlatText(r), Is.EqualTo("hello"),
                "trailing space must be stripped from final line in normal mode");
        }

        [Test]
        public void Nowrap_single_trailing_space_stripped_on_final_line() {
            // Regression anchor: nowrap must also strip the trailing space.
            var r = Break("hello ", "nowrap");
            Assert.That(r.Lines.Count, Is.EqualTo(1), "nowrap: no wrap");
            Assert.That(FlatText(r), Is.EqualTo("hello"),
                "trailing space stripped in nowrap mode (collapse=collapse)");
        }

        [Test]
        public void Pre_line_leading_spaces_after_newline_stripped() {
            // A14b regression: a segment starting with spaces (collapsed to one)
            // should have that leading space stripped at the start of its line.
            // "a\n   b" — second segment "   b" collapses to " b"; leading " " stripped.
            var r = Break("a\n   b", "pre-line");
            Assert.That(r.Lines.Count, Is.EqualTo(2), "\\n forces break");
            Assert.That(LineText(r.Lines[1]), Is.EqualTo("b"),
                "pre-line: leading collapsed space stripped at start of new segment line");
        }

        [Test]
        public void Pre_line_only_spaces_on_segment_produces_empty_line() {
            // "a\n   \nb" — middle segment is all spaces; collapses to " "; stripped → empty.
            // The forced breaks still produce 3 lines; the middle line has no fragments.
            var r = Break("a\n   \nb", "pre-line");
            Assert.That(r.Lines.Count, Is.EqualTo(3), "two \\n produce three segment lines");
            Assert.That(LineText(r.Lines[1]), Is.EqualTo(""),
                "pre-line: all-space segment collapses to nothing on its line");
        }

        [Test]
        public void Break_spaces_consecutive_spaces_wrap_when_overflow() {
            // "a   b" at 24px: "a" (8px) + " "(8px) = 16, " "(8px) = 24, " "(8px) = 32 > 24.
            // With break-spaces every space is a wrap opportunity; spaces wrap at boundary.
            var r = Break("a   b", "break-spaces", 24);
            // Line 1 contains "a" and at least one space; b ends up on a later line.
            Assert.That(r.Lines.Count, Is.GreaterThanOrEqualTo(2),
                "break-spaces: consecutive spaces at EOL trigger wrapping when overflow");
            // The word "b" must appear on the last line.
            string lastLineText = LineText(r.Lines[r.Lines.Count - 1]);
            Assert.That(lastLineText, Does.Contain("b"),
                "break-spaces: non-space content after wrapping spaces must be on a new line");
        }

        [Test]
        public void Break_spaces_preserves_wide_line_no_wrap() {
            // Sanity: on a very wide line, break-spaces should preserve all spaces with
            // no wrapping (only \n segments break lines).
            var r = Break("a b c", "break-spaces", 9999);
            Assert.That(r.Lines.Count, Is.EqualTo(1), "no wrapping when everything fits");
            Assert.That(FlatText(r), Is.EqualTo("a b c"),
                "break-spaces on wide line: all spaces preserved, no extra line breaks");
        }
    }
}