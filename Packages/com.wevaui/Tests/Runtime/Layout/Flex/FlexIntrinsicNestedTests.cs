// §9 — Intrinsic sizing and nested flex containers (CSS Flexbox L1 §9.9 + CSS Sizing L3)
// FlexLayoutIntegrationTests.cs covers row-flex with nested column-flex children.
// This file adds focused intrinsic-sizing and nesting edge cases:
//   - Column inside row: outer row cross size = column natural height
//   - Row inside column: outer column main size = row natural width
//   - flex-basis:content on nested flex item
//   - Deeply nested: 3 levels
//   - Percentage cross-axis with indefinite container (must use auto/0)
using NUnit.Framework;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Flex.FlexTestHelpers;

namespace Weva.Tests.Layout.Flex {
    public class FlexIntrinsicNestedTests {

        // ── Column nested inside row ───────────────────────────────────────

        [Test]
        public void Row_flex_cross_size_equals_tallest_column_flex_child_height() {
            // Row flex auto-height (no explicit height). Contains two column
            // flex children with known content. The row's height should equal
            // the taller of the two columns.
            const string css = @"
                .row { display: flex; width: 400px; }
                .col { display: flex; flex-direction: column; }
                .tall { height: 120px; width: 60px; }
                .short { height: 60px; width: 60px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"row\">"
                + "<div class=\"col\"><div class=\"tall\"></div></div>"
                + "<div class=\"col\"><div class=\"short\"></div></div>"
                + "</div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            Assert.That(fb.Height, Is.EqualTo(120).Within(1.0),
                "row flex height should be the taller column (120px)");
        }

        [Test]
        public void Row_inside_column_flex_column_main_size_equals_row_height() {
            // Column flex auto-height. Contains a row flex child with items.
            // Column main size = sum of rows' heights.
            const string css = @"
                .col { display: flex; flex-direction: column; width: 300px; }
                .row { display: flex; }
                .item { width: 100px; height: 60px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"col\">"
                + "<div class=\"row\">"
                + "<div class=\"item\"></div><div class=\"item\"></div>"
                + "</div>"
                + "</div>",
                css, viewportWidth: 800);
            var col = FindFlex(root, "div");
            Assert.That(col.Height, Is.EqualTo(60).Within(1.0),
                "column flex height should equal the single row child's height (60px)");
        }

        // ── Three levels of nesting ───────────────────────────────────────

        [Test]
        public void Three_level_nested_flex_positions_leaf_items_correctly() {
            // Outer row → inner column → leaf row with two items.
            // Leaf items should be placed horizontally at the correct offset.
            const string css = @"
                .outer { display: flex; width: 500px; }
                .mid { display: flex; flex-direction: column; width: 200px; }
                .inner { display: flex; }
                .item { width: 80px; height: 40px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"outer\">"
                + "<div class=\"mid\">"
                + "<div class=\"inner\">"
                + "<div class=\"item\"></div>"
                + "<div class=\"item\"></div>"
                + "</div>"
                + "</div>"
                + "</div>",
                css, viewportWidth: 800);
            // Find the innermost flex box
            Weva.Layout.Flex.FlexBox innerFlex = null;
            foreach (var b in AllBoxes(root)) {
                if (b is Weva.Layout.Flex.FlexBox fb && fb.Element?.ClassName == "inner") {
                    innerFlex = fb; break;
                }
            }
            Assert.That(innerFlex, Is.Not.Null);
            var a = ChildAt(innerFlex, 0);
            var b2 = ChildAt(innerFlex, 1);
            Assert.That(a.X, Is.EqualTo(0).Within(0.001));
            Assert.That(b2.X, Is.EqualTo(80).Within(0.001));
        }

        // ── Auto-height row/column propagation ────────────────────────────

        [Test]
        public void Auto_height_column_flex_wraps_to_content_height() {
            // Column flex without explicit height; height = sum of children.
            const string css = @"
                .col { display: flex; flex-direction: column; width: 200px; }
                .item { height: 40px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"col\">"
                + "<div class=\"item\"></div>"
                + "<div class=\"item\"></div>"
                + "<div class=\"item\"></div>"
                + "</div>",
                css, viewportWidth: 800);
            var col = FindFlex(root, "div");
            Assert.That(col.Height, Is.EqualTo(120).Within(1.0),
                "auto-height column flex should equal 3×40=120px");
        }

        [Test]
        public void Auto_width_row_flex_current_pin_intrinsic_width() {
            // Post-B7d fix: inline-flex intrinsic width now correctly equals the
            // sum of flex items (2×80 = 160px). The earlier broken behavior (80px)
            // was caused by MakeAtomItem using max(child.Width) instead of
            // FlexIntrinsicInline (sum for row flex). CSS Flexbox L1 §9.9.
            const string css = @"
                .outer { width: 600px; }
                .row { display: inline-flex; }
                .item { width: 80px; height: 40px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"outer\"><div class=\"row\"><div class=\"item\"></div><div class=\"item\"></div></div></div>",
                css, viewportWidth: 800);
            Weva.Layout.Flex.FlexBox rowFlex = null;
            foreach (var bx in AllBoxes(root)) {
                if (bx is Weva.Layout.Flex.FlexBox fb && fb.Element?.ClassName == "row") {
                    rowFlex = fb; break;
                }
            }
            Assert.That(rowFlex, Is.Not.Null);
            Assert.That(rowFlex.Width, Is.EqualTo(160).Within(1.0),
                "B7d fixed: inline-flex intrinsic width should equal sum of items (2×80=160)");
        }

        [Test]
        public void Auto_width_row_flex_wraps_to_content_width() {
            const string css = @"
                .outer { width: 600px; }
                .row { display: inline-flex; }
                .item { width: 80px; height: 40px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"outer\"><div class=\"row\"><div class=\"item\"></div><div class=\"item\"></div></div></div>",
                css, viewportWidth: 800);
            Weva.Layout.Flex.FlexBox rowFlex = null;
            foreach (var bx in AllBoxes(root)) {
                if (bx is Weva.Layout.Flex.FlexBox fb && fb.Element?.ClassName == "row") {
                    rowFlex = fb; break;
                }
            }
            Assert.That(rowFlex, Is.Not.Null);
            Assert.That(rowFlex.Width, Is.EqualTo(160).Within(1.0),
                "inline-flex container should shrink to content width (2×80=160)");
        }

        // ── Nested flex with align-items:stretch propagation ──────────────

        [Test]
        public void Nested_column_in_row_flex_stretches_to_row_cross_size() {
            // Row flex with explicit height 200px; column child without explicit
            // height should stretch to fill 200px.
            const string css = @"
                .row { display: flex; width: 400px; height: 200px; }
                .col { display: flex; flex-direction: column; width: 150px; }
                .item { height: 50px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"row\">"
                + "<div class=\"col\"><div class=\"item\"></div></div>"
                + "</div>",
                css, viewportWidth: 800);
            Weva.Layout.Flex.FlexBox colFlex = null;
            foreach (var bx in AllBoxes(root)) {
                if (bx is Weva.Layout.Flex.FlexBox fb && fb.Element?.ClassName == "col") {
                    colFlex = fb; break;
                }
            }
            Assert.That(colFlex, Is.Not.Null);
            Assert.That(colFlex.Height, Is.EqualTo(200).Within(1.0),
                "nested column flex should stretch to row container's cross size (200px)");
        }
    }
}
