using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Layout.Tables;
using Weva.Layout.Text;

namespace Weva.Tests.Layout.Tables {
    // Direct unit coverage for TableTrackResolver — the colspan / rowspan
    // placement and column-width resolution pass for tables. The audit
    // (CODE_AUDIT_FINDINGS.md TG8) flagged this type as having zero direct
    // hits: rowspan / colspan correctness was being exercised only through
    // golden fixtures. These tests pin the placement algorithm directly by
    // hand-constructing TableBox / TableRowBox / TableCellBox trees and
    // invoking TableTrackResolver.Resolve.
    //
    // The auto-layout path only reads cell.IntrinsicMinWidth /
    // IntrinsicMaxWidth plus cell.Element's colspan / rowspan attributes, so
    // we can drive it without running the full LayoutEngine — we just stamp
    // the intrinsic widths the way TableLayout would and let the resolver
    // run cell placement + width distribution.
    public class TableTrackResolverDirectTests {
        // Minimal LayoutContext suitable for TableTrackResolver. The auto-
        // layout column path doesn't touch font metrics, but the fixed-
        // layout / column-hint path does call StyleResolver.FontSizePx, so
        // we plug in MonoFontMetrics for completeness.
        static LayoutContext NewCtx() {
            return new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = 800,
                ViewportHeightPx = 600,
                RootFontSizePx = 16,
                DpiPixelsPerInch = 96
            };
        }

        // Build a `<td>` element with optional colspan / rowspan attributes.
        // The resolver reads these via Element.GetAttribute during PlaceCells.
        static Element TdElement(int colspan = 1, int rowspan = 1) {
            var el = new Element("td");
            if (colspan != 1) el.SetAttribute("colspan", colspan.ToString());
            if (rowspan != 1) el.SetAttribute("rowspan", rowspan.ToString());
            return el;
        }

        // Build a TableCellBox carrying the supplied element + intrinsic
        // min/max widths. We feed equal min / max so the auto-layout path's
        // distribution simplifies to "each column = its cell's intrinsic
        // width" when no spans overlap — easy to assert against.
        static TableCellBox NewCell(Element el, double intrinsicWidth) {
            return new TableCellBox {
                Element = el,
                IntrinsicMinWidth = intrinsicWidth,
                IntrinsicMaxWidth = intrinsicWidth
            };
        }

        static TableRowBox NewRow(params TableCellBox[] cells) {
            var row = new TableRowBox { Element = new Element("tr") };
            foreach (var c in cells) row.AddChild(c);
            return row;
        }

        static TableBox NewTable() {
            return new TableBox { Element = new Element("table") };
        }

        // 2x2 table, no spans: each cell gets its own column, ColumnIndex
        // walks 0,1 on row 0 then 0,1 on row 1, and the resolver produces a
        // ColumnWidths array of length 2 totalling the available width.
        [Test]
        public void Two_by_two_no_spans_resolves_two_columns_two_rows() {
            var table = NewTable();
            var a = NewCell(TdElement(), 50);
            var b = NewCell(TdElement(), 70);
            var c = NewCell(TdElement(), 50);
            var d = NewCell(TdElement(), 70);
            var rows = new List<TableRowBox> { NewRow(a, b), NewRow(c, d) };

            TableTrackResolver.Resolve(table, rows, 200, NewCtx());

            // Placement: each cell sits in its column index.
            Assert.That(a.ColumnIndex, Is.EqualTo(0));
            Assert.That(b.ColumnIndex, Is.EqualTo(1));
            Assert.That(c.ColumnIndex, Is.EqualTo(0));
            Assert.That(d.ColumnIndex, Is.EqualTo(1));

            // ColSpan / RowSpan default to 1 even with no explicit attribute
            // (placement normalises this).
            Assert.That(a.ColSpan, Is.EqualTo(1));
            Assert.That(a.RowSpan, Is.EqualTo(1));

            // Column count = 2, widths array length = 2.
            Assert.That(table.ColumnWidths, Is.Not.Null);
            Assert.That(table.ColumnWidths.Length, Is.EqualTo(2),
                "2x2 table with no spans must produce exactly 2 columns.");
            Assert.That(table.ColumnOffsets.Length, Is.EqualTo(2));
        }

        // <td colspan="2"> on a 2-cell row 0: cell A occupies cols 0-1; the
        // second author cell B falls into col 2. Row 1's first cell starts
        // at col 0 (no rowspan carry).
        [Test]
        public void Colspan_two_pushes_next_sibling_to_column_two() {
            var table = NewTable();
            var a = NewCell(TdElement(colspan: 2), 100);
            var b = NewCell(TdElement(), 40);
            var c = NewCell(TdElement(), 30);
            var d = NewCell(TdElement(), 30);
            var e = NewCell(TdElement(), 40);
            var rows = new List<TableRowBox> {
                NewRow(a, b),
                NewRow(c, d, e)
            };

            TableTrackResolver.Resolve(table, rows, 200, NewCtx());

            Assert.That(a.ColumnIndex, Is.EqualTo(0),
                "colspan=2 cell starts at col 0.");
            Assert.That(a.ColSpan, Is.EqualTo(2),
                "colspan attribute must be parsed and stamped onto the cell.");
            Assert.That(b.ColumnIndex, Is.EqualTo(2),
                "Cell after a colspan=2 must skip the spanned column and land at col 2.");

            Assert.That(c.ColumnIndex, Is.EqualTo(0),
                "Row 2 first cell starts at col 0 (no rowspan carry from row 1).");
            Assert.That(d.ColumnIndex, Is.EqualTo(1));
            Assert.That(e.ColumnIndex, Is.EqualTo(2));

            // Three columns total.
            Assert.That(table.ColumnWidths.Length, Is.EqualTo(3),
                "Max occupied column index is 2 → 3 columns.");
        }

        // <td rowspan="2"> on row 0 col 0: cell A occupies col 0, rows 0-1.
        // Row 1's first author cell falls into col 1 (col 0 is still
        // occupied by A's rowspan).
        [Test]
        public void Rowspan_two_blocks_next_row_first_column() {
            var table = NewTable();
            var a = NewCell(TdElement(rowspan: 2), 40);
            var b = NewCell(TdElement(), 50);
            var c = NewCell(TdElement(), 60);
            var rows = new List<TableRowBox> {
                NewRow(a, b),
                NewRow(c)
            };

            TableTrackResolver.Resolve(table, rows, 200, NewCtx());

            Assert.That(a.ColumnIndex, Is.EqualTo(0));
            Assert.That(a.RowSpan, Is.EqualTo(2),
                "rowspan attribute must be parsed and stamped onto the cell.");
            Assert.That(a.ColSpan, Is.EqualTo(1));
            Assert.That(b.ColumnIndex, Is.EqualTo(1));

            Assert.That(c.ColumnIndex, Is.EqualTo(1),
                "Row 2's first cell must skip col 0 (still occupied by row 1's rowspan=2) and land at col 1.");

            Assert.That(table.ColumnWidths.Length, Is.EqualTo(2),
                "Two columns total — col 0 (spanning A) and col 1 (B over C).");
        }

        // Combined colspan+rowspan: the 2x2 occupancy of a single cell.
        // Cell A spans cols 0-1 and rows 0-1. Sibling B on row 0 starts at
        // col 2. Row 1's first sibling C must therefore start at col 2 too
        // (cols 0 and 1 are still occupied by A's rowspan).
        [Test]
        public void Combined_colspan_and_rowspan_tracks_two_by_two_occupancy() {
            var table = NewTable();
            var a = NewCell(TdElement(colspan: 2, rowspan: 2), 100);
            var b = NewCell(TdElement(), 30);
            var c = NewCell(TdElement(), 30);
            var rows = new List<TableRowBox> {
                NewRow(a, b),
                NewRow(c)
            };

            TableTrackResolver.Resolve(table, rows, 200, NewCtx());

            Assert.That(a.ColumnIndex, Is.EqualTo(0));
            Assert.That(a.ColSpan, Is.EqualTo(2));
            Assert.That(a.RowSpan, Is.EqualTo(2));
            Assert.That(b.ColumnIndex, Is.EqualTo(2),
                "Sibling after a colspan=2 cell on the same row starts at col 2.");

            Assert.That(c.ColumnIndex, Is.EqualTo(2),
                "Row 2's first cell must skip BOTH cols 0 and 1 (rowspan=2 of cell A) and land at col 2.");

            Assert.That(table.ColumnWidths.Length, Is.EqualTo(3),
                "Combined 2x2 occupancy + 1 sibling column = 3 columns total.");
        }

        // rowspan="0" — per HTML, means "span to the end of the row group".
        // The resolver normalises this to (rowsRemaining) for the current
        // pass. With 3 rows and a rowspan=0 cell on row 0, the cell must end
        // up spanning all 3 rows.
        [Test]
        public void Rowspan_zero_normalises_to_rows_remaining() {
            var table = NewTable();
            var a = NewCell(TdElement(rowspan: 0), 40);
            var b = NewCell(TdElement(), 50);
            var c = NewCell(TdElement(), 50);
            var d = NewCell(TdElement(), 50);
            var rows = new List<TableRowBox> {
                NewRow(a, b),
                NewRow(c),
                NewRow(d)
            };

            TableTrackResolver.Resolve(table, rows, 200, NewCtx());

            Assert.That(a.RowSpan, Is.EqualTo(3),
                "rowspan=0 on row 0 of a 3-row table must normalise to 3 (span to end).");
            // Rows 1 and 2's first author cells must skip col 0 since it's
            // still occupied by A's normalised rowspan.
            Assert.That(c.ColumnIndex, Is.EqualTo(1),
                "Row 1's first cell must skip col 0 (A still occupies it).");
            Assert.That(d.ColumnIndex, Is.EqualTo(1),
                "Row 2's first cell must skip col 0 (A still occupies it).");
        }

        // <colgroup span="3"> as a direct child of the table that has no
        // <col> children: the resolver applies the span hint to columns
        // 0-2. We exercise the column-hint path by giving the colgroup a
        // width style and then asserting the resolver picked up a 3-column
        // table layout (placement detects 2 columns from the row but the
        // colgroup span pushes the column count up via column hints).
        [Test]
        public void Colgroup_with_span_three_distributes_hint_across_three_columns() {
            var table = NewTable();

            // <colgroup span="3" style="width: 150px">. Build a BlockBox
            // with display: table-column-group plus a span attribute so
            // CollectColumnHints picks it up. We share a single style
            // instance — the resolver only reads display + width.
            var colgroupEl = new Element("colgroup");
            colgroupEl.SetAttribute("span", "3");
            var colgroupStyle = new ComputedStyle(colgroupEl);
            colgroupStyle.Set("display", "table-column-group");
            colgroupStyle.Set("width", "150px");
            var colgroup = new BlockBox { Element = colgroupEl, Style = colgroupStyle };
            table.AddChild(colgroup);

            // Three cells in one row so PlaceCells finds 3 actual columns
            // (colgroup hints alone do not extend the column count past the
            // cell occupancy — the resolver runs PlaceCells first).
            var a = NewCell(TdElement(), 20);
            var b = NewCell(TdElement(), 20);
            var c = NewCell(TdElement(), 20);
            table.AddChild(NewRow(a, b, c));
            var rows = new List<TableRowBox>();
            foreach (var ch in table.Children) if (ch is TableRowBox r) rows.Add(r);

            TableTrackResolver.Resolve(table, rows, 600, NewCtx());

            // Three columns end up resolved (one per cell + colgroup span).
            Assert.That(table.ColumnWidths.Length, Is.EqualTo(3),
                "Three cells must produce three columns.");

            // Each column's hint = 150 / 3 = 50px. With intrinsic widths of
            // only 20px each, the resolver must lift each column's
            // min/max up to the 50px hint before the slack-distribution
            // step — so every column ends up at least 50px wide.
            for (int i = 0; i < 3; i++) {
                Assert.That(table.ColumnWidths[i], Is.GreaterThanOrEqualTo(50 - 0.001),
                    "Column " + i + " must honour the colgroup span=3 width hint (50px per column).");
            }
        }

        // Anonymous-table-inserted rows: rows whose parent is a synthetic
        // (anonymous) row group / table must still be tracked correctly by
        // the resolver. We simulate the post-I1 shape by handing the
        // resolver an authored TableBox whose rows have no Element
        // (anonymous synthesised rows) and verify placement still works.
        // This pins that PlaceCells doesn't crash on Element-less rows and
        // produces sensible column indices.
        [Test]
        public void Anonymous_rows_without_element_are_tracked_normally() {
            var table = NewTable();

            // Two synthetic rows (Element = null, as I1 synthesises them).
            var anonRow1 = new TableRowBox(); // Element left null
            var anonRow2 = new TableRowBox(); // Element left null
            var a = NewCell(TdElement(), 50);
            var b = NewCell(TdElement(), 50);
            var c = NewCell(TdElement(), 50);
            var d = NewCell(TdElement(), 50);
            anonRow1.AddChild(a);
            anonRow1.AddChild(b);
            anonRow2.AddChild(c);
            anonRow2.AddChild(d);
            var rows = new List<TableRowBox> { anonRow1, anonRow2 };

            TableTrackResolver.Resolve(table, rows, 200, NewCtx());

            // The anon rows themselves have no Element, but the resolver
            // walks `row.Children` looking for TableCellBox — Element-less
            // rows are fine. Placement must still assign sensible
            // ColumnIndex values.
            Assert.That(a.ColumnIndex, Is.EqualTo(0));
            Assert.That(b.ColumnIndex, Is.EqualTo(1));
            Assert.That(c.ColumnIndex, Is.EqualTo(0));
            Assert.That(d.ColumnIndex, Is.EqualTo(1));
            Assert.That(table.ColumnWidths.Length, Is.EqualTo(2),
                "Synthetic / anonymous rows must still produce a correct column count.");
        }

        // Zero-row table: the resolver must handle empty input without
        // crashing and stamp empty ColumnWidths / ColumnOffsets arrays so
        // downstream consumers can read them unconditionally.
        [Test]
        public void Empty_rows_list_produces_empty_column_arrays() {
            var table = NewTable();
            var rows = new List<TableRowBox>();

            TableTrackResolver.Resolve(table, rows, 200, NewCtx());

            Assert.That(table.ColumnWidths, Is.Not.Null,
                "Resolver must always stamp ColumnWidths, even when empty.");
            Assert.That(table.ColumnWidths.Length, Is.EqualTo(0),
                "Zero rows → zero columns.");
            Assert.That(table.ColumnOffsets, Is.Not.Null);
            Assert.That(table.ColumnOffsets.Length, Is.EqualTo(0));
        }

        // Mixed: row 0 has cell with rowspan=2 starting at col 0 AND a
        // cell with colspan=2 to its right. Row 1's only cell must skip
        // col 0 (rowspan) and land at col 1 (colspan range is in row 0
        // only). This pins the interaction of the two span axes during
        // placement.
        [Test]
        public void Mixed_rowspan_and_colspan_in_same_row_place_correctly() {
            var table = NewTable();
            var a = NewCell(TdElement(rowspan: 2), 40);        // col 0, rows 0-1
            var b = NewCell(TdElement(colspan: 2), 80);        // cols 1-2, row 0
            var c = NewCell(TdElement(), 40);                  // col 1 on row 1 (col 0 blocked)
            var d = NewCell(TdElement(), 40);                  // col 2 on row 1
            var rows = new List<TableRowBox> {
                NewRow(a, b),
                NewRow(c, d)
            };

            TableTrackResolver.Resolve(table, rows, 300, NewCtx());

            Assert.That(a.ColumnIndex, Is.EqualTo(0));
            Assert.That(a.RowSpan, Is.EqualTo(2));
            Assert.That(b.ColumnIndex, Is.EqualTo(1));
            Assert.That(b.ColSpan, Is.EqualTo(2));

            Assert.That(c.ColumnIndex, Is.EqualTo(1),
                "Row 1 first cell skips col 0 (A's rowspan) → lands at col 1.");
            Assert.That(d.ColumnIndex, Is.EqualTo(2));

            Assert.That(table.ColumnWidths.Length, Is.EqualTo(3));
        }

        // Column-offset monotonicity: ColumnOffsets[c+1] > ColumnOffsets[c]
        // for every adjacent pair. This is a contract of the resolver —
        // column offsets walk left-to-right monotonically, separated by
        // (columnWidth + borderSpacingX). Without this contract,
        // TableLayout's per-cell X assignment would mis-position cells.
        [Test]
        public void Column_offsets_walk_left_to_right_monotonically() {
            var table = NewTable();
            table.BorderSpacingX = 2;  // visible 2px UA default
            var a = NewCell(TdElement(), 40);
            var b = NewCell(TdElement(), 50);
            var c = NewCell(TdElement(), 60);
            var rows = new List<TableRowBox> { NewRow(a, b, c) };

            TableTrackResolver.Resolve(table, rows, 200, NewCtx());

            Assert.That(table.ColumnOffsets.Length, Is.EqualTo(3));
            // Offsets start at borderSpacingX and each subsequent offset
            // grows by (width[c-1] + borderSpacingX).
            Assert.That(table.ColumnOffsets[0], Is.EqualTo(2).Within(0.001),
                "First column offset = leading border-spacing (2px).");
            for (int c2 = 1; c2 < 3; c2++) {
                Assert.That(table.ColumnOffsets[c2],
                    Is.GreaterThan(table.ColumnOffsets[c2 - 1]),
                    "Column offsets must be strictly increasing left-to-right; col " + c2 + " <= col " + (c2 - 1) + ".");
                double expectedStep = table.ColumnWidths[c2 - 1] + table.BorderSpacingX;
                Assert.That(table.ColumnOffsets[c2] - table.ColumnOffsets[c2 - 1],
                    Is.EqualTo(expectedStep).Within(0.001),
                    "Step from col " + (c2 - 1) + " to col " + c2 + " must equal previous width + border-spacing.");
            }
        }

        // colspan="0" — the HTML-table parsing model treats colspan=0 as
        // an invalid value; the resolver normalises it to 1. Pins the
        // fallback so a malformed input doesn't silently produce a zero-
        // span cell that would corrupt later placement.
        [Test]
        public void Invalid_colspan_zero_normalises_to_one() {
            var table = NewTable();
            var a = NewCell(TdElement(colspan: 0), 50);
            var b = NewCell(TdElement(), 50);
            var rows = new List<TableRowBox> { NewRow(a, b) };

            TableTrackResolver.Resolve(table, rows, 200, NewCtx());

            Assert.That(a.ColSpan, Is.EqualTo(1),
                "colspan=0 is invalid for HTML tables; resolver must normalise to 1.");
            Assert.That(a.ColumnIndex, Is.EqualTo(0));
            Assert.That(b.ColumnIndex, Is.EqualTo(1),
                "Sibling after a normalised colspan=1 lands at col 1, not col 0.");
            Assert.That(table.ColumnWidths.Length, Is.EqualTo(2));
        }
    }
}
