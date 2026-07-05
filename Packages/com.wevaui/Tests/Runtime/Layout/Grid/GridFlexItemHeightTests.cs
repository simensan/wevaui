using NUnit.Framework;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Grid.GridTestHelpers;

namespace Weva.Tests.Layout.Grid {
    // Regression tests for the bug where a row-flex grid item with
    // `overflow:hidden` and non-stretch align-self had its Height collapsed
    // to the shorter sibling's height.
    //
    // Root cause: HasScrollableOverflowOnGridRowAxis erroneously returned
    // true for `overflow:hidden`, causing bbIntrinsicCross=0 for the flex
    // grid item during row track sizing. The row track then sized to the
    // shorter sibling (text label) instead of the flex container.
    //
    // Repro topology (from "Shadow quality" row in settings.html):
    //   .row  { display: grid; grid-template-columns: 200px 1fr;
    //           align-items: center; }
    //   .ctrl { display: inline-flex; border: 1px solid;
    //           overflow: hidden; }   ← the overflow:hidden triggered the bug
    //   .seg  { padding: 6px 12px; font-size: 12px; }
    //
    // With MonoFontMetrics (lineHeightEm=1.2):
    //   seg line-height  = 12 × 1.2 = 14.4
    //   seg height       = 6 + 14.4 + 6 = 26.4
    //   ctrl height      = 26.4 + 1(borderT) + 1(borderB) = 28.4
    //   label line-height = 13 × 1.2 = 15.6
    //   row track        = max(15.6, 28.4) = 28.4
    //
    // Before the fix: ctrl.Height ≈ 15.6 (collapsed to label line-height).
    // After the fix:  ctrl.Height ≈ 28.4 (flex-computed height preserved).
    public class GridFlexItemHeightTests {

        // Core regression: the flex grid item with overflow:hidden must keep
        // its flex-derived height, NOT collapse to the shorter text-label
        // line-height.
        [Test]
        public void Row_flex_grid_item_keeps_flex_height_with_align_center() {
            // seg span: padding 6px top+bottom, font-size 12px
            //   → MonoFontMetrics line-height = 14.4 → seg height = 26.4
            // ctrl border 1px solid → ctrl height = 28.4
            // label font-size 13px → line-height = 15.6
            const string css = @"
                .row  { display: grid; grid-template-columns: 200px 1fr; align-items: center; width: 800px; }
                .ctrl { display: inline-flex; border: 1px solid; overflow: hidden; }
                .seg  { padding: 6px 12px; font-size: 12px; }
            ";
            const string html = @"
                <div class='row'>
                    <span class='label' style='font-size:13px'>Quality</span>
                    <div class='ctrl'>
                        <span class='seg'>Low</span>
                        <span class='seg'>Medium</span>
                        <span class='seg'>High</span>
                        <span class='seg'>Ultra</span>
                    </div>
                </div>";

            var (root, _, _) = Build(html, css, viewportWidth: 800);
            var ctrl = FindByClass(root, "ctrl");

            // MonoFontMetrics: seg height = 6+6 + 12*1.2 = 26.4; +2 border = 28.4
            // ctrl must NOT collapse to ≈15.6 (label line-height).
            Assert.That(ctrl.Height, Is.EqualTo(28.4).Within(0.5),
                "ctrl.Height should be the flex-derived height (≈28.4), not the label line-height (≈15.6)");
        }

        // The auto row track must fit the taller flex item, not the label.
        [Test]
        public void Row_track_sizes_to_flex_item_not_label() {
            const string css = @"
                .row  { display: grid; grid-template-columns: 200px 1fr; align-items: center; width: 800px; }
                .ctrl { display: inline-flex; border: 1px solid; overflow: hidden; }
                .seg  { padding: 6px 12px; font-size: 12px; }
            ";
            const string html = @"
                <div class='row'>
                    <span class='label' style='font-size:13px'>Quality</span>
                    <div class='ctrl'>
                        <span class='seg'>Low</span>
                        <span class='seg'>Medium</span>
                        <span class='seg'>High</span>
                        <span class='seg'>Ultra</span>
                    </div>
                </div>";

            var (root, _, _) = Build(html, css, viewportWidth: 800);
            var grid = FindGridByClass(root, "row");

            // Row track should fit the ctrl (≈28.4), not the label (≈15.6).
            // grid.Height == track height (no explicit height on grid, no padding).
            Assert.That(grid.Height, Is.GreaterThanOrEqualTo(28.0),
                "Row track height should accommodate the flex item (≈28.4), not just the label (≈15.6)");
        }

        // The label must be vertically centered within the row track
        // (align-items: center on the grid).
        [Test]
        public void Label_is_vertically_centered_in_row_track() {
            const string css = @"
                .row  { display: grid; grid-template-columns: 200px 1fr; align-items: center; width: 800px; }
                .ctrl { display: inline-flex; border: 1px solid; overflow: hidden; }
                .seg  { padding: 6px 12px; font-size: 12px; }
            ";
            const string html = @"
                <div class='row'>
                    <div class='label' style='font-size:13px'>Quality</div>
                    <div class='ctrl'>
                        <span class='seg'>Low</span>
                        <span class='seg'>Medium</span>
                        <span class='seg'>High</span>
                        <span class='seg'>Ultra</span>
                    </div>
                </div>";

            var (root, _, _) = Build(html, css, viewportWidth: 800);
            var label = FindByClass(root, "label");
            var ctrl = FindByClass(root, "ctrl");

            // track ≈ 28.4; label ≈ 15.6 → center offset ≈ (28.4-15.6)/2 ≈ 6.4
            // ctrl ≈ 28.4 → center offset ≈ 0 (fills the track)
            Assert.That(ctrl.Y, Is.EqualTo(0).Within(0.5),
                "ctrl should sit at the top of the cell (its height fills the track)");
            Assert.That(label.Y, Is.GreaterThan(0),
                "label should be pushed down by the center offset (shorter than ctrl)");
            // Center offset < half ctrl height
            Assert.That(label.Y, Is.LessThan(ctrl.Height * 0.5 + 1),
                "label center offset should be less than half the ctrl height");
        }

        // Regression: seg items inside the flex container must NOT overflow
        // (their combined cross-axis size must fit within ctrl.Height).
        [Test]
        public void Seg_items_do_not_overflow_ctrl_height() {
            const string css = @"
                .row  { display: grid; grid-template-columns: 200px 1fr; align-items: center; width: 800px; }
                .ctrl { display: inline-flex; border: 1px solid; overflow: hidden; }
                .seg  { padding: 6px 12px; font-size: 12px; }
            ";
            const string html = @"
                <div class='row'>
                    <span class='label' style='font-size:13px'>Quality</span>
                    <div class='ctrl'>
                        <span class='seg'>Low</span>
                        <span class='seg'>Medium</span>
                        <span class='seg'>High</span>
                        <span class='seg'>Ultra</span>
                    </div>
                </div>";

            var (root, _, _) = Build(html, css, viewportWidth: 800);
            var ctrl = FindByClass(root, "ctrl");

            // Each seg: padding 6+6 + line-height 14.4 = 26.4; border of ctrl = 1
            // All segs are row-flex items on one line → Y+Height ≤ ctrl.Height.
            foreach (var child in ctrl.Children) {
                if (child.Element?.TagName == "span") {
                    double bottom = child.Y + child.Height;
                    Assert.That(bottom, Is.LessThanOrEqualTo(ctrl.Height + 0.5),
                        $"seg child bottom ({bottom:F2}) overflows ctrl.Height ({ctrl.Height:F2})");
                }
            }
        }

        // Verify that a GENUINE scroll container (overflow:auto) still
        // contributes 0 to track sizing (preserving the existing behaviour
        // that guards the hero-picker scroll-viewport).
        [Test]
        public void Overflow_auto_scroll_container_contributes_zero_to_track() {
            // A grid item with overflow:auto and no explicit height should
            // contribute 0 to auto row track sizing (the content is scrollable,
            // not reflowed into the track). The row track falls back to siblings.
            const string css = @"
                .row    { display: grid; grid-template-columns: 200px 1fr; width: 800px; }
                .scroll { overflow: auto; }
                .tall   { height: 200px; }
            ";
            const string html = @"
                <div class='row'>
                    <div class='anchor' style='height:30px'></div>
                    <div class='scroll'>
                        <div class='tall'></div>
                    </div>
                </div>";

            var (root, _, _) = Build(html, css, viewportWidth: 800);
            var grid = FindGridByClass(root, "row");

            // The scroll container's 200px tall child must NOT inflate the row
            // track (it scrolls instead). Row track ≈ 30px (the anchor height).
            Assert.That(grid.Height, Is.LessThanOrEqualTo(35),
                "overflow:auto grid item must contribute 0 to row track sizing");
        }

        [Test]
        public void Item_min_height_floors_the_auto_row_track() {
            // Regression (css-effects .shape / .mask-card): a grid item whose
            // natural content height is small but whose min-height is large
            // must NOT collapse its auto row track to the content height. The
            // item's min-height floors its intrinsic contribution, so the row
            // (and the stretched item) end up at least min-height tall.
            // Pre-fix the clip-path / mask boxes rendered as ~23px squashed
            // slivers instead of 150px.
            const string css = @"
                .grid { display: grid; grid-template-columns: 1fr 1fr;
                        gap: 18px; align-content: start; }
                .cell { min-height: 150px; display: flex;
                        align-items: center; justify-content: center; }";
            const string html = @"
                <div class='grid'>
                    <div class='cell'>a</div>
                    <div class='cell'>b</div>
                </div>";
            var (root, _, _) = Build(html, css, viewportWidth: 800);
            var cell = FindByClass(root, "cell");
            Assert.That(cell, Is.Not.Null);
            Assert.That(cell.Height, Is.GreaterThanOrEqualTo(150.0 - 0.5),
                $"min-height:150px must floor the grid item / row track (got {cell.Height:F1})");
        }

        [Test]
        public void Aspect_ratio_column_flex_grid_item_stays_square_after_outoflow_restore() {
            // Regression (stats.html equipment `.slot`): a column-flex grid item
            // with aspect-ratio:1/1 is correctly sized square by the end-of-
            // layout aspect fixup, but the out-of-flow restoration pass (present
            // because the doc has an absolutely-positioned element) re-runs the
            // flex pass, which collapses the column-flex height back to its
            // content sum. The aspect fixup must be re-applied (grid-items-only)
            // after that restoration so the slot stays square instead of
            // rendering as a squashed sliver (was 125x92 vs 125x125).
            const string css = @"
                .grid { display:grid; grid-template-columns: repeat(2, 1fr); width: 400px; }
                .cell { aspect-ratio: 1 / 1; display:flex; flex-direction:column;
                        align-items:center; justify-content:center; }
                .ofl  { position:absolute; top:0; left:0; }";
            const string html = @"
                <div class='wrap'>
                  <div class='grid'><div class='cell'>a</div><div class='cell'>b</div></div>
                  <div class='ofl'>x</div>
                </div>";
            var (root, _, _) = Build(html, css, viewportWidth: 800);
            var cell = FindByClass(root, "cell");
            Assert.That(cell, Is.Not.Null);
            Assert.That(cell.Height, Is.GreaterThan(100.0),
                $"aspect-ratio grid item must not collapse to content height (got {cell.Height:F1})");
            Assert.That(cell.Height, Is.EqualTo(cell.Width).Within(2.5),
                $"aspect-ratio:1/1 grid item must stay square ({cell.Width:F1}x{cell.Height:F1})");
        }

        [Test]
        public void Item_min_width_floors_the_auto_column_track() {
            // Dual of the above on the inline axis: a content-narrow item with
            // a large min-width floors its auto column track.
            const string css = @"
                .grid { display: grid; grid-template-columns: auto auto;
                        gap: 10px; justify-content: start; }
                .cell { min-width: 200px; display: flex; }";
            const string html = @"
                <div class='grid'>
                    <div class='cell'>x</div>
                    <div class='cell'>y</div>
                </div>";
            var (root, _, _) = Build(html, css, viewportWidth: 800);
            var cell = FindByClass(root, "cell");
            Assert.That(cell, Is.Not.Null);
            Assert.That(cell.Width, Is.GreaterThanOrEqualTo(200.0 - 0.5),
                $"min-width:200px must floor the grid item / column track (got {cell.Width:F1})");
        }
    }
}
