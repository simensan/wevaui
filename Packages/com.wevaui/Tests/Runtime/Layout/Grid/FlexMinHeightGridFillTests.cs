using NUnit.Framework;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Grid.GridTestHelpers;

namespace Weva.Tests.Layout.Grid {
    // FLEX-MINHEIGHT-FILL — a `flex: 1` child (and a `grid-auto-rows: 1fr`
    // grid inside it) must fill a `min-height`-floored column-flex container,
    // matching Chrome, WITHOUT ballooning the layout.
    //
    // Canonical repro (gold-shop): `.shop { min-height: 100vh; display: flex;
    // flex-direction: column }` containing a top bar and `.packs { flex: 1;
    // display: grid; grid-auto-rows: 1fr }`. The packs grid must grow to fill
    // the viewport and its rows must extend the pack cards — not sit at content
    // height with empty space below.
    //
    // The bug was a feedback loop: BlockLayout's pre-grid pass block-STACKS the
    // grid's children vertically (a `repeat(3,1fr)` 6-item grid stacks to
    // ~2269px, not its real 2-row ~646px). Reading that inflated height as the
    // flex base size (and as the auto-minimum main size via GridIntrinsicCross,
    // which reads the FILLED child extents) made the column grow past its
    // min-height; the grid then filled the inflated height; that filled height
    // fed back as the next base — `.shop` ballooned 917 → 2601px.
    //
    // The fix: GridLayout.MaxContentBlockSize measures the grid's row tracks
    // against ZERO available space (1fr-as-content), independent of any
    // flex/grid-assigned Height. FlexLayout.ComputeBaseSize and
    // AutomaticMinimumMainSize use it for a column-flex grid item, so the base
    // and the auto-minimum are stretch-free and the container settles at its
    // min-height while the grid fills the distributed space.
    public class FlexMinHeightGridFillTests {

        // Core repro: the grid (flex:1) fills the min-height column and the
        // 1fr rows extend the cards. No balloon.
        [Test]
        public void Flex1_grid_fills_min_height_vh_column() {
            const string css = @"
                .shop  { min-height: 100vh; display: flex; flex-direction: column; }
                .bar   { height: 100px; }
                .packs { flex: 1 1 auto; display: grid;
                         grid-template-columns: 1fr 1fr; grid-auto-rows: 1fr; }
                .pack  { }";
            const string html = @"
                <div class='shop'>
                    <div class='bar'></div>
                    <div class='packs'>
                        <div class='pack'>a</div>
                        <div class='pack'>b</div>
                        <div class='pack'>c</div>
                        <div class='pack'>d</div>
                    </div>
                </div>";

            var (root, _, _) = Build(html, css, viewportWidth: 800, viewportHeight: 1000);
            var shop = FindByClass(root, "shop");
            var packs = FindByClass(root, "packs");
            var pack = FindByClass(root, "pack");

            // shop fills the viewport (min-height:100vh) and does NOT balloon.
            Assert.That(shop.Height, Is.EqualTo(1000).Within(2),
                $"shop should settle at 100vh (got {shop.Height:F1})");
            // packs (flex:1) fills the remaining space below the 100px bar.
            Assert.That(packs.Height, Is.EqualTo(900).Within(3),
                $"packs (flex:1) should fill the column below the bar (got {packs.Height:F1})");
            // 4 cards in a 2-col grid = 2 rows; grid-auto-rows:1fr → ~450 each.
            Assert.That(pack.Height, Is.EqualTo(450).Within(3),
                $"grid-auto-rows:1fr should extend each card to fill the grown grid (got {pack.Height:F1})");
        }

        // Explicit balloon guard: the column must NOT exceed its min-height
        // floor by re-feeding the grown/filled grid height back into its size.
        [Test]
        public void Min_height_vh_column_does_not_balloon() {
            const string css = @"
                .shop  { min-height: 100vh; display: flex; flex-direction: column; }
                .bar   { height: 80px; }
                .packs { flex: 1 1 auto; display: grid;
                         grid-template-columns: repeat(3, 1fr); grid-auto-rows: 1fr; gap: 20px; }
                .pack  { }";
            // 6 cards in a 3-col grid = 2 rows — the gold-shop shape that
            // block-stacked to ~3x the viewport before the fix.
            const string html = @"
                <div class='shop'>
                    <div class='bar'></div>
                    <div class='packs'>
                        <div class='pack'>a</div><div class='pack'>b</div><div class='pack'>c</div>
                        <div class='pack'>d</div><div class='pack'>e</div><div class='pack'>f</div>
                    </div>
                </div>";

            var (root, _, _) = Build(html, css, viewportWidth: 1200, viewportHeight: 900);
            var shop = FindByClass(root, "shop");

            Assert.That(shop.Height, Is.EqualTo(900).Within(2),
                $"shop must settle at 100vh, not balloon (got {shop.Height:F1})");
        }

        // Isolated nested case: a definite min-height column (not vh) with a
        // flex:1 grid child fills correctly too.
        [Test]
        public void Flex1_grid_fills_definite_min_height_column() {
            const string css = @"
                .col  { min-height: 600px; width: 400px; display: flex; flex-direction: column; }
                .grid { flex: 1 1 auto; display: grid;
                        grid-template-columns: 1fr; grid-auto-rows: 1fr; }
                .cell { }";
            const string html = @"
                <div class='col'>
                    <div class='grid'>
                        <div class='cell'>x</div>
                        <div class='cell'>y</div>
                    </div>
                </div>";

            var (root, _, _) = Build(html, css, viewportWidth: 800, viewportHeight: 800);
            var col = FindByClass(root, "col");
            var grid = FindGridByClass(root, "grid");
            var cell = FindByClass(root, "cell");

            Assert.That(col.Height, Is.EqualTo(600).Within(2),
                $"col should settle at min-height:600 (got {col.Height:F1})");
            Assert.That(grid.Height, Is.EqualTo(600).Within(3),
                $"grid (flex:1) should fill the 600px column (got {grid.Height:F1})");
            Assert.That(cell.Height, Is.EqualTo(300).Within(3),
                $"each 1fr row should be ~300 (half the filled 600px grid), got {cell.Height:F1}");
        }

        // Broader regression: a plain (non-grid) flex:1 child also fills a
        // min-height column now that HasDefiniteMain honours min-height.
        [Test]
        public void Plain_flex1_child_fills_min_height_column() {
            const string css = @"
                .col  { min-height: 500px; width: 300px; display: flex; flex-direction: column; }
                .head { height: 60px; }
                .body { flex: 1 1 auto; }";
            const string html = @"
                <div class='col'>
                    <div class='head'></div>
                    <div class='body'></div>
                </div>";

            var (root, _, _) = Build(html, css, viewportWidth: 800, viewportHeight: 800);
            var col = FindByClass(root, "col");
            var body = FindByClass(root, "body");

            Assert.That(col.Height, Is.EqualTo(500).Within(2),
                $"col should settle at min-height:500 (got {col.Height:F1})");
            Assert.That(body.Height, Is.EqualTo(440).Within(3),
                $"body (flex:1) should fill the column below the 60px head (got {body.Height:F1})");
        }

        // The content-driven case must be unaffected: when the column's content
        // EXCEEDS its min-height, the column grows to content (min-height is a
        // floor, not a cap) and flex:1 children get no extra free space.
        [Test]
        public void Content_taller_than_min_height_grows_to_content() {
            const string css = @"
                .col  { min-height: 200px; width: 300px; display: flex; flex-direction: column; }
                .tall { height: 400px; }
                .body { flex: 1 1 auto; }";
            const string html = @"
                <div class='col'>
                    <div class='tall'></div>
                    <div class='body'>x</div>
                </div>";

            var (root, _, _) = Build(html, css, viewportWidth: 800, viewportHeight: 800);
            var col = FindByClass(root, "col");

            // 400px child alone exceeds the 200px floor → col grows past it.
            Assert.That(col.Height, Is.GreaterThanOrEqualTo(400.0 - 0.5),
                $"col must grow to fit content taller than its min-height (got {col.Height:F1})");
        }
    }
}
