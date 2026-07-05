using System.Collections.Generic;
using System.Globalization;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Layout.Boxes;

namespace Weva.Layout.Tables {
    // Column-width and cell-placement pass for tables.
    //
    // Implements the common CSS 2.1 table cases used by authored UI:
    // automatic layout from cell intrinsic widths, fixed layout from
    // col/colgroup/first-row width hints, colspan, and rowspan occupancy.
    internal static class TableTrackResolver {
        public static void Resolve(TableBox table, List<TableRowBox> rows, double availableWidth, LayoutContext ctx) {
            int colCount = PlaceCells(rows);

            if (colCount == 0) {
                table.ColumnWidths = new double[0];
                table.ColumnOffsets = new double[0];
                return;
            }

            double interSpacing = table.BorderSpacingX * (colCount + 1);
            double avail = availableWidth - interSpacing;
            if (avail < 0) avail = 0;

            var colHints = CollectColumnHints(table, colCount, avail, ctx);
            bool fixedLayout = table.Style != null
                && CssStringUtil.EqualsIgnoreCaseTrimmed(table.Style.Get("table-layout"), "fixed")
                && availableWidth > 0;
            var widths = fixedLayout
                ? ResolveFixed(table, rows, colCount, avail, colHints, ctx)
                : ResolveAuto(rows, colCount, avail, colHints);

            var offsets = new double[colCount];
            double cursor = table.BorderSpacingX;
            for (int c = 0; c < colCount; c++) {
                offsets[c] = cursor;
                cursor += widths[c] + table.BorderSpacingX;
            }

            table.ColumnWidths = widths;
            table.ColumnOffsets = offsets;
        }

        static double[] ResolveAuto(List<TableRowBox> rows, int colCount, double avail, double[] colHints) {
            var colMin = new double[colCount];
            var colMax = new double[colCount];

            for (int r = 0; r < rows.Count; r++) {
                for (int i = 0; i < rows[r].Children.Count; i++) {
                    if (rows[r].Children[i] is TableCellBox cell) {
                        int span = cell.ColSpan > 0 ? cell.ColSpan : 1;
                        DistributeSpannedIntrinsic(cell.IntrinsicMinWidth, colMin, cell.ColumnIndex, span);
                        DistributeSpannedIntrinsic(cell.IntrinsicMaxWidth, colMax, cell.ColumnIndex, span);
                    }
                }
            }

            for (int c = 0; c < colCount; c++) {
                if (colHints != null && colHints[c] > 0) {
                    if (colHints[c] > colMin[c]) colMin[c] = colHints[c];
                    if (colHints[c] > colMax[c]) colMax[c] = colHints[c];
                }
                if (colMax[c] < colMin[c]) colMax[c] = colMin[c];
            }

            double sumMin = 0;
            double sumMax = 0;
            for (int c = 0; c < colCount; c++) {
                sumMin += colMin[c];
                sumMax += colMax[c];
            }

            var widths = new double[colCount];
            if (sumMax <= avail || sumMax <= 0) {
                double slack = avail - sumMax;
                if (slack > 0 && sumMax > 0) {
                    for (int c = 0; c < colCount; c++) {
                        widths[c] = colMax[c] + slack * (colMax[c] / sumMax);
                    }
                } else if (sumMax > 0) {
                    for (int c = 0; c < colCount; c++) widths[c] = colMax[c];
                } else {
                    double each = colCount > 0 ? avail / colCount : 0;
                    for (int c = 0; c < colCount; c++) widths[c] = each;
                }
            } else if (sumMin >= avail) {
                for (int c = 0; c < colCount; c++) widths[c] = colMin[c];
            } else {
                double slack = avail - sumMin;
                double maxMinusMin = sumMax - sumMin;
                for (int c = 0; c < colCount; c++) {
                    double range = colMax[c] - colMin[c];
                    double share = maxMinusMin > 0 ? slack * (range / maxMinusMin) : 0;
                    widths[c] = colMin[c] + share;
                }
            }
            return widths;
        }

        static double[] ResolveFixed(TableBox table, List<TableRowBox> rows, int colCount, double avail,
                                     double[] colHints, LayoutContext ctx) {
            var widths = new double[colCount];
            var set = new bool[colCount];

            for (int c = 0; c < colCount; c++) {
                if (colHints != null && colHints[c] > 0) {
                    widths[c] = colHints[c];
                    set[c] = true;
                }
            }

            var firstRow = rows.Count > 0 ? rows[0] : null;
            if (firstRow != null) {
                for (int i = 0; i < firstRow.Children.Count; i++) {
                    if (!(firstRow.Children[i] is TableCellBox cell)) continue;
                    double explicitWidth = ResolveOuterWidth(cell, table, avail, ctx);
                    if (explicitWidth <= 0) continue;
                    int start = cell.ColumnIndex;
                    int span = cell.ColSpan > 0 ? cell.ColSpan : 1;
                    if (start < 0 || start >= colCount) continue;
                    if (start + span > colCount) span = colCount - start;
                    double share = explicitWidth / span;
                    for (int c = start; c < start + span; c++) {
                        if (set[c]) continue;
                        widths[c] = share;
                        set[c] = true;
                    }
                }
            }

            double used = 0;
            int unset = 0;
            for (int c = 0; c < colCount; c++) {
                if (set[c]) used += widths[c];
                else unset++;
            }
            double remaining = avail - used;
            if (remaining < 0) remaining = 0;
            double each = unset > 0 ? remaining / unset : 0;
            for (int c = 0; c < colCount; c++) {
                if (!set[c]) widths[c] = each;
            }
            return widths;
        }

        static int PlaceCells(List<TableRowBox> rows) {
            var active = new List<int>(8);
            int colCount = 0;
            for (int r = 0; r < rows.Count; r++) {
                var row = rows[r];
                int col = 0;
                for (int i = 0; i < row.Children.Count; i++) {
                    if (!(row.Children[i] is TableCellBox cell)) continue;
                    while (col < active.Count && active[col] > 0) col++;
                    int colSpan = GetColSpan(cell);
                    int rowSpan = GetRowSpan(cell, rows.Count - r);
                    cell.ColumnIndex = col;
                    cell.ColSpan = colSpan;
                    cell.RowSpan = rowSpan;
                    EnsureActive(active, col + colSpan);
                    if (rowSpan > 1) {
                        for (int c = col; c < col + colSpan; c++) {
                            if (rowSpan > active[c]) active[c] = rowSpan;
                        }
                    }
                    col += colSpan;
                    if (col > colCount) colCount = col;
                }
                for (int c = 0; c < active.Count; c++) {
                    if (active[c] > 0) active[c]--;
                }
            }
            if (active.Count > colCount) {
                for (int c = 0; c < active.Count; c++) {
                    if (active[c] > 0 && c + 1 > colCount) colCount = c + 1;
                }
            }
            return colCount;
        }

        static double[] CollectColumnHints(TableBox table, int colCount, double availableContentWidth, LayoutContext ctx) {
            var hints = new double[colCount];
            if (table == null || colCount <= 0) return hints;
            int colIndex = 0;
            for (int i = 0; i < table.Children.Count && colIndex < colCount; i++) {
                var child = table.Children[i] as BlockBox;
                if (child?.Element == null) continue;
                string disp = StyleResolver.Display(child.Style);
                if (disp == "table-column") {
                    ApplyColumnHint(hints, ref colIndex, GetSpan(child.Element, 1), ResolveColumnWidth(child, availableContentWidth, ctx));
                } else if (disp == "table-column-group") {
                    int before = colIndex;
                    for (int j = 0; j < child.Children.Count && colIndex < colCount; j++) {
                        var colBox = child.Children[j] as BlockBox;
                        if (colBox?.Element == null) continue;
                        if (StyleResolver.Display(colBox.Style) != "table-column") continue;
                        ApplyColumnHint(hints, ref colIndex, GetSpan(colBox.Element, 1), ResolveColumnWidth(colBox, availableContentWidth, ctx));
                    }
                    if (colIndex == before) {
                        ApplyColumnHint(hints, ref colIndex, GetSpan(child.Element, 1), ResolveColumnWidth(child, availableContentWidth, ctx));
                    }
                }
            }
            return hints;
        }

        static void ApplyColumnHint(double[] hints, ref int colIndex, int span, double width) {
            if (hints == null || colIndex >= hints.Length) return;
            if (span < 1) span = 1;
            if (colIndex + span > hints.Length) span = hints.Length - colIndex;
            if (width > 0) {
                double share = width / span;
                for (int c = colIndex; c < colIndex + span; c++) {
                    if (share > hints[c]) hints[c] = share;
                }
            }
            colIndex += span;
        }

        static double ResolveColumnWidth(BlockBox box, double basis, LayoutContext ctx) {
            if (box == null) return 0;
            double fs = StyleResolver.FontSizePx(box.Style, box.Parent?.Style, ctx);
            if (box.Style != null) {
                var parsed = box.Style.GetParsed(CssProperties.WidthId);
                var r = StyleResolver.ResolveLengthFromParsed(parsed, ctx, fs, basis);
                if (r.Kind == StyleResolver.LengthKind.Length && r.Pixels > 0) return r.Pixels;
            }
            string raw = box.Element?.GetAttribute("width");
            if (string.IsNullOrWhiteSpace(raw)) return 0;
            if (double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var px) && px > 0) {
                return px;
            }
            return StyleResolver.ResolveLengthPx(raw, 0, box.Style, ctx, fs, basis);
        }

        static double ResolveOuterWidth(TableCellBox cell, TableBox table, double basis, LayoutContext ctx) {
            if (cell?.Style == null) return 0;
            double fs = StyleResolver.FontSizePx(cell.Style, cell.Parent?.Style, ctx);
            var parsed = cell.Style.GetParsed(CssProperties.WidthId);
            var r = StyleResolver.ResolveLengthFromParsed(parsed, ctx, fs, basis);
            if (r.Kind != StyleResolver.LengthKind.Length || r.Pixels <= 0) return 0;
            bool borderBox = cell.Style.Get(CssProperties.BoxSizingId) == "border-box";
            double frame = cell.PaddingLeft + cell.PaddingRight + cell.BorderLeft + cell.BorderRight;
            return borderBox ? r.Pixels : r.Pixels + frame;
        }

        static void DistributeSpannedIntrinsic(double width, double[] columns, int start, int span) {
            if (width <= 0 || columns == null || columns.Length == 0) return;
            if (start < 0 || start >= columns.Length) return;
            int count = span;
            if (count < 1) count = 1;
            if (start + count > columns.Length) count = columns.Length - start;
            double share = width / count;
            for (int c = start; c < start + count; c++) {
                if (share > columns[c]) columns[c] = share;
            }
        }

        static int GetColSpan(TableCellBox cell) {
            return GetSpan(cell?.Element, 1, 1000);
        }

        static int GetRowSpan(TableCellBox cell, int rowsRemaining) {
            if (cell?.Element == null) return 1;
            string raw = cell.Element.GetAttribute("rowspan");
            if (string.IsNullOrWhiteSpace(raw)) return 1;
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var span)) return 1;
            if (span == 0) return rowsRemaining > 0 ? rowsRemaining : 1;
            if (span < 1) return 1;
            if (span > 65534) span = 65534;
            return rowsRemaining > 0 && span > rowsRemaining ? rowsRemaining : span;
        }

        static int GetSpan(Weva.Dom.Element element, int fallback, int max = 1000) {
            if (element == null) return fallback;
            string raw = element.GetAttribute("span");
            if (string.IsNullOrWhiteSpace(raw)) raw = element.GetAttribute("colspan");
            if (string.IsNullOrWhiteSpace(raw)) return fallback;
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var span)) return fallback;
            if (span < 1) return fallback;
            return span > max ? max : span;
        }

        static void EnsureActive(List<int> active, int count) {
            while (active.Count < count) active.Add(0);
        }
    }
}
