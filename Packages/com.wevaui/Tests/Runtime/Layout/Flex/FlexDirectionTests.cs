using NUnit.Framework;
using Weva.Layout.Flex;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Flex.FlexTestHelpers;

namespace Weva.Tests.Layout.Flex {
    public class FlexDirectionTests {
        const string FlexCss = @"
            .flex { display: flex; }
            .col { display: flex; flex-direction: column; }
            .colr { display: flex; flex-direction: column-reverse; }
            .rowr { display: flex; flex-direction: row-reverse; }
            .item { width: 100px; height: 50px; }
        ";

        [Test]
        public void Row_default_lays_items_horizontally() {
            var (root, _, _) = Build(
                "<div class=\"flex\"><div class=\"item\"></div><div class=\"item\"></div><div class=\"item\"></div></div>",
                FlexCss, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            Assert.That(fb, Is.Not.Null);
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1); var c = ChildAt(fb, 2);
            Assert.That(a.X, Is.EqualTo(0).Within(0.001));
            Assert.That(b.X, Is.EqualTo(100).Within(0.001));
            Assert.That(c.X, Is.EqualTo(200).Within(0.001));
            Assert.That(a.Y, Is.EqualTo(0).Within(0.001));
            Assert.That(b.Y, Is.EqualTo(0).Within(0.001));
            Assert.That(c.Y, Is.EqualTo(0).Within(0.001));
        }

        [Test]
        public void Column_stacks_items_vertically() {
            var (root, _, _) = Build(
                "<div class=\"col\" style=\"height:300px\"><div class=\"item\"></div><div class=\"item\"></div></div>",
                FlexCss, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1);
            Assert.That(a.X, Is.EqualTo(0).Within(0.001));
            Assert.That(b.X, Is.EqualTo(0).Within(0.001));
            Assert.That(a.Y, Is.EqualTo(0).Within(0.001));
            Assert.That(b.Y, Is.EqualTo(50).Within(0.001));
        }

        [Test]
        public void Row_reverse_positions_from_far_end() {
            var (root, _, _) = Build(
                "<div class=\"rowr\" style=\"width:600px\"><div class=\"item\"></div><div class=\"item\"></div></div>",
                FlexCss, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1);
            Assert.That(a.X, Is.EqualTo(500).Within(0.001));
            Assert.That(b.X, Is.EqualTo(400).Within(0.001));
        }

        [Test]
        public void Direction_rtl_flips_row_main_axis() {
            var (root, _, _) = Build(
                "<div class=\"flex\" style=\"direction:rtl;width:600px\"><div class=\"item\"></div><div class=\"item\"></div></div>",
                FlexCss, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1);
            Assert.That(a.X, Is.EqualTo(500).Within(0.001));
            Assert.That(b.X, Is.EqualTo(400).Within(0.001));
        }

        [Test]
        public void Column_reverse_positions_from_bottom() {
            var (root, _, _) = Build(
                "<div class=\"colr\" style=\"height:300px\"><div class=\"item\"></div><div class=\"item\"></div></div>",
                FlexCss, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1);
            Assert.That(a.Y, Is.EqualTo(250).Within(0.001));
            Assert.That(b.Y, Is.EqualTo(200).Within(0.001));
        }

        [Test]
        public void Row_default_stretches_items_to_container_height() {
            var (root, _, _) = Build(
                "<div class=\"flex\" style=\"height:200px\"><div style=\"width:100px\"></div></div>",
                FlexCss, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0);
            Assert.That(a.Height, Is.EqualTo(200).Within(0.001));
        }

        [Test]
        public void Column_stretches_items_to_container_width() {
            var (root, _, _) = Build(
                "<div class=\"col\" style=\"width:300px;height:400px\"><div style=\"height:50px\"></div></div>",
                FlexCss, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0);
            Assert.That(a.Width, Is.EqualTo(300).Within(0.001));
        }

        [Test]
        public void Row_blockified_spans_are_content_sized_not_container_width() {
            var (root, _, _) = Build(
                "<div class=\"flex\" style=\"width:400px\">"
                + "<span>AB</span>"
                + "<span>CD</span>"
                + "</div>",
                FlexCss, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            Assert.That(fb, Is.Not.Null);
            var a = ChildAt(fb, 0);
            var b = ChildAt(fb, 1);
            Assert.That(a, Is.Not.Null);
            Assert.That(b, Is.Not.Null);
            Assert.That(a.Width, Is.LessThan(200), "First span should be content-sized, not half or full container width");
            Assert.That(b.Width, Is.LessThan(200), "Second span should be content-sized, not half or full container width");
            Assert.That(b.X, Is.GreaterThan(a.X), "Second span should be to the right of first (row layout)");
            Assert.That(a.Y, Is.EqualTo(b.Y).Within(0.001), "Both spans should be on the same row");
        }

        [Test]
        public void Row_div_children_without_explicit_width_are_content_sized() {
            var (root, _, _) = Build(
                "<div class=\"flex\" style=\"width:400px\">"
                + "<div>Hello</div>"
                + "<div>World</div>"
                + "</div>",
                FlexCss, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            Assert.That(fb, Is.Not.Null);
            var a = ChildAt(fb, 0);
            var b = ChildAt(fb, 1);
            Assert.That(a, Is.Not.Null);
            Assert.That(b, Is.Not.Null);
            Assert.That(a.Width, Is.LessThan(200), "First div should be content-sized, not half or full container width");
            Assert.That(b.X, Is.GreaterThan(a.X), "Second div should be to the right of first (row layout)");
        }
    }
}
