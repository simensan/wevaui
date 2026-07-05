using System.Collections.Generic;

namespace Weva.Layout.Grid {
    // CSS Grid auto-placement (spec §8.5), simplified for v1.
    //
    // Pipeline:
    //   1. Resolve each item's GridItemPlacement to a PartialPlacement (lines + spans;
    //      0 means auto/unresolved).
    //   2. Compute final row/column for items that have at least one definite axis,
    //      marking cells occupied.
    //   3. For items with both axes auto, walk row-major (or column-major) through
    //      the explicit grid placing each at the next free cell. Sparse vs dense
    //      controls whether the cursor rewinds for each item.
    //
    // v1 simplifications:
    //   - The "implicit grid expansion" is supported on the major axis only; when an
    //     item with explicit placement falls outside the explicit grid, the grid is
    //     extended with grid-auto-rows / grid-auto-columns.
    //   - Negative line numbers resolve once we know the final track count so this
    //     function does the negative resolution AFTER explicit-grid placements have
    //     extended the grid.
    public sealed class GridPlacementResult {
        public int Rows;
        public int Columns;
        public List<GridArea> ItemAreas = new();
    }

    internal static class GridAutoPlacement {
        public static GridPlacementResult Place(List<GridPlacementResolver.PartialPlacement> partials,
                                                GridContainerProperties props,
                                                int explicitRows,
                                                int explicitColumns) {
            var result = new GridPlacementResult();
            var slots = new List<GridArea?>(partials.Count);
            var occupied = new List<GridArea>();
            int rows = explicitRows;
            int cols = explicitColumns;

            // First pass: items with both row and column lines explicitly fixed (or
            // resolvable from spans). Determine grid extents.
            for (int i = 0; i < partials.Count; i++) slots.Add(null);

            for (int i = 0; i < partials.Count; i++) {
                var p = partials[i];
                if (HasDefiniteLine(p.RowStart, p.RowEnd) &&
                    HasDefiniteLine(p.ColumnStart, p.ColumnEnd)) {
                    var area = ResolveExplicit(p);
                    slots[i] = area;
                    if (area.RowEnd - 1 > rows) rows = area.RowEnd - 1;
                    if (area.ColumnEnd - 1 > cols) cols = area.ColumnEnd - 1;
                }
            }

            for (int i = 0; i < partials.Count; i++) {
                if (slots[i].HasValue) continue;
                var p = partials[i];
                bool rowFixed = HasDefiniteLine(p.RowStart, p.RowEnd);
                bool colFixed = HasDefiniteLine(p.ColumnStart, p.ColumnEnd);
                if (rowFixed && !colFixed) {
                    var (rs, re) = ResolveLineAndSpan(p.RowStart, p.RowEnd, p.RowStartSpan, p.RowEndSpan);
                    if (re - 1 > rows) rows = re - 1;
                } else if (colFixed && !rowFixed) {
                    var (cs, ce) = ResolveLineAndSpan(p.ColumnStart, p.ColumnEnd, p.ColumnStartSpan, p.ColumnEndSpan);
                    if (ce - 1 > cols) cols = ce - 1;
                }
            }

            if (rows < 1) rows = 1;
            if (cols < 1) cols = 1;

            // Track occupancy. List of areas placed so far; we resize the grid
            // as items push extents out.
            for (int i = 0; i < partials.Count; i++) {
                if (slots[i].HasValue) occupied.Add(slots[i].Value);
            }

            bool flowColumn = props.AutoFlow == GridAutoFlow.Column || props.AutoFlow == GridAutoFlow.ColumnDense;
            bool dense = props.AutoFlow == GridAutoFlow.RowDense || props.AutoFlow == GridAutoFlow.ColumnDense;

            int autoMajorCursor = 1;
            int autoMinorCursor = 1;

            for (int i = 0; i < partials.Count; i++) {
                if (slots[i].HasValue) continue;
                var p = partials[i];
                bool rowFixed = HasDefiniteLine(p.RowStart, p.RowEnd);
                bool colFixed = HasDefiniteLine(p.ColumnStart, p.ColumnEnd);

                if (rowFixed && !colFixed) {
                    var (rs, re) = ResolveLineAndSpan(p.RowStart, p.RowEnd, p.RowStartSpan, p.RowEndSpan);
                    int span = SpanFrom(p.ColumnStart, p.ColumnEnd, p.ColumnStartSpan, p.ColumnEndSpan);
                    if (span < 1) span = 1;
                    int searchCol = dense ? 1 : 1; // row-fixed items don't move the auto cursor in sparse mode either
                    GridArea placed;
                    while (true) {
                        var area = new GridArea(rs, re, searchCol, searchCol + span);
                        if (!Overlaps(area, occupied)) {
                            placed = area;
                            break;
                        }
                        searchCol++;
                    }
                    if (placed.ColumnEnd - 1 > cols) cols = placed.ColumnEnd - 1;
                    if (placed.RowEnd - 1 > rows) rows = placed.RowEnd - 1;
                    slots[i] = placed;
                    occupied.Add(placed);
                    continue;
                }
                if (colFixed && !rowFixed) {
                    var (cs, ce) = ResolveLineAndSpan(p.ColumnStart, p.ColumnEnd, p.ColumnStartSpan, p.ColumnEndSpan);
                    int span = SpanFrom(p.RowStart, p.RowEnd, p.RowStartSpan, p.RowEndSpan);
                    if (span < 1) span = 1;

                    // Sparse: if the item's column is less than the cursor's column, bump row.
                    int searchRow = dense ? 1 : autoMajorCursor;
                    int searchMinor = dense ? 1 : autoMinorCursor;
                    if (!dense && !flowColumn) {
                        if (cs < searchMinor) {
                            searchRow++;
                        }
                    }
                    GridArea placed;
                    int r = searchRow;
                    while (true) {
                        var area = new GridArea(r, r + span, cs, ce);
                        if (!Overlaps(area, occupied)) {
                            placed = area;
                            break;
                        }
                        r++;
                    }
                    if (placed.ColumnEnd - 1 > cols) cols = placed.ColumnEnd - 1;
                    if (placed.RowEnd - 1 > rows) rows = placed.RowEnd - 1;
                    slots[i] = placed;
                    occupied.Add(placed);
                    if (!dense && !flowColumn) {
                        autoMajorCursor = placed.RowStart;
                        autoMinorCursor = placed.ColumnEnd;
                    }
                    continue;
                }

                // Fully auto.
                int rowSpan = SpanFrom(p.RowStart, p.RowEnd, p.RowStartSpan, p.RowEndSpan);
                int colSpan = SpanFrom(p.ColumnStart, p.ColumnEnd, p.ColumnStartSpan, p.ColumnEndSpan);
                if (rowSpan < 1) rowSpan = 1;
                if (colSpan < 1) colSpan = 1;

                int startMajor = dense ? 1 : autoMajorCursor;
                int startMinor = dense ? 1 : autoMinorCursor;

                GridArea autoPlaced;
                if (flowColumn) {
                    autoPlaced = FindFreeColumnFlow(rowSpan, colSpan, ref rows, ref cols, occupied, startMajor, startMinor);
                } else {
                    autoPlaced = FindFreeRowFlow(rowSpan, colSpan, ref rows, ref cols, occupied, startMajor, startMinor);
                }
                slots[i] = autoPlaced;
                occupied.Add(autoPlaced);
                if (!dense) {
                    if (flowColumn) {
                        autoMajorCursor = autoPlaced.ColumnStart;
                        autoMinorCursor = autoPlaced.RowStart + rowSpan;
                    } else {
                        autoMajorCursor = autoPlaced.RowStart;
                        autoMinorCursor = autoPlaced.ColumnStart + colSpan;
                    }
                }
            }

            for (int i = 0; i < partials.Count; i++) {
                result.ItemAreas.Add(slots[i] ?? new GridArea(1, 2, 1, 2));
            }
            result.Rows = rows;
            result.Columns = cols;
            return result;
        }

        static bool HasAnyLine(int start, int end, int startSpan, int endSpan) {
            return start > 0 || end > 0 || startSpan > 0 || endSpan > 0;
        }

        static bool HasDefiniteLine(int start, int end) {
            return start > 0 || end > 0;
        }

        static GridArea ResolveExplicit(GridPlacementResolver.PartialPlacement p) {
            var (rs, re) = ResolveLineAndSpan(p.RowStart, p.RowEnd, p.RowStartSpan, p.RowEndSpan);
            var (cs, ce) = ResolveLineAndSpan(p.ColumnStart, p.ColumnEnd, p.ColumnStartSpan, p.ColumnEndSpan);
            return new GridArea(rs, re, cs, ce);
        }

        static (int start, int end) ResolveLineAndSpan(int start, int end, int startSpan, int endSpan) {
            int s = start, e = end;
            if (s == 0 && e == 0) {
                int span = startSpan > 0 ? startSpan : endSpan > 0 ? endSpan : 1;
                return (1, 1 + span);
            }
            if (s == 0 && e > 0) {
                int span = startSpan > 0 ? startSpan : 1;
                s = e - span;
                if (s < 1) s = 1;
                if (e <= s) e = s + 1;
                return (s, e);
            }
            if (s > 0 && e == 0) {
                int span = endSpan > 0 ? endSpan : 1;
                e = s + span;
                return (s, e);
            }
            if (s > e) { int t = s; s = e; e = t; }
            if (e <= s) e = s + 1;
            return (s, e);
        }

        static int SpanFrom(int start, int end, int startSpan, int endSpan) {
            if (start > 0 && end > 0) return end - start;
            if (startSpan > 0) return startSpan;
            if (endSpan > 0) return endSpan;
            return 1;
        }

        static GridArea PlaceOneAxis(GridPlacementResolver.PartialPlacement p,
                                     bool rowFixed,
                                     ref int rows, ref int cols,
                                     List<GridArea> occupied,
                                     bool dense) {
            int rs, re, cs, ce;
            if (rowFixed) {
                (rs, re) = ResolveLineAndSpan(p.RowStart, p.RowEnd, p.RowStartSpan, p.RowEndSpan);
                int span = SpanFrom(p.ColumnStart, p.ColumnEnd, p.ColumnStartSpan, p.ColumnEndSpan);
                int startSearch = 1;
                while (true) {
                    cs = startSearch;
                    ce = cs + span;
                    var area = new GridArea(rs, re, cs, ce);
                    if (!Overlaps(area, occupied)) {
                        if (ce - 1 > cols) cols = ce - 1;
                        if (re - 1 > rows) rows = re - 1;
                        return area;
                    }
                    startSearch++;
                }
            } else {
                (cs, ce) = ResolveLineAndSpan(p.ColumnStart, p.ColumnEnd, p.ColumnStartSpan, p.ColumnEndSpan);
                int span = SpanFrom(p.RowStart, p.RowEnd, p.RowStartSpan, p.RowEndSpan);
                int startSearch = 1;
                while (true) {
                    rs = startSearch;
                    re = rs + span;
                    var area = new GridArea(rs, re, cs, ce);
                    if (!Overlaps(area, occupied)) {
                        if (ce - 1 > cols) cols = ce - 1;
                        if (re - 1 > rows) rows = re - 1;
                        return area;
                    }
                    startSearch++;
                }
            }
        }

        static GridArea FindFreeRowFlow(int rowSpan, int colSpan,
                                        ref int rows, ref int cols,
                                        List<GridArea> occupied,
                                        int startRow, int startCol) {
            // If the item's column span exceeds the available column count,
            // extend `cols` to fit before entering the placement loop. The
            // inner `while (c + colSpan - 1 > cols)` walks `r++, c=1` to find
            // a row wide enough — but if EVERY row is too narrow (because
            // colSpan > cols globally), the walk never exits. Per CSS Grid
            // L1 §8.5, the implicit grid grows to accommodate items, so
            // making cols at least colSpan is spec-correct, not a clamp.
            if (colSpan > cols) cols = colSpan;
            int r = startRow, c = startCol;
            while (true) {
                while (c + colSpan - 1 > cols) {
                    r++;
                    c = 1;
                }
                if (r > rows && r + rowSpan - 1 > rows) {
                    rows = r + rowSpan - 1;
                }
                var area = new GridArea(r, r + rowSpan, c, c + colSpan);
                if (!Overlaps(area, occupied)) {
                    if (area.RowEnd - 1 > rows) rows = area.RowEnd - 1;
                    if (area.ColumnEnd - 1 > cols) cols = area.ColumnEnd - 1;
                    return area;
                }
                c++;
            }
        }

        static GridArea FindFreeColumnFlow(int rowSpan, int colSpan,
                                           ref int rows, ref int cols,
                                           List<GridArea> occupied,
                                           int startCol, int startRow) {
            // Symmetric to FindFreeRowFlow: when laying out column-first, an
            // item with rowSpan > rows would spin forever inside the inner
            // `while (r + rowSpan - 1 > rows)` walk.
            if (rowSpan > rows) rows = rowSpan;
            int r = startRow, c = startCol;
            while (true) {
                while (r + rowSpan - 1 > rows) {
                    c++;
                    r = 1;
                }
                if (c > cols && c + colSpan - 1 > cols) {
                    cols = c + colSpan - 1;
                }
                var area = new GridArea(r, r + rowSpan, c, c + colSpan);
                if (!Overlaps(area, occupied)) {
                    if (area.RowEnd - 1 > rows) rows = area.RowEnd - 1;
                    if (area.ColumnEnd - 1 > cols) cols = area.ColumnEnd - 1;
                    return area;
                }
                r++;
            }
        }

        static bool Overlaps(GridArea a, List<GridArea> occupied) {
            for (int i = 0; i < occupied.Count; i++) {
                var b = occupied[i];
                if (a.RowStart < b.RowEnd && b.RowStart < a.RowEnd &&
                    a.ColumnStart < b.ColumnEnd && b.ColumnStart < a.ColumnEnd) {
                    return true;
                }
            }
            return false;
        }
    }
}
