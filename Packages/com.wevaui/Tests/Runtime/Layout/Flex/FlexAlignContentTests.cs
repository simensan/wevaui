using NUnit.Framework;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Flex.FlexTestHelpers;

namespace Weva.Tests.Layout.Flex {
    public class FlexAlignContentTests {
        const string Css = @"
            .flex { display: flex; flex-wrap: wrap; width: 250px; height: 400px; }
            .item { width: 120px; height: 50px; }
        ";

        // Two lines: line1 = [a,b]; line2 = [c]. Each natural cross size = 50.
        // Container cross = 400. Free space = 400 - 100 = 300.

        [Test]
        public void FlexStart_packs_lines_at_top() {
            var (root, _, _) = Build(
                "<div class=\"flex\" style=\"align-content:flex-start\"><div class=\"item\"></div><div class=\"item\"></div><div class=\"item\"></div></div>",
                Css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var c = ChildAt(fb, 2);
            Assert.That(a.Y, Is.EqualTo(0).Within(0.001));
            Assert.That(c.Y, Is.EqualTo(50).Within(0.001));
        }

        [Test]
        public void FlexEnd_packs_lines_at_bottom() {
            var (root, _, _) = Build(
                "<div class=\"flex\" style=\"align-content:flex-end\"><div class=\"item\"></div><div class=\"item\"></div><div class=\"item\"></div></div>",
                Css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var c = ChildAt(fb, 2);
            Assert.That(a.Y, Is.EqualTo(300).Within(0.001));
            Assert.That(c.Y, Is.EqualTo(350).Within(0.001));
        }

        [Test]
        public void Center_centers_lines_in_container() {
            var (root, _, _) = Build(
                "<div class=\"flex\" style=\"align-content:center\"><div class=\"item\"></div><div class=\"item\"></div><div class=\"item\"></div></div>",
                Css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var c = ChildAt(fb, 2);
            Assert.That(a.Y, Is.EqualTo(150).Within(0.001));
            Assert.That(c.Y, Is.EqualTo(200).Within(0.001));
        }

        [Test]
        public void SpaceBetween_distributes_gaps_between_lines() {
            var (root, _, _) = Build(
                "<div class=\"flex\" style=\"align-content:space-between\"><div class=\"item\"></div><div class=\"item\"></div><div class=\"item\"></div></div>",
                Css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var c = ChildAt(fb, 2);
            Assert.That(a.Y, Is.EqualTo(0).Within(0.001));
            Assert.That(c.Y, Is.EqualTo(350).Within(0.001));
        }

        [Test]
        public void Stretch_default_grows_each_line_proportionally() {
            var (root, _, _) = Build(
                "<div class=\"flex\"><div class=\"item\"></div><div class=\"item\"></div><div class=\"item\"></div></div>",
                Css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var c = ChildAt(fb, 2);
            // free space 300 distributed across 2 lines: each line +150, so c.Y=line1 cross size = 200.
            Assert.That(a.Y, Is.EqualTo(0).Within(0.001));
            Assert.That(c.Y, Is.EqualTo(200).Within(0.001));
        }
    }
}
