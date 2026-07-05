using NUnit.Framework;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Flex.FlexTestHelpers;

namespace Weva.Tests.Layout.Flex {
    // CSS Flexbox §8.3: `align-self: stretch` resizes the flex item to fill
    // the cross axis, but the stretched cross size MUST be clamped by the
    // item's min/max cross-axis size (min-/max-height in row flex,
    // min-/max-width in column flex). Tracker item A12.
    public class FlexAlignSelfStretchMinMaxTests {

        [Test]
        public void Stretch_clamps_to_max_height_in_row_flex() {
            // Row flex, cross axis = height. Container is 200px tall; the
            // item's max-height: 50px must cap the stretched height to 50.
            const string css = @"
                .flex { display: flex; width: 600px; height: 200px; align-items: stretch; }
                .item { width: 100px; max-height: 50px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"flex\"><div class=\"item\"></div></div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0);
            Assert.That(a.Height, Is.EqualTo(50).Within(0.001),
                "align-self:stretch should clamp the stretched height to max-height.");
        }

        [Test]
        public void Stretch_expands_to_min_height_when_container_is_smaller_in_row_flex() {
            // Row flex, cross axis = height. Container is only 100px tall but
            // min-height: 200px must override and grow the item to 200.
            const string css = @"
                .flex { display: flex; width: 600px; height: 100px; align-items: stretch; }
                .item { width: 100px; min-height: 200px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"flex\"><div class=\"item\"></div></div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0);
            Assert.That(a.Height, Is.EqualTo(200).Within(0.001),
                "align-self:stretch should expand to min-height even when it exceeds the line cross size.");
        }

        [Test]
        public void Row_flex_max_width_does_not_clamp_cross_but_column_flex_max_width_does() {
            // Row flex: cross axis = height, so a max-width on the item must
            // NOT affect the stretched height. The item should still stretch
            // to the full 200px container height.
            const string rowCss = @"
                .flex { display: flex; flex-direction: row; width: 600px; height: 200px; align-items: stretch; }
                .item { width: 100px; max-width: 30px; }
            ";
            var (rowRoot, _, _) = Build(
                "<div class=\"flex\"><div class=\"item\"></div></div>",
                rowCss, viewportWidth: 800);
            var rowFlex = FindFlex(rowRoot, "div");
            var rowItem = ChildAt(rowFlex, 0);
            Assert.That(rowItem.Height, Is.EqualTo(200).Within(0.001),
                "Row flex stretches on height; max-width must not clamp the cross axis.");

            // Column flex: cross axis = width. Same max-width: 30px must now
            // clamp the stretched width to 30, even though the container is
            // 600px wide. height:100px is set so the column flex has a
            // definite main size; max-width applies only to the cross axis.
            const string colCss = @"
                .flex { display: flex; flex-direction: column; width: 600px; height: 100px; align-items: stretch; }
                .item { height: 50px; max-width: 30px; }
            ";
            var (colRoot, _, _) = Build(
                "<div class=\"flex\"><div class=\"item\"></div></div>",
                colCss, viewportWidth: 800);
            var colFlex = FindFlex(colRoot, "div");
            var colItem = ChildAt(colFlex, 0);
            Assert.That(colItem.Width, Is.EqualTo(30).Within(0.001),
                "Column flex stretches on width; max-width must clamp the stretched width to 30.");
        }
    }
}
