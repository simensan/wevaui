using NUnit.Framework;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    // CSS Overflow L4 §6 — line-clamp truncates after N lines and the
    // legacy `-webkit-line-clamp` syntax is honored as an alias.
    public class LineClampTests {
        const string Ellipsis = "…";

        static BlockBox FirstByTag(Box root, string tag) {
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && !(b is AnonymousBlockBox) && bb.Element?.TagName == tag) return bb;
            }
            return null;
        }

        static int LineCount(BlockBox box) {
            int n = 0;
            foreach (var c in box.Children) if (c is LineBox) n++;
            return n;
        }

        static string LineText(LineBox line) {
            var sb = new System.Text.StringBuilder();
            foreach (var c in line.Children) {
                if (c is TextRun tr) sb.Append(tr.Text);
            }
            return sb.ToString();
        }

        static LineBox NthLine(BlockBox box, int n) {
            int seen = 0;
            foreach (var c in box.Children) {
                if (c is LineBox lb) {
                    seen++;
                    if (seen == n) return lb;
                }
            }
            return null;
        }

        [Test]
        public void Line_clamp_2_keeps_first_two_lines_and_ellipsises_second() {
            // Force wrapping with a narrow width — 4 words of 5 chars each
            // (~40px each) plus spaces. Width 100 gives 2 words per line.
            var (root, _, _) = Build(
                "<p style=\"width:100px;line-clamp:2\">aaaa bbbb cccc dddd eeee ffff gggg</p>",
                null, 800);
            var p = FirstByTag(root, "p");
            Assert.That(p, Is.Not.Null);
            Assert.That(LineCount(p), Is.EqualTo(2),
                "expected exactly 2 LineBoxes after clamping; got " + LineCount(p));
            var second = NthLine(p, 2);
            Assert.That(second, Is.Not.Null);
            Assert.That(LineText(second).EndsWith(Ellipsis), Is.True,
                "expected second (last visible) line to end with ellipsis, got: " + LineText(second));
        }

        [Test]
        public void Webkit_line_clamp_alias_works_same_as_line_clamp() {
            var (root, _, _) = Build(
                "<p style=\"width:100px;-webkit-line-clamp:1\">aaaa bbbb cccc dddd eeee</p>",
                null, 800);
            var p = FirstByTag(root, "p");
            Assert.That(LineCount(p), Is.EqualTo(1));
            var line = NthLine(p, 1);
            Assert.That(LineText(line).EndsWith(Ellipsis), Is.True);
        }

        [Test]
        public void Line_clamp_none_leaves_all_lines() {
            var (root, _, _) = Build(
                "<p style=\"width:100px;line-clamp:none\">aaaa bbbb cccc dddd eeee ffff gggg</p>",
                null, 800);
            var p = FirstByTag(root, "p");
            Assert.That(LineCount(p), Is.GreaterThanOrEqualTo(3),
                "line-clamp:none should preserve all lines; got " + LineCount(p));
        }

        [Test]
        public void Line_clamp_higher_than_actual_lines_is_noop() {
            var (root, _, _) = Build(
                "<p style=\"width:400px;line-clamp:10\">short content fits in one line</p>",
                null, 800);
            var p = FirstByTag(root, "p");
            int lineCount = LineCount(p);
            Assert.That(lineCount, Is.LessThanOrEqualTo(2));
            var first = NthLine(p, 1);
            Assert.That(LineText(first).EndsWith(Ellipsis), Is.False,
                "line-clamp larger than actual line count should NOT add ellipsis; got: " + LineText(first));
        }

        [Test]
        public void Line_clamp_zero_treated_as_none() {
            var (root, _, _) = Build(
                "<p style=\"width:100px;line-clamp:0\">aaaa bbbb cccc dddd eeee ffff</p>",
                null, 800);
            var p = FirstByTag(root, "p");
            Assert.That(LineCount(p), Is.GreaterThanOrEqualTo(2),
                "line-clamp:0 should be treated as none (no truncation), but only " + LineCount(p) + " line(s) remain");
        }

        [Test]
        public void Standard_line_clamp_wins_over_webkit_when_both_set() {
            // The standard `line-clamp` takes precedence over the legacy
            // `-webkit-line-clamp` when both are declared — matches the
            // CSS cascade order at equal specificity (later wins, and
            // standard form is the spec target).
            var (root, _, _) = Build(
                "<p style=\"width:100px;-webkit-line-clamp:3;line-clamp:1\">aaaa bbbb cccc dddd eeee ffff</p>",
                null, 800);
            var p = FirstByTag(root, "p");
            Assert.That(LineCount(p), Is.EqualTo(1));
        }
    }
}
