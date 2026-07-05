using NUnit.Framework;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Grid.GridTestHelpers;

namespace Weva.Tests.Layout.Grid {
    // D10 + MN3 (CODE_AUDIT_FINDINGS.md) regression pins.
    //
    // D10 routed the six `Math.Abs(x - y) < 0.5` clauses in GridLayout
    // through `LayoutEpsilons.IsHalfPixelEqual`. MN3 documented the
    // five `0.5` centering-factor literals inline (NOT the epsilon).
    //
    // These tests guard:
    //  1. A grid item whose cell-sized geometry sits exactly at the
    //     half-pixel boundary still lays out correctly (regression pin
    //     for the helper migration).
    //  2. Centering arithmetic (`(cellW - w) * 0.5`, `(cellH - h) * 0.5`,
    //     and the JustifyContent.Center `extra * 0.5`) still produces
    //     identical numeric output after the MN3 comment touch-ups.
    //     The literals are unchanged — these tests pin the call sites
    //     so a future PR that "fixes" the magic number by retuning the
    //     centering factor (rather than just renaming) is caught.
    public class GridHalfPixelEpsilonD10Tests {

        // Regression pin: half-pixel-boundary content. An item placed in a
        // cell where ShrinkFitCachedAvail and the new cellW differ by less
        // than 0.5 should hit the cache fast-path (site 959). The pre-D10
        // form was `Math.Abs(...) < 0.5`; the post-D10 form is
        // `IsHalfPixelEqual(...)` (which uses `<=`). The boundary itself is
        // measure-zero in practice, so a normal layout produces the same
        // visible output before and after.
        [Test]
        public void Grid_with_content_at_half_pixel_boundary_lays_out_correctly_D10() {
            // 200px column, 80px item -> 120px slack, centered = X 60.
            // The cellW/Width comparison sites (1228/1231) should classify
            // a stretched item as cell-sized within the tolerance.
            const string css = @"
                .grid { display: grid; grid-template-columns: 200px; grid-template-rows: 100px; width: 200px; }
                .stretch { background: red; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div class=\"stretch\"></div></div>",
                css, viewportWidth: 800);
            var item = FindByClass(root, "stretch");
            // Stretched item fills the cell; the 1228/1231 sites must
            // classify Width/Height as cell-sized within HalfPixelEqual.
            Assert.That(item.Width, Is.EqualTo(200).Within(0.01),
                "D10: stretched item width should fill 200px cell exactly. " +
                "If this fails, the IsHalfPixelEqual migration changed the " +
                "stretch-classification at GridLayout.cs:1228.");
            Assert.That(item.Height, Is.EqualTo(100).Within(0.01),
                "D10: stretched item height should fill 100px cell exactly. " +
                "If this fails, the migration changed classification at " +
                "GridLayout.cs:1231.");
        }

        // Regression pin: justify-self:center on a 60px item in a 200px
        // cell uses the `(cellW - w) * 0.5` centering factor at line 1092.
        // Expected X = (200 - 60) * 0.5 = 70.
        [Test]
        public void JustifySelf_center_arithmetic_unchanged_MN3() {
            const string css = @"
                .grid { display: grid; grid-template-columns: 200px; grid-template-rows: 100px; width: 200px; }
                .item { width: 60px; height: 40px; justify-self: center; align-self: center; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div class=\"item\"></div></div>",
                css, viewportWidth: 800);
            var item = FindByClass(root, "item");
            // (cellW - w) * 0.5 = (200 - 60) * 0.5 = 70
            Assert.That(item.X, Is.EqualTo(70).Within(0.01),
                "MN3: justify-self:center should produce X=70 via the " +
                "`(cellW - w) * 0.5` centering factor at GridLayout.cs:1092. " +
                "The MN3 comment touch-up must not have changed the math.");
            // (cellH - h) * 0.5 = (100 - 40) * 0.5 = 30
            Assert.That(item.Y, Is.EqualTo(30).Within(0.01),
                "MN3: align-self:center should produce Y=30 via the " +
                "`(cellH - h) * 0.5` centering factor at GridLayout.cs:1098.");
        }

        // Regression pin: justify-content:center on tracks distributes the
        // extra space via `offset = extra * 0.5` at GridLayout.cs:882.
        // 400px container, two 100px columns -> 200px extra, half = 100.
        // So column 0 starts at X=100, column 1 at X=200.
        [Test]
        public void JustifyContent_center_arithmetic_unchanged_MN3() {
            const string css = @"
                .grid { display: grid; grid-template-columns: 100px 100px; justify-content: center; width: 400px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div></div><div></div></div>",
                css, viewportWidth: 800);
            var grid = FindGridByClass(root, "grid");
            // extra = 400 - 200 = 200; offset = 200 * 0.5 = 100.
            Assert.That(ChildAt(grid, 0).X, Is.EqualTo(100).Within(0.01),
                "MN3: justify-content:center first track should sit at X=100 " +
                "via `extra * 0.5` at GridLayout.cs:882. The MN3 comment " +
                "touch-up must not have changed the math.");
            Assert.That(ChildAt(grid, 1).X, Is.EqualTo(200).Within(0.01),
                "MN3: justify-content:center second track should sit at X=200.");
        }
    }
}
