using NUnit.Framework;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Grid.GridTestHelpers;

namespace Weva.Tests.Layout.Grid {
    public class GridAlignmentTests {
        [Test]
        public void Justify_items_center_centers_horizontally() {
            const string css = @"
                .grid { display: grid; grid-template-columns: 200px; grid-template-rows: 100px; justify-items: center; width: 200px; }
                .item { width: 80px; height: 60px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div class=\"item\"></div></div>",
                css, viewportWidth: 800);
            var item = FindByClass(root, "item");
            Assert.That(item.Width, Is.EqualTo(80).Within(0.01));
            Assert.That(item.X, Is.EqualTo(60).Within(0.01));
        }

        [Test]
        public void Align_items_end_aligns_to_bottom() {
            const string css = @"
                .grid { display: grid; grid-template-columns: 200px; grid-template-rows: 100px; align-items: end; width: 200px; }
                .item { width: 80px; height: 30px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div class=\"item\"></div></div>",
                css, viewportWidth: 800);
            var item = FindByClass(root, "item");
            Assert.That(item.Y, Is.EqualTo(70).Within(0.01));
        }

        [Test]
        public void Justify_self_overrides_container_default() {
            const string css = @"
                .grid { display: grid; grid-template-columns: 200px; grid-template-rows: 100px; justify-items: center; width: 200px; }
                .item { width: 60px; height: 40px; justify-self: end; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div class=\"item\"></div></div>",
                css, viewportWidth: 800);
            var item = FindByClass(root, "item");
            Assert.That(item.X, Is.EqualTo(140).Within(0.01));
        }

        [Test]
        public void Place_items_center_centers_both_axes() {
            const string css = @"
                .grid { display: grid; grid-template-columns: 200px; grid-template-rows: 100px; place-items: center; width: 200px; }
                .item { width: 60px; height: 40px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div class=\"item\"></div></div>",
                css, viewportWidth: 800);
            var item = FindByClass(root, "item");
            Assert.That(item.X, Is.EqualTo(70).Within(0.01));
            Assert.That(item.Y, Is.EqualTo(30).Within(0.01));
        }

        [Test]
        public void Justify_content_space_between_distributes_tracks() {
            const string css = @"
                .grid { display: grid; grid-template-columns: 100px 100px; justify-content: space-between; width: 400px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div></div><div></div></div>",
                css, viewportWidth: 800);
            var grid = FindGridByClass(root, "grid");
            Assert.That(ChildAt(grid, 0).X, Is.EqualTo(0).Within(0.01));
            Assert.That(ChildAt(grid, 1).X, Is.EqualTo(300).Within(0.01));
        }

        [Test]
        public void Align_content_stretch_default_for_multi_row_with_extra_space() {
            const string css = @"
                .grid { display: grid; grid-template-columns: 100px; grid-template-rows: 50px 50px; align-content: stretch; height: 200px; width: 100px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div></div><div></div></div>",
                css, viewportWidth: 800);
            var grid = FindGridByClass(root, "grid");
            // 200 - 50 - 50 = 100 extra distributed evenly: each row +50 = 100px high.
            Assert.That(ChildAt(grid, 0).Height, Is.EqualTo(100).Within(0.01));
            Assert.That(ChildAt(grid, 1).Y, Is.EqualTo(100).Within(0.01));
        }

        [Test]
        public void Stretch_is_default_when_no_explicit_size() {
            const string css = @"
                .grid { display: grid; grid-template-columns: 200px; grid-template-rows: 100px; width: 200px; }
                .item { background: red; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div class=\"item\"></div></div>",
                css, viewportWidth: 800);
            var item = FindByClass(root, "item");
            Assert.That(item.Width, Is.EqualTo(200).Within(0.01));
            Assert.That(item.Height, Is.EqualTo(100).Within(0.01));
        }

        [Test]
        public void Justify_content_stretch_expands_definite_tracks_to_fill() {
            const string css = @"
                .grid { display: grid; grid-template-columns: 100px 100px; justify-content: stretch; width: 400px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div></div><div></div></div>",
                css, viewportWidth: 800);
            var grid = FindGridByClass(root, "grid");
            // 400 - 100 - 100 = 200 extra distributed evenly: each track +100 = 200px wide.
            Assert.That(ChildAt(grid, 0).X, Is.EqualTo(0).Within(0.01));
            Assert.That(ChildAt(grid, 0).Width, Is.EqualTo(200).Within(0.01));
            Assert.That(ChildAt(grid, 1).X, Is.EqualTo(200).Within(0.01));
            Assert.That(ChildAt(grid, 1).Width, Is.EqualTo(200).Within(0.01));
        }

        [Test]
        public void Justify_content_start_keeps_definite_tracks_at_intrinsic() {
            const string css = @"
                .grid { display: grid; grid-template-columns: 100px 100px; justify-content: start; width: 400px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div></div><div></div></div>",
                css, viewportWidth: 800);
            var grid = FindGridByClass(root, "grid");
            Assert.That(ChildAt(grid, 0).X, Is.EqualTo(0).Within(0.01));
            Assert.That(ChildAt(grid, 0).Width, Is.EqualTo(100).Within(0.01));
            Assert.That(ChildAt(grid, 1).X, Is.EqualTo(100).Within(0.01));
            Assert.That(ChildAt(grid, 1).Width, Is.EqualTo(100).Within(0.01));
        }

        [Test]
        public void Justify_content_center_centers_track_block() {
            const string css = @"
                .grid { display: grid; grid-template-columns: 100px 100px; justify-content: center; width: 400px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div></div><div></div></div>",
                css, viewportWidth: 800);
            var grid = FindGridByClass(root, "grid");
            // 400 - 200 = 200 extra, 100 on each side.
            Assert.That(ChildAt(grid, 0).X, Is.EqualTo(100).Within(0.01));
            Assert.That(ChildAt(grid, 1).X, Is.EqualTo(200).Within(0.01));
        }
    }
}
