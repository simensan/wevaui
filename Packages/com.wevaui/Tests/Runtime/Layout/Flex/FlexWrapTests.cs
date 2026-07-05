using NUnit.Framework;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Flex.FlexTestHelpers;

namespace Weva.Tests.Layout.Flex {
    public class FlexWrapTests {
        const string Css = @"
            .flex { display: flex; width: 300px; }
            .item { width: 120px; height: 50px; }
        ";

        [Test]
        public void Nowrap_default_keeps_all_items_on_one_line() {
            var (root, _, _) = Build(
                "<div class=\"flex\"><div class=\"item\" style=\"flex-shrink:0\"></div><div class=\"item\" style=\"flex-shrink:0\"></div><div class=\"item\" style=\"flex-shrink:0\"></div></div>",
                Css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1); var c = ChildAt(fb, 2);
            Assert.That(a.Y, Is.EqualTo(0).Within(0.001));
            Assert.That(b.Y, Is.EqualTo(0).Within(0.001));
            Assert.That(c.Y, Is.EqualTo(0).Within(0.001));
        }

        [Test]
        public void Wrap_creates_second_line_when_overflow() {
            var (root, _, _) = Build(
                "<div class=\"flex\" style=\"flex-wrap:wrap\"><div class=\"item\"></div><div class=\"item\"></div><div class=\"item\"></div></div>",
                Css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1); var c = ChildAt(fb, 2);
            Assert.That(a.X, Is.EqualTo(0).Within(0.001));
            Assert.That(b.X, Is.EqualTo(120).Within(0.001));
            Assert.That(c.X, Is.EqualTo(0).Within(0.001));
            Assert.That(c.Y, Is.EqualTo(50).Within(0.001));
        }

        [Test]
        public void Wrap_multiple_items_per_line() {
            var (root, _, _) = Build(
                "<div class=\"flex\" style=\"flex-wrap:wrap\"><div class=\"item\"></div><div class=\"item\"></div><div class=\"item\"></div><div class=\"item\"></div></div>",
                Css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0);
            var b = ChildAt(fb, 1);
            var c = ChildAt(fb, 2);
            var d = ChildAt(fb, 3);
            Assert.That(a.Y, Is.EqualTo(0).Within(0.001));
            Assert.That(b.Y, Is.EqualTo(0).Within(0.001));
            Assert.That(c.Y, Is.EqualTo(50).Within(0.001));
            Assert.That(d.Y, Is.EqualTo(50).Within(0.001));
        }

        [Test]
        public void Wrap_reverse_reverses_cross_axis_order() {
            var (root, _, _) = Build(
                "<div class=\"flex\" style=\"flex-wrap:wrap-reverse;height:200px\"><div class=\"item\"></div><div class=\"item\"></div><div class=\"item\"></div></div>",
                Css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var c = ChildAt(fb, 2);
            Assert.That(a.Y, Is.GreaterThan(c.Y));
        }

        [Test]
        public void Wrap_respects_cross_axis_gap_between_lines() {
            var (root, _, _) = Build(
                "<div class=\"flex\" style=\"flex-wrap:wrap;gap:20px\"><div class=\"item\"></div><div class=\"item\"></div><div class=\"item\"></div></div>",
                Css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1); var c = ChildAt(fb, 2);
            Assert.That(a.X, Is.EqualTo(0).Within(0.001));
            Assert.That(b.X, Is.EqualTo(140).Within(0.001));
            Assert.That(c.X, Is.EqualTo(0).Within(0.001));
            Assert.That(c.Y, Is.EqualTo(50 + 20).Within(0.001));
        }
    }
}
