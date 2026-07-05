using Weva.Layout.Boxes;

namespace Weva.Layout.Tables {
    // <td> / <th>. Inside the cell is a normal block formatting context — the
    // cell is laid out by BlockLayout in advance to compute intrinsic
    // dimensions, then TableLayout overwrites X/Y/Width and re-flows content
    // at the resolved column width if needed (mirroring the FlexBox pattern
    // documented at the top of FlexLayout.cs).
    public sealed class TableCellBox : BlockBox {
        // Column index assigned by TableLayout during the cell-placement pass.
        // 0-based.
        public int ColumnIndex { get; internal set; }

        // Number of columns this cell spans. Parsed from the HTML colspan
        // attribute by TableTrackResolver, clamped to the HTML table limit.
        public int ColSpan { get; internal set; } = 1;

        // Number of rows this cell spans. Parsed from the HTML rowspan
        // attribute by TableTrackResolver. A value of 0 means "span to the
        // end of the row group" in HTML; the resolver normalizes it to the
        // concrete row count for this layout pass.
        public int RowSpan { get; internal set; } = 1;

        // Pre-table-layout intrinsic max-content / min-content widths captured
        // from the box's BlockLayout-computed Width. TableTrackResolver feeds
        // these into the column-width resolution algorithm. Stored on the box
        // rather than a side-table so survivor reuse across Layout passes
        // doesn't need a fresh dictionary.
        public double IntrinsicMaxWidth { get; internal set; }
        public double IntrinsicMinWidth { get; internal set; }

        internal override void ResetForPool() {
            base.ResetForPool();
            ColumnIndex = 0;
            ColSpan = 1;
            RowSpan = 1;
            IntrinsicMaxWidth = 0;
            IntrinsicMinWidth = 0;
        }
    }
}
