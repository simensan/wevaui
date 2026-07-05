using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Css.Cascade.Shorthands;
using Weva.Dom;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Layout.Multicol;
using Weva.Paint;
using Weva.Paint.Conversion;

namespace Weva.Tests.Layout {
    // Coverage matrix (see CSS_OPEN_GAPS.md C2):
    //
    // A) CSS Multicol §3 geometry resolution (column-count / column-width / both / neither)
    // B) Balanced height distribution (auto-height container)
    // C) Sequential fill (explicit container height)
    // D) column-gap arithmetic
    // E) Single oversized child — stays in column, column overflows (v1 behaviour)
    // F) `columns` shorthand expansion via ColumnsShorthandExpander
    // G) `column-rule` shorthand expansion via ColumnRuleShorthandExpander
    // H) MulticolBox is produced by BoxBuilder for non-auto column properties
    // I) Flex/grid containers are NOT multicol containers even with column-count set
    // J) column-rule paint commands emitted for multicol boxes with non-none rule
    // K) Zero-children multicol container produces a valid empty MulticolBox
    // L) column-count=1 is a degenerate single-column case — no gaps emitted
    // M) Nested multicol — outer distributes; inner runs its own pass normally
    public class MulticolLayoutTests {

        // -----------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------

        static IEnumerable<Box> Walk(Box root) {
            yield return root;
            foreach (var c in root.Children)
                foreach (var d in Walk(c)) yield return d;
        }

        static T FindFirst<T>(Box root) where T : Box {
            foreach (var b in Walk(root)) if (b is T t) return t;
            return null;
        }

        static List<T> FindAll<T>(Box root) where T : Box {
            var result = new List<T>();
            foreach (var b in Walk(root)) if (b is T t) result.Add(t);
            return result;
        }

        static (Box root, Dictionary<Element, ComputedStyle> styles, LayoutContext ctx)
            Build(string html, string css = null, double viewportWidth = 900, double viewportHeight = 600)
            => LayoutTestHelpers.Build(html, css, viewportWidth, viewportHeight);

        // Simple 1-sentence assertion helper so tests read clearly.
        static double Tol => 0.6; // half-pixel tolerance for layout arithmetic

        // -----------------------------------------------------------------------
        // A1 — column-count only: used width = (avail - gap*(N-1)) / N
        // -----------------------------------------------------------------------
        [Test]
        public void Column_count_only_resolves_used_width() {
            // Container is 600px wide; column-count:3; no authored gap, so
            // `column-gap: normal` = 1em = 16px (css-align-3 §8.4, Chrome).
            // Expected used width = (600 - 2*16) / 3 = 568 / 3.
            var (root, _, _) = Build(
                "<div id='mc'><div>A</div><div>B</div><div>C</div></div>",
                "#mc { width: 600px; column-count: 3; }");

            var mc = FindFirst<MulticolBox>(root);
            Assert.That(mc, Is.Not.Null, "MulticolBox must be created when column-count is set");
            Assert.That(mc.UsedColumnCount, Is.EqualTo(3));
            Assert.That(mc.UsedColumnWidth, Is.EqualTo(568.0 / 3.0).Within(Tol));
        }

        // -----------------------------------------------------------------------
        // A2 — column-width only: column count derived from available width
        // -----------------------------------------------------------------------
        [Test]
        public void Column_width_only_derives_column_count_from_available_width() {
            // Container 600px; column-width:150px; default gap = 1em = 16px.
            // N = floor((600 + 16) / (150 + 16)) = floor(3.71) = 3 (Chrome).
            var (root, _, _) = Build(
                "<div id='mc'><div>A</div><div>B</div></div>",
                "#mc { width: 600px; column-width: 150px; }");

            var mc = FindFirst<MulticolBox>(root);
            Assert.That(mc, Is.Not.Null);
            Assert.That(mc.UsedColumnCount, Is.EqualTo(3));
            // Used width absorbs the leftover: (600 - 2*16) / 3 = 568 / 3.
            Assert.That(mc.UsedColumnWidth, Is.EqualTo(568.0 / 3.0).Within(Tol));
        }

        // -----------------------------------------------------------------------
        // A3 — both column-count and column-width: count caps
        // -----------------------------------------------------------------------
        [Test]
        public void Both_specified_count_caps_at_smaller_of_count_and_derived_width_count() {
            // Container 600px; column-width:100px → N_from_width=6; column-count:3.
            // Spec: usedCount = min(3, 6) = 3.
            var (root, _, _) = Build(
                "<div id='mc'><div>A</div></div>",
                "#mc { width: 600px; column-count: 3; column-width: 100px; }");

            var mc = FindFirst<MulticolBox>(root);
            Assert.That(mc, Is.Not.Null);
            Assert.That(mc.UsedColumnCount, Is.EqualTo(3));
        }

        // -----------------------------------------------------------------------
        // A4 — no column properties → no MulticolBox (plain BlockBox)
        // -----------------------------------------------------------------------
        [Test]
        public void No_column_properties_produces_plain_block_box() {
            var (root, _, _) = Build("<div id='plain'><div>A</div></div>");
            var mc = FindFirst<MulticolBox>(root);
            Assert.That(mc, Is.Null, "MulticolBox must NOT be created when no column properties are set");
        }

        // -----------------------------------------------------------------------
        // B — balanced distribution: children share height across columns
        // -----------------------------------------------------------------------
        [Test]
        public void Balanced_distribution_places_children_in_multiple_columns() {
            // 3 children each 50px tall, 3 columns → each column gets one child.
            var (root, _, _) = Build(
                "<div id='mc'>" +
                "<div id='c1'></div><div id='c2'></div><div id='c3'></div>" +
                "</div>",
                "#mc { width: 600px; column-count: 3; }" +
                "#c1, #c2, #c3 { height: 50px; }");

            var mc = FindFirst<MulticolBox>(root);
            Assert.That(mc, Is.Not.Null);

            // Find the three children.
            var kids = mc.ChildList.OfType<BlockBox>().ToList();
            Assert.That(kids.Count, Is.EqualTo(3));

            // Each should be in a different column (different X).
            var xs = kids.Select(k => k.X).Distinct().ToList();
            Assert.That(xs.Count, Is.EqualTo(3),
                "3 children must land in 3 distinct columns (distinct X values)");

            // All should start at the same Y (top of their respective columns).
            foreach (var k in kids) {
                Assert.That(k.Y, Is.EqualTo(kids[0].Y).Within(Tol),
                    "Balanced distribution: all first-in-column children share the same Y");
            }
        }

        // -----------------------------------------------------------------------
        // C — sequential fill with explicit height
        // -----------------------------------------------------------------------
        [Test]
        public void Explicit_container_height_uses_sequential_fill() {
            // Container is 100px tall, 3 columns. 4 children each 40px.
            // Column capacity = 100px. Children 1-2 go in col 0 (40+40=80<100),
            // but child 3 (80+40=120>100) moves to col 1, child 4 to col 1 as well
            // (40+40=80<100). So col 0 has 2, col 1 has 2, col 2 empty.
            var (root, _, _) = Build(
                "<div id='mc'>" +
                "<div id='c1'></div><div id='c2'></div>" +
                "<div id='c3'></div><div id='c4'></div>" +
                "</div>",
                "#mc { width: 600px; height: 100px; column-count: 3; }" +
                "#c1,#c2,#c3,#c4 { height: 40px; }");

            var mc = FindFirst<MulticolBox>(root);
            Assert.That(mc, Is.Not.Null);

            var kids = mc.ChildList.OfType<BlockBox>().ToList();
            Assert.That(kids.Count, Is.EqualTo(4));

            // Children 1 and 2 should share column 0 (same X, different Y).
            Assert.That(kids[0].X, Is.EqualTo(kids[1].X).Within(Tol),
                "Children 1 and 2 must be in the same column");
            Assert.That(kids[1].Y, Is.GreaterThan(kids[0].Y),
                "Child 2 must sit below child 1 in the same column");

            // Children 3 and 4 go to the next column (different X than 1/2).
            Assert.That(kids[2].X, Is.GreaterThan(kids[1].X + Tol),
                "Child 3 must start in a new column to the right of children 1/2");
        }

        // -----------------------------------------------------------------------
        // D — column-gap arithmetic
        // -----------------------------------------------------------------------
        [Test]
        public void Column_gap_is_subtracted_from_available_width_for_column_sizing() {
            // Container 620px, column-count:2, column-gap:20px.
            // usedWidth = (620 - 20) / 2 = 300px.
            var (root, _, _) = Build(
                "<div id='mc'><div>A</div><div>B</div></div>",
                "#mc { width: 620px; column-count: 2; column-gap: 20px; }");

            var mc = FindFirst<MulticolBox>(root);
            Assert.That(mc, Is.Not.Null);
            Assert.That(mc.UsedGap, Is.EqualTo(20).Within(Tol));
            Assert.That(mc.UsedColumnWidth, Is.EqualTo(300).Within(Tol));

            // Column 2's X origin = paddingLeft + 300 (col width) + 20 (gap) = 320.
            var kids = mc.ChildList.OfType<BlockBox>().ToList();
            if (kids.Count >= 2) {
                double expectedCol2X = mc.BorderLeft + mc.PaddingLeft + 300 + 20;
                Assert.That(kids[1].X, Is.EqualTo(expectedCol2X + kids[1].MarginLeft).Within(Tol));
            }
        }

        // -----------------------------------------------------------------------
        // E — oversized child stays in column; column overflows (v1 behaviour)
        // -----------------------------------------------------------------------
        [Test]
        public void Single_child_taller_than_column_stays_in_column_and_overflows() {
            // 2 columns, child is 300px tall. Balanced guess = 300/2 = 150.
            // That child alone exceeds 150px but since cursor=0 it can't move.
            // It stays in col 0; col 0 height = 300.
            var (root, _, _) = Build(
                "<div id='mc'><div id='tall'></div></div>",
                "#mc { width: 400px; column-count: 2; }" +
                "#tall { height: 300px; }");

            var mc = FindFirst<MulticolBox>(root);
            Assert.That(mc, Is.Not.Null);
            // Container height = tallest column height + padding/border frame.
            double frameV = mc.PaddingTop + mc.PaddingBottom + mc.BorderTop + mc.BorderBottom;
            Assert.That(mc.Height, Is.EqualTo(300 + frameV).Within(Tol),
                "Container must grow to accommodate a child taller than balanced column height");
        }

        // -----------------------------------------------------------------------
        // F — `columns` shorthand expansion
        // -----------------------------------------------------------------------
        [Test]
        public void Columns_shorthand_count_only_expands_width_to_auto() {
            var expander = new ColumnsShorthandExpander();
            var d = expander.Expand("3").ToDictionary(kv => kv.Key, kv => kv.Value);
            Assert.That(d["column-count"], Is.EqualTo("3"));
            Assert.That(d["column-width"], Is.EqualTo("auto"));
        }

        [Test]
        public void Columns_shorthand_width_only_expands_count_to_auto() {
            var expander = new ColumnsShorthandExpander();
            var d = expander.Expand("200px").ToDictionary(kv => kv.Key, kv => kv.Value);
            Assert.That(d["column-width"], Is.EqualTo("200px"));
            Assert.That(d["column-count"], Is.EqualTo("auto"));
        }

        [Test]
        public void Columns_shorthand_width_and_count_in_any_order() {
            var expander = new ColumnsShorthandExpander();
            // Width first.
            var d1 = expander.Expand("150px 2").ToDictionary(kv => kv.Key, kv => kv.Value);
            Assert.That(d1["column-width"], Is.EqualTo("150px"));
            Assert.That(d1["column-count"], Is.EqualTo("2"));
            // Count first.
            var d2 = expander.Expand("2 150px").ToDictionary(kv => kv.Key, kv => kv.Value);
            Assert.That(d2["column-width"], Is.EqualTo("150px"));
            Assert.That(d2["column-count"], Is.EqualTo("2"));
        }

        [Test]
        public void Columns_shorthand_auto_expands_both_to_auto() {
            var expander = new ColumnsShorthandExpander();
            var d = expander.Expand("auto").ToDictionary(kv => kv.Key, kv => kv.Value);
            Assert.That(d["column-count"], Is.EqualTo("auto"));
            Assert.That(d["column-width"], Is.EqualTo("auto"));
        }

        // -----------------------------------------------------------------------
        // G — `column-rule` shorthand expansion
        // -----------------------------------------------------------------------
        [Test]
        public void Column_rule_shorthand_style_only_fills_defaults() {
            var expander = new ColumnRuleShorthandExpander();
            var d = expander.Expand("solid").ToDictionary(kv => kv.Key, kv => kv.Value);
            Assert.That(d["column-rule-style"], Is.EqualTo("solid"));
            Assert.That(d["column-rule-width"], Is.EqualTo("medium"));
            Assert.That(d["column-rule-color"], Is.EqualTo("currentcolor"));
        }

        [Test]
        public void Column_rule_shorthand_width_style_color_all_tokens() {
            var expander = new ColumnRuleShorthandExpander();
            var d = expander.Expand("2px dashed red").ToDictionary(kv => kv.Key, kv => kv.Value);
            Assert.That(d["column-rule-width"], Is.EqualTo("2px"));
            Assert.That(d["column-rule-style"], Is.EqualTo("dashed"));
            Assert.That(d["column-rule-color"], Is.EqualTo("red"));
        }

        // -----------------------------------------------------------------------
        // H — BoxBuilder produces MulticolBox for block containers with column props
        // -----------------------------------------------------------------------
        [Test]
        public void Box_builder_produces_MulticolBox_for_block_with_column_count() {
            var (root, _, _) = Build(
                "<div id='mc'><div>A</div></div>",
                "#mc { column-count: 2; }");
            var mc = FindFirst<MulticolBox>(root);
            Assert.That(mc, Is.Not.Null,
                "BoxBuilder must produce a MulticolBox for a div with column-count");
        }

        [Test]
        public void Box_builder_produces_MulticolBox_for_block_with_column_width() {
            var (root, _, _) = Build(
                "<div id='mc'><div>A</div></div>",
                "#mc { column-width: 100px; }");
            var mc = FindFirst<MulticolBox>(root);
            Assert.That(mc, Is.Not.Null,
                "BoxBuilder must produce a MulticolBox for a div with column-width");
        }

        // -----------------------------------------------------------------------
        // I — flex/grid containers are NOT multicol containers
        // -----------------------------------------------------------------------
        [Test]
        public void Flex_container_with_column_count_is_not_a_MulticolBox() {
            // Per CSS Multicol §2: flex containers are not multicol containers.
            var (root, _, _) = Build(
                "<div id='flex'><div>A</div></div>",
                "#flex { display: flex; column-count: 3; }");
            var mc = FindFirst<MulticolBox>(root);
            Assert.That(mc, Is.Null,
                "A flex container must NOT become a MulticolBox even when column-count is set");
        }

        // -----------------------------------------------------------------------
        // J — column-rule paint: FillRect commands emitted for gaps
        // -----------------------------------------------------------------------
        [Test]
        public void Column_rule_none_emits_no_extra_paint_commands() {
            // column-rule-style defaults to "none" — no rules should appear.
            var (root, styles, ctx) = Build(
                "<div id='mc'><div>A</div><div>B</div></div>",
                "#mc { width: 400px; column-count: 2; }");

            var mc = FindFirst<MulticolBox>(root);
            Assert.That(mc, Is.Not.Null);

            var converter = new BoxToPaintConverter();
            var list = converter.Convert(root);

            // Count FillRect commands (column rules would be FillRect if style != none).
            // For a "none" rule, no additional fills beyond normal box decoration.
            int fills = 0;
            foreach (var cmd in list.Commands) {
                if (cmd is FillRectCommand) fills++;
            }
            // 2-column layout with no background and no column-rule → 0 FillRects.
            Assert.That(fills, Is.EqualTo(0),
                "No FillRect commands expected when no background or column-rule is set");
        }

        [Test]
        public void Column_rule_solid_emits_fill_rect_for_each_gap() {
            // 3 columns → 2 gaps → 2 column rules.
            var (root, styles, ctx) = Build(
                "<div id='mc'><div>A</div><div>B</div><div>C</div></div>",
                "#mc { width: 600px; column-count: 3; column-rule: 2px solid red; }");

            var mc = FindFirst<MulticolBox>(root);
            Assert.That(mc, Is.Not.Null);
            Assert.That(mc.UsedColumnCount, Is.EqualTo(3));

            var converter = new BoxToPaintConverter();
            var list = converter.Convert(root);

            // Count FillRect commands — each column rule is a FillRect.
            int fills = 0;
            foreach (var cmd in list.Commands) {
                if (cmd is FillRectCommand) fills++;
            }
            // Exactly 2 gap rules (N-1 for N=3 columns) with no backgrounds.
            Assert.That(fills, Is.EqualTo(2),
                "Exactly N-1 FillRect commands expected for N-1 column gaps with a solid rule");
        }

        // -----------------------------------------------------------------------
        // K — empty multicol container (no in-flow children)
        // -----------------------------------------------------------------------
        [Test]
        public void Empty_multicol_container_is_valid_and_has_correct_geometry() {
            var (root, _, _) = Build(
                "<div id='mc'></div>",
                "#mc { width: 300px; column-count: 2; }");

            var mc = FindFirst<MulticolBox>(root);
            Assert.That(mc, Is.Not.Null);
            Assert.That(mc.UsedColumnCount, Is.EqualTo(2));
            Assert.That(mc.ColumnHeights, Is.Not.Null);
            // Container with no children has height = frame only (padding + border).
            double frame = mc.PaddingTop + mc.PaddingBottom + mc.BorderTop + mc.BorderBottom;
            Assert.That(mc.Height, Is.EqualTo(frame).Within(Tol));
        }

        // -----------------------------------------------------------------------
        // L — column-count: 1 is degenerate — single column, full width, no gaps
        // -----------------------------------------------------------------------
        [Test]
        public void Column_count_1_produces_single_column_at_full_width() {
            var (root, _, _) = Build(
                "<div id='mc'><div id='c'>A</div></div>",
                "#mc { width: 400px; column-count: 1; }");

            var mc = FindFirst<MulticolBox>(root);
            Assert.That(mc, Is.Not.Null);
            Assert.That(mc.UsedColumnCount, Is.EqualTo(1));
            // Single column → no gap used.
            Assert.That(mc.UsedColumnWidth, Is.EqualTo(400).Within(Tol));
        }

        // -----------------------------------------------------------------------
        // M — multicol inside flex: the multicol post-pass runs after flex
        // -----------------------------------------------------------------------
        [Test]
        public void Multicol_inside_flex_item_still_produces_MulticolBox() {
            var (root, _, _) = Build(
                "<div id='flex'><div id='mc'><div>A</div><div>B</div></div></div>",
                "#flex { display: flex; width: 800px; }" +
                "#mc { column-count: 2; flex: 1; }");

            var mcs = FindAll<MulticolBox>(root);
            Assert.That(mcs.Count, Is.EqualTo(1),
                "One MulticolBox must be produced for the multicol child inside a flex container");
            Assert.That(mcs[0].UsedColumnCount, Is.EqualTo(2));
        }
    }
}
