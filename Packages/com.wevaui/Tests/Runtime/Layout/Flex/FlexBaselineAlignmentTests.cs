using NUnit.Framework;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Flex.FlexTestHelpers;

namespace Weva.Tests.Layout.Flex {
    public class FlexBaselineAlignmentTests {
        const string Css = @"
            .flex { display: flex; align-items: baseline; width: 600px; height: 200px; }
        ";

        [Test]
        public void Two_items_with_different_font_sizes_align_baselines() {
            // Two paragraphs side by side with different font-sizes; their first
            // baselines should align at the line's max baseline. With mono metrics
            // (ascent = 0.8 * fontSize), 16px text has baseline 12.8 from top;
            // 32px text has baseline 25.6 from top. The 16px box should be pushed
            // down by (25.6 - 12.8) = 12.8 to align baselines.
            var (root, _, _) = Build(
                "<div class=\"flex\">" +
                "<p style=\"font-size:16px;margin:0\">a</p>" +
                "<p style=\"font-size:32px;margin:0\">b</p>" +
                "</div>",
                Css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var small = ChildAt(fb, 0);
            var large = ChildAt(fb, 1);
            // The taller item's first-baseline (25.6) is the line's max-above.
            // Small item shifts down by (25.6 - 12.8) = 12.8.
            Assert.That(large.Y, Is.EqualTo(0).Within(0.001));
            Assert.That(small.Y, Is.EqualTo(12.8).Within(0.001));
        }

        [Test]
        public void Items_without_text_use_bottom_of_content_as_baseline() {
            var (root, _, _) = Build(
                "<div class=\"flex\">" +
                "<div style=\"width:50px;height:60px\"></div>" +
                "<p style=\"font-size:16px;margin:0\">x</p>" +
                "</div>",
                Css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var box = ChildAt(fb, 0);
            var text = ChildAt(fb, 1);
            // Box has no text → synthesised baseline = bottom of box (60px).
            // Text baseline = 12.8. Max above = 60. Text shifts down by 60-12.8.
            Assert.That(box.Y, Is.EqualTo(0).Within(0.001));
            Assert.That(text.Y, Is.EqualTo(60 - 12.8).Within(0.001));
        }

        [Test]
        public void Container_align_items_baseline_applies_to_all_items() {
            // Without per-item override, every item participates in baseline.
            var (root, _, _) = Build(
                "<div class=\"flex\">" +
                "<p style=\"font-size:16px;margin:0\">a</p>" +
                "<p style=\"font-size:24px;margin:0\">b</p>" +
                "<p style=\"font-size:48px;margin:0\">c</p>" +
                "</div>",
                Css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            // Largest baseline is 48 * 0.8 = 38.4. Items shift to align.
            var a = ChildAt(fb, 0);
            var b = ChildAt(fb, 1);
            var c = ChildAt(fb, 2);
            Assert.That(c.Y, Is.EqualTo(0).Within(0.001));
            Assert.That(b.Y, Is.EqualTo(38.4 - 24 * 0.8).Within(0.001));
            Assert.That(a.Y, Is.EqualTo(38.4 - 16 * 0.8).Within(0.001));
        }

        [Test]
        public void Align_self_baseline_overrides_container_stretch() {
            const string css = @"
                .flex { display: flex; align-items: stretch; width: 600px; height: 200px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"flex\">" +
                "<p style=\"font-size:16px;margin:0;align-self:baseline\">a</p>" +
                "<p style=\"font-size:32px;margin:0;align-self:baseline\">b</p>" +
                "</div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var small = ChildAt(fb, 0);
            // Same as the basic two-item case.
            Assert.That(small.Y, Is.EqualTo(12.8).Within(0.001));
        }

        [Test]
        public void Column_flex_baseline_falls_back_to_flex_start() {
            // Per CSS Flexbox §9.4.1, a column flex's synthesised baseline
            // equals its cross-start edge → behaves like flex-start in v1.
            const string css = @"
                .flex { display: flex; flex-direction: column; align-items: baseline; width: 200px; height: 600px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"flex\">" +
                "<p style=\"font-size:16px;margin:0;width:50px\">a</p>" +
                "<p style=\"font-size:32px;margin:0;width:80px\">b</p>" +
                "</div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0);
            var b = ChildAt(fb, 1);
            // Column flex: cross axis is horizontal → both items pinned to X=0.
            Assert.That(a.X, Is.EqualTo(0).Within(0.001));
            Assert.That(b.X, Is.EqualTo(0).Within(0.001));
        }

        [Test]
        public void Multiple_flex_lines_each_have_their_own_baseline_set() {
            // Wrap forces multiple lines; baseline is computed per-line.
            const string css = @"
                .flex { display: flex; flex-wrap: wrap; align-items: baseline; width: 200px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"flex\">" +
                "<p style=\"font-size:16px;margin:0;width:80px\">a</p>" +
                "<p style=\"font-size:32px;margin:0;width:80px\">b</p>" +
                "<p style=\"font-size:16px;margin:0;width:80px\">c</p>" +
                "<p style=\"font-size:48px;margin:0;width:80px\">d</p>" +
                "</div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0);
            var b = ChildAt(fb, 1);
            var c = ChildAt(fb, 2);
            var d = ChildAt(fb, 3);
            // Two items per line. Line 1: a (16) + b (32). Line 2: c (16) + d (48).
            // Line 1 max baseline = 25.6 → a.Y = 12.8, b.Y = 0.
            // Line 2 max baseline = 38.4 → c.Y is at its line's start + (38.4-12.8).
            Assert.That(b.Y, Is.EqualTo(0).Within(0.001));
            Assert.That(a.Y, Is.EqualTo(12.8).Within(0.001));
            // c & d are on the second line: their Y values are larger than a's.
            Assert.That(d.Y, Is.GreaterThan(a.Y));
            Assert.That(c.Y, Is.GreaterThan(d.Y));
        }
    }
}
