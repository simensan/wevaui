using NUnit.Framework;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Flex.FlexTestHelpers;

namespace Weva.Tests.Layout.Flex {
    public class FlexOrderTests {
        const string Css = @"
            .flex { display: flex; width: 600px; }
            .item { width: 100px; height: 50px; }
        ";

        [Test]
        public void Negative_order_moves_item_to_front() {
            var (root, _, _) = Build(
                "<div class=\"flex\"><div class=\"item\" id=\"a\"></div><div class=\"item\" id=\"b\" style=\"order:-1\"></div></div>",
                Css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0);
            var b = ChildAt(fb, 1);
            // 'b' had order -1 so it lays first; positions reflect that.
            Assert.That(b.X, Is.EqualTo(0).Within(0.001));
            Assert.That(a.X, Is.EqualTo(100).Within(0.001));
        }

        [Test]
        public void Equal_order_keeps_document_order() {
            var (root, _, _) = Build(
                "<div class=\"flex\"><div class=\"item\" style=\"order:1\"></div><div class=\"item\" style=\"order:1\"></div></div>",
                Css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1);
            Assert.That(a.X, Is.EqualTo(0).Within(0.001));
            Assert.That(b.X, Is.EqualTo(100).Within(0.001));
        }

        [Test]
        public void Order_does_not_affect_cross_axis_position() {
            var (root, _, _) = Build(
                "<div class=\"flex\" style=\"height:200px\"><div class=\"item\" style=\"order:5\"></div><div class=\"item\" style=\"order:1\"></div></div>",
                Css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0);
            var b = ChildAt(fb, 1);
            Assert.That(a.Y, Is.EqualTo(0).Within(0.001));
            Assert.That(b.Y, Is.EqualTo(0).Within(0.001));
        }
    }
}
