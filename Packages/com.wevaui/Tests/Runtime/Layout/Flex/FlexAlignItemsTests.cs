using NUnit.Framework;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Flex.FlexTestHelpers;

namespace Weva.Tests.Layout.Flex {
    public class FlexAlignItemsTests {
        const string Css = @"
            .flex { display: flex; width: 600px; height: 200px; }
            .small { width: 100px; height: 50px; }
        ";

        [Test]
        public void Stretch_default_grows_item_to_container_height() {
            var (root, _, _) = Build(
                "<div class=\"flex\"><div style=\"width:100px\"></div></div>",
                Css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0);
            Assert.That(a.Height, Is.EqualTo(200).Within(0.001));
        }

        [Test]
        public void Stretch_does_not_override_explicit_height() {
            var (root, _, _) = Build(
                "<div class=\"flex\"><div class=\"small\"></div></div>",
                Css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0);
            Assert.That(a.Height, Is.EqualTo(50).Within(0.001));
        }

        [Test]
        public void FlexStart_pins_items_to_top() {
            var (root, _, _) = Build(
                "<div class=\"flex\" style=\"align-items:flex-start\"><div class=\"small\"></div></div>",
                Css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0);
            Assert.That(a.Y, Is.EqualTo(0).Within(0.001));
        }

        [Test]
        public void FlexEnd_pins_items_to_bottom() {
            var (root, _, _) = Build(
                "<div class=\"flex\" style=\"align-items:flex-end\"><div class=\"small\"></div></div>",
                Css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0);
            Assert.That(a.Y, Is.EqualTo(150).Within(0.001));
        }

        [Test]
        public void Center_centers_item_vertically() {
            var (root, _, _) = Build(
                "<div class=\"flex\" style=\"align-items:center\"><div class=\"small\"></div></div>",
                Css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0);
            Assert.That(a.Y, Is.EqualTo(75).Within(0.001));
        }

        [Test]
        public void Baseline_with_single_item_pins_to_top() {
            // With only one baseline-aligned item and no taller text neighbour,
            // the item's own baseline IS the line's max baseline, so it sits at
            // Y=0. Behaviour matches the v0.x flex-start fallback for this trivial
            // case but is computed via the real baseline algorithm.
            var (root, _, _) = Build(
                "<div class=\"flex\" style=\"align-items:baseline\"><div class=\"small\"></div></div>",
                Css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0);
            Assert.That(a.Y, Is.EqualTo(0).Within(0.001));
        }
    }
}
