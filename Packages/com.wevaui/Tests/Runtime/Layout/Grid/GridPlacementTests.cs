using NUnit.Framework;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Grid.GridTestHelpers;

namespace Weva.Tests.Layout.Grid {
    public class GridPlacementTests {
        [Test]
        public void Explicit_grid_row_and_column_resolve_correctly() {
            const string css = @"
                .grid { display: grid; grid-template-columns: 100px 100px 100px; grid-template-rows: 80px 80px 80px; width: 300px; }
                .item { grid-row-start: 2; grid-row-end: 4; grid-column-start: 1; grid-column-end: 3; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div class=\"item\"></div></div>",
                css, viewportWidth: 800);
            var item = FindByClass(root, "item");
            Assert.That(item.X, Is.EqualTo(0).Within(0.01));
            Assert.That(item.Y, Is.EqualTo(80).Within(0.01));
            Assert.That(item.Width, Is.EqualTo(200).Within(0.01));
            Assert.That(item.Height, Is.EqualTo(160).Within(0.01));
        }

        [Test]
        public void Grid_row_span_two() {
            const string css = @"
                .grid { display: grid; grid-template-columns: 100px 100px; grid-template-rows: 50px 50px 50px; width: 200px; }
                .item { grid-row-start: 1; grid-row-end: span 2; grid-column-start: 1; grid-column-end: 2; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div class=\"item\"></div></div>",
                css, viewportWidth: 400);
            var item = FindByClass(root, "item");
            Assert.That(item.Y, Is.EqualTo(0).Within(0.01));
            Assert.That(item.Height, Is.EqualTo(100).Within(0.01));
        }

        [Test]
        public void Negative_column_start_resolves_from_end() {
            const string css = @"
                .grid { display: grid; grid-template-columns: 50px 50px 50px 50px; grid-template-rows: 30px; width: 200px; }
                .item { grid-row-start: 1; grid-row-end: 2; grid-column-start: -2; grid-column-end: -1; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div class=\"item\"></div></div>",
                css, viewportWidth: 400);
            var item = FindByClass(root, "item");
            // -2 -> line index 4 (5 lines for 4 tracks: 1,2,3,4,5; -1=5, -2=4); -1 -> 5.
            // So columns 4..5 -> the 4th track (last). X = 150.
            Assert.That(item.X, Is.EqualTo(150).Within(0.01));
            Assert.That(item.Width, Is.EqualTo(50).Within(0.01));
        }

        [Test]
        public void Grid_row_one_to_negative_one_spans_full_grid() {
            const string css = @"
                .grid { display: grid; grid-template-columns: 100px; grid-template-rows: 30px 30px 30px; width: 100px; }
                .item { grid-column-start: 1; grid-column-end: 2; grid-row-start: 1; grid-row-end: -1; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div class=\"item\"></div></div>",
                css, viewportWidth: 400);
            var item = FindByClass(root, "item");
            Assert.That(item.Y, Is.EqualTo(0).Within(0.01));
            Assert.That(item.Height, Is.EqualTo(90).Within(0.01));
        }

        [Test]
        public void Named_area_resolves_to_template_areas_region() {
            const string css = @"
                .grid {
                    display: grid;
                    grid-template-columns: 100px 100px;
                    grid-template-rows: 50px 50px;
                    grid-template-areas: ""a a"" ""b c"";
                    width: 200px;
                }
                .x { grid-area: a; }
                .y { grid-area: c; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div class=\"x\"></div><div class=\"y\"></div></div>",
                css, viewportWidth: 400);
            var x = FindByClass(root, "x");
            var y = FindByClass(root, "y");
            Assert.That(x.X, Is.EqualTo(0).Within(0.01));
            Assert.That(x.Y, Is.EqualTo(0).Within(0.01));
            Assert.That(x.Width, Is.EqualTo(200).Within(0.01));
            Assert.That(x.Height, Is.EqualTo(50).Within(0.01));
            Assert.That(y.X, Is.EqualTo(100).Within(0.01));
            Assert.That(y.Y, Is.EqualTo(50).Within(0.01));
            Assert.That(y.Width, Is.EqualTo(100).Within(0.01));
            Assert.That(y.Height, Is.EqualTo(50).Within(0.01));
        }

        [Test]
        public void Named_area_three_by_two_places_e_in_center() {
            // 3-column x 2-row grid; child with grid-area: e should land at col 2, row 2.
            const string css = @"
                .grid {
                    display: grid;
                    grid-template-columns: 100px 100px 100px;
                    grid-template-rows: 50px 50px;
                    grid-template-areas: ""a b c"" ""d e f"";
                    width: 300px;
                }
                .item { grid-area: e; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div class=\"item\"></div></div>",
                css, viewportWidth: 400);
            var item = FindByClass(root, "item");
            Assert.That(item.X, Is.EqualTo(100).Within(0.01));
            Assert.That(item.Y, Is.EqualTo(50).Within(0.01));
            Assert.That(item.Width, Is.EqualTo(100).Within(0.01));
            Assert.That(item.Height, Is.EqualTo(50).Within(0.01));
        }

        [Test]
        public void Hud_three_by_three_named_areas_place_corners_correctly() {
            // Mirrors the randhtml demo's .hud layout: 3x3 with named corners,
            // edges and center hole. Verifies all named areas resolve to the
            // expected cells.
            const string css = @"
                .hud {
                    display: grid;
                    grid-template-columns: 100px 200px 100px;
                    grid-template-rows: 60px 100px 60px;
                    grid-template-areas:
                        ""tl  top  tr""
                        ""lft .    rgt""
                        ""bl  bot  br"";
                    width: 400px;
                }
                .tl { grid-area: tl; }
                .top { grid-area: top; }
                .tr { grid-area: tr; }
                .lft { grid-area: lft; }
                .rgt { grid-area: rgt; }
                .bl { grid-area: bl; }
                .bot { grid-area: bot; }
                .br { grid-area: br; }
            ";
            var (root, _, _) = Build(
                "<div class=\"hud\">" +
                "<div class=\"tl\"></div><div class=\"top\"></div><div class=\"tr\"></div>" +
                "<div class=\"lft\"></div><div class=\"rgt\"></div>" +
                "<div class=\"bl\"></div><div class=\"bot\"></div><div class=\"br\"></div>" +
                "</div>",
                css, viewportWidth: 600);
            var tl = FindByClass(root, "tl");
            Assert.That(tl.X, Is.EqualTo(0).Within(0.01));
            Assert.That(tl.Y, Is.EqualTo(0).Within(0.01));
            var top = FindByClass(root, "top");
            Assert.That(top.X, Is.EqualTo(100).Within(0.01));
            Assert.That(top.Y, Is.EqualTo(0).Within(0.01));
            Assert.That(top.Width, Is.EqualTo(200).Within(0.01));
            var tr = FindByClass(root, "tr");
            Assert.That(tr.X, Is.EqualTo(300).Within(0.01));
            Assert.That(tr.Y, Is.EqualTo(0).Within(0.01));
            var lft = FindByClass(root, "lft");
            Assert.That(lft.X, Is.EqualTo(0).Within(0.01));
            Assert.That(lft.Y, Is.EqualTo(60).Within(0.01));
            var rgt = FindByClass(root, "rgt");
            Assert.That(rgt.X, Is.EqualTo(300).Within(0.01));
            Assert.That(rgt.Y, Is.EqualTo(60).Within(0.01));
            var bl = FindByClass(root, "bl");
            Assert.That(bl.X, Is.EqualTo(0).Within(0.01));
            Assert.That(bl.Y, Is.EqualTo(160).Within(0.01));
            var bot = FindByClass(root, "bot");
            Assert.That(bot.X, Is.EqualTo(100).Within(0.01));
            Assert.That(bot.Y, Is.EqualTo(160).Within(0.01));
            var br = FindByClass(root, "br");
            Assert.That(br.X, Is.EqualTo(300).Within(0.01));
            Assert.That(br.Y, Is.EqualTo(160).Within(0.01));
        }

        [Test]
        public void Grid_area_four_value_form() {
            const string css = @"
                .grid { display: grid; grid-template-columns: 50px 50px 50px 50px; grid-template-rows: 30px 30px 30px; width: 200px; }
                .item { grid-area: 1 / 2 / 3 / 4; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div class=\"item\"></div></div>",
                css, viewportWidth: 400);
            var item = FindByClass(root, "item");
            Assert.That(item.X, Is.EqualTo(50).Within(0.01));
            Assert.That(item.Y, Is.EqualTo(0).Within(0.01));
            Assert.That(item.Width, Is.EqualTo(100).Within(0.01));
            Assert.That(item.Height, Is.EqualTo(60).Within(0.01));
        }

        [Test]
        public void Auto_placement_fills_row_major() {
            const string css = @"
                .grid { display: grid; grid-template-columns: 50px 50px 50px; grid-template-rows: 30px 30px; width: 150px; }
                .item { background: red; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div class=\"item\"></div><div class=\"item\"></div><div class=\"item\"></div><div class=\"item\"></div></div>",
                css, viewportWidth: 400);
            var grid = FindGridByClass(root, "grid");
            var c0 = ChildAt(grid, 0);
            var c1 = ChildAt(grid, 1);
            var c2 = ChildAt(grid, 2);
            var c3 = ChildAt(grid, 3);
            Assert.That(c0.X, Is.EqualTo(0).Within(0.01));
            Assert.That(c0.Y, Is.EqualTo(0).Within(0.01));
            Assert.That(c1.X, Is.EqualTo(50).Within(0.01));
            Assert.That(c2.X, Is.EqualTo(100).Within(0.01));
            Assert.That(c3.X, Is.EqualTo(0).Within(0.01));
            Assert.That(c3.Y, Is.EqualTo(30).Within(0.01));
        }

        [Test]
        public void Auto_placement_column_flow_fills_top_to_bottom() {
            const string css = @"
                .grid { display: grid; grid-template-columns: 50px 50px; grid-template-rows: 30px 30px; grid-auto-flow: column; width: 100px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div></div><div></div><div></div><div></div></div>",
                css, viewportWidth: 400);
            var grid = FindGridByClass(root, "grid");
            var c0 = ChildAt(grid, 0);
            var c1 = ChildAt(grid, 1);
            Assert.That(c0.X, Is.EqualTo(0).Within(0.01));
            Assert.That(c0.Y, Is.EqualTo(0).Within(0.01));
            Assert.That(c1.X, Is.EqualTo(0).Within(0.01));
            Assert.That(c1.Y, Is.EqualTo(30).Within(0.01));
        }

        [Test]
        public void Sparse_vs_dense_differs_when_holes_exist() {
            // Sparse: cursor never goes back, so item-2 (1x1) lands AFTER item-1 (1x2 starting on row 1 col 2).
            const string sparseCss = @"
                .grid { display: grid; grid-template-columns: 50px 50px 50px; grid-template-rows: 30px 30px; width: 150px; }
                .a { grid-column-start: 2; grid-column-end: span 2; }
                .b { background: red; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div class=\"a\"></div><div class=\"b\"></div></div>",
                sparseCss, viewportWidth: 400);
            var b = FindByClass(root, "b");
            // Sparse (default) places .b on row 2 col 1.
            Assert.That(b.Y, Is.EqualTo(30).Within(0.01));
            Assert.That(b.X, Is.EqualTo(0).Within(0.01));

            const string denseCss = @"
                .grid { display: grid; grid-template-columns: 50px 50px 50px; grid-template-rows: 30px 30px; grid-auto-flow: row dense; width: 150px; }
                .a { grid-column-start: 2; grid-column-end: span 2; }
                .b { background: red; }
            ";
            var (root2, _, _) = Build(
                "<div class=\"grid\"><div class=\"a\"></div><div class=\"b\"></div></div>",
                denseCss, viewportWidth: 400);
            var b2 = FindByClass(root2, "b");
            // Dense backfills the hole at row 1 col 1.
            Assert.That(b2.Y, Is.EqualTo(0).Within(0.01));
            Assert.That(b2.X, Is.EqualTo(0).Within(0.01));
        }

        [Test]
        public void Span_one_resolves_to_default_width() {
            const string css = @"
                .grid { display: grid; grid-template-columns: 100px 100px 100px; grid-template-rows: 30px; width: 300px; }
                .item { grid-column-start: 2; grid-column-end: span 1; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div class=\"item\"></div></div>",
                css, viewportWidth: 400);
            var item = FindByClass(root, "item");
            Assert.That(item.X, Is.EqualTo(100).Within(0.01));
            Assert.That(item.Width, Is.EqualTo(100).Within(0.01));
        }

        [Test]
        public void Grid_column_shorthand_with_slash() {
            const string css = @"
                .grid { display: grid; grid-template-columns: 50px 50px 50px 50px; grid-template-rows: 30px; width: 200px; }
                .item { grid-column: 2 / 4; grid-row: 1 / 2; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div class=\"item\"></div></div>",
                css, viewportWidth: 400);
            var item = FindByClass(root, "item");
            Assert.That(item.X, Is.EqualTo(50).Within(0.01));
            Assert.That(item.Width, Is.EqualTo(100).Within(0.01));
        }

        [Test]
        public void Custom_ident_with_integer_picks_nth_named_line() {
            // Per CSS Grid L1 §8: `<custom-ident> <integer>` selects the Nth
            // line carrying that name. Three lines named "col" sit at the
            // boundaries 0/100/200/300. `col 2` is the second `col` line (x=100);
            // `col 3` is the third (x=200) -- so the item spans col2..col3,
            // occupying the second 100px track.
            const string css = @"
                .grid { display: grid; grid-template-columns: [col] 100px [col] 100px [col] 100px; grid-template-rows: 30px; width: 300px; }
                .x { grid-column: col 2 / col 3; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div class=\"x\"></div></div>",
                css, viewportWidth: 400);
            var item = FindByClass(root, "x");
            Assert.That(item.X, Is.EqualTo(100).Within(0.01));
            Assert.That(item.Width, Is.EqualTo(100).Within(0.01));
        }

        [Test]
        public void Bare_custom_ident_still_resolves_to_first_named_line() {
            // Regression: with no integer suffix, a bare name continues to
            // resolve to the first matching line. Here the first `col` is at
            // x=0 and the second is at x=100, so `col / col` spans the first
            // 100px column. The bare-name path must not regress when the
            // resolver gains the "Nth match" logic.
            const string css = @"
                .grid { display: grid; grid-template-columns: [col] 100px [col] 100px [col] 100px; grid-template-rows: 30px; width: 300px; }
                .x { grid-column-start: col; grid-column-end: 2; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div class=\"x\"></div></div>",
                css, viewportWidth: 400);
            var item = FindByClass(root, "x");
            Assert.That(item.X, Is.EqualTo(0).Within(0.01));
            Assert.That(item.Width, Is.EqualTo(100).Within(0.01));
        }

        [Test]
        public void Implicit_grid_extends_with_auto_rows() {
            const string css = @"
                .grid { display: grid; grid-template-columns: 50px 50px; grid-auto-rows: 40px; width: 100px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div></div><div></div><div></div><div></div></div>",
                css, viewportWidth: 400);
            var grid = FindGridByClass(root, "grid");
            var c2 = ChildAt(grid, 2);
            // Third item lands on row 2 col 1; row 2 height = grid-auto-rows = 40.
            Assert.That(c2.Y, Is.EqualTo(40).Within(0.01));
            Assert.That(c2.Height, Is.EqualTo(40).Within(0.01));
        }
    }
}
