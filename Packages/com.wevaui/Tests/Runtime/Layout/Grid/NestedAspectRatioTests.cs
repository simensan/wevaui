using NUnit.Framework;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Grid.GridTestHelpers;

namespace Weva.Tests.Layout.Grid {
    // CSS Grid + Sizing L4 §5 — a grid item whose intrinsic cross size
    // derives from its `aspect-ratio` must feed that derived size into
    // the row track sizing of any GRID-CONTAINER ancestor whose row
    // track resolves to min-content (because `1fr` with no available
    // space collapses to min-content). The fixup in `GridLayout` does
    // this for direct grid children; the bug #236 is that nested grid
    // structures (grid > grid > item) lose the aspect-ratio signal
    // somewhere between the inner item's intrinsic measure and the
    // outer container's track resolution.
    public class NestedAspectRatioTests {
        const string css = @"
            .outer {
                display: grid;
                grid-template-columns: 1fr;
                grid-template-rows: min-content;
                width: 400px;
            }
            .inner {
                display: grid;
                grid-template-columns: repeat(4, 1fr);
                gap: 8px;
            }
            .item { aspect-ratio: 1 / 1; }
        ";

        [Test]
        public void Outer_1fr_min_content_row_sizes_to_inner_aspect_ratio() {
            var (root, _, _) = Build(
                @"<div class=""outer"">
                    <div class=""inner"">
                        <div class=""item""></div>
                        <div class=""item""></div>
                        <div class=""item""></div>
                        <div class=""item""></div>
                    </div>
                </div>", css, viewportWidth: 400);

            var outer = FindByClass(root, "outer");
            var inner = FindByClass(root, "inner");
            // 4-col 1fr grid in a 400-px-wide container with 8px gaps gives
            // each column ~94px; each .item is 1:1, so should be ~94px tall.
            // Inner grid is one row → ~94px. Outer's 1fr min-content row
            // should match.
            Assert.That(inner.Height, Is.GreaterThan(80),
                $"inner grid row should size to its aspect-ratio items (~94px), got {inner.Height}");
            Assert.That(outer.Height, Is.GreaterThanOrEqualTo(inner.Height - 0.5),
                $"outer 1fr min-content row must contain inner (inner={inner.Height} outer={outer.Height})");
        }

        // CSS Sizing L4 §5 SYMMETRIC direction (#21 candidate):
        // when a grid row track resolves TALLER than the column track
        // (e.g. a flex parent stretches the grid container to a definite
        // height that's larger than the column-derived item size), an
        // `aspect-ratio` item's width should expand from row height ×
        // ratio, overflowing the column track. Chrome does this for
        // stats.html `.slot { aspect-ratio:1/1 }` in a 4×2 grid where
        // gear-grid (`flex:1`) gets stretched to a tall container.
        // Currently Unity sizes the slot from column track only, leaving
        // half the row track empty.
        //
        [Test]
        public void Aspect_ratio_item_expands_width_from_taller_row_track() {
            const string ratioCss = @"
                .stretchy {
                    display: grid;
                    grid-template-columns: repeat(4, 1fr);
                    grid-template-rows: 1fr 1fr;
                    gap: 8px;
                    width: 500px;
                    height: 400px;
                }
                .cell { aspect-ratio: 1 / 1; }
            ";
            var (root, _, _) = Build(
                @"<div class=""stretchy"">
                    <div class=""cell""></div><div class=""cell""></div>
                    <div class=""cell""></div><div class=""cell""></div>
                    <div class=""cell""></div><div class=""cell""></div>
                    <div class=""cell""></div><div class=""cell""></div>
                </div>", ratioCss, viewportWidth: 600);

            var cell = FindByClass(root, "cell");
            // Column track = (500 - 24)/4 = 119; row track = (400 - 8)/2 = 196.
            // With aspect-ratio:1/1 and row > col, cell width should expand
            // to row height (~196) — overflowing the column track.
            Assert.That(cell.Height, Is.GreaterThan(180),
                $"cell height should match row track ~196px, got {cell.Height}");
            Assert.That(cell.Width, Is.EqualTo(cell.Height).Within(2.0),
                $"cell aspect-ratio:1/1 → width should match height ({cell.Height}), got {cell.Width}");
        }
    }
}
