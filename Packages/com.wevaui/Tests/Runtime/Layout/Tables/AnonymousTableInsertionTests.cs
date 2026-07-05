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
    // Regression coverage for CSS_COMPLIANCE_ISSUES.md I1 — CSS 2.1 §17.2.1 /
    // CSS Tables L3 §3.7 "anonymous table object" insertion. A bare
    // `<div style="display: table-cell">` that lacks the enclosing
    // table-row / row-group / table ancestors must trigger synthesis of the
    // missing anonymous wrappers so the cell participates in the table
    // sizing algorithm — without it the cell falls through to BlockLayout
    // and renders as an ordinary block, losing column alignment and
    // border-spacing semantics.
    public class AnonymousTableInsertionTests {
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

        // I1: two bare table-cell siblings (no table ancestor) must synthesize
        // a complete anonymous chain — table → tbody → row → cells — and the
        // cells must lay out side-by-side at the same Y as siblings inside the
        // synthesized row.
        [Test]
        public void Two_bare_table_cell_siblings_synthesize_table_tbody_row_and_lay_out_side_by_side_I1() {
            var (root, _) = BuildWithRealUA(
                "<div id=\"wrap\" style=\"width: 200px;\">" +
                "  <div style=\"display: table-cell;\">A</div>" +
                "  <div style=\"display: table-cell;\">B</div>" +
                "</div>",
                authorCss: null,
                viewportWidth: 800);

            // A TableBox must exist as a synthetic wrapper inside #wrap.
            var table = FindFirst<TableBox>(root);
            Assert.That(table, Is.Not.Null,
                "Bare display:table-cell siblings must synthesize a wrapping TableBox.");

            // Synthetic table has no DOM element / style.
            Assert.That(table.Element, Is.Null,
                "Synthetic anonymous TableBox must have no Element reference.");
            Assert.That(table.Style, Is.Null,
                "Synthetic anonymous TableBox must have no Style (anonymous, not cascaded).");

            // The synthetic chain: table → tbody → row → cells.
            var rowGroups = FindAll<TableRowGroupBox>(root);
            Assert.That(rowGroups.Count, Is.EqualTo(1),
                "Exactly one synthetic tbody should wrap the cells' synthetic row.");
            var tbody = rowGroups[0];
            Assert.That(tbody.Element, Is.Null,
                "Synthetic anonymous TableRowGroupBox must have no Element.");

            var rows = FindAll<TableRowBox>(root);
            Assert.That(rows.Count, Is.EqualTo(1),
                "Exactly one synthetic row should wrap the consecutive cells.");
            var row = rows[0];
            Assert.That(row.Element, Is.Null,
                "Synthetic anonymous TableRowBox must have no Element.");

            // Both cells live inside the synthetic row.
            int cellsInRow = 0;
            foreach (var ch in row.Children) if (ch is TableCellBox) cellsInRow++;
            Assert.That(cellsInRow, Is.EqualTo(2),
                "Both consecutive table-cells must share ONE synthetic row, not get one each.");

            // Two cells must lay out side-by-side at the same row Y.
            var cells = FindAll<TableCellBox>(root);
            Assert.That(cells.Count, Is.EqualTo(2));
            Assert.That(table.ColumnWidths, Is.Not.Null,
                "Synthetic table must run track resolution and produce ColumnWidths.");
            Assert.That(table.ColumnWidths.Length, Is.EqualTo(2),
                "Two cells in one row → two columns.");

            // Sibling cells at the same row index → same row.Y; differing X.
            Assert.That(cells[1].X, Is.GreaterThan(cells[0].X),
                "Side-by-side cells must have distinct X positions, with the 2nd to the right of the 1st.");
        }

        // I1: a bare `display: table-row` with cells inside (no enclosing
        // table) must be wrapped in a synthetic tbody + table; the cells
        // inside the row inherit the row's position (row Y propagates).
        [Test]
        public void Bare_table_row_synthesizes_tbody_and_table_wrapper_I1() {
            var (root, _) = BuildWithRealUA(
                "<div id=\"wrap\" style=\"width: 200px;\">" +
                "  <div style=\"display: table-row;\">" +
                "    <span style=\"display: table-cell;\">A</span>" +
                "    <span style=\"display: table-cell;\">B</span>" +
                "  </div>" +
                "</div>",
                authorCss: null,
                viewportWidth: 800);

            // Synthetic wrappers above the row.
            var table = FindFirst<TableBox>(root);
            Assert.That(table, Is.Not.Null,
                "Bare table-row must synthesize an enclosing TableBox.");
            Assert.That(table.Element, Is.Null,
                "Synthetic TableBox has no Element.");

            var rowGroups = FindAll<TableRowGroupBox>(root);
            Assert.That(rowGroups.Count, Is.EqualTo(1),
                "Bare table-row needs exactly one synthetic tbody.");
            Assert.That(rowGroups[0].Element, Is.Null,
                "Synthetic tbody has no Element.");

            // The author-declared row is the ONLY TableRowBox (no extra
            // synthesized rows around the existing one).
            var rows = FindAll<TableRowBox>(root);
            Assert.That(rows.Count, Is.EqualTo(1),
                "The author's table-row must be reused — no extra synthetic rows.");
            // Author row has a backing Element; the synthetic chain above it
            // confirms the row's Element survived through the wrap.
            Assert.That(rows[0].Element, Is.Not.Null,
                "Author table-row must keep its Element after wrapping.");

            var cells = FindAll<TableCellBox>(root);
            Assert.That(cells.Count, Is.EqualTo(2));
            // Cells inherit the row's geometry as siblings — same row-relative
            // Y inside that row.
            Assert.That(cells[0].Parent, Is.SameAs(rows[0]),
                "Cell 1 still belongs to the author-declared row (not a synthesized one).");
            Assert.That(cells[1].Parent, Is.SameAs(rows[0]),
                "Cell 2 still belongs to the author-declared row.");
        }

        // I1: a cell whose parent IS a table (but not a row) must synthesize
        // a row between the table and the cell. Per CSS Tables L3 §3.7 the
        // wrapper for a misparented cell is `table-row` — `table` is a valid
        // parent for `table-row` (no further row-group wrapping required).
        [Test]
        public void Cell_inside_bare_table_synthesizes_intermediate_row_I1() {
            var (root, _) = BuildWithRealUA(
                "<div style=\"display: table; width: 200px;\">" +
                "  <div style=\"display: table-cell;\">X</div>" +
                "</div>",
                authorCss: null,
                viewportWidth: 800);

            // The author table is the only TableBox — no extra anonymous one.
            var tables = FindAll<TableBox>(root);
            Assert.That(tables.Count, Is.EqualTo(1),
                "Author `display: table` survives — no second synthetic table created above it.");
            var table = tables[0];
            Assert.That(table.Element, Is.Not.Null,
                "Author table-display div must keep its Element reference.");

            // Exactly one synthetic row between the table and the cell.
            // No row-group wrapper is needed — `table` is a valid parent for
            // `table-row` per spec.
            var rows = FindAll<TableRowBox>(root);
            Assert.That(rows.Count, Is.EqualTo(1),
                "Cell directly inside a table needs a synthetic row.");
            Assert.That(rows[0].Element, Is.Null,
                "Synthetic row is anonymous (no Element).");

            // Cell is reachable: table → row → cell.
            var cells = FindAll<TableCellBox>(root);
            Assert.That(cells.Count, Is.EqualTo(1));
            Assert.That(cells[0].Parent, Is.SameAs(rows[0]),
                "Author cell must now be parented by the synthetic row.");
            Assert.That(rows[0].Parent, Is.SameAs(table),
                "Synthetic row must be parented directly by the author table.");
        }

        // I1b: bare text content sitting between two <td> siblings of a <tr>
        // must be wrapped in an anonymous TableCellBox (CSS Tables L3 §3.7).
        // Today such text would otherwise fall through BoxFinalize's general
        // inline-wrapping path and end up as an AnonymousBlockBox sibling of
        // the cells — which is invisible to the column algorithm and
        // collapses the row visually.
        [Test]
        public void Text_between_table_cells_is_wrapped_in_anonymous_cell_I1b() {
            var (root, _) = BuildWithRealUA(
                "<table><tr><td>A</td>BARE TEXT<td>C</td></tr></table>",
                "table { border-spacing: 0; }",
                viewportWidth: 800);

            // Exactly one TableRowBox (the author's <tr>).
            var rows = FindAll<TableRowBox>(root);
            Assert.That(rows.Count, Is.EqualTo(1),
                "Author <tr> must not be wrapped or duplicated.");
            var tr = rows[0];

            // Row's direct children must be exactly three TableCellBoxes:
            // author A, synthetic wrapping "BARE TEXT", author C.
            var cellsInRow = new List<TableCellBox>();
            foreach (var ch in tr.Children) {
                if (ch is TableCellBox tcb) cellsInRow.Add(tcb);
            }
            Assert.That(cellsInRow.Count, Is.EqualTo(3),
                "Row must contain three cells: author A, anonymous-wrapped 'BARE TEXT', author C.");

            // Tree shape check: every direct child of the row IS a cell — no
            // dangling AnonymousBlockBox / TextRun siblings.
            foreach (var ch in tr.Children) {
                Assert.That(ch, Is.InstanceOf<TableCellBox>(),
                    "Row's direct children must all be TableCellBox after I1b synthesis; got " + ch.GetType().Name);
            }

            // The middle cell is the synthetic one — its Element/Style are null
            // (matching the anonymous-wrapper pattern from I1).
            var midCell = cellsInRow[1];
            Assert.That(midCell.Element, Is.Null,
                "Anonymous wrapping cell for 'BARE TEXT' must have no Element.");
            Assert.That(midCell.Style, Is.Null,
                "Anonymous wrapping cell for 'BARE TEXT' must have no Style.");

            // The flanking cells are author-declared (<td>A</td>, <td>C</td>).
            Assert.That(cellsInRow[0].Element, Is.Not.Null,
                "First cell must keep its author <td> element.");
            Assert.That(cellsInRow[0].Element.TagName, Is.EqualTo("td"));
            Assert.That(cellsInRow[2].Element, Is.Not.Null,
                "Third cell must keep its author <td> element.");
            Assert.That(cellsInRow[2].Element.TagName, Is.EqualTo("td"));

            // The synthetic cell must actually contain the bare text: walk its
            // subtree for a TextRun whose Text matches "BARE TEXT". Going via
            // Walk handles whatever intermediate wrappers BoxFinalize left
            // around the run (typically an AnonymousBlockBox).
            bool foundBareText = false;
            foreach (var d in Walk(midCell)) {
                if (d is TextRun tr2 && tr2.Text != null && tr2.Text.Contains("BARE TEXT")) {
                    foundBareText = true;
                    break;
                }
            }
            Assert.That(foundBareText, Is.True,
                "Synthetic cell must contain the 'BARE TEXT' TextRun, not lose it.");
        }

        // I1b: pure whitespace between two <td> siblings must NOT spawn an
        // anonymous cell. Per CSS Tables L3 §3.7 whitespace between
        // table-internal boxes is dropped for the purposes of anonymous-cell
        // generation; the row should still produce just two cells.
        [Test]
        public void Whitespace_between_table_cells_does_not_synthesize_cell_I1b() {
            var (root, _) = BuildWithRealUA(
                "<table><tr><td>A</td>   <td>B</td></tr></table>",
                "table { border-spacing: 0; }",
                viewportWidth: 800);

            var rows = FindAll<TableRowBox>(root);
            Assert.That(rows.Count, Is.EqualTo(1),
                "One author <tr> — no extra synthetic rows.");
            var tr = rows[0];

            int cellsInRow = 0;
            foreach (var ch in tr.Children) {
                if (ch is TableCellBox) cellsInRow++;
            }
            Assert.That(cellsInRow, Is.EqualTo(2),
                "Whitespace between cells must not generate an anonymous cell — exactly 2 author cells survive.");

            // No non-cell direct child should remain on the row either.
            foreach (var ch in tr.Children) {
                Assert.That(ch, Is.InstanceOf<TableCellBox>(),
                    "Row must have only TableCellBox children after I1b; whitespace must be dropped, not wrapped.");
            }

            var cells = FindAll<TableCellBox>(root);
            Assert.That(cells.Count, Is.EqualTo(2),
                "Whole tree contains exactly the two author cells — no anonymous cell synthesized for whitespace.");
            // Both cells are author-declared <td>s.
            Assert.That(cells[0].Element, Is.Not.Null);
            Assert.That(cells[0].Element.TagName, Is.EqualTo("td"));
            Assert.That(cells[1].Element, Is.Not.Null);
            Assert.That(cells[1].Element.TagName, Is.EqualTo("td"));
        }

        // I1b: a <tr> containing only text (no <td> children at all) must
        // have its text wrapped in a single anonymous TableCellBox. This is
        // the spec's "non-table-cell content as the only child of a row"
        // case — the synthesized cell lets the row participate in column
        // sizing instead of being a zero-cell row.
        [Test]
        public void Text_only_row_synthesizes_single_anonymous_cell_I1b() {
            var (root, _) = BuildWithRealUA(
                "<tr>text only no cells</tr>",
                authorCss: null,
                viewportWidth: 800);

            var rows = FindAll<TableRowBox>(root);
            Assert.That(rows.Count, Is.EqualTo(1),
                "Author <tr> survives — exactly one TableRowBox.");
            var tr = rows[0];

            // Exactly one cell inside the row, and it must be the synthetic
            // anonymous wrapper (Element/Style null).
            var cells = FindAll<TableCellBox>(root);
            Assert.That(cells.Count, Is.EqualTo(1),
                "Text-only row generates exactly one anonymous cell.");
            var cell = cells[0];
            Assert.That(cell.Element, Is.Null,
                "Synthetic cell for text-only row must have no Element.");
            Assert.That(cell.Style, Is.Null,
                "Synthetic cell for text-only row must have no Style.");
            Assert.That(cell.Parent, Is.SameAs(tr),
                "Synthetic cell must be parented directly by the author row.");

            // Row's direct children must be exactly that one cell — no
            // leftover TextRun sibling.
            int directCells = 0;
            foreach (var ch in tr.Children) {
                Assert.That(ch, Is.InstanceOf<TableCellBox>(),
                    "Row must contain only TableCellBox children after I1b; got " + ch.GetType().Name);
                directCells++;
            }
            Assert.That(directCells, Is.EqualTo(1),
                "Row has exactly one direct child (the synthetic cell).");

            // The cell must contain the row's text content somewhere in its
            // subtree (BoxFinalize may have left an AnonymousBlockBox or kept
            // the TextRun direct — either is fine, only the text presence
            // matters for the spec).
            bool foundText = false;
            foreach (var d in Walk(cell)) {
                if (d is TextRun textRun && textRun.Text != null && textRun.Text.Contains("text only no cells")) {
                    foundText = true;
                    break;
                }
            }
            Assert.That(foundText, Is.True,
                "Synthetic cell must contain the row's text content.");
        }

        // Regression pin: a properly-nested <table><tr><td>X</td></tr></table>
        // must NOT get extra anonymous wrappers. The chain is already valid
        // (table → implicit tbody via UA stylesheet → tr → td) so the fixup
        // pass must leave the tree alone.
        [Test]
        public void Properly_nested_table_gets_no_extra_anonymous_wrappers_I1_regression() {
            var (root, _) = BuildWithRealUA(
                "<table><tr><td>X</td></tr></table>",
                "table { border-spacing: 0; }",
                viewportWidth: 800);

            // Exactly one TableBox.
            var tables = FindAll<TableBox>(root);
            Assert.That(tables.Count, Is.EqualTo(1),
                "Properly-nested <table> must not gain an extra anonymous wrapping TableBox.");

            // Exactly one TableRowBox — the <tr>.
            var rows = FindAll<TableRowBox>(root);
            Assert.That(rows.Count, Is.EqualTo(1),
                "<tr> must not be wrapped in an extra synthetic row.");
            Assert.That(rows[0].Element, Is.Not.Null,
                "<tr>'s author element must survive untouched.");
            Assert.That(rows[0].Element.TagName, Is.EqualTo("tr"),
                "The single row must still be the author's <tr>.");

            // Exactly one TableCellBox — the <td>.
            var cells = FindAll<TableCellBox>(root);
            Assert.That(cells.Count, Is.EqualTo(1),
                "<td> must not be wrapped in an extra synthetic cell.");
            Assert.That(cells[0].Element, Is.Not.Null,
                "<td>'s author element must survive untouched.");
            Assert.That(cells[0].Element.TagName, Is.EqualTo("td"),
                "The single cell must still be the author's <td>.");

            // Row groups should be at most one (the implicit tbody from the
            // HTML parser / UA stylesheet) and it must carry an Element since
            // it isn't synthetic.
            var groups = FindAll<TableRowGroupBox>(root);
            foreach (var g in groups) {
                Assert.That(g.Element, Is.Not.Null,
                    "Regression: no synthetic anonymous tbody should be inserted on properly-nested <table>.");
            }
        }
    }
}
