namespace Weva.Layout.Multicol {
    // CSS Multi-column Layout L1 §2.
    // A MulticolBox is a BlockBox whose block children are arranged into N
    // equal-width column boxes.  The box participates in normal block flow as a
    // block-level box (its outer formatting context is unchanged); MulticolLayout
    // runs as a post-pass — exactly like FlexLayout / GridLayout — and translates
    // the children into their final column-relative X/Y positions.
    //
    // Geometry stored here after MulticolLayout.Layout():
    //   UsedColumnCount   — resolved column count (≥1).
    //   UsedColumnWidth   — resolved column width in px (content-box).
    //   UsedGap           — resolved column-gap in px.
    //   UsedColumnHeight  — the column height limit used during distribution:
    //                       for explicit-height containers this is the content
    //                       height of the container; for balanced containers it
    //                       is the converged balanced height.  Paint-level
    //                       fragmentation (MULTICOL-FRAG v1) uses this to decide
    //                       whether a child spans more than one column.
    //   ColumnHeights[]   — height of each column (sum of placed child margin-
    //                       boxes; may exceed UsedColumnHeight when a child
    //                       overflows). Null before MulticolLayout runs.
    //   These fields are read by BoxToPaintConverter to position column-rule lines.
    public sealed class MulticolBox : Weva.Layout.Boxes.BlockBox {
        public int UsedColumnCount { get; internal set; }
        public double UsedColumnWidth { get; internal set; }
        public double UsedGap { get; internal set; }

        // The column height limit applied during TryDistribute / DistributeBalanced.
        // 0 means MulticolLayout has not run yet.
        public double UsedColumnHeight { get; internal set; }

        // Height of each column after distribution (length == UsedColumnCount).
        // Null before MulticolLayout runs.
        public double[] ColumnHeights { get; internal set; }

        internal override void ResetForPool() {
            base.ResetForPool();
            UsedColumnCount = 0;
            UsedColumnWidth = 0;
            UsedGap = 0;
            UsedColumnHeight = 0;
            ColumnHeights = null;
        }
    }
}
