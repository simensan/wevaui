using System;
using System.Collections.Generic;
using Weva.Css.Values;

namespace Weva.Layout.Grid {
    public static class GridAreasParser {
        public sealed class ParseException : Exception {
            public ParseException(string message) : base(message) { }
        }

        public sealed class AreasMap {
            public int Rows { get; }
            public int Columns { get; }
            // For each named area: 1-based grid lines (start inclusive, end exclusive).
            public IReadOnlyDictionary<string, GridArea> Areas { get; }

            public AreasMap(int rows, int columns, IReadOnlyDictionary<string, GridArea> areas) {
                Rows = rows;
                Columns = columns;
                Areas = areas;
            }

            public static readonly AreasMap Empty = new AreasMap(0, 0, new Dictionary<string, GridArea>());
        }

        public static AreasMap Parse(string text) {
            if (string.IsNullOrEmpty(text)) return AreasMap.Empty;
            string trimmed = text.Trim();
            if (CssStringUtil.EqualsIgnoreCase(trimmed, "none")) return AreasMap.Empty;

            var rows = ExtractQuotedRows(trimmed);
            if (rows.Count == 0) return AreasMap.Empty;

            int columns = -1;
            var grid = new List<List<string>>();
            for (int r = 0; r < rows.Count; r++) {
                var cells = SplitCells(rows[r]);
                if (columns < 0) columns = cells.Count;
                else if (cells.Count != columns) throw new ParseException("grid-template-areas rows must have the same number of cells (row " + (r + 1) + ")");
                grid.Add(cells);
            }
            int rowCount = grid.Count;

            // Compute bounding boxes per name.
            var bbox = new Dictionary<string, int[]>(); // [r0, r1, c0, c1]
            for (int r = 0; r < rowCount; r++) {
                for (int c = 0; c < columns; c++) {
                    string cell = grid[r][c];
                    if (cell == ".") continue;
                    if (string.IsNullOrEmpty(cell)) continue;
                    if (!bbox.TryGetValue(cell, out var rect)) {
                        bbox[cell] = new int[] { r, r, c, c };
                    } else {
                        if (r < rect[0]) rect[0] = r;
                        if (r > rect[1]) rect[1] = r;
                        if (c < rect[2]) rect[2] = c;
                        if (c > rect[3]) rect[3] = c;
                    }
                }
            }

            // Verify every cell of each bbox holds the area name (rectangularity).
            foreach (var kv in bbox) {
                var n = kv.Key;
                var rect = kv.Value;
                for (int r = rect[0]; r <= rect[1]; r++) {
                    for (int c = rect[2]; c <= rect[3]; c++) {
                        if (grid[r][c] != n) throw new ParseException("Named area '" + n + "' is not a rectangle");
                    }
                }
            }

            var areas = new Dictionary<string, GridArea>();
            foreach (var kv in bbox) {
                var rect = kv.Value;
                areas[kv.Key] = new GridArea(rect[0] + 1, rect[1] + 2, rect[2] + 1, rect[3] + 2);
            }
            return new AreasMap(rowCount, columns, areas);
        }

        static List<string> ExtractQuotedRows(string s) {
            var list = new List<string>();
            int i = 0;
            while (i < s.Length) {
                char c = s[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '"' || c == '\'') {
                    char quote = c;
                    int start = i + 1;
                    int j = start;
                    while (j < s.Length && s[j] != quote) j++;
                    if (j >= s.Length) throw new ParseException("Unterminated string in grid-template-areas");
                    list.Add(s.Substring(start, j - start));
                    i = j + 1;
                    continue;
                }
                throw new ParseException("Expected quoted string in grid-template-areas");
            }
            return list;
        }

        static List<string> SplitCells(string row) {
            var cells = new List<string>();
            int i = 0;
            int n = row.Length;
            while (i < n) {
                while (i < n && char.IsWhiteSpace(row[i])) i++;
                if (i >= n) break;
                if (row[i] == '.') {
                    int run = 0;
                    while (i < n && row[i] == '.') { run++; i++; }
                    for (int k = 0; k < run; k++) cells.Add(".");
                    continue;
                }
                int start = i;
                while (i < n && !char.IsWhiteSpace(row[i])) i++;
                cells.Add(row.Substring(start, i - start));
            }
            return cells;
        }
    }
}
