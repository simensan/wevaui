using NUnit.Framework;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    public class LogicalLayoutTests {
        static BlockBox FindBlock(Box root, string id) {
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element?.GetAttribute("id") == id) return bb;
            }
            return null;
        }

        static TextRun FirstTextRun(Box root) {
            foreach (var b in AllBoxes(root)) {
                if (b is TextRun tr) return tr;
            }
            return null;
        }

        [Test]
        public void Logical_box_properties_map_to_rtl_physical_edges() {
            var (root, _, _) = Build(
                "<div id=\"box\"></div>",
                "#box { direction: rtl; inline-size: 120px; block-size: 40px; " +
                "margin-inline-start: 10px; margin-inline-end: 20px; " +
                "padding-inline-start: 3px; padding-inline-end: 5px; " +
                "border-inline-start: 7px solid red; border-inline-end-width: 2px; border-inline-end-style: solid; }",
                viewportWidth: 400);

            var box = FindBlock(root, "box");
            Assert.That(box, Is.Not.Null);
            Assert.That(box.Width, Is.EqualTo(120 + 3 + 5 + 7 + 2).Within(0.001));
            Assert.That(box.Height, Is.EqualTo(40).Within(0.001));
            Assert.That(box.MarginRight, Is.EqualTo(10).Within(0.001));
            Assert.That(box.MarginLeft, Is.EqualTo(20).Within(0.001));
            Assert.That(box.PaddingRight, Is.EqualTo(3).Within(0.001));
            Assert.That(box.PaddingLeft, Is.EqualTo(5).Within(0.001));
            Assert.That(box.BorderRight, Is.EqualTo(7).Within(0.001));
            Assert.That(box.BorderLeft, Is.EqualTo(2).Within(0.001));
        }

        [Test]
        public void Logical_and_physical_conflicts_use_cascade_order() {
            var (rootA, _, _) = Build(
                "<div id=\"box\"></div>",
                "#box { width: 20px; margin-inline-start: 10px; margin-left: 30px; }",
                viewportWidth: 400);
            var a = FindBlock(rootA, "box");
            Assert.That(a.MarginLeft, Is.EqualTo(30).Within(0.001));

            var (rootB, _, _) = Build(
                "<div id=\"box\"></div>",
                "#box { width: 20px; margin-left: 30px; margin-inline-start: 10px; }",
                viewportWidth: 400);
            var b = FindBlock(rootB, "box");
            Assert.That(b.MarginLeft, Is.EqualTo(10).Within(0.001));
        }

        [Test]
        public void Logical_insets_map_before_absolute_positioning() {
            var (root, _, _) = Build(
                "<div id=\"host\"><div id=\"abs\"></div></div>",
                "#host { position: relative; width: 300px; height: 100px; } " +
                "#abs { position: absolute; direction: rtl; inline-size: 20px; block-size: 10px; " +
                "inset-inline-start: 30px; inset-block-start: 12px; }",
                viewportWidth: 400);

            var abs = FindBlock(root, "abs");
            Assert.That(abs, Is.Not.Null);
            Assert.That(abs.X, Is.EqualTo(250).Within(0.001));
            Assert.That(abs.Y, Is.EqualTo(12).Within(0.001));
        }

        [Test]
        public void Sideways_lr_inline_axis_runs_bottom_to_top_in_ltr() {
            var (root, _, _) = Build(
                "<div id=\"host\"><div id=\"abs\"></div></div>",
                "#host { position: relative; width: 300px; height: 100px; } " +
                "#abs { position: absolute; writing-mode: sideways-lr; direction: ltr; " +
                "inline-size: 20px; block-size: 10px; " +
                "inset-inline-start: 30px; inset-block-start: 12px; }",
                viewportWidth: 400);

            var abs = FindBlock(root, "abs");
            Assert.That(abs, Is.Not.Null);
            Assert.That(abs.X, Is.EqualTo(12).Within(0.001));
            Assert.That(abs.Y, Is.EqualTo(50).Within(0.001));
        }

        [Test]
        public void Vertical_lr_inline_axis_still_runs_top_to_bottom_in_ltr() {
            var (root, _, _) = Build(
                "<div id=\"host\"><div id=\"abs\"></div></div>",
                "#host { position: relative; width: 300px; height: 100px; } " +
                "#abs { position: absolute; writing-mode: vertical-lr; direction: ltr; " +
                "inline-size: 20px; block-size: 10px; " +
                "inset-inline-start: 30px; inset-block-start: 12px; }",
                viewportWidth: 400);

            var abs = FindBlock(root, "abs");
            Assert.That(abs, Is.Not.Null);
            Assert.That(abs.X, Is.EqualTo(12).Within(0.001));
            Assert.That(abs.Y, Is.EqualTo(30).Within(0.001));
        }

        [Test]
        public void Sideways_lr_inline_axis_runs_top_to_bottom_in_rtl() {
            var (root, _, _) = Build(
                "<div id=\"host\"><div id=\"abs\"></div></div>",
                "#host { position: relative; width: 300px; height: 100px; } " +
                "#abs { position: absolute; writing-mode: sideways-lr; direction: rtl; " +
                "inline-size: 20px; block-size: 10px; " +
                "inset-inline-start: 30px; inset-block-start: 12px; }",
                viewportWidth: 400);

            var abs = FindBlock(root, "abs");
            Assert.That(abs, Is.Not.Null);
            Assert.That(abs.X, Is.EqualTo(12).Within(0.001));
            Assert.That(abs.Y, Is.EqualTo(30).Within(0.001));
        }

        [Test]
        public void Text_align_start_and_end_follow_direction() {
            var (rootStart, _, _) = Build(
                "<p id=\"p\">hi</p>",
                "#p { direction: rtl; text-align: start; width: 200px; }",
                viewportWidth: 400);
            var startRun = FirstTextRun(rootStart);
            Assert.That(startRun, Is.Not.Null);
            Assert.That(startRun.X, Is.EqualTo(184).Within(0.001));

            var (rootEnd, _, _) = Build(
                "<p id=\"p\">hi</p>",
                "#p { direction: rtl; text-align: end; width: 200px; }",
                viewportWidth: 400);
            var endRun = FirstTextRun(rootEnd);
            Assert.That(endRun, Is.Not.Null);
            Assert.That(endRun.X, Is.EqualTo(0).Within(0.001));
        }
    }
}
