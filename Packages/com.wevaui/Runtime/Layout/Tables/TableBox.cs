using Weva.Layout.Boxes;

namespace Weva.Layout.Tables {
    // BlockBox subclass that participates in the parent block flow exactly like
    // a regular block (BlockLayout sizes its outer frame), and is then visited
    // by TableLayout to arrange its row-group / row / cell descendants.
    //
    // CSS Tables Module Level 3 §3: a "table wrapper box" wraps the actual
    // table grid + caption. v1 collapses these into a single TableBox: the
    // wrapper and the inner table grid have the same X/Y/Width and the caption
    // (if any) is positioned by TableLayout above the grid.
    public sealed class TableBox : BlockBox {
        public bool IsInline { get; internal set; }

        // Resolved column widths after TableTrackResolver runs. Length matches
        // the maximum cell-column-index +1 across rows. Recreated each Layout
        // pass; the array reference is overwritten so cached survivors can
        // pick up new column counts on the next pass.
        public double[] ColumnWidths { get; internal set; }

        // X offset of each column relative to the table's content-box origin.
        // Same length as ColumnWidths.
        public double[] ColumnOffsets { get; internal set; }

        // CSS Tables L3 §6.1: separated borders model spacing. In collapsed
        // mode layout suppresses this spacing; collapsed border conflict
        // resolution/painting is handled separately and is still partial.
        public double BorderSpacingX { get; internal set; }
        public double BorderSpacingY { get; internal set; }

        internal override void ResetForPool() {
            base.ResetForPool();
            IsInline = false;
            ColumnWidths = null;
            ColumnOffsets = null;
            BorderSpacingX = 0;
            BorderSpacingY = 0;
        }
    }
}
