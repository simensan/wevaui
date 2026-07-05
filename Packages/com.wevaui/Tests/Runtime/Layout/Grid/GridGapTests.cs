using NUnit.Framework;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Grid.GridTestHelpers;

namespace Weva.Tests.Layout.Grid {
    public class GridGapTests {
        [Test]
        public void Column_gap_separates_columns() {
            const string css = @"
                .grid { display: grid; grid-template-columns: 100px 100px; column-gap: 20px; width: 220px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div></div><div></div></div>",
                css, viewportWidth: 800);
            var grid = FindGridByClass(root, "grid");
            Assert.That(ChildAt(grid, 0).X, Is.EqualTo(0).Within(0.01));
            Assert.That(ChildAt(grid, 1).X, Is.EqualTo(120).Within(0.01));
        }

        [Test]
        public void Row_gap_separates_rows() {
            const string css = @"
                .grid { display: grid; grid-template-columns: 100px; grid-template-rows: 30px 30px; row-gap: 15px; width: 100px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div></div><div></div></div>",
                css, viewportWidth: 800);
            var grid = FindGridByClass(root, "grid");
            Assert.That(ChildAt(grid, 0).Y, Is.EqualTo(0).Within(0.01));
            Assert.That(ChildAt(grid, 1).Y, Is.EqualTo(45).Within(0.01));
        }

        [Test]
        public void Gap_shorthand_sets_both_axes() {
            const string css = @"
                .grid { display: grid; grid-template-columns: 50px 50px; grid-template-rows: 30px 30px; gap: 10px; width: 110px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div></div><div></div><div></div><div></div></div>",
                css, viewportWidth: 800);
            var grid = FindGridByClass(root, "grid");
            Assert.That(ChildAt(grid, 1).X, Is.EqualTo(60).Within(0.01));
            Assert.That(ChildAt(grid, 2).Y, Is.EqualTo(40).Within(0.01));
        }

        [Test]
        public void Gap_respected_in_fr_distribution() {
            const string css = @"
                .grid { display: grid; grid-template-columns: 1fr 1fr; gap: 40px; width: 240px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div></div><div></div></div>",
                css, viewportWidth: 800);
            var grid = FindGridByClass(root, "grid");
            Assert.That(ChildAt(grid, 0).Width, Is.EqualTo(100).Within(0.01));
            Assert.That(ChildAt(grid, 1).Width, Is.EqualTo(100).Within(0.01));
            Assert.That(ChildAt(grid, 1).X, Is.EqualTo(140).Within(0.01));
        }

        // E2 (CSS Box Alignment L3 §8.3): row-gap percentages resolve against
        // the grid container's block-axis (height in horizontal-tb), NOT the
        // inline axis. With width=400 and height=200, `row-gap: 50%` must
        // resolve to 100px (50% of 200), not 200px (50% of 400).
        [Test]
        public void Row_gap_percent_resolves_against_container_height_E2() {
            const string css = @"
                .grid {
                    display: grid;
                    grid-template-columns: 100px;
                    grid-template-rows: 30px 30px;
                    row-gap: 50%;
                    width: 400px;
                    height: 200px;
                }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div></div><div></div></div>",
                css, viewportWidth: 800);
            var grid = FindGridByClass(root, "grid");
            // row1 sits at 0; row2 sits at row1.height (30) + row-gap.
            // 50% of height(200) = 100px → row2.Y = 130.
            Assert.That(ChildAt(grid, 0).Y, Is.EqualTo(0).Within(0.01));
            Assert.That(ChildAt(grid, 1).Y, Is.EqualTo(130).Within(0.01));
        }

        // Regression pin for E2: column-gap percentages MUST continue to
        // resolve against width (the inline-axis size). With width=200 and
        // height=400, `column-gap: 50%` must be 100px (50% of 200), not
        // 200px (50% of 400).
        [Test]
        public void Column_gap_percent_resolves_against_container_width_E2_regression() {
            const string css = @"
                .grid {
                    display: grid;
                    grid-template-columns: 50px 50px;
                    grid-template-rows: 30px;
                    column-gap: 50%;
                    width: 200px;
                    height: 400px;
                }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div></div><div></div></div>",
                css, viewportWidth: 800);
            var grid = FindGridByClass(root, "grid");
            // col1 at 0; col2 = col1.width(50) + column-gap.
            // 50% of width(200) = 100px → col2.X = 150.
            Assert.That(ChildAt(grid, 0).X, Is.EqualTo(0).Within(0.01));
            Assert.That(ChildAt(grid, 1).X, Is.EqualTo(150).Within(0.01));
        }

        // Indefinite block-axis collapse: with `height: auto` the percentage
        // row-gap contributes 0 per spec (CSS Box Alignment L3 §8.3 — "If the
        // size of the containing block is indefinite, this resolves to zero").
        [Test]
        public void Row_gap_percent_collapses_to_zero_when_height_indefinite_E2() {
            const string css = @"
                .grid {
                    display: grid;
                    grid-template-columns: 100px;
                    grid-template-rows: 30px 30px;
                    row-gap: 50%;
                    width: 400px;
                }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div></div><div></div></div>",
                css, viewportWidth: 800);
            var grid = FindGridByClass(root, "grid");
            // No definite height → 50% row-gap collapses to 0 → rows pack
            // with zero gap between them.
            Assert.That(ChildAt(grid, 0).Y, Is.EqualTo(0).Within(0.01));
            Assert.That(ChildAt(grid, 1).Y, Is.EqualTo(30).Within(0.01));
        }
    }
}
