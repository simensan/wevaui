// §7 — gap / row-gap / column-gap extension coverage (CSS Box Alignment §8.3)
// FlexGapTests.cs covers gap on row/column directions and % gap resolution.
// This file adds:
//   - gap with flex-wrap (per-line gap; no gap at edges)
//   - asymmetric row-gap vs column-gap in wrap
//   - gap in column-direction flex
//   - gap does NOT appear before the first or after the last item on each line
//   - gap with column direction + wrap
using NUnit.Framework;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Flex.FlexTestHelpers;

namespace Weva.Tests.Layout.Flex {
    public class FlexGapExtTests {

        // ── gap in wrap — no gap at line edges ────────────────────────────

        [Test]
        public void Gap_in_wrap_applies_only_between_items_not_at_line_edges() {
            // 300px container, 120px items, gap:20px. Line 1: items a and b
            // with a 20px gap between them. Item c wraps to line 2.
            // Line 2 has only one item (c) — no gap on that line.
            const string css = @"
                .flex { display: flex; flex-wrap: wrap; align-content: flex-start; width: 300px; gap: 20px; }
                .item { width: 120px; height: 50px; flex-shrink: 0; }
            ";
            var (root, _, _) = Build(
                "<div class=\"flex\"><div class=\"item\"></div><div class=\"item\"></div><div class=\"item\"></div></div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1); var c = ChildAt(fb, 2);
            // Line 1: a at 0, b at 140 (120+20)
            Assert.That(a.X, Is.EqualTo(0).Within(0.001));
            Assert.That(b.X, Is.EqualTo(140).Within(0.001), "20px gap between a and b");
            // Line 2: c wraps; row-gap separates lines
            Assert.That(c.Y, Is.EqualTo(70).Within(0.001), "c at Y=50+20=70 (row-gap)");
            Assert.That(c.X, Is.EqualTo(0).Within(0.001), "line 2 starts at X=0 (no gap before c)");
        }

        [Test]
        public void Row_gap_between_wrapped_lines_in_row_flex() {
            // Distinct row-gap=30 and column-gap=10.
            // row-gap applies between lines (Y axis) when flex-direction=row.
            const string css = @"
                .flex { display: flex; flex-wrap: wrap; align-content: flex-start; width: 250px; row-gap: 30px; column-gap: 10px; }
                .item { width: 120px; height: 50px; flex-shrink: 0; }
            ";
            var (root, _, _) = Build(
                "<div class=\"flex\"><div class=\"item\"></div><div class=\"item\"></div><div class=\"item\"></div></div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1); var c = ChildAt(fb, 2);
            // Line 1: a and b; column-gap=10 between them.
            Assert.That(b.X, Is.EqualTo(130).Within(0.001), "column-gap=10 between a and b");
            // Line 2: c at Y = 50 (line1 height) + 30 (row-gap) = 80
            Assert.That(c.Y, Is.EqualTo(80).Within(0.001), "row-gap=30 between lines");
        }

        [Test]
        public void Column_gap_in_column_direction_flex() {
            // column-gap is the main-axis gap in a column flex.
            // Two items, main-axis gap = row-gap.
            // In column flex: row-gap separates items vertically.
            const string css = @"
                .flex { display: flex; flex-direction: column; height: 400px; row-gap: 40px; }
                .item { height: 80px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"flex\"><div class=\"item\"></div><div class=\"item\"></div></div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1);
            Assert.That(a.Y, Is.EqualTo(0).Within(0.001));
            Assert.That(b.Y, Is.EqualTo(120).Within(0.001), "b at 80 (height) + 40 (row-gap) = 120");
        }

        [Test]
        public void Gap_shorthand_sets_both_row_and_column_gap() {
            // gap:15px should set row-gap=15 and column-gap=15.
            // In a row flex, column-gap separates items and row-gap separates
            // wrapped lines.
            const string css = @"
                .flex { display: flex; width: 250px; flex-wrap: wrap; align-content: flex-start; gap: 15px; }
                .item { width: 110px; height: 50px; flex-shrink: 0; }
            ";
            var (root, _, _) = Build(
                "<div class=\"flex\"><div class=\"item\"></div><div class=\"item\"></div><div class=\"item\"></div></div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1); var c = ChildAt(fb, 2);
            // Line 1: a(0), b(110+15=125). c wraps to line 2.
            Assert.That(b.X, Is.EqualTo(125).Within(0.001));
            // Line 2: c at Y = 50+15 = 65
            Assert.That(c.Y, Is.EqualTo(65).Within(0.001));
            Assert.That(c.X, Is.EqualTo(0).Within(0.001));
        }

        [Test]
        public void Gap_in_column_wrap_separates_columns() {
            // Column flex with wrap: items wrap into new columns.
            // column-gap separates the columns (cross axis gap in column flex).
            // align-content:flex-start prevents column stretch from expanding
            // the first column's cross size beyond the item width.
            const string css = @"
                .flex { display: flex; flex-direction: column; flex-wrap: wrap; align-content: flex-start; width: 300px; height: 120px; column-gap: 20px; }
                .item { width: 80px; height: 50px; flex-shrink: 0; }
            ";
            var (root, _, _) = Build(
                "<div class=\"flex\"><div class=\"item\"></div><div class=\"item\"></div><div class=\"item\"></div></div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var c = ChildAt(fb, 2);
            // First column: a, b. Second column: c. Gap=20 between columns.
            // a.X=0, c.X=80+20=100.
            Assert.That(a.X, Is.EqualTo(0).Within(0.001));
            Assert.That(c.X, Is.EqualTo(100).Within(0.001),
                "column-gap=20 separates wrapped column (80+20=100)");
        }

        [Test]
        public void No_gap_before_first_item_or_after_last_item() {
            // Ensures gap is only between items, not at container edges.
            const string css = @"
                .flex { display: flex; width: 600px; gap: 100px; }
                .item { width: 100px; height: 50px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"flex\"><div class=\"item\"></div><div class=\"item\"></div></div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1);
            Assert.That(a.X, Is.EqualTo(0).Within(0.001), "no gap before first item");
            Assert.That(b.X, Is.EqualTo(200).Within(0.001), "gap only between items: 100+100=200");
        }
    }
}
