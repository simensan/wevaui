using NUnit.Framework;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Grid.GridTestHelpers;

namespace Weva.Tests.Layout.Grid {
    // E7 — CSS Grid Layout L1 §9: an absolutely-positioned child of a grid
    // container whose `grid-column-*` / `grid-row-*` / `grid-area` resolves
    // to a definite grid area uses THAT area as its containing block, NOT
    // the grid container's padding edge. When the placement is `auto` on
    // both axes (no grid-* properties set), the fallback to the padding
    // edge is preserved per spec.
    public class GridAbsPosContainingBlockTests {
        [Test]
        public void AbsPos_with_grid_placement_origin_at_cell_top_left() {
            // grid-template-columns: 100px 100px 100px, item placed in column
            // 2 / row 1 with `left:0; top:0` should land at the origin of
            // column 2 (x=100), NOT the grid container's padding edge (x=0).
            const string css = @"
                .grid {
                    display: grid;
                    grid-template-columns: 100px 100px 100px;
                    grid-template-rows: 60px 60px;
                    width: 300px;
                    position: relative;
                }
                .item {
                    position: absolute;
                    grid-column: 2;
                    grid-row: 1;
                    left: 0;
                    top: 0;
                    width: 20px;
                    height: 20px;
                }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div class=\"item\"></div></div>",
                css, viewportWidth: 800);
            var item = FindByClass(root, "item");
            Assert.That(item, Is.Not.Null);
            Assert.That(item.X, Is.EqualTo(100).Within(0.01),
                "abs-pos child with grid-column:2 + left:0 must align to the column-2 track start, not the grid container's padding edge");
            Assert.That(item.Y, Is.EqualTo(0).Within(0.01),
                "row 1 starts at y=0");
        }

        [Test]
        public void AbsPos_with_grid_placement_percent_size_fills_grid_area() {
            // width:100%; height:100% on an abs-pos grid child with definite
            // placement should resolve against the GRID AREA (200x120 here),
            // not the grid container's padding box (400x120).
            const string css = @"
                .grid {
                    display: grid;
                    grid-template-columns: 100px 100px 200px;
                    grid-template-rows: 60px 60px;
                    width: 400px;
                    position: relative;
                }
                .item {
                    position: absolute;
                    grid-column-start: 3;
                    grid-column-end: 4;
                    grid-row-start: 1;
                    grid-row-end: 3;
                    left: 0;
                    top: 0;
                    width: 100%;
                    height: 100%;
                }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div class=\"item\"></div></div>",
                css, viewportWidth: 800);
            var item = FindByClass(root, "item");
            Assert.That(item, Is.Not.Null);
            // Column 3 starts at x = 200, row 1..3 spans 60 + 60 = 120.
            Assert.That(item.X, Is.EqualTo(200).Within(0.01));
            Assert.That(item.Y, Is.EqualTo(0).Within(0.01));
            Assert.That(item.Width, Is.EqualTo(200).Within(0.01),
                "width:100% on an abs-pos grid child resolves against the grid AREA width, not the grid container's");
            Assert.That(item.Height, Is.EqualTo(120).Within(0.01),
                "height:100% resolves against the spanned row tracks");
        }

        [Test]
        public void AbsPos_without_grid_placement_falls_back_to_padding_edge() {
            // No grid-* placement properties => CB falls back to the grid
            // container's padding edge per CSS Position L3 §4.3 (the spec
            // default). `left: 0` lands at the container's left padding
            // edge (x=0), and `width: 100%` resolves against the container's
            // padding-box width (400px).
            const string css = @"
                .grid {
                    display: grid;
                    grid-template-columns: 100px 100px 200px;
                    grid-template-rows: 60px;
                    width: 400px;
                    position: relative;
                }
                .item {
                    position: absolute;
                    left: 0;
                    top: 0;
                    width: 100%;
                    height: 40px;
                }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div class=\"item\"></div></div>",
                css, viewportWidth: 800);
            var item = FindByClass(root, "item");
            Assert.That(item, Is.Not.Null);
            Assert.That(item.X, Is.EqualTo(0).Within(0.01),
                "without explicit grid placement, abs-pos falls back to the grid container's padding edge");
            Assert.That(item.Y, Is.EqualTo(0).Within(0.01));
            Assert.That(item.Width, Is.EqualTo(400).Within(0.01),
                "width:100% resolves against the grid container's full padding-box width");
        }

        [Test]
        public void AbsPos_with_grid_area_named_uses_named_area_as_cb() {
            // `grid-area: b` should resolve to the named area's rect for the
            // containing block. Area `b` here is the bottom row, column 1
            // (0..100 x 60..120). `left:0; top:0; width:100%; height:100%`
            // must fill it.
            const string css = @"
                .grid {
                    display: grid;
                    grid-template-columns: 100px 100px;
                    grid-template-rows: 60px 60px;
                    grid-template-areas: ""a a"" ""b c"";
                    width: 200px;
                    position: relative;
                }
                .item {
                    position: absolute;
                    grid-area: b;
                    left: 0;
                    top: 0;
                    width: 100%;
                    height: 100%;
                }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div class=\"item\"></div></div>",
                css, viewportWidth: 400);
            var item = FindByClass(root, "item");
            Assert.That(item, Is.Not.Null);
            Assert.That(item.X, Is.EqualTo(0).Within(0.01));
            Assert.That(item.Y, Is.EqualTo(60).Within(0.01),
                "named area `b` is the bottom row, starting at y=60");
            Assert.That(item.Width, Is.EqualTo(100).Within(0.01));
            Assert.That(item.Height, Is.EqualTo(60).Within(0.01));
        }
    }
}
