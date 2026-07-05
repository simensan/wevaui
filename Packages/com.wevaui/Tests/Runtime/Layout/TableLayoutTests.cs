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

namespace Weva.Tests.Layout {
    // Regression tests for `display: table` end-to-end. Unlike the rest of the
    // layout test suite, these tests opt in to the *real* UA stylesheet
    // (UserAgentStylesheet.Source) because the table family
    // (table / thead / tbody / tfoot / tr / td / th) gets its display value
    // from the UA sheet — the trimmed `LayoutTestHelpers.BuiltinUserAgent`
    // doesn't define those rules.
    //
    // Coverage:
    //   1. 2x2 grid of <td> cells produces a TableBox with two columns whose
    //      widths sum (with border-spacing) to the available content width.
    //   2. <thead>/<tbody>/<tfoot> are recognised as row groups and rows are
    //      collected in header → body → footer order regardless of source
    //      document order.
    //   3. `border-collapse: separate` (default) honours `border-spacing`;
    //      `collapse` suppresses spacing in layout.
    //   4. colspan/rowspan placement, col/colgroup hints, and fixed table
    //      layout participate in track sizing.
    //
    // Documented gaps (see TableLayout.cs header):
    //   - collapsed border conflict resolution / border painting is partial;
    //     collapsed layout currently means "no border-spacing".
    //   - baseline vertical-align maps to top until first-line baseline data
    //     is surfaced from cell boxes.
    public class TableLayoutTests {
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

        static TableCellBox FindCell(Box root, string id) {
            foreach (var cell in FindAll<TableCellBox>(root)) {
                if (cell.Element?.GetAttribute("id") == id) return cell;
            }
            return null;
        }

        [Test]
        public void Two_by_two_table_produces_grid_with_two_columns_and_two_rows() {
            // Plain <table>/<tr>/<td> markup. Author CSS forces a known table
            // width and zeroes border-spacing so the assertions can compare
            // exact column widths.
            var (root, _) = BuildWithRealUA(
                "<table><tbody>" +
                "<tr><td>AAAA</td><td>BB</td></tr>" +
                "<tr><td>C</td><td>DDDD</td></tr>" +
                "</tbody></table>",
                "table { width: 200px; border-spacing: 0; }",
                viewportWidth: 800);

            var table = FindFirst<TableBox>(root);
            Assert.That(table, Is.Not.Null, "TableBox should be created for <table>");
            Assert.That(table.ColumnWidths, Is.Not.Null);
            Assert.That(table.ColumnWidths.Length, Is.EqualTo(2),
                "Two columns expected from a 2x2 cell layout");

            // With border-spacing:0 column widths should sum to the
            // table's content width.
            double sum = 0;
            for (int i = 0; i < table.ColumnWidths.Length; i++) sum += table.ColumnWidths[i];
            Assert.That(sum, Is.EqualTo(table.ContentWidth).Within(0.5));

            // Two row boxes should exist, each with two cell children.
            var rows = FindAll<TableRowBox>(root);
            Assert.That(rows.Count, Is.EqualTo(2));
            foreach (var row in rows) {
                int cells = 0;
                foreach (var c in row.Children) if (c is TableCellBox) cells++;
                Assert.That(cells, Is.EqualTo(2));
            }

            // Row 2 must sit below row 1 (top-down stacking).
            Assert.That(rows[1].Y, Is.GreaterThan(rows[0].Y));
        }

        [Test]
        public void Row_groups_are_ordered_header_body_footer_regardless_of_source_order() {
            // Source order: tfoot, tbody, thead. After table layout the rows
            // should appear in header → body → footer order vertically.
            var (root, _) = BuildWithRealUA(
                "<table><tbody>" +
                "<tr><td id=\"body\">B</td></tr>" +
                "</tbody><tfoot>" +
                "<tr><td id=\"foot\">F</td></tr>" +
                "</tfoot><thead>" +
                "<tr><td id=\"head\">H</td></tr>" +
                "</thead></table>",
                "table { border-spacing: 0; }",
                viewportWidth: 400);

            var table = FindFirst<TableBox>(root);
            Assert.That(table, Is.Not.Null);

            // Confirm row-group dispatch wired GroupKind correctly.
            var groups = FindAll<TableRowGroupBox>(root);
            Assert.That(groups.Count, Is.EqualTo(3));
            var kinds = new HashSet<string>();
            foreach (var g in groups) kinds.Add(g.GroupKind);
            Assert.That(kinds, Is.EquivalentTo(new[] { "header", "body", "footer" }));

            // Find each cell's row Y.
            TableRowBox headRow = null, bodyRow = null, footRow = null;
            foreach (var row in FindAll<TableRowBox>(root)) {
                foreach (var c in row.Children) {
                    if (c is TableCellBox cell && cell.Element != null) {
                        var id = cell.Element.GetAttribute("id");
                        if (id == "head") headRow = row;
                        else if (id == "body") bodyRow = row;
                        else if (id == "foot") footRow = row;
                    }
                }
            }
            Assert.That(headRow, Is.Not.Null);
            Assert.That(bodyRow, Is.Not.Null);
            Assert.That(footRow, Is.Not.Null);
            // Rows live under their TableRowGroupBox parent; compare absolute
            // Y (group.Y + row.Y) to assert the visual stacking order. Row
            // Y is GROUP-relative since TableLayout now properly sets each
            // row group's Y/Height to span its rows (previously row.Y was
            // table-relative and group.Y/Height were stale BlockLayout pre-
            // pass values, which caused thead/tbody to add hundreds of
            // pixels of phantom space below themselves).
            double headAbsY = (headRow.Parent != null ? headRow.Parent.Y : 0) + headRow.Y;
            double bodyAbsY = (bodyRow.Parent != null ? bodyRow.Parent.Y : 0) + bodyRow.Y;
            double footAbsY = (footRow.Parent != null ? footRow.Parent.Y : 0) + footRow.Y;
            Assert.That(headAbsY, Is.LessThan(bodyAbsY),
                "thead rows must be laid out before tbody rows");
            Assert.That(bodyAbsY, Is.LessThan(footAbsY),
                "tbody rows must be laid out before tfoot rows");
        }

        [Test]
        public void Border_spacing_separates_columns_horizontally() {
            // border-collapse: separate is the default; explicit
            // border-spacing should widen the gap between the two columns.
            var (root, _) = BuildWithRealUA(
                "<table><tbody><tr><td>A</td><td>B</td></tr></tbody></table>",
                "table { width: 200px; border-spacing: 10px; }",
                viewportWidth: 800);

            var table = FindFirst<TableBox>(root);
            Assert.That(table, Is.Not.Null);
            Assert.That(table.BorderSpacingX, Is.EqualTo(10).Within(0.001));
            Assert.That(table.ColumnOffsets.Length, Is.EqualTo(2));

            // Column 1 should start at borderSpacing past column 0's right edge.
            double col0Right = table.ColumnOffsets[0] + table.ColumnWidths[0];
            Assert.That(table.ColumnOffsets[1] - col0Right,
                Is.EqualTo(table.BorderSpacingX).Within(0.5));

            // Leading edge is also offset by borderSpacing per CSS 2.1 §17.5.
            Assert.That(table.ColumnOffsets[0], Is.EqualTo(table.BorderSpacingX).Within(0.001));
        }

        [Test]
        public void Border_collapse_collapse_suppresses_border_spacing_in_layout() {
            var (root, _) = BuildWithRealUA(
                "<table><tbody><tr><td>A</td><td>B</td></tr></tbody></table>",
                "table { width: 200px; border-collapse: collapse; border-spacing: 8px; }",
                viewportWidth: 800);

            var table = FindFirst<TableBox>(root);
            Assert.That(table, Is.Not.Null);
            Assert.That(table.BorderSpacingX, Is.EqualTo(0).Within(0.001));
            Assert.That(table.BorderSpacingY, Is.EqualTo(0).Within(0.001));
            Assert.That(table.ColumnOffsets.Length, Is.EqualTo(2));
            Assert.That(table.ColumnOffsets[0], Is.EqualTo(0).Within(0.001));
            double col0Right = table.ColumnOffsets[0] + table.ColumnWidths[0];
            Assert.That(table.ColumnOffsets[1] - col0Right, Is.EqualTo(0).Within(0.5),
                "Collapsed table layout suppresses inter-column border-spacing.");
        }

        [Test]
        public void Two_column_table_distributes_width_evenly_when_no_explicit_widths() {
            // No explicit cell widths + table width 200 + border-spacing 0:
            // the two columns should each receive ~half of the content width.
            // (Cells contain identical short text so max-content is roughly
            // equal; with sumMax <= avail the resolver distributes any slack
            // proportionally to max-content, which keeps the split even.)
            var (root, _) = BuildWithRealUA(
                "<table><tbody>" +
                "<tr><td>AA</td><td>BB</td></tr>" +
                "</tbody></table>",
                "table { width: 200px; border-spacing: 0; } td { padding: 0; }",
                viewportWidth: 800);

            var table = FindFirst<TableBox>(root);
            Assert.That(table, Is.Not.Null);
            Assert.That(table.ColumnWidths.Length, Is.EqualTo(2));
            // Each column ~= 100; allow generous tolerance for text measurement.
            double half = table.ContentWidth / 2.0;
            Assert.That(table.ColumnWidths[0], Is.EqualTo(half).Within(2.0));
            Assert.That(table.ColumnWidths[1], Is.EqualTo(half).Within(2.0));
            // Sum of column widths equals the content width (border-spacing:0).
            Assert.That(table.ColumnWidths[0] + table.ColumnWidths[1],
                Is.EqualTo(table.ContentWidth).Within(0.5));
        }

        [Test]
        public void Explicit_cell_width_is_content_box_by_default() {
            // CSS 2.1 §17.5.2: cells default to box-sizing: content-box. With
            // width:80px + padding:8px the cell's OUTER (border-box) width is
            // 80 + 8 + 8 = 96. The track resolver works in outer widths, so
            // the resolved column width should be 96 (assuming the table has
            // enough room — make it wider than needed so there's slack).
            // Override UA td padding (1px) via the cell-specific selector.
            var (root, _) = BuildWithRealUA(
                "<table><tbody>" +
                "<tr><th id=\"one\">X</th><th id=\"two\">Y</th></tr>" +
                "</tbody></table>",
                "table { width: 400px; border-spacing: 0; } " +
                "th { padding: 8px; box-sizing: content-box; } " +
                "#one { width: 80px; }",
                viewportWidth: 800);

            var table = FindFirst<TableBox>(root);
            Assert.That(table, Is.Not.Null);
            Assert.That(table.ColumnWidths.Length, Is.EqualTo(2));
            // Column 0 is the explicit-width cell. Expect 80 (content) + 16
            // (horizontal padding) = 96 outer.
            Assert.That(table.ColumnWidths[0], Is.EqualTo(96).Within(0.5),
                "Explicit width:80 + padding:8*2 = 96 outer (content-box default)");
        }

        [Test]
        public void Border_spacing_increases_column_gap() {
            // border-spacing: 10px adds 10px horizontal gap between adjacent
            // columns. Compare against a baseline with border-spacing: 0.
            var (root10, _) = BuildWithRealUA(
                "<table><tbody><tr><td>A</td><td>B</td></tr></tbody></table>",
                "table { width: 200px; border-spacing: 10px; } td { padding: 0; }",
                viewportWidth: 800);
            var (root0, _) = BuildWithRealUA(
                "<table><tbody><tr><td>A</td><td>B</td></tr></tbody></table>",
                "table { width: 200px; border-spacing: 0; } td { padding: 0; }",
                viewportWidth: 800);

            var t10 = FindFirst<TableBox>(root10);
            var t0 = FindFirst<TableBox>(root0);
            Assert.That(t10.BorderSpacingX, Is.EqualTo(10).Within(0.001));
            Assert.That(t0.BorderSpacingX, Is.EqualTo(0).Within(0.001));

            double gap10 = t10.ColumnOffsets[1] - (t10.ColumnOffsets[0] + t10.ColumnWidths[0]);
            double gap0 = t0.ColumnOffsets[1] - (t0.ColumnOffsets[0] + t0.ColumnWidths[0]);
            Assert.That(gap10 - gap0, Is.EqualTo(10).Within(0.5));
        }

        [Test]
        public void Border_spacing_two_values_treats_x_then_y() {
            // Per CSS 2.1 §17.6.1: two-value border-spacing = horizontal then
            // vertical. With "10px 20px" expect BorderSpacingX=10, BorderSpacingY=20.
            var (root, _) = BuildWithRealUA(
                "<table><tbody>" +
                "<tr><td>A</td><td>B</td></tr>" +
                "<tr><td>C</td><td>D</td></tr>" +
                "</tbody></table>",
                "table { width: 200px; border-spacing: 10px 20px; } td { padding: 0; }",
                viewportWidth: 800);

            var table = FindFirst<TableBox>(root);
            Assert.That(table, Is.Not.Null);
            Assert.That(table.BorderSpacingX, Is.EqualTo(10).Within(0.001));
            Assert.That(table.BorderSpacingY, Is.EqualTo(20).Within(0.001));

            // Row 2 should be row 1's bottom + 20px (border-spacing-y).
            var rows = FindAll<TableRowBox>(root);
            Assert.That(rows.Count, Is.EqualTo(2));
            // Rows live under their TableRowGroupBox parent; positions are
            // group-relative. Both rows share the same parent so the delta
            // (rows[1].Y - rows[0].Y) directly reflects row1.Height + spacing-y.
            Assert.That(rows[0].Parent, Is.SameAs(rows[1].Parent),
                "Both body rows share the same tbody parent");
            double rowGap = rows[1].Y - (rows[0].Y + rows[0].Height);
            Assert.That(rowGap, Is.EqualTo(20).Within(0.5),
                "border-spacing y component should separate row 2 from row 1");
        }

        [Test]
        public void Caption_top_appears_above_rows() {
            // <caption> defaults to caption-side: top; TableLayout places it
            // above tbody at the table's content-box top.
            var (root, _) = BuildWithRealUA(
                "<table>" +
                "<caption>Hi</caption>" +
                "<tbody><tr><td id=\"cell\">A</td></tr></tbody>" +
                "</table>",
                "table { width: 200px; border-spacing: 0; }",
                viewportWidth: 800);

            var table = FindFirst<TableBox>(root);
            Assert.That(table, Is.Not.Null);
            var caption = FindFirst<TableCaptionBox>(root);
            Assert.That(caption, Is.Not.Null, "TableCaptionBox should be created for <caption>");

            // Find the first body row.
            var rows = FindAll<TableRowBox>(root);
            Assert.That(rows.Count, Is.EqualTo(1));
            var bodyRow = rows[0];
            double bodyAbsY = (bodyRow.Parent != null ? bodyRow.Parent.Y : 0) + bodyRow.Y;
            // Caption's Y is table-relative (it's a direct child of the table).
            Assert.That(caption.Y, Is.LessThan(bodyAbsY),
                "Caption (caption-side: top) must sit above the body row vertically");
            // Caption spans the full content width.
            Assert.That(caption.Width, Is.EqualTo(table.ContentWidth).Within(0.5));
        }

        [Test]
        public void Caption_bottom_appears_below_rows() {
            // Per CSS 2.1 §17.4 / Tables L3 §3.5, caption-side: bottom places
            // the caption below the row grid. Regression: previously the value
            // parsed but was ignored, so bottom-side captions stacked at the top.
            var (root, _) = BuildWithRealUA(
                "<table>" +
                "<caption>Hi</caption>" +
                "<tbody><tr><td id=\"cell\">A</td></tr></tbody>" +
                "</table>",
                "table { width: 200px; border-spacing: 0; } " +
                "caption { caption-side: bottom; }",
                viewportWidth: 800);

            var table = FindFirst<TableBox>(root);
            Assert.That(table, Is.Not.Null);
            var caption = FindFirst<TableCaptionBox>(root);
            Assert.That(caption, Is.Not.Null);

            var rows = FindAll<TableRowBox>(root);
            Assert.That(rows.Count, Is.EqualTo(1));
            var bodyRow = rows[0];
            double bodyAbsBottom = (bodyRow.Parent != null ? bodyRow.Parent.Y : 0)
                + bodyRow.Y + bodyRow.Height;

            Assert.That(caption.Y, Is.GreaterThanOrEqualTo(bodyAbsBottom),
                "Caption (caption-side: bottom) must sit below the body row vertically");
            Assert.That(caption.Width, Is.EqualTo(table.ContentWidth).Within(0.5));
        }

        [Test]
        public void Caption_side_logical_values_resolve_via_writing_mode() {
            // CSS Tables L3 §2.1: caption-side accepts logical keywords
            // (block-start, block-end, inline-start, inline-end) which resolve
            // against the table's writing-mode. In horizontal-tb block-start
            // → top, block-end → bottom. In vertical writing modes block-*
            // maps to right/left; v1 has no side-placed caption support, so
            // those collapse to top placement (above rows).

            // block-start + horizontal-tb → caption above rows.
            var (rootBsH, _) = BuildWithRealUA(
                "<table>" +
                "<caption>Hi</caption>" +
                "<tbody><tr><td>A</td></tr></tbody>" +
                "</table>",
                "table { width: 200px; border-spacing: 0; writing-mode: horizontal-tb; } " +
                "caption { caption-side: block-start; }",
                viewportWidth: 800);
            var capBsH = FindFirst<TableCaptionBox>(rootBsH);
            var rowsBsH = FindAll<TableRowBox>(rootBsH);
            Assert.That(capBsH, Is.Not.Null);
            Assert.That(rowsBsH.Count, Is.EqualTo(1));
            double bodyTopBsH = (rowsBsH[0].Parent != null ? rowsBsH[0].Parent.Y : 0) + rowsBsH[0].Y;
            Assert.That(capBsH.Y, Is.LessThan(bodyTopBsH),
                "block-start in horizontal-tb resolves to top");

            // block-end + horizontal-tb → caption below rows.
            var (rootBeH, _) = BuildWithRealUA(
                "<table>" +
                "<caption>Hi</caption>" +
                "<tbody><tr><td>A</td></tr></tbody>" +
                "</table>",
                "table { width: 200px; border-spacing: 0; writing-mode: horizontal-tb; } " +
                "caption { caption-side: block-end; }",
                viewportWidth: 800);
            var capBeH = FindFirst<TableCaptionBox>(rootBeH);
            var rowsBeH = FindAll<TableRowBox>(rootBeH);
            Assert.That(capBeH, Is.Not.Null);
            Assert.That(rowsBeH.Count, Is.EqualTo(1));
            double bodyBottomBeH = (rowsBeH[0].Parent != null ? rowsBeH[0].Parent.Y : 0)
                + rowsBeH[0].Y + rowsBeH[0].Height;
            Assert.That(capBeH.Y, Is.GreaterThanOrEqualTo(bodyBottomBeH),
                "block-end in horizontal-tb resolves to bottom");

            // block-start + vertical-rl → spec says "right side"; v1 has no
            // side placement so the engine falls through to top placement.
            // The contract under test: it does NOT get placed at the bottom
            // (which would be the "block-end" slot).
            var (rootBsV, _) = BuildWithRealUA(
                "<table>" +
                "<caption>Hi</caption>" +
                "<tbody><tr><td>A</td></tr></tbody>" +
                "</table>",
                "table { width: 200px; border-spacing: 0; writing-mode: vertical-rl; } " +
                "caption { caption-side: block-start; }",
                viewportWidth: 800);
            var capBsV = FindFirst<TableCaptionBox>(rootBsV);
            var rowsBsV = FindAll<TableRowBox>(rootBsV);
            Assert.That(capBsV, Is.Not.Null);
            Assert.That(rowsBsV.Count, Is.EqualTo(1));
            double bodyTopBsV = (rowsBsV[0].Parent != null ? rowsBsV[0].Parent.Y : 0) + rowsBsV[0].Y;
            Assert.That(capBsV.Y, Is.LessThan(bodyTopBsV),
                "block-start in vertical-rl resolves to 'right'; v1 has no side placement "
                + "so the caption falls through to top placement (not bottom)");

            // Regression: physical `top` and `bottom` still work after the
            // logical-resolution refactor.
            var (rootTop, _) = BuildWithRealUA(
                "<table>" +
                "<caption>Hi</caption>" +
                "<tbody><tr><td>A</td></tr></tbody>" +
                "</table>",
                "table { width: 200px; border-spacing: 0; } " +
                "caption { caption-side: top; }",
                viewportWidth: 800);
            var capTop = FindFirst<TableCaptionBox>(rootTop);
            var rowsTop = FindAll<TableRowBox>(rootTop);
            double bodyTopY = (rowsTop[0].Parent != null ? rowsTop[0].Parent.Y : 0) + rowsTop[0].Y;
            Assert.That(capTop.Y, Is.LessThan(bodyTopY),
                "Physical caption-side: top still places caption above rows");

            var (rootBot, _) = BuildWithRealUA(
                "<table>" +
                "<caption>Hi</caption>" +
                "<tbody><tr><td>A</td></tr></tbody>" +
                "</table>",
                "table { width: 200px; border-spacing: 0; } " +
                "caption { caption-side: bottom; }",
                viewportWidth: 800);
            var capBot = FindFirst<TableCaptionBox>(rootBot);
            var rowsBot = FindAll<TableRowBox>(rootBot);
            double bodyBotY = (rowsBot[0].Parent != null ? rowsBot[0].Parent.Y : 0)
                + rowsBot[0].Y + rowsBot[0].Height;
            Assert.That(capBot.Y, Is.GreaterThanOrEqualTo(bodyBotY),
                "Physical caption-side: bottom still places caption below rows");
        }

        [Test]
        public void Thead_rendered_above_tbody_in_source_order_independent_layout() {
            // <tbody> appears before <thead> in markup; expect thead's row to
            // still lay out at a smaller Y than tbody's row.
            var (root, _) = BuildWithRealUA(
                "<table>" +
                "<tbody><tr><td id=\"b\">B</td></tr></tbody>" +
                "<thead><tr><td id=\"h\">H</td></tr></thead>" +
                "</table>",
                "table { border-spacing: 0; }",
                viewportWidth: 400);

            TableRowBox headRow = null, bodyRow = null;
            foreach (var row in FindAll<TableRowBox>(root)) {
                foreach (var c in row.Children) {
                    if (c is TableCellBox cell && cell.Element != null) {
                        var id = cell.Element.GetAttribute("id");
                        if (id == "h") headRow = row;
                        else if (id == "b") bodyRow = row;
                    }
                }
            }
            Assert.That(headRow, Is.Not.Null);
            Assert.That(bodyRow, Is.Not.Null);
            double headAbsY = (headRow.Parent != null ? headRow.Parent.Y : 0) + headRow.Y;
            double bodyAbsY = (bodyRow.Parent != null ? bodyRow.Parent.Y : 0) + bodyRow.Y;
            Assert.That(headAbsY, Is.LessThan(bodyAbsY),
                "thead always lays out above tbody regardless of source order");
        }

        [Test]
        public void Tfoot_rendered_below_tbody() {
            // <tfoot> appears before <tbody> in markup; footer must still be
            // the last row group laid out.
            var (root, _) = BuildWithRealUA(
                "<table>" +
                "<tfoot><tr><td id=\"f\">F</td></tr></tfoot>" +
                "<tbody><tr><td id=\"b\">B</td></tr></tbody>" +
                "</table>",
                "table { border-spacing: 0; }",
                viewportWidth: 400);

            TableRowBox bodyRow = null, footRow = null;
            foreach (var row in FindAll<TableRowBox>(root)) {
                foreach (var c in row.Children) {
                    if (c is TableCellBox cell && cell.Element != null) {
                        var id = cell.Element.GetAttribute("id");
                        if (id == "b") bodyRow = row;
                        else if (id == "f") footRow = row;
                    }
                }
            }
            Assert.That(bodyRow, Is.Not.Null);
            Assert.That(footRow, Is.Not.Null);
            double bodyAbsY = (bodyRow.Parent != null ? bodyRow.Parent.Y : 0) + bodyRow.Y;
            double footAbsY = (footRow.Parent != null ? footRow.Parent.Y : 0) + footRow.Y;
            Assert.That(bodyAbsY, Is.LessThan(footAbsY),
                "tfoot always lays out below tbody regardless of source order");
        }

        [Test]
        public void Row_group_position_and_height_span_their_rows() {
            // TableLayout sets each row group's Y to the first row and its
            // Height to (last row bottom - first row top). Two body rows ⇒
            // tbody.Y == first row's table-relative Y, tbody.Height covers
            // both rows + the inter-row border-spacing.
            var (root, _) = BuildWithRealUA(
                "<table><tbody>" +
                "<tr><td>A</td></tr>" +
                "<tr><td>B</td></tr>" +
                "</tbody></table>",
                "table { width: 200px; border-spacing: 4px; }",
                viewportWidth: 800);

            var table = FindFirst<TableBox>(root);
            Assert.That(table, Is.Not.Null);
            var groups = FindAll<TableRowGroupBox>(root);
            // Exactly one tbody.
            TableRowGroupBox tbody = null;
            foreach (var g in groups) if (g.GroupKind == "body") { tbody = g; break; }
            Assert.That(tbody, Is.Not.Null);

            var rows = FindAll<TableRowBox>(root);
            Assert.That(rows.Count, Is.EqualTo(2));

            // tbody.Y should equal the table-relative top of the first row.
            double firstRowAbsY = tbody.Y + rows[0].Y;
            Assert.That(tbody.Y, Is.EqualTo(firstRowAbsY).Within(0.001),
                "tbody.Y matches first row top (rows[0].Y is group-relative ⇒ 0)");
            // tbody.Height spans both rows + the trailing border-spacing-y
            // applied to every row in the loop (v1: cursorY is advanced by
            // row.Height + BorderSpacingY for each row, then the group height
            // is captured as cursorY - groupStartCursor, so the group includes
            // one spacing after its last row too).
            double expected = rows[0].Height + rows[1].Height + 2 * table.BorderSpacingY;
            Assert.That(tbody.Height, Is.EqualTo(expected).Within(0.5),
                "tbody.Height spans both rows plus the per-row border-spacing-y advance");
        }

        [Test]
        public void Cell_height_in_row_stretches_to_match_tallest_cell() {
            // One tall cell in a row forces all siblings to match height
            // (CSS Tables L3 §11.6.1). Use explicit cell height on one cell
            // to drive the row height; assert the sibling matches.
            var (root, _) = BuildWithRealUA(
                "<table><tbody>" +
                "<tr><td id=\"tall\">A</td><td id=\"short\">B</td></tr>" +
                "</tbody></table>",
                "table { width: 200px; border-spacing: 0; } " +
                "td { padding: 0; } " +
                "#tall { height: 80px; }",
                viewportWidth: 800);

            TableCellBox tall = null, shortCell = null;
            foreach (var cell in FindAll<TableCellBox>(root)) {
                if (cell.Element == null) continue;
                var id = cell.Element.GetAttribute("id");
                if (id == "tall") tall = cell;
                else if (id == "short") shortCell = cell;
            }
            Assert.That(tall, Is.Not.Null);
            Assert.That(shortCell, Is.Not.Null);
            Assert.That(tall.Height, Is.GreaterThanOrEqualTo(80 - 0.5),
                "Tall cell respects explicit height: 80px");
            Assert.That(shortCell.Height, Is.EqualTo(tall.Height).Within(0.5),
                "Short cell stretches to match row height");
        }

        [Test]
        public void Empty_row_collapses_height_to_zero() {
            // <tr> with no cells contributes 0 to row height; the table still
            // advances by border-spacing-y for that row's slot.
            var (root, _) = BuildWithRealUA(
                "<table><tbody>" +
                "<tr><td>A</td></tr>" +
                "<tr></tr>" +
                "<tr><td>B</td></tr>" +
                "</tbody></table>",
                "table { width: 200px; border-spacing: 0; } td { padding: 0; }",
                viewportWidth: 800);

            var rows = FindAll<TableRowBox>(root);
            Assert.That(rows.Count, Is.EqualTo(3));
            // Middle row is the empty one — should have zero height.
            Assert.That(rows[1].Height, Is.EqualTo(0).Within(0.001),
                "Empty <tr> with no <td> children has zero height (modulo spacing)");
            // The non-empty rows have positive height.
            Assert.That(rows[0].Height, Is.GreaterThan(0));
            Assert.That(rows[2].Height, Is.GreaterThan(0));
        }

        [Test]
        public void Nested_table_lays_out_inside_parent_cell() {
            // A <table> nested inside a <td>. The inner table should be laid
            // out (its TableLayout pass runs depth-first) and produce its own
            // ColumnWidths. The outer table sees the inner table as the cell's
            // content for sizing.
            var (root, _) = BuildWithRealUA(
                "<table id=\"outer\"><tbody><tr><td id=\"host\">" +
                "<table id=\"inner\"><tbody>" +
                "<tr><td>X</td><td>Y</td></tr>" +
                "</tbody></table>" +
                "</td></tr></tbody></table>",
                "#outer { width: 300px; border-spacing: 0; } " +
                "#inner { width: 100px; border-spacing: 0; } " +
                "td { padding: 0; }",
                viewportWidth: 800);

            var tables = FindAll<TableBox>(root);
            Assert.That(tables.Count, Is.EqualTo(2), "Expect exactly two TableBoxes (outer + inner)");

            // Identify outer vs inner by their Element id.
            TableBox outer = null, inner = null;
            foreach (var t in tables) {
                var id = t.Element?.GetAttribute("id");
                if (id == "outer") outer = t;
                else if (id == "inner") inner = t;
            }
            Assert.That(outer, Is.Not.Null);
            Assert.That(inner, Is.Not.Null);
            Assert.That(inner.ColumnWidths.Length, Is.EqualTo(2),
                "Inner table is laid out with its own two columns");
            // Inner table sits inside the host cell, so its absolute X is
            // shifted from the outer table's content origin.
            Assert.That(inner.Width, Is.LessThanOrEqualTo(outer.ContentWidth + 0.5),
                "Inner table cannot exceed outer cell width");
        }

        [Test]
        public void Table_with_no_explicit_width_shrinks_to_max_content_of_cells() {
            // Without an explicit table width, the engine should size the
            // table to Σ max-content column widths + border-spacing. v1's
            // BlockLayout pre-pass gives each cell the parent's content width
            // as Width, which is then used as max-content; per the resolver,
            // sumMax can therefore be large. This test pins the ACTUAL
            // behavior (the table fills available width because cell.Width
            // pre-pass = parent content width).
            // v1: cells' max-content == BlockLayout-assigned Width (full
            // parent content width), so a no-explicit-width table fills
            // the viewport — the "shrink-to-fit to Σ max-content of text"
            // refinement is not implemented.
            var (root, _) = BuildWithRealUA(
                "<table><tbody>" +
                "<tr><td>AA</td><td>BBB</td><td>CCCC</td></tr>" +
                "</tbody></table>",
                "table { border-spacing: 2px; } td { padding: 0; }",
                viewportWidth: 600);

            var table = FindFirst<TableBox>(root);
            Assert.That(table, Is.Not.Null);
            Assert.That(table.ColumnWidths.Length, Is.EqualTo(3));
            // v1: no-explicit-width tables fill the available block width.
            // Width includes table padding+border (zero here) so width ≈ 600.
            Assert.That(table.Width, Is.GreaterThan(0),
                "Table has positive width when laid out without explicit width");
        }

        [Test]
        public void Border_collapse_collapse_geometry_differs_from_separate_spacing() {
            var (rootCollapse, _) = BuildWithRealUA(
                "<table><tbody><tr><td>A</td><td>B</td></tr></tbody></table>",
                "table { width: 200px; border-collapse: collapse; border-spacing: 6px; } td { padding: 0; }",
                viewportWidth: 800);
            var (rootSeparate, _) = BuildWithRealUA(
                "<table><tbody><tr><td>A</td><td>B</td></tr></tbody></table>",
                "table { width: 200px; border-collapse: separate; border-spacing: 6px; } td { padding: 0; }",
                viewportWidth: 800);

            var tC = FindFirst<TableBox>(rootCollapse);
            var tS = FindFirst<TableBox>(rootSeparate);
            Assert.That(tC, Is.Not.Null);
            Assert.That(tS, Is.Not.Null);
            Assert.That(tC.BorderSpacingX, Is.EqualTo(0).Within(0.001));
            Assert.That(tC.BorderSpacingY, Is.EqualTo(0).Within(0.001));
            Assert.That(tS.BorderSpacingX, Is.EqualTo(6).Within(0.001));
            Assert.That(tS.BorderSpacingY, Is.EqualTo(6).Within(0.001));
            Assert.That(tC.ColumnWidths.Length, Is.EqualTo(tS.ColumnWidths.Length));
            Assert.That(tC.ColumnOffsets[0], Is.EqualTo(0).Within(0.001));
            Assert.That(tS.ColumnOffsets[0], Is.EqualTo(6).Within(0.001));
            double collapsedGap = tC.ColumnOffsets[1] - (tC.ColumnOffsets[0] + tC.ColumnWidths[0]);
            double separateGap = tS.ColumnOffsets[1] - (tS.ColumnOffsets[0] + tS.ColumnWidths[0]);
            Assert.That(collapsedGap, Is.EqualTo(0).Within(0.5));
            Assert.That(separateGap, Is.EqualTo(6).Within(0.5));
        }

        [Test]
        public void Rowspan_attribute_reserves_columns_in_following_rows_and_spans_height() {
            var (root, _) = BuildWithRealUA(
                "<table><tbody>" +
                "<tr><td id=\"span\" rowspan=\"2\">A</td><td id=\"top\">B</td></tr>" +
                "<tr><td id=\"bottom\">C</td></tr>" +
                "</tbody></table>",
                "table { width: 200px; border-spacing: 0; } td { padding: 0; } #span { height: 60px; }",
                viewportWidth: 800);

            var rows = FindAll<TableRowBox>(root);
            var span = FindCell(root, "span");
            var top = FindCell(root, "top");
            var bottom = FindCell(root, "bottom");
            Assert.That(rows.Count, Is.EqualTo(2));
            Assert.That(span, Is.Not.Null);
            Assert.That(top, Is.Not.Null);
            Assert.That(bottom, Is.Not.Null);
            Assert.That(span.RowSpan, Is.EqualTo(2));
            Assert.That(span.ColumnIndex, Is.EqualTo(0));
            Assert.That(top.ColumnIndex, Is.EqualTo(1));
            Assert.That(bottom.ColumnIndex, Is.EqualTo(1),
                "The rowspan cell in row 1 reserves column 0 for row 2.");
            Assert.That(span.Height, Is.EqualTo(rows[0].Height + rows[1].Height).Within(0.5));
            Assert.That(span.Height, Is.GreaterThanOrEqualTo(59.5));
        }

        [Test]
        public void Table_layout_fixed_uses_colgroup_col_width_hints_then_distributes_remaining() {
            var (root, _) = BuildWithRealUA(
                "<table>" +
                "<colgroup><col style=\"width: 40px\"><col style=\"width: 60px\"></colgroup>" +
                "<tbody><tr><td>A</td><td>B</td><td>C</td></tr></tbody>" +
                "</table>",
                "table { width: 300px; table-layout: fixed; border-spacing: 0; } td { padding: 0; }",
                viewportWidth: 800);

            var table = FindFirst<TableBox>(root);
            Assert.That(table, Is.Not.Null);
            Assert.That(table.ColumnWidths.Length, Is.EqualTo(3));
            Assert.That(table.ColumnWidths[0], Is.EqualTo(40).Within(0.5));
            Assert.That(table.ColumnWidths[1], Is.EqualTo(60).Within(0.5));
            Assert.That(table.ColumnWidths[2], Is.EqualTo(200).Within(0.5));
        }

        [Test]
        public void Display_table_column_on_non_col_element_contributes_column_hint() {
            // Author-declared `display: table-column` on a non-<col> element
            // must be honored as a column track (CSS Tables L3 §2.1): the
            // resolver consults computed display, not just the HTML tag name.
            var (rootDiv, _) = BuildWithRealUA(
                "<table>" +
                "<div id=\"col0\" style=\"display: table-column; width: 80px\"></div>" +
                "<div id=\"col1\" style=\"display: table-column; width: 60px\"></div>" +
                "<tbody><tr><td>A</td><td>B</td><td>C</td></tr></tbody>" +
                "</table>",
                "table { width: 300px; table-layout: fixed; border-spacing: 0; } td { padding: 0; }",
                viewportWidth: 800);

            var tDiv = FindFirst<TableBox>(rootDiv);
            Assert.That(tDiv, Is.Not.Null);
            Assert.That(tDiv.ColumnWidths.Length, Is.EqualTo(3));
            Assert.That(tDiv.ColumnWidths[0], Is.EqualTo(80).Within(0.5),
                "display: table-column on a <div> contributes its width hint");
            Assert.That(tDiv.ColumnWidths[1], Is.EqualTo(60).Within(0.5),
                "Second display: table-column <div> contributes its hint");
            Assert.That(tDiv.ColumnWidths[2], Is.EqualTo(160).Within(0.5),
                "Unhinted column takes the remaining 300 - 80 - 60 = 160");

            // Regression guard: existing <col> path still works.
            var (rootCol, _) = BuildWithRealUA(
                "<table>" +
                "<col style=\"width: 80px\">" +
                "<col style=\"width: 60px\">" +
                "<tbody><tr><td>A</td><td>B</td><td>C</td></tr></tbody>" +
                "</table>",
                "table { width: 300px; table-layout: fixed; border-spacing: 0; } td { padding: 0; }",
                viewportWidth: 800);

            var tCol = FindFirst<TableBox>(rootCol);
            Assert.That(tCol, Is.Not.Null);
            Assert.That(tCol.ColumnWidths.Length, Is.EqualTo(3));
            Assert.That(tCol.ColumnWidths[0], Is.EqualTo(80).Within(0.5));
            Assert.That(tCol.ColumnWidths[1], Is.EqualTo(60).Within(0.5));
            Assert.That(tCol.ColumnWidths[2], Is.EqualTo(160).Within(0.5));
        }

        [Test]
        public void Table_layout_fixed_uses_first_row_widths_for_unhinted_columns() {
            var (root, _) = BuildWithRealUA(
                "<table><tbody><tr><td id=\"a\">A</td><td>B</td><td>C</td></tr></tbody></table>",
                "table { width: 300px; table-layout: fixed; border-spacing: 0; } " +
                "td { padding: 0; } #a { width: 90px; }",
                viewportWidth: 800);

            var table = FindFirst<TableBox>(root);
            Assert.That(table, Is.Not.Null);
            Assert.That(table.ColumnWidths.Length, Is.EqualTo(3));
            Assert.That(table.ColumnWidths[0], Is.EqualTo(90).Within(0.5));
            Assert.That(table.ColumnWidths[1], Is.EqualTo(105).Within(0.5));
            Assert.That(table.ColumnWidths[2], Is.EqualTo(105).Within(0.5));
        }

        [Test]
        public void Colspan_attribute_spans_columns() {
            // A <td colspan=2> should occupy two resolved grid columns.
            var (root, _) = BuildWithRealUA(
                "<table><tbody>" +
                "<tr><td colspan=\"2\">SPAN</td></tr>" +
                "<tr><td>A</td><td>B</td></tr>" +
                "</tbody></table>",
                "table { width: 200px; border-spacing: 0; }",
                viewportWidth: 800);

            var table = FindFirst<TableBox>(root);
            Assert.That(table, Is.Not.Null);
            // here because of row 2 — but row 1's cell only occupies one
            var rows = FindAll<TableRowBox>(root);
            Assert.That(rows.Count, Is.EqualTo(2));
            TableCellBox spanCell = null;
            foreach (var c in rows[0].Children) if (c is TableCellBox tc) { spanCell = tc; break; }
            Assert.That(spanCell, Is.Not.Null);
            Assert.That(spanCell.ColSpan, Is.EqualTo(2));
            Assert.That(spanCell.Width, Is.EqualTo(table.ColumnWidths[0] + table.ColumnWidths[1]).Within(0.5));
        }

        [Test]
        public void Auto_layout_cell_percentage_widths_resolve_against_table_content_width() {
            // CSS 2.1 §17.5.2 / CSS Tables L3 §4: a cell's percentage width
            // resolves against the table's available (content) width. With
            // table width:600px, border-spacing:0, padding:0:
            //   td#a width:50% -> 300
            //   td#b width:30% -> 180
            //   td#c (no width) -> remaining ~120 via max-content + slack
            var (root, _) = BuildWithRealUA(
                "<table><tbody>" +
                "<tr><td id=\"a\">A</td><td id=\"b\">B</td><td id=\"c\">C</td></tr>" +
                "</tbody></table>",
                "table { width: 600px; border-spacing: 0; } " +
                "td { padding: 0; box-sizing: content-box; } " +
                "#a { width: 50%; } #b { width: 30%; }",
                viewportWidth: 1000);

            var table = FindFirst<TableBox>(root);
            Assert.That(table, Is.Not.Null);
            Assert.That(table.ColumnWidths.Length, Is.EqualTo(3));
            Assert.That(table.ColumnWidths[0], Is.EqualTo(300).Within(1.0),
                "td width:50% of 600px table content width = 300");
            Assert.That(table.ColumnWidths[1], Is.EqualTo(180).Within(1.0),
                "td width:30% of 600px table content width = 180");
            Assert.That(table.ColumnWidths[2], Is.EqualTo(120).Within(1.0),
                "Third cell with no width takes the remainder (~120)");
            Assert.That(table.ColumnWidths[0] + table.ColumnWidths[1] + table.ColumnWidths[2],
                Is.EqualTo(table.ContentWidth).Within(0.5));
        }

        [Test]
        public void Explicit_row_height_acts_as_floor_for_row_height() {
            // <tr style="height: 60px"> on row 1 must be honored as a floor
            // even when the row's cells produce shorter natural content.
            // Companion path: a 2-row-spanning cell of height 80 still leaves
            // row 1 at >= 60 (the explicit floor wins over equal-split
            // redistribution), and row 2 absorbs the remaining height.
            var (root, _) = BuildWithRealUA(
                "<table><tbody>" +
                "<tr id=\"r1\"><td id=\"a\">A</td><td id=\"b\">B</td></tr>" +
                "<tr id=\"r2\"><td id=\"c\">C</td><td id=\"d\">D</td></tr>" +
                "</tbody></table>",
                "table { width: 200px; border-spacing: 0; } td { padding: 0; } " +
                "#r1 { height: 60px; }",
                viewportWidth: 800);

            var rows = FindAll<TableRowBox>(root);
            Assert.That(rows.Count, Is.EqualTo(2));
            Assert.That(rows[0].Height, Is.GreaterThanOrEqualTo(60 - 0.5),
                "Row 1's explicit height: 60px must act as a floor");
            var cellA = FindCell(root, "a");
            var cellB = FindCell(root, "b");
            Assert.That(cellA, Is.Not.Null);
            Assert.That(cellB, Is.Not.Null);
            Assert.That(cellA.Height, Is.EqualTo(rows[0].Height).Within(0.5),
                "Cells in row 1 stretch to the floored row height");
            Assert.That(cellB.Height, Is.EqualTo(rows[0].Height).Within(0.5));

            // Companion: rowspan=2 cell of height 80 spanning two rows where
            // row 1 has the 60px floor. Without the floor, equal-split would
            // give each row 40 (or natural+extra/2). With the floor row 1
            // stays >= 60 and row 2 picks up the remaining height.
            var (root2, _) = BuildWithRealUA(
                "<table><tbody>" +
                "<tr id=\"r1\"><td id=\"span\" rowspan=\"2\">S</td><td id=\"top\">T</td></tr>" +
                "<tr id=\"r2\"><td id=\"bottom\">B</td></tr>" +
                "</tbody></table>",
                "table { width: 200px; border-spacing: 0; } td { padding: 0; } " +
                "#r1 { height: 60px; } #span { height: 80px; }",
                viewportWidth: 800);

            var rows2 = FindAll<TableRowBox>(root2);
            Assert.That(rows2.Count, Is.EqualTo(2));
            Assert.That(rows2[0].Height, Is.GreaterThanOrEqualTo(60 - 0.5),
                "Row 1 explicit floor preserved against rowspan redistribution");
            Assert.That(rows2[0].Height + rows2[1].Height, Is.GreaterThanOrEqualTo(80 - 0.5),
                "Spanned cell's 80px height is covered by the two rows");
            Assert.That(rows2[1].Height, Is.GreaterThanOrEqualTo(20 - 0.5),
                "Row 2 absorbs the slack left after row 1's floor");

            // Regression guard: no explicit row height, redistribution
            // continues to split equally between the two rows.
            var (root3, _) = BuildWithRealUA(
                "<table><tbody>" +
                "<tr><td id=\"span\" rowspan=\"2\">S</td><td>T</td></tr>" +
                "<tr><td>B</td></tr>" +
                "</tbody></table>",
                "table { width: 200px; border-spacing: 0; } td { padding: 0; } " +
                "#span { height: 80px; }",
                viewportWidth: 800);

            var rows3 = FindAll<TableRowBox>(root3);
            Assert.That(rows3.Count, Is.EqualTo(2));
            Assert.That(rows3[0].Height + rows3[1].Height, Is.GreaterThanOrEqualTo(80 - 0.5));
            Assert.That(rows3[0].Height, Is.EqualTo(rows3[1].Height).Within(1.0),
                "With no explicit floor, rowspan extra distributes equally");
        }

        [Test]
        public void Explicit_table_height_acts_as_floor_for_table_height() {
            // CSS 2.1 §17.5.3: an author-declared `height` on a <table>
            // behaves as a minimum. When the row stack is shorter than the
            // declared height, the table box expands to the floor; when the
            // row stack overflows, the rows win and contentBottom dictates
            // the final height (no cropping in v1).
            var (root, _) = BuildWithRealUA(
                "<table style=\"height: 400px\"><tbody>" +
                "<tr><td>A</td><td>B</td></tr>" +
                "<tr><td>C</td><td>D</td></tr>" +
                "<tr><td>E</td><td>F</td></tr>" +
                "</tbody></table>",
                "table { width: 200px; border-spacing: 0; } td { padding: 0; line-height: 50px; }",
                viewportWidth: 800);

            var table = FindFirst<TableBox>(root);
            Assert.That(table, Is.Not.Null);
            var rows = FindAll<TableRowBox>(root);
            Assert.That(rows.Count, Is.EqualTo(3));

            double rowStack = 0;
            for (int i = 0; i < rows.Count; i++) rowStack += rows[i].Height;
            Assert.That(rowStack, Is.LessThan(400),
                "Setup precondition: natural row stack must be smaller than the 400px author height");

            Assert.That(table.Height, Is.EqualTo(400).Within(0.5),
                "Author-declared `height: 400px` must be honored as a floor (CSS 2.1 §17.5.3)");

            // Regression guard: when content overflows the explicit height,
            // contentBottom wins (rows can't be cropped by the author height).
            var (root2, _) = BuildWithRealUA(
                "<table style=\"height: 40px\"><tbody>" +
                "<tr><td>A</td><td>B</td></tr>" +
                "<tr><td>C</td><td>D</td></tr>" +
                "<tr><td>E</td><td>F</td></tr>" +
                "</tbody></table>",
                "table { width: 200px; border-spacing: 0; } td { padding: 0; line-height: 50px; }",
                viewportWidth: 800);

            var table2 = FindFirst<TableBox>(root2);
            var rows2 = FindAll<TableRowBox>(root2);
            double rowStack2 = 0;
            for (int i = 0; i < rows2.Count; i++) rowStack2 += rows2[i].Height;
            Assert.That(rowStack2, Is.GreaterThan(40),
                "Setup precondition: row stack must overflow the 40px author height");
            Assert.That(table2.Height, Is.GreaterThanOrEqualTo(rowStack2 - 0.5),
                "When row stack overflows the author height, the rows win — no cropping");
            Assert.That(table2.Height, Is.GreaterThan(40 + 0.5),
                "Author height: 40px must not clamp the table below contentBottom");
        }

        [Test]
        public void Auto_layout_cell_with_no_width_still_falls_through_to_max_content_path() {
            // Regression guard for the percentage-basis fix: cells WITHOUT an
            // explicit width must still take the MeasureMaxContentWidth path
            // (IntrinsicMin defaults to 0, IntrinsicMax = BlockLayout-computed
            // width). Two no-width cells with identical max-content should
            // split the table content width evenly.
            var (root, _) = BuildWithRealUA(
                "<table><tbody>" +
                "<tr><td>AA</td><td>BB</td></tr>" +
                "</tbody></table>",
                "table { width: 400px; border-spacing: 0; } td { padding: 0; }",
                viewportWidth: 800);

            var table = FindFirst<TableBox>(root);
            Assert.That(table, Is.Not.Null);
            Assert.That(table.ColumnWidths.Length, Is.EqualTo(2));
            double half = table.ContentWidth / 2.0;
            Assert.That(table.ColumnWidths[0], Is.EqualTo(half).Within(1.0));
            Assert.That(table.ColumnWidths[1], Is.EqualTo(half).Within(1.0));
        }

        [Test]
        public void Row_with_visibility_collapse_has_zero_height_and_compacts_row_stack() {
            // CSS Tables L3 §11: `visibility: collapse` on a <tr> hides the row
            // AND removes its slot. Three rows where the middle row is collapsed
            // — the third row sits directly below the first.
            var (root, _) = BuildWithRealUA(
                "<table><tbody>" +
                "<tr id=\"r1\"><td>A</td></tr>" +
                "<tr id=\"r2\"><td>B</td></tr>" +
                "<tr id=\"r3\"><td>C</td></tr>" +
                "</tbody></table>",
                "table { width: 200px; border-spacing: 0; } td { padding: 0; line-height: 20px; } " +
                "#r2 { visibility: collapse; }",
                viewportWidth: 800);

            var rows = FindAll<TableRowBox>(root);
            Assert.That(rows.Count, Is.EqualTo(3));
            Assert.That(rows[1].Height, Is.EqualTo(0).Within(0.001),
                "Middle row's height must be 0 when visibility: collapse");
            Assert.That(rows[2].Y, Is.EqualTo(rows[0].Y + rows[0].Height).Within(0.5),
                "Row 3 must sit directly below row 1 (collapsed row's space is removed)");

            // Collapsed-row cells must not paint. The painter's IsVisibilityHidden
            // gate already treats `collapse` like `hidden`; verify the collapsed
            // row's cells inherit/carry the collapse keyword so paint elides them.
            TableCellBox collapsedCell = null;
            foreach (var c in rows[1].Children) {
                if (c is TableCellBox tc) { collapsedCell = tc; break; }
            }
            Assert.That(collapsedCell, Is.Not.Null);
            Assert.That(rows[1].Style?.Get("visibility"), Is.EqualTo("collapse"));

            // Regression guard: visibility: hidden on the middle row keeps its
            // layout slot (CSS 2.1 §11.2). The third row sits BELOW the second.
            var (rootH, _) = BuildWithRealUA(
                "<table><tbody>" +
                "<tr id=\"r1\"><td>A</td></tr>" +
                "<tr id=\"r2\"><td>B</td></tr>" +
                "<tr id=\"r3\"><td>C</td></tr>" +
                "</tbody></table>",
                "table { width: 200px; border-spacing: 0; } td { padding: 0; line-height: 20px; } " +
                "#r2 { visibility: hidden; }",
                viewportWidth: 800);

            var rowsH = FindAll<TableRowBox>(rootH);
            Assert.That(rowsH.Count, Is.EqualTo(3));
            Assert.That(rowsH[1].Height, Is.GreaterThan(0),
                "visibility: hidden must preserve the row's height");
            Assert.That(rowsH[2].Y, Is.GreaterThan(rowsH[0].Y + rowsH[0].Height + 0.5),
                "Row 3 must sit below the hidden-but-still-present row 2");

            // Regression guard: default visibility (visible) keeps normal layout.
            var (rootV, _) = BuildWithRealUA(
                "<table><tbody>" +
                "<tr id=\"r1\"><td>A</td></tr>" +
                "<tr id=\"r2\"><td>B</td></tr>" +
                "<tr id=\"r3\"><td>C</td></tr>" +
                "</tbody></table>",
                "table { width: 200px; border-spacing: 0; } td { padding: 0; line-height: 20px; }",
                viewportWidth: 800);

            var rowsV = FindAll<TableRowBox>(rootV);
            Assert.That(rowsV.Count, Is.EqualTo(3));
            Assert.That(rowsV[1].Height, Is.GreaterThan(0));
            Assert.That(rowsV[2].Y, Is.GreaterThan(rowsV[1].Y + rowsV[1].Height - 0.5));
        }
    }
}
