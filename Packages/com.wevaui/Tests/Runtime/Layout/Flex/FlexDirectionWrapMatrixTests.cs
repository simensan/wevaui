// §1 — direction × wrap matrix (CSS Flexbox L1 §5)
// Covers the 4×3 = 12 combinations that are not already pinned in
// FlexDirectionTests.cs (which only checks row-nowrap and column basics
// without wrap interaction). All wrap tests pin align-content:flex-start
// to isolate line placement from cross-axis stretch expansion.
using NUnit.Framework;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Flex.FlexTestHelpers;

namespace Weva.Tests.Layout.Flex {
    public class FlexDirectionWrapMatrixTests {

        // ── row × nowrap ─────────────────────────────────────────────────
        [Test]
        public void Row_nowrap_all_items_on_same_y() {
            var css = @"
                .flex { display: flex; flex-direction: row; flex-wrap: nowrap; width: 250px; height: 250px; }
                .item { width: 120px; height: 50px; flex-shrink: 0; }
            ";
            var (root, _, _) = Build(
                "<div class=\"flex\"><div class=\"item\"></div><div class=\"item\"></div><div class=\"item\"></div></div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1); var c = ChildAt(fb, 2);
            Assert.That(a.Y, Is.EqualTo(0).Within(0.001));
            Assert.That(b.Y, Is.EqualTo(0).Within(0.001));
            Assert.That(c.Y, Is.EqualTo(0).Within(0.001));
        }

        // ── row × wrap ────────────────────────────────────────────────────
        [Test]
        public void Row_wrap_third_item_moves_to_second_line() {
            // align-content:flex-start prevents first-line stretch from
            // pushing the second line down further than the natural 50px.
            var css = @"
                .flex { display: flex; flex-direction: row; flex-wrap: wrap; align-content: flex-start; width: 250px; height: 250px; }
                .item { width: 120px; height: 50px; flex-shrink: 0; }
            ";
            var (root, _, _) = Build(
                "<div class=\"flex\"><div class=\"item\"></div><div class=\"item\"></div><div class=\"item\"></div></div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1); var c = ChildAt(fb, 2);
            Assert.That(a.Y, Is.EqualTo(0).Within(0.001));
            Assert.That(b.Y, Is.EqualTo(0).Within(0.001));
            Assert.That(c.Y, Is.EqualTo(50).Within(0.001), "third item wraps to line 2");
            Assert.That(c.X, Is.EqualTo(0).Within(0.001), "wrapped item resets to line start");
        }

        // ── row × wrap-reverse ────────────────────────────────────────────
        [Test]
        public void Row_wrap_reverse_first_line_has_larger_y_than_second_line() {
            // wrap-reverse inverts the cross-axis ordering: second line
            // (containing 'c') sits above the first (containing 'a','b').
            var css = @"
                .flex { display: flex; flex-direction: row; flex-wrap: wrap-reverse; width: 250px; height: 200px; }
                .item { width: 120px; height: 50px; flex-shrink: 0; }
            ";
            var (root, _, _) = Build(
                "<div class=\"flex\"><div class=\"item\"></div><div class=\"item\"></div><div class=\"item\"></div></div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0);
            var c = ChildAt(fb, 2);
            Assert.That(a.Y, Is.GreaterThan(c.Y),
                "wrap-reverse: first line (a) must be below second line (c)");
        }

        // ── row-reverse × nowrap ──────────────────────────────────────────
        [Test]
        public void Row_reverse_nowrap_items_placed_right_to_left() {
            var css = @"
                .flex { display: flex; flex-direction: row-reverse; flex-wrap: nowrap; width: 400px; height: 100px; }
                .item { width: 100px; height: 50px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"flex\"><div class=\"item\"></div><div class=\"item\"></div><div class=\"item\"></div></div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1); var c = ChildAt(fb, 2);
            // row-reverse: first source item lands at right end (400-100=300)
            Assert.That(a.X, Is.EqualTo(300).Within(0.001));
            Assert.That(b.X, Is.EqualTo(200).Within(0.001));
            Assert.That(c.X, Is.EqualTo(100).Within(0.001));
        }

        // ── row-reverse × wrap ────────────────────────────────────────────
        [Test]
        public void Row_reverse_wrap_items_right_to_left_and_overflow_wraps() {
            // align-content:flex-start prevents stretch from changing second line Y.
            var css = @"
                .flex { display: flex; flex-direction: row-reverse; flex-wrap: wrap; align-content: flex-start; width: 250px; height: 200px; }
                .item { width: 120px; height: 50px; flex-shrink: 0; }
            ";
            var (root, _, _) = Build(
                "<div class=\"flex\"><div class=\"item\"></div><div class=\"item\"></div><div class=\"item\"></div></div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1); var c = ChildAt(fb, 2);
            // row-reverse: a is at the right end of 250px container → x=130
            Assert.That(a.X, Is.EqualTo(130).Within(0.001));
            Assert.That(b.X, Is.EqualTo(10).Within(0.001));
            // c wraps to second row at Y=50
            Assert.That(c.Y, Is.EqualTo(50).Within(0.001));
        }

        // ── row-reverse × wrap-reverse ────────────────────────────────────
        [Test]
        public void Row_reverse_wrap_reverse_first_line_below_overflow_line() {
            // wrap-reverse: a,b are on the FIRST source line (bottom) and
            // c is on the overflow line (top). So a.Y > c.Y.
            var css = @"
                .flex { display: flex; flex-direction: row-reverse; flex-wrap: wrap-reverse; width: 250px; height: 200px; }
                .item { width: 120px; height: 50px; flex-shrink: 0; }
            ";
            var (root, _, _) = Build(
                "<div class=\"flex\"><div class=\"item\"></div><div class=\"item\"></div><div class=\"item\"></div></div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var c = ChildAt(fb, 2);
            Assert.That(a.Y, Is.GreaterThan(c.Y),
                "wrap-reverse: first-source line is below the overflow line");
        }

        // ── column × nowrap ───────────────────────────────────────────────
        [Test]
        public void Column_nowrap_items_stacked_in_source_order() {
            var css = @"
                .flex { display: flex; flex-direction: column; flex-wrap: nowrap; width: 200px; height: 300px; }
                .item { width: 100px; height: 50px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"flex\"><div class=\"item\"></div><div class=\"item\"></div><div class=\"item\"></div></div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1); var c = ChildAt(fb, 2);
            Assert.That(a.Y, Is.EqualTo(0).Within(0.001));
            Assert.That(b.Y, Is.EqualTo(50).Within(0.001));
            Assert.That(c.Y, Is.EqualTo(100).Within(0.001));
            Assert.That(a.X, Is.EqualTo(0).Within(0.001));
            Assert.That(b.X, Is.EqualTo(0).Within(0.001));
        }

        // ── column × wrap ────────────────────────────────────────────────
        [Test]
        public void Column_wrap_third_item_starts_new_column() {
            // Two 50px items fit in a 120px-high container; third wraps.
            // Use explicit item width 80px; wrap to second column at x=80.
            // align-content:flex-start pins columns against left edge only.
            var css = @"
                .flex { display: flex; flex-direction: column; flex-wrap: wrap; width: 200px; height: 120px; }
                .item { width: 80px; height: 50px; flex-shrink: 0; }
            ";
            var (root, _, _) = Build(
                "<div class=\"flex\"><div class=\"item\"></div><div class=\"item\"></div><div class=\"item\"></div></div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1); var c = ChildAt(fb, 2);
            // a and b on first column (X=0)
            Assert.That(a.X, Is.EqualTo(0).Within(0.001));
            Assert.That(b.X, Is.EqualTo(0).Within(0.001));
            // c wraps: its X must be larger than a.X
            Assert.That(c.X, Is.GreaterThan(a.X + 0.001),
                "third item must start a new column (X > first column X)");
            Assert.That(c.Y, Is.EqualTo(0).Within(0.001), "new column starts at top");
        }

        // ── column × wrap-reverse ────────────────────────────────────────
        [Test]
        public void Column_wrap_reverse_overflow_column_to_left_of_first() {
            var css = @"
                .flex { display: flex; flex-direction: column; flex-wrap: wrap-reverse; width: 200px; height: 120px; }
                .item { width: 80px; height: 50px; flex-shrink: 0; }
            ";
            var (root, _, _) = Build(
                "<div class=\"flex\"><div class=\"item\"></div><div class=\"item\"></div><div class=\"item\"></div></div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var c = ChildAt(fb, 2);
            // wrap-reverse: first column is placed at the right,
            // overflow column wraps to the left.
            Assert.That(c.X, Is.LessThan(a.X),
                "wrap-reverse column: overflow column must be to the left of first column");
        }

        // ── column-reverse × nowrap ───────────────────────────────────────
        [Test]
        public void Column_reverse_nowrap_items_placed_bottom_to_top() {
            var css = @"
                .flex { display: flex; flex-direction: column-reverse; flex-wrap: nowrap; width: 200px; height: 300px; }
                .item { width: 100px; height: 50px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"flex\"><div class=\"item\"></div><div class=\"item\"></div><div class=\"item\"></div></div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1); var c = ChildAt(fb, 2);
            // column-reverse: first source item sits at the bottom
            Assert.That(a.Y, Is.GreaterThan(b.Y),
                "column-reverse: item 0 must be below item 1");
            Assert.That(b.Y, Is.GreaterThan(c.Y),
                "column-reverse: item 1 must be below item 2");
        }

        // ── column-reverse × wrap ────────────────────────────────────────
        [Test]
        public void Column_reverse_wrap_items_bottom_to_top_and_overflow_new_column() {
            var css = @"
                .flex { display: flex; flex-direction: column-reverse; flex-wrap: wrap; width: 200px; height: 120px; }
                .item { width: 80px; height: 50px; flex-shrink: 0; }
            ";
            var (root, _, _) = Build(
                "<div class=\"flex\"><div class=\"item\"></div><div class=\"item\"></div><div class=\"item\"></div></div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1); var c = ChildAt(fb, 2);
            // column-reverse: within first column, a (source 0) is below b (source 1)
            Assert.That(a.Y, Is.GreaterThan(b.Y),
                "column-reverse: source-0 must be below source-1 in same column");
            // c wraps to a new column (X > first column's X)
            Assert.That(c.X, Is.GreaterThan(a.X + 0.001),
                "column-reverse+wrap: overflow item must move to a new column");
        }

        // ── column-reverse × wrap-reverse ────────────────────────────────
        [Test]
        public void Column_reverse_wrap_reverse_overflow_column_left_items_bottom_to_top() {
            var css = @"
                .flex { display: flex; flex-direction: column-reverse; flex-wrap: wrap-reverse; width: 200px; height: 120px; }
                .item { width: 80px; height: 50px; flex-shrink: 0; }
            ";
            var (root, _, _) = Build(
                "<div class=\"flex\"><div class=\"item\"></div><div class=\"item\"></div><div class=\"item\"></div></div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1); var c = ChildAt(fb, 2);
            // column-reverse: a below b in first column
            Assert.That(a.Y, Is.GreaterThan(b.Y),
                "column-reverse: item 0 must be below item 1 in same column");
            // wrap-reverse: overflow column to the left of first column
            Assert.That(c.X, Is.LessThan(a.X),
                "wrap-reverse: overflow column must be to the left of the first column");
        }
    }
}
