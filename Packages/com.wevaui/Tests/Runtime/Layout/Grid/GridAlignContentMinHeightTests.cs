using System.Linq;
using NUnit.Framework;
using Weva.Dom;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout.Grid {
    // Regression (grid-playground): align-content on a grid whose height is auto
    // but floored by a definite min-height. The min-height must give align-content
    // real free space (rows fill the min-height container) — and FinalizeContainerSize
    // must not collapse the grid back below min-height. The engine zeroed the
    // row-axis available size for auto-height grids and ignored min-height, so
    // align-content:space-between packed the rows at the top instead of spreading
    // them to the bottom.
    public class GridAlignContentMinHeightTests {

        static Box FirstWithClass(Box root, string cls) =>
            AllBoxes(root).FirstOrDefault(b =>
                b.Element != null && (b.Element.GetAttribute("class") ?? "").Split(' ').Contains(cls));

        static double AbsY(Box b) { double y = 0; for (var p = b; p != null; p = p.Parent) y += p.Y; return y; }

        [Test]
        public void Align_content_space_between_spreads_rows_to_min_height() {
            // content-box (default): min-height 150 => content height 150.
            // 2 rows x 40 = 80, no gap => free 70. space-between: row1 top, row2
            // bottom (content top + 150 - 40 = +110).
            const string css = @"
                .g { display: grid; grid-template-columns: repeat(2, 60px);
                     grid-template-rows: repeat(2, 40px);
                     align-content: space-between; min-height: 150px; }";
            const string html =
                @"<div class='g'><div class='c c1'></div><div class='c c2'></div><div class='c c3'></div><div class='c c4'></div></div>";
            var (root, _, _) = Build(html, css, viewportWidth: 600);

            var g = FirstWithClass(root, "g");
            var c1 = FirstWithClass(root, "c1");
            var c3 = FirstWithClass(root, "c3");
            Assert.That(g, Is.Not.Null);
            Assert.That(g.Height, Is.GreaterThanOrEqualTo(149.5),
                $"grid must keep its min-height (150), got {g.Height:F1}");
            double gTop = AbsY(g);
            // Row 1 at the top.
            Assert.That(AbsY(c1) - gTop, Is.EqualTo(0).Within(1.0), $"row1 at top, got {AbsY(c1) - gTop:F1}");
            // Row 2 pushed to the bottom by space-between (top + 110).
            Assert.That(AbsY(c3) - gTop, Is.EqualTo(110).Within(1.5),
                $"row2 should be spread to the bottom (~110 from grid top), got {AbsY(c3) - gTop:F1}");
        }
    }
}
