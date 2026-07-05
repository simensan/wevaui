using System.Collections.Generic;
using NUnit.Framework;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    public class TextFormattingPropertyTests {
        static BlockBox FirstByTag(Box root, string tag) {
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && !(b is AnonymousBlockBox) && bb.Element?.TagName == tag) return bb;
            }
            return null;
        }

        static List<LineBox> Lines(BlockBox box) {
            var list = new List<LineBox>();
            foreach (var c in box.Children) if (c is LineBox lb) list.Add(lb);
            return list;
        }

        static string LineText(LineBox line) {
            var sb = new System.Text.StringBuilder();
            foreach (var c in line.Children) if (c is TextRun tr) sb.Append(tr.Text);
            return sb.ToString();
        }

        static TextRun FirstRun(LineBox line) {
            foreach (var c in line.Children) if (c is TextRun tr) return tr;
            return null;
        }

        [Test]
        public void Text_indent_shifts_and_reflows_first_line_only() {
            var (root, _, _) = Build(
                "<p style=\"width:64px;text-indent:16px;font-family:monospace;font-size:16px\">aaaa aaaa</p>",
                null, 800);
            var p = FirstByTag(root, "p");
            var lines = Lines(p);
            Assert.That(lines.Count, Is.EqualTo(2));
            Assert.That(LineText(lines[0]), Is.EqualTo("aaaa"));
            Assert.That(LineText(lines[1]), Is.EqualTo("aaaa"));
            Assert.That(FirstRun(lines[0]).X, Is.EqualTo(16).Within(0.001));
            Assert.That(FirstRun(lines[1]).X, Is.EqualTo(0).Within(0.001));
        }

        [Test]
        public void Text_align_justify_does_not_justify_final_line_by_default() {
            var (root, _, _) = Build(
                "<p style=\"width:100px;text-align:justify;font-family:monospace;font-size:16px\">aa bb</p>",
                null, 800);
            var line = Lines(FirstByTag(root, "p"))[0];
            Assert.That(line.IsFinalLine, Is.True);
            Assert.That(line.Width, Is.EqualTo(40).Within(0.001));
            Assert.That(FirstRun(line).X, Is.EqualTo(0).Within(0.001));
        }

        [Test]
        public void Text_align_last_can_align_final_line() {
            var (root, _, _) = Build(
                "<p style=\"width:100px;text-align:left;text-align-last:right;font-family:monospace;font-size:16px\">aa bb</p>",
                null, 800);
            var line = Lines(FirstByTag(root, "p"))[0];
            Assert.That(line.IsFinalLine, Is.True);
            Assert.That(FirstRun(line).X, Is.EqualTo(60).Within(0.001));
        }

        [Test]
        public void Text_wrap_nowrap_uses_collapsed_whitespace_without_wrapping() {
            var (root, _, _) = Build(
                "<p style=\"width:32px;text-wrap:nowrap;font-family:monospace;font-size:16px\">aa aa</p>",
                null, 800);
            var lines = Lines(FirstByTag(root, "p"));
            Assert.That(lines.Count, Is.EqualTo(1));
            Assert.That(LineText(lines[0]), Is.EqualTo("aa aa"));
            Assert.That(lines[0].Width, Is.GreaterThan(32));
        }
    }
}
