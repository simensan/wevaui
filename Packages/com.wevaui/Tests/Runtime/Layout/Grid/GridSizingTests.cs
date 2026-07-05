using NUnit.Framework;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Grid.GridTestHelpers;

namespace Weva.Tests.Layout.Grid {
    public class GridSizingTests {
        [Test]
        public void Two_fr_tracks_split_container_in_half() {
            const string css = @"
                .grid { display: grid; grid-template-columns: 1fr 1fr; width: 400px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div></div><div></div></div>",
                css, viewportWidth: 800);
            var grid = FindGridByClass(root, "grid");
            var c0 = ChildAt(grid, 0);
            var c1 = ChildAt(grid, 1);
            Assert.That(c0.Width, Is.EqualTo(200).Within(0.01));
            Assert.That(c1.Width, Is.EqualTo(200).Within(0.01));
            Assert.That(c1.X, Is.EqualTo(200).Within(0.01));
        }

        [Test]
        public void Fr_one_to_two_splits_one_to_two() {
            const string css = @"
                .grid { display: grid; grid-template-columns: 1fr 2fr; width: 300px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div></div><div></div></div>",
                css, viewportWidth: 800);
            var grid = FindGridByClass(root, "grid");
            Assert.That(ChildAt(grid, 0).Width, Is.EqualTo(100).Within(0.01));
            Assert.That(ChildAt(grid, 1).Width, Is.EqualTo(200).Within(0.01));
        }

        [Test]
        public void Fixed_plus_fr_reserves_then_fills() {
            const string css = @"
                .grid { display: grid; grid-template-columns: 100px 1fr; width: 400px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div></div><div></div></div>",
                css, viewportWidth: 800);
            var grid = FindGridByClass(root, "grid");
            Assert.That(ChildAt(grid, 0).Width, Is.EqualTo(100).Within(0.01));
            Assert.That(ChildAt(grid, 1).Width, Is.EqualTo(300).Within(0.01));
        }

        [Test]
        public void Auto_tracks_consume_no_fixed_space_when_empty() {
            const string css = @"
                .grid { display: grid; grid-template-columns: auto 1fr auto; width: 400px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div></div><div></div><div></div></div>",
                css, viewportWidth: 800);
            var grid = FindGridByClass(root, "grid");
            // Auto items have no intrinsic width here (empty divs); fr fills.
            var c1 = ChildAt(grid, 1);
            Assert.That(c1.Width, Is.GreaterThan(300).And.LessThanOrEqualTo(400));
        }

        [Test]
        public void Minmax_with_zero_min_distributes_fr() {
            const string css = @"
                .grid { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 1fr); width: 200px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div></div><div></div></div>",
                css, viewportWidth: 800);
            var grid = FindGridByClass(root, "grid");
            Assert.That(ChildAt(grid, 0).Width, Is.EqualTo(100).Within(0.01));
            Assert.That(ChildAt(grid, 1).Width, Is.EqualTo(100).Within(0.01));
        }

        [Test]
        public void Minmax_min_of_100_enforced() {
            const string css = @"
                .grid { display: grid; grid-template-columns: minmax(100px, 1fr) minmax(100px, 1fr); width: 200px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div></div><div></div></div>",
                css, viewportWidth: 800);
            var grid = FindGridByClass(root, "grid");
            Assert.That(ChildAt(grid, 0).Width, Is.EqualTo(100).Within(0.01));
            Assert.That(ChildAt(grid, 1).Width, Is.EqualTo(100).Within(0.01));
        }

        [Test]
        public void Repeat_three_fr_makes_three_equal_columns() {
            const string css = @"
                .grid { display: grid; grid-template-columns: repeat(3, 1fr); width: 600px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div></div><div></div><div></div></div>",
                css, viewportWidth: 800);
            var grid = FindGridByClass(root, "grid");
            for (int i = 0; i < 3; i++) {
                Assert.That(ChildAt(grid, i).Width, Is.EqualTo(200).Within(0.01));
                Assert.That(ChildAt(grid, i).X, Is.EqualTo(i * 200).Within(0.01));
            }
        }

        [Test]
        public void Auto_fill_creates_three_tracks_in_350px_for_100px_pattern() {
            const string css = @"
                .grid { display: grid; grid-template-columns: repeat(auto-fill, 100px); width: 350px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div></div><div></div><div></div></div>",
                css, viewportWidth: 800);
            var grid = FindGridByClass(root, "grid");
            for (int i = 0; i < 3; i++) {
                Assert.That(ChildAt(grid, i).Width, Is.EqualTo(100).Within(0.01));
                Assert.That(ChildAt(grid, i).X, Is.EqualTo(i * 100).Within(0.01));
            }
        }

        [Test]
        public void Auto_fit_collapses_empty_tracks() {
            const string css = @"
                .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(100px, 1fr)); width: 300px; }
                .single { background: red; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div class=\"single\"></div></div>",
                css, viewportWidth: 800);
            var grid = FindGridByClass(root, "grid");
            // 300px / 100px = 3 tracks materialised; only one has an item; auto-fit collapses
            // the other two; the single item track stretches to fill the container.
            var c0 = ChildAt(grid, 0);
            Assert.That(c0.Width, Is.EqualTo(300).Within(0.01));
        }

        [Test]
        public void Gap_reduces_available_for_fr() {
            const string css = @"
                .grid { display: grid; grid-template-columns: 1fr 1fr; gap: 20px; width: 220px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div></div><div></div></div>",
                css, viewportWidth: 800);
            var grid = FindGridByClass(root, "grid");
            Assert.That(ChildAt(grid, 0).Width, Is.EqualTo(100).Within(0.01));
            Assert.That(ChildAt(grid, 1).Width, Is.EqualTo(100).Within(0.01));
            Assert.That(ChildAt(grid, 1).X, Is.EqualTo(120).Within(0.01));
        }

        [Test]
        public void Three_fr_with_remainder_pixels_distribute_evenly() {
            const string css = @"
                .grid { display: grid; grid-template-columns: 1fr 1fr 1fr; width: 301px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div></div><div></div><div></div></div>",
                css, viewportWidth: 800);
            var grid = FindGridByClass(root, "grid");
            // 301/3 ≈ 100.33 each.
            Assert.That(ChildAt(grid, 0).Width + ChildAt(grid, 1).Width + ChildAt(grid, 2).Width,
                Is.EqualTo(301).Within(0.01));
        }

        [Test]
        public void Fr_zero_remainder_when_fixed_exceeds_container() {
            const string css = @"
                .grid { display: grid; grid-template-columns: 200px 200px 1fr; width: 300px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div></div><div></div><div></div></div>",
                css, viewportWidth: 800);
            var grid = FindGridByClass(root, "grid");
            // Fixed sums to 400 > 300, so fr track gets 0.
            Assert.That(ChildAt(grid, 2).Width, Is.LessThanOrEqualTo(0.01));
        }

        [Test]
        public void Percentage_track_resolves_against_container_width() {
            const string css = @"
                .grid { display: grid; grid-template-columns: 50% 50%; width: 400px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div></div><div></div></div>",
                css, viewportWidth: 800);
            var grid = FindGridByClass(root, "grid");
            Assert.That(ChildAt(grid, 0).Width, Is.EqualTo(200).Within(0.01));
            Assert.That(ChildAt(grid, 1).Width, Is.EqualTo(200).Within(0.01));
        }

        [Test]
        public void Single_fixed_track_does_not_overflow_other_items() {
            const string css = @"
                .grid { display: grid; grid-template-columns: 100px; grid-template-rows: 30px 30px; width: 100px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div></div><div></div></div>",
                css, viewportWidth: 800);
            var grid = FindGridByClass(root, "grid");
            Assert.That(ChildAt(grid, 0).X, Is.EqualTo(0).Within(0.01));
            Assert.That(ChildAt(grid, 1).X, Is.EqualTo(0).Within(0.01));
            Assert.That(ChildAt(grid, 1).Y, Is.EqualTo(30).Within(0.01));
        }

        [Test]
        public void Row_height_from_grid_template_rows() {
            const string css = @"
                .grid { display: grid; grid-template-columns: 100px; grid-template-rows: 50px 70px; width: 100px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div></div><div></div></div>",
                css, viewportWidth: 800);
            var grid = FindGridByClass(root, "grid");
            Assert.That(ChildAt(grid, 0).Height, Is.EqualTo(50).Within(0.01));
            Assert.That(ChildAt(grid, 1).Height, Is.EqualTo(70).Within(0.01));
            Assert.That(ChildAt(grid, 1).Y, Is.EqualTo(50).Within(0.01));
        }

        [Test]
        public void Container_height_grows_to_sum_of_rows_when_auto() {
            const string css = @"
                .grid { display: grid; grid-template-columns: 100px; grid-template-rows: 50px 70px; width: 100px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div></div><div></div></div>",
                css, viewportWidth: 800);
            var grid = FindGridByClass(root, "grid");
            Assert.That(grid.Height, Is.EqualTo(120).Within(0.01));
        }
    }
}
