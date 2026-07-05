using NUnit.Framework;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Flex.FlexTestHelpers;

namespace Weva.Tests.Layout.Flex {
    public class FlexJustifyContentTests {
        const string Css = @"
            .flex { display: flex; width: 600px; }
            .item { width: 100px; height: 50px; }
        ";

        static (double x0, double x1, double x2) RunThree(string justify) {
            var html = "<div class=\"flex\" style=\"justify-content:" + justify + "\">"
                + "<div class=\"item\"></div><div class=\"item\"></div><div class=\"item\"></div></div>";
            var (root, _, _) = Build(html, Css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            return (ChildAt(fb, 0).X, ChildAt(fb, 1).X, ChildAt(fb, 2).X);
        }

        [Test]
        public void FlexStart_packs_items_at_start() {
            var (a, b, c) = RunThree("flex-start");
            Assert.That(a, Is.EqualTo(0).Within(0.001));
            Assert.That(b, Is.EqualTo(100).Within(0.001));
            Assert.That(c, Is.EqualTo(200).Within(0.001));
        }

        [Test]
        public void FlexEnd_packs_items_at_end() {
            var (a, b, c) = RunThree("flex-end");
            Assert.That(a, Is.EqualTo(300).Within(0.001));
            Assert.That(b, Is.EqualTo(400).Within(0.001));
            Assert.That(c, Is.EqualTo(500).Within(0.001));
        }

        [Test]
        public void Center_groups_items_in_middle() {
            var (a, b, c) = RunThree("center");
            Assert.That(a, Is.EqualTo(150).Within(0.001));
            Assert.That(b, Is.EqualTo(250).Within(0.001));
            Assert.That(c, Is.EqualTo(350).Within(0.001));
        }

        [Test]
        public void SpaceBetween_puts_first_and_last_at_edges() {
            var (a, b, c) = RunThree("space-between");
            Assert.That(a, Is.EqualTo(0).Within(0.001));
            Assert.That(b, Is.EqualTo(250).Within(0.001));
            Assert.That(c, Is.EqualTo(500).Within(0.001));
        }

        [Test]
        public void SpaceAround_distributes_half_gaps_at_edges() {
            var (a, b, c) = RunThree("space-around");
            Assert.That(a, Is.EqualTo(50).Within(0.001));
            Assert.That(b, Is.EqualTo(250).Within(0.001));
            Assert.That(c, Is.EqualTo(450).Within(0.001));
        }

        [Test]
        public void SpaceEvenly_makes_all_gaps_equal() {
            var (a, b, c) = RunThree("space-evenly");
            Assert.That(a, Is.EqualTo(75).Within(0.001));
            Assert.That(b, Is.EqualTo(250).Within(0.001));
            Assert.That(c, Is.EqualTo(425).Within(0.001));
        }

        [Test]
        public void Start_keyword_aliases_FlexStart() {
            var (a, b, c) = RunThree("start");
            Assert.That(a, Is.EqualTo(0).Within(0.001));
            Assert.That(b, Is.EqualTo(100).Within(0.001));
            Assert.That(c, Is.EqualTo(200).Within(0.001));
        }
    }
}
