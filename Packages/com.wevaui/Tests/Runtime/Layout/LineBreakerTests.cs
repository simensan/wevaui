using System.Collections.Generic;
using NUnit.Framework;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Layout.Text;
using Weva.Paint;

namespace Weva.Tests.Layout {
    public class LineBreakerTests {
        static readonly MonoFontMetrics Mono = new MonoFontMetrics();

        sealed class StyledMetrics : IStyledFontMetrics {
            public double LineHeight(double fontSize) => fontSize;
            public double Measure(string text, double fontSize) => (text?.Length ?? 0) * 10;
            public double Ascent(double fontSize) => fontSize * 0.8;
            public double Descent(double fontSize) => fontSize * 0.2;
            public double Measure(string text, double fontSize, string family, FontStyle style, int weight) {
                return (text?.Length ?? 0) * (weight >= 700 ? 14 : 10);
            }
            public double Measure(string text, int start, int length, double fontSize) {
                if (string.IsNullOrEmpty(text) || length <= 0) return 0;
                if (start < 0) { length += start; start = 0; }
                if (start >= text.Length) return 0;
                if (start + length > text.Length) length = text.Length - start;
                return length * 10;
            }
            public double Measure(string text, int start, int length, double fontSize, string family, FontStyle style, int weight) {
                if (string.IsNullOrEmpty(text) || length <= 0) return 0;
                if (start < 0) { length += start; start = 0; }
                if (start >= text.Length) return 0;
                if (start + length > text.Length) length = text.Length - start;
                return length * (weight >= 700 ? 14 : 10);
            }
            public double LineHeight(double fontSize, string family, FontStyle style, int weight) => fontSize;
            public double Ascent(double fontSize, string family, FontStyle style, int weight) => fontSize * 0.8;
            public double Descent(double fontSize, string family, FontStyle style, int weight) => fontSize * 0.2;
        }

        static LineBreaker.Item Item(string text, double fontSize = 16, string ws = "normal") {
            return new LineBreaker.Item {
                Text = text,
                FontSize = fontSize,
                FontFamily = null,
                Color = "black",
                WhiteSpace = ws,
                Metrics = Mono
            };
        }

        static List<TextRun> RunsOnLine(LineBox line) {
            var list = new List<TextRun>();
            foreach (var c in line.Children) if (c is TextRun tr) list.Add(tr);
            return list;
        }

        // 16px mono => char width = 0.5 * 16 = 8px

        [Test]
        public void Single_short_text_fits_one_line() {
            var br = new LineBreaker();
            var r = br.Break(new List<LineBreaker.Item> { Item("hello") }, 1000);
            Assert.That(r.Lines.Count, Is.EqualTo(1));
            Assert.That(RunsOnLine(r.Lines[0]).Count, Is.EqualTo(1));
            Assert.That(RunsOnLine(r.Lines[0])[0].Text, Is.EqualTo("hello"));
        }

        [Test]
        public void Styled_metrics_receive_font_weight_when_measuring() {
            var br = new LineBreaker();
            var item = Item("abc");
            item.Metrics = new StyledMetrics();
            item.FontWeight = 700;
            var r = br.Break(new List<LineBreaker.Item> { item }, 1000);
            Assert.That(r.Lines[0].Width, Is.EqualTo(42).Within(0.001));
        }

        [Test]
        public void Wraps_at_whitespace_boundary() {
            // "hello world" = 11 chars * 8 = 88px. Width 64 forces "hello" then "world".
            var br = new LineBreaker();
            var r = br.Break(new List<LineBreaker.Item> { Item("hello world") }, 64);
            Assert.That(r.Lines.Count, Is.EqualTo(2));
            Assert.That(RunsOnLine(r.Lines[0])[0].Text, Is.EqualTo("hello"));
            Assert.That(RunsOnLine(r.Lines[1])[0].Text, Is.EqualTo("world"));
        }

        [Test]
        public void Long_word_overflows_when_longer_than_line() {
            // "supercalifragilistic" = 20 chars * 8 = 160px > 64. We don't break inside words;
            // it should overflow on its own line.
            var br = new LineBreaker();
            var r = br.Break(new List<LineBreaker.Item> { Item("supercalifragilistic") }, 64);
            Assert.That(r.Lines.Count, Is.EqualTo(1));
            Assert.That(RunsOnLine(r.Lines[0])[0].Text, Is.EqualTo("supercalifragilistic"));
            Assert.That(r.Lines[0].Width, Is.GreaterThan(64));
        }

        [Test]
        public void Multiple_consecutive_spaces_collapse_to_one_in_normal() {
            var br = new LineBreaker();
            var r = br.Break(new List<LineBreaker.Item> { Item("a    b") }, 1000);
            Assert.That(r.Lines.Count, Is.EqualTo(1));
            // Expected fragments: "a" " " "b" => width = 8 + 8 + 8 = 24
            Assert.That(r.Lines[0].Width, Is.EqualTo(24).Within(0.001));
        }

        [Test]
        public void Newlines_treated_as_spaces_in_normal() {
            var br = new LineBreaker();
            var r = br.Break(new List<LineBreaker.Item> { Item("a\nb") }, 1000);
            Assert.That(r.Lines.Count, Is.EqualTo(1));
            Assert.That(r.Lines[0].Width, Is.EqualTo(24).Within(0.001));
        }

        [Test]
        public void White_space_pre_preserves_all_whitespace_and_does_not_wrap_on_space() {
            var br = new LineBreaker();
            // 3 spaces preserved, total "a   b" = 5 chars * 8 = 40
            var r = br.Break(new List<LineBreaker.Item> { Item("a   b", ws: "pre") }, 1000);
            Assert.That(r.Lines.Count, Is.EqualTo(1));
            Assert.That(r.Lines[0].Width, Is.EqualTo(40).Within(0.001));
        }

        [Test]
        public void White_space_pre_breaks_on_newline_only() {
            var br = new LineBreaker();
            var r = br.Break(new List<LineBreaker.Item> { Item("a\nb", ws: "pre") }, 1000);
            Assert.That(r.Lines.Count, Is.EqualTo(2));
            Assert.That(r.Lines[0].Width, Is.EqualTo(8).Within(0.001));
            Assert.That(r.Lines[1].Width, Is.EqualTo(8).Within(0.001));
        }

        [Test]
        public void White_space_pre_wrap_preserves_and_wraps() {
            var br = new LineBreaker();
            // "hello world" with width 64 should wrap, AND preserve the space if it's at the start of a line is... actually pre-wrap treats trailing space as zero-width. We simply verify it wraps.
            var r = br.Break(new List<LineBreaker.Item> { Item("hello world", ws: "pre-wrap") }, 64);
            Assert.That(r.Lines.Count, Is.EqualTo(2));
        }

        [Test]
        public void White_space_pre_wrap_preserves_trailing_space_as_hanging_space_on_wrap() {
            // CSS Text L3 §3: in pre-wrap, the U+0020 space at the end of a
            // line that would otherwise wrap "hangs" — it MUST be preserved
            // on the outgoing line, not trimmed.
            // "hello world" with width 64: "hello"=40, " "=8, "world"=40.
            // After "hello world"-prefix the trailing " " sits on line 1
            // (X=48); "world" overflows and wraps to line 2. The hanging
            // space contributes 8px to line 1's width => 48.
            var br = new LineBreaker();
            var r = br.Break(new List<LineBreaker.Item> { Item("hello world", ws: "pre-wrap") }, 64);
            Assert.That(r.Lines.Count, Is.EqualTo(2));
            Assert.That(r.Lines[0].Width, Is.EqualTo(48).Within(0.001));
            Assert.That(RunsOnLine(r.Lines[1])[0].Text, Is.EqualTo("world"));
        }

        [Test]
        public void White_space_normal_still_trims_trailing_space_on_wrap() {
            // Regression guard for the pre-wrap hanging-space fix: normal /
            // nowrap MUST still collapse-strip trailing spaces per CSS Text
            // L3 §3. Same input as the pre-wrap case but with ws=normal —
            // line 1 should be exactly "hello" (40px), no hanging space.
            var br = new LineBreaker();
            var r = br.Break(new List<LineBreaker.Item> { Item("hello world") }, 64);
            Assert.That(r.Lines.Count, Is.EqualTo(2));
            Assert.That(r.Lines[0].Width, Is.EqualTo(40).Within(0.001));
            var line0 = RunsOnLine(r.Lines[0]);
            Assert.That(line0[line0.Count - 1].Text, Is.Not.EqualTo(" "));
        }

        [Test]
        public void White_space_nowrap_never_wraps() {
            var br = new LineBreaker();
            var r = br.Break(new List<LineBreaker.Item> { Item("hello world", ws: "nowrap") }, 32);
            Assert.That(r.Lines.Count, Is.EqualTo(1));
        }

        [Test]
        public void Mixed_style_runs_measured_separately() {
            var br = new LineBreaker();
            var items = new List<LineBreaker.Item> {
                Item("Click "),    // 6 chars * 8 = 48
                Item("here"),       // 4 chars * 8 = 32
                Item(" to start")   // 9 chars * 8 = 72
            };
            var r = br.Break(items, 1000);
            Assert.That(r.Lines.Count, Is.EqualTo(1));
            // Internally the runs are split by token boundaries; we expect at least 3 fragments.
            Assert.That(RunsOnLine(r.Lines[0]).Count, Is.GreaterThanOrEqualTo(3));
            // total visible width: "Click " "here" " " "to" " " "start" -> "Click here to start" = 19 chars * 8 = 152
            Assert.That(r.Lines[0].Width, Is.EqualTo(152).Within(0.001));
        }

        [Test]
        public void Trailing_whitespace_at_end_of_line_removed_in_normal() {
            // "aa bb" 5 chars * 8 = 40. With width 32, "aa" goes on line 1 then wraps; trailing
            // space at end of "aa" should be removed before line break.
            var br = new LineBreaker();
            var r = br.Break(new List<LineBreaker.Item> { Item("aa bb") }, 32);
            Assert.That(r.Lines.Count, Is.EqualTo(2));
            var line0 = RunsOnLine(r.Lines[0]);
            Assert.That(line0[line0.Count - 1].Text, Is.Not.EqualTo(" "));
        }

        [Test]
        public void Leading_whitespace_at_start_of_line_stripped_in_normal() {
            var br = new LineBreaker();
            var r = br.Break(new List<LineBreaker.Item> { Item("   hello") }, 1000);
            Assert.That(r.Lines.Count, Is.EqualTo(1));
            // Only "hello" should appear; leading spaces collapse + strip.
            Assert.That(r.Lines[0].Width, Is.EqualTo(40).Within(0.001));
        }

        [Test]
        public void Pre_line_collapses_spaces_but_preserves_newlines() {
            var br = new LineBreaker();
            var r = br.Break(new List<LineBreaker.Item> { Item("a   b\nc", ws: "pre-line") }, 1000);
            Assert.That(r.Lines.Count, Is.EqualTo(2));
            // Line 1: "a b" = 24; line 2: "c" = 8
            Assert.That(r.Lines[0].Width, Is.EqualTo(24).Within(0.001));
            Assert.That(r.Lines[1].Width, Is.EqualTo(8).Within(0.001));
        }

        [Test]
        public void Empty_input_produces_one_empty_line_only_when_explicitly_finalized() {
            var br = new LineBreaker();
            var r = br.Break(new List<LineBreaker.Item>(), 1000);
            // No items -> no lines; an empty paragraph case is handled by InlineLayout.
            Assert.That(r.Lines.Count, Is.EqualTo(0));
        }

        [Test]
        public void Wrap_uses_largest_metric_for_line_height() {
            var big = Item("X", fontSize: 32);
            var small = Item("y", fontSize: 16);
            var br = new LineBreaker();
            var r = br.Break(new List<LineBreaker.Item> { big, small }, 1000);
            Assert.That(r.Lines.Count, Is.EqualTo(1));
            Assert.That(r.Lines[0].Height, Is.EqualTo(Mono.LineHeight(32)).Within(0.001));
        }

        [Test]
        public void Run_widths_cumulative_x_offsets() {
            var br = new LineBreaker();
            var r = br.Break(new List<LineBreaker.Item> { Item("ab cd") }, 1000);
            var runs = RunsOnLine(r.Lines[0]);
            Assert.That(runs[0].X, Is.EqualTo(0).Within(0.001));
            double cumulative = 0;
            foreach (var rn in runs) {
                Assert.That(rn.X, Is.EqualTo(cumulative).Within(0.001));
                cumulative += rn.Width;
            }
        }
    }
}
