using NUnit.Framework;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    // CSS Logical Properties L1 §3 — inline-axis logical properties under RTL.
    //
    // In RTL horizontal writing (direction: rtl), the inline axis is mirrored:
    //   inline-start = right, inline-end = left.
    //
    // Round 1 (LogicalBlockAxisTests) covered the block axis in LTR.
    // Round 2 (this file) covers the inline axis isolated sub-properties under RTL:
    //   margin-inline-start/end, padding-inline-start/end, border-inline-start/end,
    //   inset-inline-start/end (positioned), and inline-size on block elements.
    //
    // Spec: CSS Logical Properties L1 §3 + §4.
    //   RTL + horizontal-tb: inline-start = right, inline-end = left.
    public class LogicalInlineAxisRtlTests {
        static BlockBox FindBlock(Box root, string id) {
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element?.GetAttribute("id") == id) return bb;
            }
            return null;
        }

        // ---- margin-inline-start / margin-inline-end under RTL ----

        [Test]
        public void Margin_inline_start_maps_to_margin_right_in_rtl() {
            // RTL: inline-start = right.
            var (root, _, _) = Build(
                "<div id=\"a\"></div><div id=\"b\"></div>",
                "#a { direction: rtl; width: 100px; height: 20px; margin-inline-start: 15px; } " +
                "#b { direction: rtl; width: 100px; height: 20px; margin-right: 15px; }",
                viewportWidth: 400);
            var a = FindBlock(root, "a");
            var b = FindBlock(root, "b");
            Assert.That(a, Is.Not.Null);
            Assert.That(b, Is.Not.Null);
            Assert.That(a.MarginRight, Is.EqualTo(b.MarginRight).Within(0.001),
                "margin-inline-start must equal margin-right in RTL");
            Assert.That(a.MarginLeft, Is.EqualTo(0).Within(0.001),
                "margin-inline-start must NOT affect margin-left in RTL");
        }

        [Test]
        public void Margin_inline_end_maps_to_margin_left_in_rtl() {
            // RTL: inline-end = left.
            var (root, _, _) = Build(
                "<div id=\"a\"></div><div id=\"b\"></div>",
                "#a { direction: rtl; width: 100px; height: 20px; margin-inline-end: 20px; } " +
                "#b { direction: rtl; width: 100px; height: 20px; margin-left: 20px; }",
                viewportWidth: 400);
            var a = FindBlock(root, "a");
            var b = FindBlock(root, "b");
            Assert.That(a, Is.Not.Null);
            Assert.That(b, Is.Not.Null);
            Assert.That(a.MarginLeft, Is.EqualTo(b.MarginLeft).Within(0.001),
                "margin-inline-end must equal margin-left in RTL");
            Assert.That(a.MarginRight, Is.EqualTo(0).Within(0.001),
                "margin-inline-end must NOT affect margin-right in RTL");
        }

        // ---- padding-inline-start / padding-inline-end under RTL ----

        [Test]
        public void Padding_inline_start_maps_to_padding_right_in_rtl() {
            var (root, _, _) = Build(
                "<div id=\"a\"></div><div id=\"b\"></div>",
                "#a { direction: rtl; width: 100px; height: 20px; padding-inline-start: 8px; } " +
                "#b { direction: rtl; width: 100px; height: 20px; padding-right: 8px; }",
                viewportWidth: 400);
            var a = FindBlock(root, "a");
            var b = FindBlock(root, "b");
            Assert.That(a, Is.Not.Null);
            Assert.That(b, Is.Not.Null);
            Assert.That(a.PaddingRight, Is.EqualTo(b.PaddingRight).Within(0.001),
                "padding-inline-start must equal padding-right in RTL");
            Assert.That(a.PaddingLeft, Is.EqualTo(0).Within(0.001),
                "padding-inline-start must NOT bleed into padding-left in RTL");
        }

        [Test]
        public void Padding_inline_end_maps_to_padding_left_in_rtl() {
            var (root, _, _) = Build(
                "<div id=\"a\"></div><div id=\"b\"></div>",
                "#a { direction: rtl; width: 100px; height: 20px; padding-inline-end: 12px; } " +
                "#b { direction: rtl; width: 100px; height: 20px; padding-left: 12px; }",
                viewportWidth: 400);
            var a = FindBlock(root, "a");
            var b = FindBlock(root, "b");
            Assert.That(a, Is.Not.Null);
            Assert.That(b, Is.Not.Null);
            Assert.That(a.PaddingLeft, Is.EqualTo(b.PaddingLeft).Within(0.001),
                "padding-inline-end must equal padding-left in RTL");
            Assert.That(a.PaddingRight, Is.EqualTo(0).Within(0.001),
                "padding-inline-end must NOT bleed into padding-right in RTL");
        }

        // ---- border-inline-start / border-inline-end under RTL ----

        [Test]
        public void Border_inline_start_maps_to_border_right_in_rtl() {
            var (root, _, _) = Build(
                "<div id=\"a\"></div><div id=\"b\"></div>",
                "#a { direction: rtl; width: 100px; height: 20px; " +
                "border-inline-start: 5px solid red; } " +
                "#b { direction: rtl; width: 100px; height: 20px; " +
                "border-right: 5px solid red; }",
                viewportWidth: 400);
            var a = FindBlock(root, "a");
            var b = FindBlock(root, "b");
            Assert.That(a, Is.Not.Null);
            Assert.That(b, Is.Not.Null);
            Assert.That(a.BorderRight, Is.EqualTo(b.BorderRight).Within(0.001),
                "border-inline-start must equal border-right in RTL");
            Assert.That(a.BorderLeft, Is.EqualTo(0).Within(0.001),
                "border-inline-start must NOT bleed into border-left in RTL");
        }

        [Test]
        public void Border_inline_end_maps_to_border_left_in_rtl() {
            var (root, _, _) = Build(
                "<div id=\"a\"></div><div id=\"b\"></div>",
                "#a { direction: rtl; width: 100px; height: 20px; " +
                "border-inline-end: 6px solid blue; } " +
                "#b { direction: rtl; width: 100px; height: 20px; " +
                "border-left: 6px solid blue; }",
                viewportWidth: 400);
            var a = FindBlock(root, "a");
            var b = FindBlock(root, "b");
            Assert.That(a, Is.Not.Null);
            Assert.That(b, Is.Not.Null);
            Assert.That(a.BorderLeft, Is.EqualTo(b.BorderLeft).Within(0.001),
                "border-inline-end must equal border-left in RTL");
            Assert.That(a.BorderRight, Is.EqualTo(0).Within(0.001),
                "border-inline-end must NOT bleed into border-right in RTL");
        }

        // ---- inset-inline-start / inset-inline-end under RTL positioned ----

        [Test]
        public void Inset_inline_start_maps_to_right_in_rtl_positioned() {
            // RTL absolute positioned: inset-inline-start = right inset.
            // Host 300px wide. inset-inline-start:40px → right:40px → X = 300-40-60 = 200.
            var (root, _, _) = Build(
                "<div id=\"host\"><div id=\"abs\"></div></div>",
                "#host { position: relative; width: 300px; height: 100px; } " +
                "#abs { position: absolute; direction: rtl; " +
                "width: 60px; height: 20px; inset-inline-start: 40px; }",
                viewportWidth: 400);
            var abs = FindBlock(root, "abs");
            Assert.That(abs, Is.Not.Null);
            // right:40px on a 300px parent → box placed at X=200 (300-40-60).
            Assert.That(abs.X, Is.EqualTo(200).Within(0.001),
                "inset-inline-start must map to `right` in RTL");
        }

        [Test]
        public void Inset_inline_end_maps_to_left_in_rtl_positioned() {
            // RTL absolute positioned: inset-inline-end = left inset.
            // Host 300px wide. inset-inline-end:30px → left:30px → X = 30.
            var (root, _, _) = Build(
                "<div id=\"host\"><div id=\"abs\"></div></div>",
                "#host { position: relative; width: 300px; height: 100px; } " +
                "#abs { position: absolute; direction: rtl; " +
                "width: 60px; height: 20px; inset-inline-end: 30px; }",
                viewportWidth: 400);
            var abs = FindBlock(root, "abs");
            Assert.That(abs, Is.Not.Null);
            Assert.That(abs.X, Is.EqualTo(30).Within(0.001),
                "inset-inline-end must map to `left` in RTL");
        }

        // ---- LTR contrast: inline-start/end must NOT flip in LTR ----

        [Test]
        public void Margin_inline_start_maps_to_margin_left_in_ltr() {
            // Regression guard: LTR must remain unaffected after the RTL path.
            var (root, _, _) = Build(
                "<div id=\"a\"></div><div id=\"b\"></div>",
                "#a { direction: ltr; width: 100px; height: 20px; margin-inline-start: 10px; } " +
                "#b { direction: ltr; width: 100px; height: 20px; margin-left: 10px; }",
                viewportWidth: 400);
            var a = FindBlock(root, "a");
            var b = FindBlock(root, "b");
            Assert.That(a, Is.Not.Null);
            Assert.That(b, Is.Not.Null);
            Assert.That(a.MarginLeft, Is.EqualTo(b.MarginLeft).Within(0.001),
                "margin-inline-start must equal margin-left in LTR (regression guard)");
        }

        // ---- inline-size under RTL ----

        [Test]
        public void Inline_size_sets_width_in_rtl_horizontal_writing() {
            // direction:rtl, horizontal-tb: inline axis is still horizontal.
            // inline-size sets the physical width.
            var (root, _, _) = Build(
                "<div id=\"a\"></div>",
                "#a { direction: rtl; inline-size: 150px; height: 30px; }",
                viewportWidth: 400);
            var a = FindBlock(root, "a");
            Assert.That(a, Is.Not.Null);
            Assert.That(a.Width, Is.EqualTo(150).Within(0.001),
                "inline-size must set physical width in RTL horizontal-tb writing mode");
        }
    }
}
