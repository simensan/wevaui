// TableLayout — formatting context for `display: table` / `display: inline-table`.
//
// Dispatch site: BoxBuilder constructs a TableBox for elements whose computed
// display is `table` (or `inline-table`). LayoutEngine runs a post-pass after
// BlockLayout (RunTablePasses) that walks the box tree depth-first and invokes
// TableLayout.Layout on every TableBox.
//
// Algorithm follows CSS 2.1 §17.5 (separated borders model only) and the CSS
// Tables Module Level 3 column-resolution flow.
//
// Current simplifications (also documented in CONFORMANCE.md):
//   - `border-collapse: collapse` suppresses border-spacing for layout, but
//     border conflict resolution / collapsed border painting are not yet
//     implemented.
//   - `vertical-align` on cells supports `top` (default), `middle`, and
//     `bottom`. `baseline` is treated as `top` in v1 — baseline alignment
//     across sibling cells needs first-line baseline data that the cell
//     boxes don't surface yet.
//   - colspan and rowspan attributes participate in grid placement and
//     spanned cell width/height.
//   - Nested tables work but every nested table re-runs the full intrinsic-
//     sizing pass; no incremental layout keyed on intrinsic widths.

using System.Collections.Generic;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Layout.Boxes;

namespace Weva.Layout.Tables {
    internal sealed class TableLayout {
        // Pass-reuse invariant: constructed once per LayoutEngine. The block
        // re-flow path requires a BlockLayout reference (cells get re-laid
        // out at their final column width). LayoutScratch is engine-stable;
        // ctx refreshed each Layout pass via Reset().
        LayoutContext ctx;
        readonly BlockLayout blockLayout;

        // Per-pass scratch reused across nested table calls. Layout is
        // recursive (nested table inside a cell); we use stack discipline,
        // appending then popping a slice on every entry. Allocating fresh
        // lists per call would break the v0.8 alloc-floor goal.
        readonly List<TableRowBox> rowsScratch = new(16);
        readonly List<TableCaptionBox> captionsScratch = new(2);

        public TableLayout(BlockLayout blockLayout) {
            this.blockLayout = blockLayout;
        }

        public void Reset(LayoutContext ctx) {
            this.ctx = ctx;
        }

        public void Layout(TableBox table) {
            if (table == null) return;

            ResolveBorderSpacing(table);

            int rowsStart = rowsScratch.Count;
            int captionsStart = captionsScratch.Count;
            CollectRows(table, rowsScratch);
            CollectCaptions(table, captionsScratch);
            int rowCount = rowsScratch.Count - rowsStart;
            int captionCount = captionsScratch.Count - captionsStart;

            // Snapshot intrinsic widths from BlockLayout's first pass over
            // each cell. We do this before sizing columns because the
            // resolution algorithm consumes them without modifying cells.
            for (int r = 0; r < rowCount; r++) {
                var row = rowsScratch[rowsStart + r];
                for (int i = 0; i < row.Children.Count; i++) {
                    if (row.Children[i] is TableCellBox cell) {
                        // BlockLayout's pre-table pass set cell.Width to the
                        // available width (the parent's content width). For
                        // the intrinsic-max-content figure we take the
                        // explicit style width when present, else fall back
                        // to the BlockLayout-computed Width clamped against
                        // the cell's children.
                        double explicitW = ResolveExplicitWidth(cell, table.ContentWidth);
                        if (explicitW > 0) {
                            // CSS 2.1 §17.5.2: table cells default to box-sizing:
                            // content-box (like every CSS box unless explicitly
                            // border-box). The author's `width: 64px` is the
                            // content area; the cell's outer width is content +
                            // padding + border. The track resolver works in
                            // OUTER (border-box) units, so add the frame here
                            // unless box-sizing: border-box is set. Without
                            // this, a `<th class="col-rank" style="width:64px">`
                            // with padding 12px 16px would size the column to
                            // 64 instead of 96, and the fr-style auto column
                            // soaks up the missing 32 — visible in
                            // leaderboard.html as col-name growing 146px wider
                            // than Chrome and all fixed cols ~32px narrower.
                            bool cellBorderBox = cell.Style != null
                                && cell.Style.Get(CssProperties.BoxSizingId) == "border-box";
                            double cellHFrame = cell.PaddingLeft + cell.PaddingRight
                                + cell.BorderLeft + cell.BorderRight;
                            double outerW = cellBorderBox ? explicitW : explicitW + cellHFrame;
                            cell.IntrinsicMinWidth = outerW;
                            cell.IntrinsicMaxWidth = outerW;
                        } else {
                            cell.IntrinsicMaxWidth = MeasureMaxContentWidth(cell);
                            cell.IntrinsicMinWidth = MeasureMinContentWidth(cell);
                        }
                    }
                }
            }

            // Column resolution. Slice the rows scratch so the resolver only
            // sees this table's rows; the function takes a List and we pass
            // a temporary view to avoid an allocation per nested table.
            var rowsView = new List<TableRowBox>(rowCount);
            for (int r = 0; r < rowCount; r++) rowsView.Add(rowsScratch[rowsStart + r]);
            TableTrackResolver.Resolve(table, rowsView, table.ContentWidth, ctx);

            // I4b: column-track collapse per CSS Tables L3 §11.5. Detect which
            // columns belong to a `<col>` / `<colgroup>` with
            // `visibility: collapse` and zero their resolved widths + recompute
            // column offsets so subsequent columns slide left into the freed
            // slot. We work on the arrays the resolver already produced rather
            // than re-running track resolution — the spec compaction step
            // operates on the *resolved* track widths, not the input intrinsics.
            bool[] colCollapsed = ComputeColumnCollapseMask(table);
            if (colCollapsed != null) ApplyColumnCollapse(table, colCollapsed);

            // Cell positioning + height pass. Per CSS 2.1 §17.5.3:
            //   1. row height = max of cell border-box heights in that row;
            //   2. cells stretch to row height.
            // We go a step further by re-flowing each cell at its resolved
            // column width before measuring its height — the FlexLayout
            // pattern of "overwrite Width then re-flow content" applies.

            double cursorY = table.PaddingTop + table.BorderTop + table.BorderSpacingY;

            // CSS 2.1 §17.4 / Tables L3 §2.1: caption-side accepts physical
            // (top/bottom) and logical (block-start/block-end/inline-start/
            // inline-end) values; logical keywords resolve against the table's
            // writing-mode. v1 only positions captions above/below the row
            // stack — side placement (inline-start/end, or block-start/end in
            // vertical writing modes) falls through to top placement.
            for (int c = 0; c < captionCount; c++) {
                var cap = captionsScratch[captionsStart + c];
                if (ResolveCaptionSidePhysical(cap) == "bottom") continue;
                cap.X = table.PaddingLeft + table.BorderLeft;
                cap.Y = cursorY;
                cap.Width = table.ContentWidth;
                cursorY += cap.Height;
            }

            // Rows are children of TableRowGroupBox (thead/tbody/tfoot) or
            // direct children of the table. We assign row Y values in
            // GROUP-relative coords (or table-relative when direct), and
            // size each group's box to span its rows. Without this, the
            // BlockLayout pre-pass's stale Y/Height on the groups stays in
            // the parent chain — Walk()'s absolute-position sum then offsets
            // every row by a phantom ~200px of "thead.Height" because the
            // pre-pass stacked cells block-flow and inflated the group's
            // height.
            var rowHeights = new double[rowCount];
            var rowFloors = new double[rowCount];
            var rowCollapsed = new bool[rowCount];
            var spanningCells = new List<RowSpanMeasure>(4);
            for (int r = 0; r < rowCount; r++) {
                var row = rowsScratch[rowsStart + r];
                rowCollapsed[r] = IsRowVisibilityCollapse(row);
                if (rowCollapsed[r]) continue;
                rowFloors[r] = ResolveExplicitRowHeight(row, table);
                if (rowFloors[r] > rowHeights[r]) rowHeights[r] = rowFloors[r];
                for (int i = 0; i < row.Children.Count; i++) {
                    if (!(row.Children[i] is TableCellBox cell)) continue;
                    int colIdx = cell.ColumnIndex;
                    int colSpan = cell.ColSpan > 0 ? cell.ColSpan : 1;
                    double colW = SumColumns(table, colIdx, colSpan);
                    if (cell.Width != colW) blockLayout.RelayoutContentAt(cell, colW);
                    double natural = cell.Height;
                    int rowSpan = cell.RowSpan > 0 ? cell.RowSpan : 1;
                    if (r + rowSpan > rowCount) rowSpan = rowCount - r;
                    if (rowSpan <= 1) {
                        if (natural > rowHeights[r]) rowHeights[r] = natural;
                    } else {
                        spanningCells.Add(new RowSpanMeasure(r, rowSpan, natural));
                    }
                }
            }
            for (int i = 0; i < spanningCells.Count; i++) {
                var span = spanningCells[i];
                double covered = SumRowHeights(rowHeights, span.RowIndex, span.RowSpan, table.BorderSpacingY);
                if (span.NaturalHeight <= covered) continue;
                double extra = (span.NaturalHeight - covered) / span.RowSpan;
                for (int r = span.RowIndex; r < span.RowIndex + span.RowSpan && r < rowHeights.Length; r++) {
                    if (rowCollapsed[r]) continue;
                    rowHeights[r] += extra;
                }
            }
            // Re-assert the per-row explicit `height` floor after rowspan
            // redistribution: equal-split `extra/RowSpan` can otherwise leave
            // a row authored with `height: 60px` shorter than 60.
            for (int r = 0; r < rowCount; r++) {
                if (rowCollapsed[r]) { rowHeights[r] = 0; continue; }
                if (rowFloors[r] > rowHeights[r]) rowHeights[r] = rowFloors[r];
            }

            TableRowGroupBox currentGroup = null;
            double groupStartCursor = cursorY;
            for (int r = 0; r < rowCount; r++) {
                var row = rowsScratch[rowsStart + r];
                var rowParentGroup = row.Parent as TableRowGroupBox;
                if (rowParentGroup != currentGroup) {
                    if (currentGroup != null) {
                        currentGroup.X = table.PaddingLeft + table.BorderLeft;
                        currentGroup.Y = groupStartCursor;
                        currentGroup.Width = table.ContentWidth;
                        currentGroup.Height = cursorY - groupStartCursor;
                    }
                    currentGroup = rowParentGroup;
                    groupStartCursor = cursorY;
                }
                if (currentGroup != null) {
                    row.X = 0;
                    row.Y = cursorY - groupStartCursor;
                } else {
                    row.X = table.PaddingLeft + table.BorderLeft;
                    row.Y = cursorY;
                }
                row.Width = table.ContentWidth;

                // Position each cell at its column offset; re-flow at the
                // resolved width so multi-line content wraps to the new
                // available width. Then measure row height.
                double maxCellHeight = rowHeights[r];
                int colIdx = 0;
                for (int i = 0; i < row.Children.Count; i++) {
                    if (!(row.Children[i] is TableCellBox cell)) continue;
                    colIdx = cell.ColumnIndex;
                    int span = cell.ColSpan > 0 ? cell.ColSpan : 1;
                    double colW = SumColumns(table, colIdx, span);
                    double colX = colIdx < table.ColumnOffsets.Length ? table.ColumnOffsets[colIdx] : 0;
                    cell.X = colX;
                    cell.Y = 0;
                    if (cell.Width != colW) {
                        // The BlockLayout pre-pass sized the cell to the
                        // table-content-width; re-flow at the resolved
                        // column width so wrapping settles correctly.
                        blockLayout.RelayoutContentAt(cell, colW);
                    }
                    if (!rowCollapsed[r] && (cell.RowSpan <= 1) && cell.Height > maxCellHeight) maxCellHeight = cell.Height;
                    colIdx += span;
                }
                if (rowCollapsed[r]) maxCellHeight = 0;

                // Stretch every cell's height to the row height so backgrounds
                // paint flush across the row, and apply `vertical-align`
                // (CSS 2.1 §17.5.3). `top` (default) leaves children at their
                // natural Y inside the stretched cell; `middle` and `bottom`
                // shift direct children down by half / all of the slack so the
                // smaller-font rank-1 cell text sits centered against the
                // larger numeric cells. `baseline` is treated as `top` for
                // v1 (no first-line baseline data on cell boxes).
                for (int i = 0; i < row.Children.Count; i++) {
                    if (!(row.Children[i] is TableCellBox cell)) continue;
                    int rowSpan = cell.RowSpan > 0 ? cell.RowSpan : 1;
                    if (r + rowSpan > rowCount) rowSpan = rowCount - r;
                    double targetHeight = rowSpan > 1
                        ? SumRowHeights(rowHeights, r, rowSpan, table.BorderSpacingY)
                        : maxCellHeight;
                    double slack = targetHeight - cell.Height;
                    if (slack > 0) {
                        string va = cell.Style?.Get("vertical-align");
                        double factor = 0;
                        if (va != null) {
                            if (CssStringUtil.EqualsIgnoreCaseTrimmed(va, "middle")) factor = 0.5;
                            else if (CssStringUtil.EqualsIgnoreCaseTrimmed(va, "bottom")) factor = 1.0;
                        }
                        if (factor > 0) {
                            double shift = slack * factor;
                            for (int j = 0; j < cell.Children.Count; j++) {
                                var ch = cell.Children[j];
                                ch.Y = ch.Y + shift;
                            }
                        }
                    }
                    cell.Height = targetHeight;
                }
                row.Height = maxCellHeight;
                cursorY += row.Height;
                if (!rowCollapsed[r]) cursorY += table.BorderSpacingY;
            }
            // Close the final open group.
            if (currentGroup != null) {
                currentGroup.X = table.PaddingLeft + table.BorderLeft;
                currentGroup.Y = groupStartCursor;
                currentGroup.Width = table.ContentWidth;
                currentGroup.Height = cursorY - groupStartCursor;
            }

            // I4b: zero geometry on any row group whose `visibility: collapse`
            // caused CollectRows to skip it. BlockLayout's pre-pass left stale
            // X/Y/Width/Height on those boxes from when it stacked them as
            // ordinary blocks. Without this reset the collapsed group would
            // still occupy a visible rectangle in the box tree even though
            // none of its rows render.
            for (int i = 0; i < table.Children.Count; i++) {
                if (!(table.Children[i] is TableRowGroupBox g)) continue;
                if (!IsGroupVisibilityCollapse(g)) continue;
                g.X = table.PaddingLeft + table.BorderLeft;
                g.Y = cursorY;
                g.Width = 0;
                g.Height = 0;
            }

            // caption-side resolves to bottom — place after the row stack.
            for (int c = 0; c < captionCount; c++) {
                var cap = captionsScratch[captionsStart + c];
                if (ResolveCaptionSidePhysical(cap) != "bottom") continue;
                cap.X = table.PaddingLeft + table.BorderLeft;
                cap.Y = cursorY;
                cap.Width = table.ContentWidth;
                cursorY += cap.Height;
            }

            // Update table content height: includes padding, borders,
            // border-spacing, all rows and captions. BlockLayout's pre-pass
            // stacked cells block-flow which inflates the table height to a
            // sum-of-cell-stacked-content value; rewrite it to the actual
            // computed-row-stack height here so the outer block-flow doesn't
            // leave hundreds of pixels of phantom space below the table.
            // Per CSS 2.1 §17.5.3, the author-declared height acts as a
            // *minimum*: when the row stack is shorter, the table grows to
            // the explicit height; when the row stack overflows, the rows
            // win (content can't be cropped by author height in v1).
            double contentBottom = cursorY + table.PaddingBottom + table.BorderBottom;
            double authorHeight = ResolveExplicitTableHeight(table);
            table.Height = authorHeight > contentBottom ? authorHeight : contentBottom;

            // Pop scratch slices.
            rowsScratch.RemoveRange(rowsStart, rowsScratch.Count - rowsStart);
            captionsScratch.RemoveRange(captionsStart, captionsScratch.Count - captionsStart);
        }

        static double SumColumns(TableBox table, int start, int span) {
            if (table == null || table.ColumnWidths == null || table.ColumnWidths.Length == 0) return 0;
            if (start < 0 || start >= table.ColumnWidths.Length) return 0;
            int count = span < 1 ? 1 : span;
            if (start + count > table.ColumnWidths.Length) count = table.ColumnWidths.Length - start;
            double width = 0;
            for (int c = start; c < start + count; c++) {
                width += table.ColumnWidths[c];
                if (c > start) width += table.BorderSpacingX;
            }
            return width;
        }

        static double SumRowHeights(double[] rowHeights, int start, int span, double spacingY) {
            if (rowHeights == null || rowHeights.Length == 0) return 0;
            if (start < 0 || start >= rowHeights.Length) return 0;
            int count = span < 1 ? 1 : span;
            if (start + count > rowHeights.Length) count = rowHeights.Length - start;
            double height = 0;
            for (int r = start; r < start + count; r++) {
                height += rowHeights[r];
                if (r > start) height += spacingY;
            }
            return height;
        }

        readonly struct RowSpanMeasure {
            public readonly int RowIndex;
            public readonly int RowSpan;
            public readonly double NaturalHeight;

            public RowSpanMeasure(int rowIndex, int rowSpan, double naturalHeight) {
                RowIndex = rowIndex;
                RowSpan = rowSpan;
                NaturalHeight = naturalHeight;
            }
        }

        void ResolveBorderSpacing(TableBox table) {
            // Per CSS 2.1 §17.6.1 / CSS Tables L3 §7.1 the initial value of
            // `border-spacing` is 0. The 2px visual default for HTML `<table>`
            // elements comes from the UA stylesheet (see UserAgentStylesheet.cs),
            // not from this fallback. Synthetic table boxes (anonymous tables,
            // or `<div style="display: table">`) must inherit the spec initial.
            if (table.Style == null) {
                table.BorderSpacingX = 0;
                table.BorderSpacingY = 0;
                return;
            }
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(table.Style.Get("border-collapse"), "collapse")) {
                table.BorderSpacingX = 0;
                table.BorderSpacingY = 0;
                return;
            }
            string raw = table.Style.Get("border-spacing");
            if (string.IsNullOrEmpty(raw)) {
                table.BorderSpacingX = 0;
                table.BorderSpacingY = 0;
                return;
            }
            double fs = StyleResolver.FontSizePx(table.Style, table.Parent?.Style, ctx);
            // border-spacing accepts one or two length values.
            int sp = raw.IndexOf(' ');
            if (sp < 0) {
                double v = StyleResolver.ResolveLengthPx(raw, 0, table.Style, ctx, fs, null);
                table.BorderSpacingX = v;
                table.BorderSpacingY = v;
                return;
            }
            string a = raw.Substring(0, sp).Trim();
            string b = raw.Substring(sp + 1).Trim();
            table.BorderSpacingX = StyleResolver.ResolveLengthPx(a, 0, table.Style, ctx, fs, null);
            table.BorderSpacingY = StyleResolver.ResolveLengthPx(b, 0, table.Style, ctx, fs, null);
        }

        // Walks the table's direct + row-group children gathering rows in
        // header → body → footer order (CSS Tables L3 §3.6). Row groups with
        // `visibility: collapse` (I4b, CSS Tables L3 §11.5) are skipped
        // wholesale here — the row stack never sees their children, so they
        // reserve no space and never paint. The group box itself is also
        // zeroed downstream so it doesn't contribute a phantom rectangle.
        static void CollectRows(TableBox table, List<TableRowBox> outList) {
            // Pass 1: thead.
            for (int i = 0; i < table.Children.Count; i++) {
                if (table.Children[i] is TableRowGroupBox g && g.GroupKind == "header") {
                    if (IsGroupVisibilityCollapse(g)) continue;
                    AddRowsFromGroup(g, outList);
                }
            }
            // Pass 2: tbody + direct <tr> children + anonymous row groups.
            for (int i = 0; i < table.Children.Count; i++) {
                var c = table.Children[i];
                if (c is TableRowGroupBox g) {
                    if (g.GroupKind == "body") {
                        if (IsGroupVisibilityCollapse(g)) continue;
                        AddRowsFromGroup(g, outList);
                    }
                } else if (c is TableRowBox row) {
                    outList.Add(row);
                }
            }
            // Pass 3: tfoot.
            for (int i = 0; i < table.Children.Count; i++) {
                if (table.Children[i] is TableRowGroupBox g && g.GroupKind == "footer") {
                    if (IsGroupVisibilityCollapse(g)) continue;
                    AddRowsFromGroup(g, outList);
                }
            }
        }

        static void AddRowsFromGroup(TableRowGroupBox group, List<TableRowBox> outList) {
            for (int i = 0; i < group.Children.Count; i++) {
                if (group.Children[i] is TableRowBox row) outList.Add(row);
            }
        }

        // I4b: `visibility: collapse` on a row-group element (<thead>, <tbody>,
        // <tfoot>) drops the entire group's row stack per CSS Tables L3 §11.5.
        // Mirror of IsRowVisibilityCollapse but for the group container.
        static bool IsGroupVisibilityCollapse(TableRowGroupBox group) {
            return group?.Style != null
                && CssStringUtil.EqualsIgnoreCaseTrimmed(group.Style.Get("visibility"), "collapse");
        }

        // I4b: build a per-column mask of which tracks the author has marked
        // `visibility: collapse` via a <col> / <colgroup> child of the table.
        // Returns null when no column is collapsed (hot path — every plain
        // table avoids the mask allocation entirely). When non-null, the
        // mask length matches table.ColumnWidths.
        //
        // Mapping follows the resolver's CollectColumnHints walk: <col> spans
        // (and their colgroup parent's `span` attribute when the group has no
        // <col> children) consume consecutive column indices in declaration
        // order. A collapsed <colgroup> propagates to every column it covers.
        static bool[] ComputeColumnCollapseMask(TableBox table) {
            if (table?.ColumnWidths == null || table.ColumnWidths.Length == 0) return null;
            int colCount = table.ColumnWidths.Length;
            bool[] mask = null;
            int colIndex = 0;
            for (int i = 0; i < table.Children.Count && colIndex < colCount; i++) {
                var child = table.Children[i] as BlockBox;
                if (child?.Element == null) continue;
                string disp = StyleResolver.Display(child.Style);
                if (disp == "table-column") {
                    int span = GetSpanAttr(child.Element, 1);
                    if (IsBoxVisibilityCollapse(child)) {
                        if (mask == null) mask = new bool[colCount];
                        for (int c = colIndex; c < colIndex + span && c < colCount; c++) mask[c] = true;
                    }
                    colIndex += span;
                } else if (disp == "table-column-group") {
                    bool groupCollapsed = IsBoxVisibilityCollapse(child);
                    int colChildren = 0;
                    for (int j = 0; j < child.Children.Count && colIndex < colCount; j++) {
                        var colBox = child.Children[j] as BlockBox;
                        if (colBox?.Element == null) continue;
                        if (StyleResolver.Display(colBox.Style) != "table-column") continue;
                        int span = GetSpanAttr(colBox.Element, 1);
                        // Per CSS Tables L3 §11.5, a collapsed colgroup
                        // collapses every column it contains, even when the
                        // child <col> itself is `visibility: visible`.
                        bool collapse = groupCollapsed || IsBoxVisibilityCollapse(colBox);
                        if (collapse) {
                            if (mask == null) mask = new bool[colCount];
                            for (int c = colIndex; c < colIndex + span && c < colCount; c++) mask[c] = true;
                        }
                        colIndex += span;
                        colChildren++;
                    }
                    if (colChildren == 0) {
                        // Empty colgroup uses its own `span` attribute (default 1).
                        int span = GetSpanAttr(child.Element, 1);
                        if (groupCollapsed) {
                            if (mask == null) mask = new bool[colCount];
                            for (int c = colIndex; c < colIndex + span && c < colCount; c++) mask[c] = true;
                        }
                        colIndex += span;
                    }
                }
            }
            return mask;
        }

        static bool IsBoxVisibilityCollapse(BlockBox box) {
            return box?.Style != null
                && CssStringUtil.EqualsIgnoreCaseTrimmed(box.Style.Get("visibility"), "collapse");
        }

        static int GetSpanAttr(Weva.Dom.Element element, int fallback) {
            if (element == null) return fallback;
            string raw = element.GetAttribute("span");
            if (string.IsNullOrWhiteSpace(raw)) return fallback;
            if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var span)) return fallback;
            if (span < 1) return fallback;
            return span > 1000 ? 1000 : span;
        }

        // I4b: zero the widths of collapsed columns and re-emit column offsets
        // so cells in surviving columns slide left. Also adjust the table's
        // effective right edge / border-spacing accounting: per CSS Tables L3
        // §11.5 a collapsed column also drops the border-spacing that would
        // have sat next to it. We model this by leaving BorderSpacingX intact
        // (used by other passes) but skipping the spacing step over collapsed
        // tracks when rebuilding offsets.
        //
        // v1 cell handling for column-spanning cells: a cell that spans only
        // collapsed columns shrinks to zero width naturally via SumColumns()
        // (each collapsed track's width is now 0); a cell that spans a mix of
        // collapsed and visible columns keeps just the visible tracks' width,
        // which matches the spec's "the row cell's resolved width loses the
        // collapsed-track contribution" rule. Cells whose own column is
        // collapsed render at width 0 — equivalent to "skip the cell".
        static void ApplyColumnCollapse(TableBox table, bool[] mask) {
            if (table?.ColumnWidths == null || mask == null) return;
            int colCount = table.ColumnWidths.Length;
            if (mask.Length != colCount) return;
            bool any = false;
            for (int c = 0; c < colCount; c++) if (mask[c]) { any = true; break; }
            if (!any) return;

            // Zero collapsed widths.
            for (int c = 0; c < colCount; c++) if (mask[c]) table.ColumnWidths[c] = 0;

            // Recompute offsets: skip the border-spacing slot adjacent to a
            // dropped track. The original ResolveAuto/Fixed cursor pattern
            // starts at BorderSpacingX and adds (width + BorderSpacingX) after
            // each track. For collapsed tracks, we don't reserve their column
            // slot OR their trailing spacing — that's how surviving columns
            // compact leftward.
            var offsets = table.ColumnOffsets;
            if (offsets == null || offsets.Length != colCount) return;
            double cursor = table.BorderSpacingX;
            for (int c = 0; c < colCount; c++) {
                offsets[c] = cursor;
                if (!mask[c]) {
                    cursor += table.ColumnWidths[c] + table.BorderSpacingX;
                }
                // Collapsed track: don't advance cursor at all. Its offset is
                // still set (to the cursor of the next visible track) so any
                // accidental reads return a sane position rather than a stale
                // pre-collapse value.
            }
        }

        static void CollectCaptions(TableBox table, List<TableCaptionBox> outList) {
            for (int i = 0; i < table.Children.Count; i++) {
                if (table.Children[i] is TableCaptionBox c) outList.Add(c);
            }
        }

        // Returns the physical caption-side after resolving logical keywords
        // (block-start, block-end, inline-start, inline-end) against the
        // table's writing-mode per CSS Tables L3 §2.1. v1 collapses unsupported
        // side placements (right/left) to "top" since the engine only stacks
        // captions vertically.
        static string ResolveCaptionSidePhysical(TableCaptionBox cap) {
            if (cap.Style == null) return "top";
            string raw = cap.Style.Get("caption-side");
            if (string.IsNullOrEmpty(raw)) return "top";
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "top")) return "top";
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "bottom")) return "bottom";

            string wm = cap.Style.Get("writing-mode");
            bool verticalRl = CssStringUtil.EqualsIgnoreCaseTrimmed(wm, "vertical-rl")
                || CssStringUtil.EqualsIgnoreCaseTrimmed(wm, "sideways-rl");
            bool verticalLr = CssStringUtil.EqualsIgnoreCaseTrimmed(wm, "vertical-lr");
            bool sidewaysLr = CssStringUtil.EqualsIgnoreCaseTrimmed(wm, "sideways-lr");

            if (CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "block-start")
                || CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "inline-start")
                || CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "inline-end")) {
                if (verticalRl) return "right";
                if (verticalLr) return "left";
                if (sidewaysLr) return "left";
                return "top";
            }
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "block-end")) {
                if (verticalRl) return "left";
                if (verticalLr) return "right";
                if (sidewaysLr) return "right";
                return "bottom";
            }
            return "top";
        }

        // CSS Tables L3 §11: `visibility: collapse` on a <tr> hides the row AND
        // removes its space from the row stack (vs `hidden` which leaves the
        // slot intact). Painting already treats `collapse` like `hidden`, so
        // this gate only needs to suppress the row's contribution to layout.
        static bool IsRowVisibilityCollapse(TableRowBox row) {
            return row?.Style != null
                && CssStringUtil.EqualsIgnoreCaseTrimmed(row.Style.Get("visibility"), "collapse");
        }

        double ResolveExplicitTableHeight(TableBox table) {
            if (table?.Style == null) return 0;
            var parsed = table.Style.GetParsed(CssProperties.HeightId);
            if (parsed == null) return 0;
            double fs = StyleResolver.FontSizePx(table.Style, table.Parent?.Style, ctx);
            // Percentage table heights need a definite containing-block height;
            // when the parent doesn't supply one we fall through to 0 (auto).
            double? basis = (table.Parent != null && table.Parent.Height > 0)
                ? table.Parent.Height : (double?)null;
            var r = StyleResolver.ResolveLengthFromParsed(parsed, ctx, fs, basis);
            if (r.Kind == StyleResolver.LengthKind.Length) return r.Pixels;
            return 0;
        }

        double ResolveExplicitRowHeight(TableRowBox row, TableBox table) {
            if (row?.Style == null) return 0;
            var parsed = row.Style.GetParsed(CssProperties.HeightId);
            if (parsed == null) return 0;
            double fs = StyleResolver.FontSizePx(row.Style, row.Parent?.Style, ctx);
            double? basis = table.Height > 0 ? table.Height : (double?)null;
            var r = StyleResolver.ResolveLengthFromParsed(parsed, ctx, fs, basis);
            if (r.Kind == StyleResolver.LengthKind.Length) return r.Pixels;
            return 0;
        }

        double ResolveExplicitWidth(TableCellBox cell, double tableContentWidth) {
            if (cell.Style == null) return 0;
            // Per-style parsed cache: TableCellBox.Style.GetParsed yields the
            // already-built CssValue. Auto / empty produce ResolvedLength.Auto
            // (Kind != Length); only definite lengths return pixels. Cell
            // percentage widths resolve against the table's available content
            // width (CSS 2.1 §17.5.2 / Tables L3 §4).
            var parsed = cell.Style.GetParsed(CssProperties.WidthId);
            if (parsed == null) return 0;
            double fs = StyleResolver.FontSizePx(cell.Style, cell.Parent?.Style, ctx);
            var r = StyleResolver.ResolveLengthFromParsed(parsed, ctx, fs, tableContentWidth);
            if (r.Kind == StyleResolver.LengthKind.Length) return r.Pixels;
            return 0;
        }

        // Approximation of "max-content width" per CSS Sizing L3: for cells
        // whose only content is plain block descendants, this is the
        // BlockLayout-computed Width (which equals the available content
        // width of the parent). For cells with text, the max-content width
        // is a function of the longest unwrappable run; v1 returns the
        // BlockLayout Width as-is. The track resolver re-clamps anyway.
        static double MeasureMaxContentWidth(TableCellBox cell) {
            if (cell.Width > 0) return cell.Width;
            return MeasureChildrenIntrinsic(cell);
        }

        static double MeasureMinContentWidth(TableCellBox cell) {
            // v1: min-content = max of child block max-content min-widths.
            // For pure-text cells this conservatively returns 0, leading the
            // resolver to allow shrinking down to zero. The author can set
            // min-width on the cell to enforce a floor.
            if (cell.Style != null) {
                // Per-style parsed cache hit — avoids the CssValue.TryParse
                // round-trip on every track-resolver pass. Only definite
                // lengths produce a min-content floor; percentages/auto/calc
                // surface as fall-through to the implicit 0 floor that the
                // resolver expects.
                var parsed = cell.Style.GetParsed(CssProperties.MinWidthId);
                if (parsed is CssLength l && l.Unit != CssLengthUnit.Percent) {
                    return l.ToPixels(default);
                }
            }
            return 0;
        }

        static double MeasureChildrenIntrinsic(BlockBox box) {
            double max = 0;
            for (int i = 0; i < box.Children.Count; i++) {
                var c = box.Children[i];
                double w = c.Width;
                if (w > max) max = w;
            }
            return max;
        }
    }
}
