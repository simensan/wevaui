using NUnit.Framework;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Flex.FlexTestHelpers;

namespace Weva.Tests.Layout.Flex {
    public class FlexAlignSelfTests {
        const string Css = @"
            .flex { display: flex; width: 600px; height: 200px; align-items: flex-start; }
            .item { width: 100px; height: 50px; }
        ";

        [Test]
        public void AlignSelf_overrides_align_items_per_child() {
            var (root, _, _) = Build(
                "<div class=\"flex\"><div class=\"item\"></div><div class=\"item\" style=\"align-self:flex-end\"></div></div>",
                Css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0);
            var b = ChildAt(fb, 1);
            Assert.That(a.Y, Is.EqualTo(0).Within(0.001));
            Assert.That(b.Y, Is.EqualTo(150).Within(0.001));
        }

        [Test]
        public void AlignSelf_center_centers_individual_item() {
            var (root, _, _) = Build(
                "<div class=\"flex\"><div class=\"item\" style=\"align-self:center\"></div></div>",
                Css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0);
            Assert.That(a.Y, Is.EqualTo(75).Within(0.001));
        }

        [Test]
        public void AlignSelf_stretch_overrides_flex_start_default() {
            var (root, _, _) = Build(
                "<div class=\"flex\"><div style=\"width:100px;align-self:stretch\"></div></div>",
                Css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0);
            Assert.That(a.Height, Is.EqualTo(200).Within(0.001));
        }

        [Test]
        public void AlignSelf_auto_inherits_from_align_items() {
            var (root, _, _) = Build(
                "<div class=\"flex\" style=\"align-items:center\"><div class=\"item\" style=\"align-self:auto\"></div></div>",
                Css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0);
            Assert.That(a.Y, Is.EqualTo(75).Within(0.001));
        }
    }
}
