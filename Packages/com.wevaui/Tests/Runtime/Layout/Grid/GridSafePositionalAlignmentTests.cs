using NUnit.Framework;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Grid.GridTestHelpers;

namespace Weva.Tests.Layout.Grid {
    // Bug E6b: CSS Align L3 §6.2 lets authors prefix an alignment keyword
    // with `safe`/`unsafe` (e.g. `justify-content: safe center`). Before the
    // fix those grid-container/grid-item alignment longhands fell through to
    // the `fallback` value and the alignment was silently ignored. For v1 we
    // accept the syntax and apply the bare alignment keyword; overflow-safe
    // fallback semantics at layout time are deferred — `safe` and `unsafe`
    // parse identically.
    public class GridSafePositionalAlignmentTests {
        [Test]
        public void JustifyContent_safe_center_centers_grid_tracks() {
            const string css = @"
                .grid { display: grid; grid-template-columns: 100px 100px; justify-content: safe center; width: 400px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div></div><div></div></div>",
                css, viewportWidth: 800);
            var grid = FindGridByClass(root, "grid");
            // 400 - 200 = 200 free; safe center -> centered tracks (100 leading gutter).
            Assert.That(ChildAt(grid, 0).X, Is.EqualTo(100).Within(0.01));
            Assert.That(ChildAt(grid, 1).X, Is.EqualTo(200).Within(0.01));
        }

        [Test]
        public void AlignContent_unsafe_end_packs_rows_at_end() {
            const string css = @"
                .grid { display: grid; grid-template-columns: 100px; grid-template-rows: 50px 50px; align-content: unsafe end; height: 200px; width: 100px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div></div><div></div></div>",
                css, viewportWidth: 800);
            var grid = FindGridByClass(root, "grid");
            // 200 - 50 - 50 = 100 free; unsafe end -> rows packed at the
            // bottom: first row y=100, second row y=150.
            Assert.That(ChildAt(grid, 0).Y, Is.EqualTo(100).Within(0.01));
            Assert.That(ChildAt(grid, 1).Y, Is.EqualTo(150).Within(0.01));
        }

        [Test]
        public void AlignSelf_safe_stretch_stretches_item_to_row_height() {
            const string css = @"
                .grid { display: grid; grid-template-columns: 200px; grid-template-rows: 100px; align-items: start; width: 200px; }
                .item { width: 80px; align-self: safe stretch; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div class=\"item\"></div></div>",
                css, viewportWidth: 800);
            var item = FindByClass(root, "item");
            // safe stretch -> stretches to row height (overrides the
            // container's align-items: start).
            Assert.That(item.Height, Is.EqualTo(100).Within(0.01));
        }

        [Test]
        public void JustifySelf_unsafe_start_aligns_item_to_track_start() {
            const string css = @"
                .grid { display: grid; grid-template-columns: 200px; grid-template-rows: 100px; justify-items: center; width: 200px; }
                .item { width: 60px; height: 40px; justify-self: unsafe start; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div class=\"item\"></div></div>",
                css, viewportWidth: 800);
            var item = FindByClass(root, "item");
            // unsafe start -> aligned to the column start (x=0), overriding
            // justify-items: center.
            Assert.That(item.X, Is.EqualTo(0).Within(0.01));
        }
    }
}
