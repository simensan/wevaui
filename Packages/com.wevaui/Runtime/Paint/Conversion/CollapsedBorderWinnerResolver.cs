using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Layout.Boxes;
using Weva.Layout.Tables;

namespace Weva.Paint.Conversion {
    // CSS 2.2 §17.6.2.1 — Collapsed border conflict resolution.
    //
    // In the collapsed-borders model every interior edge is shared between
    // exactly two cells; every table-edge border is shared between a cell and
    // the table element itself. One border "wins" and is rendered; the other
    // is suppressed.
    //
    // Winner precedence (highest to lowest):
    //   1. border-style: hidden always wins (renders as invisible / suppressed).
    //   2. Larger border-width wins (after excluding style == none).
    //   3. Style priority (double > solid > dashed > dotted > ridge > outset
    //      > groove > inset; none always loses).
    //   4. Element type priority for equal styles/widths: cell > row >
    //      row-group > column > column-group > table. This resolver compares
    //      only cell vs. table because that is the only pairing this engine
    //      surfaces — cell wins.
    //   5. Side: top/left win ties over bottom/right.
    //
    // This class is intentionally pure-static and allocation-free: callers
    // pass resolved BorderEdge structs; no heap is touched.
    internal static class CollapsedBorderWinnerResolver {

        // Returns true if the table element owning `cell` uses collapsed borders.
        public static bool IsCollapsed(TableCellBox cell) {
            if (cell == null) return false;
            var tableBox = FindTable(cell);
            if (tableBox == null) return false;
            var style = tableBox.Style;
            if (style == null) return false;
            string bc = style.Get(CssProperties.BorderCollapseId);
            return !string.IsNullOrEmpty(bc)
                && CssStringUtil.EqualsIgnoreCaseTrimmed(bc, "collapse");
        }

        // Resolves the four effective border edges for `cell` in a collapsed-
        // borders table. For each cell edge the opponent is:
        //   - Top edge: the bottom edge of the cell directly above, OR the
        //     top edge of the table (if this is the first row).
        //   - Right edge: the left edge of the cell directly to the right, OR
        //     the right edge of the table (if this is the last column).
        //   - Bottom edge: the top edge of the cell directly below, OR the
        //     bottom edge of the table (if this is the last row).
        //   - Left edge: the right edge of the cell directly to the left, OR
        //     the left edge of the table (if this is the first column).
        //
        // `cellBorders` is the cell's own resolved Borders (from BorderResolver).
        // `ctx` is used to resolve the table's border widths.
        //
        // Returns the winning Borders to actually draw (losing edges are set to
        // BorderEdge.None so the paint path skips them).
        //
        // NOTE: Each shared edge is drawn by exactly one cell. The winner cell
        // draws; the loser cell suppresses. We use the rule:
        //   - For vertical (left/right) shared edges: right-cell's LEFT edge
        //     competes against left-cell's RIGHT edge. The caller (right cell)
        //     draws the winning edge on its LEFT side; the left cell draws the
        //     winning edge on its RIGHT side. In practice this means BOTH cells
        //     end up drawing the winner — which is correct because the shared
        //     pixel strip is physically inside both boxes' border areas.
        //
        //   - For horizontal (top/bottom) shared edges: lower-cell's TOP edge
        //     competes against upper-cell's BOTTOM edge. Both draw the winner.
        //
        //   - Table-edge borders: the cell and the table both have a border
        //     declaration for that side. The cell draws the winner on its own
        //     relevant side; the table itself draws nothing extra for collapsed
        //     cells (the table's own border is suppressed per-side when every
        //     adjacent cell wins). For simplicity, the table's border is NOT
        //     suppressed in this engine — the table still draws its own declared
        //     border, and the cell draws the winning edge, potentially on top.
        //     This is a known visual approximation for table-edge cases.
        //
        // PAINT MODEL: Each cell draws the WINNING edge on each of its sides.
        // For shared interior edges, both neighbors resolve and draw the same
        // winner. Because the cell boxes are positioned flush (collapsed; no
        // border-spacing), both cells' winning-edge rects occupy the same
        // physical pixel strip — drawing twice is harmless (same color, same
        // width). Alternatively, one could suppress the loser entirely; the
        // visible result is identical for opaque borders but this approach is
        // simpler with the current per-cell emission path.
        public static Borders Resolve(TableCellBox cell, Borders cellBorders, LengthContext ctx) {
            var tableBox = FindTable(cell);
            if (tableBox == null) return cellBorders;

            // Resolve table's own borders (used for table-edge comparisons).
            Borders tableBorders = tableBox.Style != null
                ? BorderResolver.ResolveBorders(tableBox.Style, ctx)
                : Borders.None;

            // Find neighboring cells.
            TableCellBox above = FindCellAbove(cell);
            TableCellBox below = FindCellBelow(cell);
            TableCellBox leftOf = FindCellLeft(cell);
            TableCellBox rightOf = FindCellRight(cell);

            // Determine whether this cell is on the table's physical edges.
            bool isTopEdge    = above == null;
            bool isBottomEdge = below == null;
            bool isLeftEdge   = leftOf == null;
            bool isRightEdge  = rightOf == null;

            // -- Top edge
            // The top edge of the current cell competes against the bottom edge of
            // the cell above. The cell above has a smaller row index and therefore
            // higher element priority per §17.6.2.1 step 4. In our PickWinner call,
            // `a` = current cell's top (lower priority), `b` = above cell's bottom
            // (higher priority). Secondary means `b` wins ties → above cell wins.
            // For the table-edge case (no cell above), the cell wins over the table
            // element (cell has higher element priority) → Primary (a wins).
            BorderEdge winTop;
            if (isTopEdge) {
                winTop = PickWinner(cellBorders.Top, tableBorders.Top, sidePreference: SidePreference.Primary);
            } else {
                var aboveBorders = above.Style != null
                    ? BorderResolver.ResolveBorders(above.Style, ctx)
                    : Borders.None;
                winTop = PickWinner(cellBorders.Top, aboveBorders.Bottom, sidePreference: SidePreference.Secondary);
            }

            // -- Bottom edge
            // `a` = current cell's bottom, `b` = below cell's top.
            // Current cell has smaller row index (higher priority) → Primary (a wins).
            // Table-edge: cell wins over table → Primary.
            BorderEdge winBottom;
            if (isBottomEdge) {
                winBottom = PickWinner(cellBorders.Bottom, tableBorders.Bottom, sidePreference: SidePreference.Primary);
            } else {
                var belowBorders = below.Style != null
                    ? BorderResolver.ResolveBorders(below.Style, ctx)
                    : Borders.None;
                winBottom = PickWinner(cellBorders.Bottom, belowBorders.Top, sidePreference: SidePreference.Primary);
            }

            // -- Left edge
            // `a` = current cell's left, `b` = left-neighbor's right.
            // Left neighbor has smaller column index (higher priority) → Secondary (b wins).
            // Table-edge: cell wins over table → Primary.
            BorderEdge winLeft;
            if (isLeftEdge) {
                winLeft = PickWinner(cellBorders.Left, tableBorders.Left, sidePreference: SidePreference.Primary);
            } else {
                var leftBorders = leftOf.Style != null
                    ? BorderResolver.ResolveBorders(leftOf.Style, ctx)
                    : Borders.None;
                winLeft = PickWinner(cellBorders.Left, leftBorders.Right, sidePreference: SidePreference.Secondary);
            }

            // -- Right edge
            // `a` = current cell's right, `b` = right-neighbor's left.
            // Current cell has smaller column index (higher priority) → Primary (a wins).
            // Table-edge: cell wins over table → Primary.
            BorderEdge winRight;
            if (isRightEdge) {
                winRight = PickWinner(cellBorders.Right, tableBorders.Right, sidePreference: SidePreference.Primary);
            } else {
                var rightBorders = rightOf.Style != null
                    ? BorderResolver.ResolveBorders(rightOf.Style, ctx)
                    : Borders.None;
                winRight = PickWinner(cellBorders.Right, rightBorders.Left, sidePreference: SidePreference.Primary);
            }

            return new Borders(winTop, winRight, winBottom, winLeft);
        }

        // Determines which of two competing border edges wins per §17.6.2.1.
        //
        // `sidePreference` encodes the tie-breaking rule for equal-priority
        // borders: Primary wins over Secondary.
        //   Primary   = top/left side (wins ties)
        //   Secondary = bottom/right side (loses ties)
        //
        // The `a` argument is always the current cell's own edge; `sidePreference`
        // tells us whether `a` is a Primary or Secondary side relative to the
        // shared edge being resolved.
        //
        // Returns the winning BorderEdge. If the winner is hidden the returned
        // edge has Style == BorderStyle.Hidden (the caller treats it as invisible).
        public static BorderEdge PickWinner(BorderEdge a, BorderEdge b, SidePreference sidePreference) {
            // Rule 1: hidden always wins regardless of width or other properties.
            bool aHidden = a.Style == BorderStyle.Hidden;
            bool bHidden = b.Style == BorderStyle.Hidden;
            if (aHidden && bHidden) {
                // Both hidden — return the "primary" winner so the edge renders
                // as hidden (invisible), which is the correct outcome.
                return sidePreference == SidePreference.Primary ? a : b;
            }
            if (aHidden) return a;
            if (bHidden) return b;

            // None loses to everything (skip it for width/style comparison).
            bool aNone = a.Style == BorderStyle.None || a.Width <= 0;
            bool bNone = b.Style == BorderStyle.None || b.Width <= 0;
            if (aNone && bNone) return BorderEdge.None;
            if (aNone) return b;
            if (bNone) return a;

            // Rule 2: wider border wins.
            double diff = a.Width - b.Width;
            if (diff > 0.001) return a;
            if (diff < -0.001) return b;

            // Rule 3: style priority. higher ordinal = higher priority.
            int aPri = StylePriority(a.Style);
            int bPri = StylePriority(b.Style);
            if (aPri > bPri) return a;
            if (bPri > aPri) return b;

            // Rule 4/5: element type (cell wins over table — both candidates
            // here are cell edges so we fall through) then side (primary wins).
            return sidePreference == SidePreference.Primary ? a : b;
        }

        // CSS 2.2 §17.6.2.1 style priority table (higher = wins).
        // ridge, outset, groove, inset are not in our BorderStyle enum so they
        // are absent here. double > solid > dashed > dotted > none.
        static int StylePriority(BorderStyle s) {
            switch (s) {
                case BorderStyle.Double: return 4;
                case BorderStyle.Solid:  return 3;
                case BorderStyle.Dashed: return 2;
                case BorderStyle.Dotted: return 1;
                default:                 return 0;
            }
        }

        // Walks up the box parent chain to find the nearest TableBox ancestor.
        // Returns null if the cell is not inside a TableBox (should not happen
        // in a well-formed table layout).
        static TableBox FindTable(TableCellBox cell) {
            for (Box b = cell?.Parent; b != null; b = b.Parent) {
                if (b is TableBox t) return t;
            }
            return null;
        }

        // Finds the TableCellBox directly above this cell (same column, previous row).
        static TableCellBox FindCellAbove(TableCellBox cell) {
            var row = cell?.Parent as TableRowBox;
            if (row == null) return null;
            var prevRow = PreviousSiblingRow(row);
            if (prevRow == null) return null;
            return FindCellInRowAtColumn(prevRow, cell.ColumnIndex);
        }

        // Finds the TableCellBox directly below this cell (same column, next row).
        static TableCellBox FindCellBelow(TableCellBox cell) {
            var row = cell?.Parent as TableRowBox;
            if (row == null) return null;
            // The cell may span multiple rows — skip to the row after the spanned range.
            int targetRowIndex = RowIndex(row) + cell.RowSpan;
            var nextRow = FindRowAtIndex(row, targetRowIndex);
            if (nextRow == null) return null;
            return FindCellInRowAtColumn(nextRow, cell.ColumnIndex);
        }

        // Finds the TableCellBox directly to the left of this cell.
        static TableCellBox FindCellLeft(TableCellBox cell) {
            var row = cell?.Parent as TableRowBox;
            if (row == null) return null;
            int targetCol = cell.ColumnIndex - 1;
            if (targetCol < 0) return null;
            return FindCellInRowAtColumn(row, targetCol);
        }

        // Finds the TableCellBox directly to the right of this cell.
        static TableCellBox FindCellRight(TableCellBox cell) {
            var row = cell?.Parent as TableRowBox;
            if (row == null) return null;
            int targetCol = cell.ColumnIndex + cell.ColSpan;
            return FindCellInRowAtColumn(row, targetCol);
        }

        // Returns the first cell in `row` whose ColumnIndex == col, or null.
        static TableCellBox FindCellInRowAtColumn(TableRowBox row, int col) {
            var children = row.Children;
            int n = children.Count;
            for (int i = 0; i < n; i++) {
                if (children[i] is TableCellBox c && c.ColumnIndex == col) return c;
            }
            return null;
        }

        // Returns the sibling TableRowBox immediately before `row` in its
        // parent row-group or table. Skips non-row siblings.
        static TableRowBox PreviousSiblingRow(TableRowBox row) {
            var parent = row?.Parent;
            if (parent == null) return null;
            var siblings = parent.Children;
            int n = siblings.Count;
            TableRowBox prev = null;
            for (int i = 0; i < n; i++) {
                var s = siblings[i];
                if (ReferenceEquals(s, row)) return prev;
                if (s is TableRowBox r) prev = r;
            }
            return null;
        }

        // Returns the 0-based index of `row` among all rows in its parent.
        static int RowIndex(TableRowBox row) {
            var parent = row?.Parent;
            if (parent == null) return 0;
            var siblings = parent.Children;
            int idx = 0;
            int n = siblings.Count;
            for (int i = 0; i < n; i++) {
                if (ReferenceEquals(siblings[i], row)) return idx;
                if (siblings[i] is TableRowBox) idx++;
            }
            return idx;
        }

        // Returns the row at absolute index `targetIndex` among all rows in
        // the same parent as the reference row.
        static TableRowBox FindRowAtIndex(TableRowBox referenceRow, int targetIndex) {
            var parent = referenceRow?.Parent;
            if (parent == null) return null;
            var siblings = parent.Children;
            int n = siblings.Count;
            int idx = 0;
            for (int i = 0; i < n; i++) {
                if (siblings[i] is TableRowBox r) {
                    if (idx == targetIndex) return r;
                    idx++;
                }
            }
            return null;
        }
    }

    // Controls the tie-breaking rule in §17.6.2.1 step 4/5.
    // Primary = top-side or left-side (wins ties).
    // Secondary = bottom-side or right-side (loses ties).
    internal enum SidePreference { Primary, Secondary }
}
