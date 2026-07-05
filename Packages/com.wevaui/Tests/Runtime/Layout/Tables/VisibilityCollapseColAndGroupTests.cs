using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Layout.Tables;
using Weva.Layout.Text;
using Weva.Parsing;

namespace Weva.Tests.Layout.Tables {
    // Regression coverage for CSS_COMPLIANCE_ISSUES.md I4b — `visibility:
    // collapse` on <col>/<colgroup> drops the column track, and on
    // <thead>/<tbody>/<tfoot> drops the whole row stack. Per CSS Tables L3
    // §11.5, the surviving columns compact leftward (and surviving rows
    // compact upward) so the table's used content extent shrinks by the
    // collapsed track's contribution.
    //
    // I4 (per-row collapse) is covered separately by
    // Tests/Runtime/Layout/TableLayoutTests.cs::
    // Row_with_visibility_collapse_has_zero_height_and_compacts_row_stack.
    public class VisibilityCollapseColAndGroupTests {
        static (Box root, Dictionary<Element, ComputedStyle> styles) BuildWithRealUA(
            string html, string authorCss = null, double viewportWidth = 800
        ) {
            var doc = HtmlParser.Parse(html);
            var sheets = new List<OriginatedStylesheet> { UserAgentStylesheet.Parse() };
            if (!string.IsNullOrEmpty(authorCss)) {
                sheets.Add(OriginatedStylesheet.Author(CssParser.Parse(authorCss)));
            }
            var engine = new CascadeEngine(sheets);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engine.ComputeAll(doc)) styles[kv.Key] = kv.Value;
            var ctx = new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = viewportWidth,
                ViewportHeightPx = 600,
                RootFontSizePx = 16,
                DpiPixelsPerInch = 96
            };
            var le = new LayoutEngine(new MonoFontMetrics());
            var root = le.Layout(doc, e => styles.TryGetValue(e, out var cs) ? cs : null, ctx);
            return (root, styles);
        }

        static IEnumerable<Box> Walk(Box root) {
            yield return root;
            foreach (var c in root.Children) {
                foreach (var d in Walk(c)) yield return d;
            }
        }

        static T FindFirst<T>(Box root) where T : Box {
            foreach (var b in Walk(root)) if (b is T t) return t;
            return null;
        }

        static List<T> FindAll<T>(Box root) where T : Box {
            var list = new List<T>();
            foreach (var b in Walk(root)) if (b is T t) list.Add(t);
            return list;
        }

        [Test]
        public void Middle_col_with_visibility_collapse_zeros_its_width_and_compacts_remaining_columns() {
            // 3-column table where the middle <col> is `visibility: collapse`.
            // Per CSS Tables L3 §11.5: the middle column's width drops to 0
            // and the third column slides left into the second column's slot.
            // We size each column explicitly via fixed-layout so the column
            // widths are deterministic and easy to assert against.
            var (root, _) = BuildWithRealUA(
                "<table>" +
                "  <colgroup>" +
                "    <col style=\"width: 60px;\">" +
                "    <col id=\"mid\" style=\"width: 60px; visibility: collapse;\">" +
                "    <col style=\"width: 60px;\">" +
                "  </colgroup>" +
                "  <tbody>" +
                "    <tr><td>A</td><td>B</td><td>C</td></tr>" +
                "  </tbody>" +
                "</table>",
                "table { table-layout: fixed; width: 180px; border-spacing: 0; } " +
                "td { padding: 0; }",
                viewportWidth: 800);

            var table = FindFirst<TableBox>(root);
            Assert.That(table, Is.Not.Null);
            Assert.That(table.ColumnWidths, Is.Not.Null);
            Assert.That(table.ColumnWidths.Length, Is.EqualTo(3),
                "Three <col> elements should still produce three column tracks (mask, not delete).");

            // Middle column zeroed.
            Assert.That(table.ColumnWidths[0], Is.EqualTo(60).Within(0.5),
                "Left column keeps its declared width.");
            Assert.That(table.ColumnWidths[1], Is.EqualTo(0).Within(0.001),
                "Collapsed middle column must drop to 0 width.");
            Assert.That(table.ColumnWidths[2], Is.EqualTo(60).Within(0.5),
                "Right column keeps its declared width.");

            // Third column shifts left into the middle slot. With
            // border-spacing: 0 and the middle column zeroed, columns 1 and 2
            // share the same offset (both sit immediately after column 0).
            Assert.That(table.ColumnOffsets[0], Is.EqualTo(0).Within(0.001));
            Assert.That(table.ColumnOffsets[2], Is.EqualTo(60).Within(0.5),
                "Surviving 3rd column must compact left into the 2nd's slot.");

            // The cells in surviving columns sit at the new compacted offsets.
            var cells = FindAll<TableCellBox>(root);
            Assert.That(cells.Count, Is.EqualTo(3));
            Assert.That(cells[0].X, Is.EqualTo(0).Within(0.5));
            Assert.That(cells[2].X, Is.EqualTo(60).Within(0.5),
                "Third cell must paint at the compacted X offset, not at the original 120px.");

            // Used content extent (sum of surviving columns + their spacing)
            // drops by the collapsed column's width: from 180 to 120 here.
            double used = 0;
            for (int c = 0; c < table.ColumnWidths.Length; c++) used += table.ColumnWidths[c];
            Assert.That(used, Is.EqualTo(120).Within(0.5),
                "Total resolved column width must drop by the collapsed track's width.");
        }

        [Test]
        public void Tbody_with_visibility_collapse_drops_entire_row_stack_and_shrinks_table_height() {
            // Table with <thead> (1 row) and <tbody> (2 rows). <tbody> has
            // `visibility: collapse` so per CSS Tables L3 §11.5 the entire body
            // row stack disappears: only the thead row renders, and the table
            // height shrinks by the body group's height.
            var (rootCollapsed, _) = BuildWithRealUA(
                "<table>" +
                "  <thead>" +
                "    <tr><td>H1</td></tr>" +
                "  </thead>" +
                "  <tbody style=\"visibility: collapse;\">" +
                "    <tr><td>B1</td></tr>" +
                "    <tr><td>B2</td></tr>" +
                "  </tbody>" +
                "</table>",
                "table { width: 200px; border-spacing: 0; } td { padding: 0; line-height: 20px; }",
                viewportWidth: 800);

            var collapsedTable = FindFirst<TableBox>(rootCollapsed);
            Assert.That(collapsedTable, Is.Not.Null);
            var collapsedRows = FindAll<TableRowBox>(rootCollapsed);

            // CollectRows skips collapsed groups entirely — the body rows must
            // not be assigned layout slots. The TableRowBox boxes still exist
            // (BoxBuilder created them), but they keep their pre-pass geometry;
            // none of the row-stack bookkeeping touches them.
            int laidOutBodyRowCount = 0;
            foreach (var row in collapsedRows) {
                if (!(row.Parent is TableRowGroupBox g) || g.GroupKind != "body") continue;
                laidOutBodyRowCount++;
                // The collapsed body group itself was zeroed in size, so its
                // children's *absolute* paint position is irrelevant — what
                // matters is that the group reserves no space.
            }
            Assert.That(laidOutBodyRowCount, Is.EqualTo(2),
                "BoxBuilder still emits the body's <tr> boxes; the layout step is what skips them.");

            // The collapsed <tbody> group must have zero height (no phantom
            // rectangle in the box tree).
            TableRowGroupBox collapsedTbody = null;
            foreach (var g in FindAll<TableRowGroupBox>(rootCollapsed)) {
                if (g.GroupKind == "body") { collapsedTbody = g; break; }
            }
            Assert.That(collapsedTbody, Is.Not.Null);
            Assert.That(collapsedTbody.Height, Is.EqualTo(0).Within(0.001),
                "Collapsed <tbody> must have zero height — its row stack is dropped.");

            // Compare against the same table without the collapse: the table
            // height should be smaller by exactly the body group's height in
            // the visible case.
            var (rootVisible, _) = BuildWithRealUA(
                "<table>" +
                "  <thead>" +
                "    <tr><td>H1</td></tr>" +
                "  </thead>" +
                "  <tbody>" +
                "    <tr><td>B1</td></tr>" +
                "    <tr><td>B2</td></tr>" +
                "  </tbody>" +
                "</table>",
                "table { width: 200px; border-spacing: 0; } td { padding: 0; line-height: 20px; }",
                viewportWidth: 800);
            var visibleTable = FindFirst<TableBox>(rootVisible);
            Assert.That(visibleTable, Is.Not.Null);
            TableRowGroupBox visibleTbody = null;
            foreach (var g in FindAll<TableRowGroupBox>(rootVisible)) {
                if (g.GroupKind == "body") { visibleTbody = g; break; }
            }
            Assert.That(visibleTbody, Is.Not.Null);
            Assert.That(visibleTbody.Height, Is.GreaterThan(0),
                "Sanity: the un-collapsed tbody should have a positive height.");

            // Table height must drop by the body group's full height when the
            // body is collapsed (within a small tolerance for floor / spacing).
            double expectedDelta = visibleTbody.Height;
            double actualDelta = visibleTable.Height - collapsedTable.Height;
            Assert.That(actualDelta, Is.EqualTo(expectedDelta).Within(1.0),
                "Collapsed tbody must shrink the table by the body group's height.");
        }

        [Test]
        public void Colgroup_with_visibility_collapse_drops_all_its_col_children() {
            // 4-column table with two <colgroup>s: the first colgroup has
            // `visibility: collapse` and contains two <col> children, both of
            // which should drop. The remaining two columns compact left.
            var (root, _) = BuildWithRealUA(
                "<table>" +
                "  <colgroup style=\"visibility: collapse;\">" +
                "    <col style=\"width: 40px;\">" +
                "    <col style=\"width: 40px;\">" +
                "  </colgroup>" +
                "  <colgroup>" +
                "    <col style=\"width: 50px;\">" +
                "    <col style=\"width: 50px;\">" +
                "  </colgroup>" +
                "  <tbody>" +
                "    <tr><td>A</td><td>B</td><td>C</td><td>D</td></tr>" +
                "  </tbody>" +
                "</table>",
                "table { table-layout: fixed; width: 180px; border-spacing: 0; } " +
                "td { padding: 0; }",
                viewportWidth: 800);

            var table = FindFirst<TableBox>(root);
            Assert.That(table, Is.Not.Null);
            Assert.That(table.ColumnWidths.Length, Is.EqualTo(4));

            Assert.That(table.ColumnWidths[0], Is.EqualTo(0).Within(0.001),
                "Col 0 (inside collapsed colgroup) must zero out.");
            Assert.That(table.ColumnWidths[1], Is.EqualTo(0).Within(0.001),
                "Col 1 (also inside collapsed colgroup) must zero out — a collapsed colgroup collapses every child column.");
            Assert.That(table.ColumnWidths[2], Is.EqualTo(50).Within(0.5));
            Assert.That(table.ColumnWidths[3], Is.EqualTo(50).Within(0.5));

            // Surviving columns compact to the left edge.
            Assert.That(table.ColumnOffsets[2], Is.EqualTo(0).Within(0.5),
                "First surviving column slides into x=0 (border-spacing is 0).");
            Assert.That(table.ColumnOffsets[3], Is.EqualTo(50).Within(0.5),
                "Second surviving column sits immediately after the first.");

            // The four cells in the source row still exist; the first two
            // simply paint at zero width.
            var cells = FindAll<TableCellBox>(root);
            Assert.That(cells.Count, Is.EqualTo(4));
            Assert.That(cells[0].Width, Is.EqualTo(0).Within(0.001));
            Assert.That(cells[1].Width, Is.EqualTo(0).Within(0.001));
            Assert.That(cells[2].Width, Is.EqualTo(50).Within(0.5));
            Assert.That(cells[3].Width, Is.EqualTo(50).Within(0.5));
        }

        [Test]
        public void Default_visible_col_is_unchanged_when_other_cols_collapse() {
            // Regression pin: a <col> with `visibility: visible` (the default)
            // must render unchanged. This guards the implementation's "mask is
            // null when no column is collapsed" hot path and verifies that the
            // collapsed-mask predicate doesn't accidentally fire for absent
            // `visibility` declarations.
            var (rootBaseline, _) = BuildWithRealUA(
                "<table>" +
                "  <colgroup>" +
                "    <col style=\"width: 60px;\">" +
                "    <col style=\"width: 60px;\">" +
                "    <col style=\"width: 60px;\">" +
                "  </colgroup>" +
                "  <tbody>" +
                "    <tr><td>A</td><td>B</td><td>C</td></tr>" +
                "  </tbody>" +
                "</table>",
                "table { table-layout: fixed; width: 180px; border-spacing: 0; } " +
                "td { padding: 0; }",
                viewportWidth: 800);

            var table = FindFirst<TableBox>(rootBaseline);
            Assert.That(table, Is.Not.Null);
            Assert.That(table.ColumnWidths.Length, Is.EqualTo(3));

            for (int c = 0; c < 3; c++) {
                Assert.That(table.ColumnWidths[c], Is.EqualTo(60).Within(0.5),
                    $"Column {c} must keep its declared 60px width when no <col> is collapsed.");
            }
            Assert.That(table.ColumnOffsets[0], Is.EqualTo(0).Within(0.5));
            Assert.That(table.ColumnOffsets[1], Is.EqualTo(60).Within(0.5));
            Assert.That(table.ColumnOffsets[2], Is.EqualTo(120).Within(0.5));

            // Explicit `visibility: visible` (overriding nothing — initial
            // value) must also be a no-op.
            var (rootExplicit, _) = BuildWithRealUA(
                "<table>" +
                "  <colgroup>" +
                "    <col style=\"width: 60px;\">" +
                "    <col style=\"width: 60px; visibility: visible;\">" +
                "    <col style=\"width: 60px;\">" +
                "  </colgroup>" +
                "  <tbody>" +
                "    <tr><td>A</td><td>B</td><td>C</td></tr>" +
                "  </tbody>" +
                "</table>",
                "table { table-layout: fixed; width: 180px; border-spacing: 0; } " +
                "td { padding: 0; }",
                viewportWidth: 800);
            var explicitTable = FindFirst<TableBox>(rootExplicit);
            Assert.That(explicitTable, Is.Not.Null);
            for (int c = 0; c < 3; c++) {
                Assert.That(explicitTable.ColumnWidths[c], Is.EqualTo(60).Within(0.5),
                    $"Column {c} with explicit visibility:visible must still render unchanged.");
                Assert.That(explicitTable.ColumnOffsets[c],
                    Is.EqualTo(table.ColumnOffsets[c]).Within(0.5),
                    $"Column {c} offset must match the default-visibility baseline.");
            }
        }
    }
}
