namespace Weva.Layout.Grid {
    // Parsed line reference: either an explicit line index (positive or negative),
    // a named line (by name from grid-template-* line names or grid-template-areas
    // implicit lines like "header-start"), a span count, or auto.
    public readonly struct GridLineRef {
        public bool IsAuto { get; }
        public bool IsSpan { get; }
        public int Index { get; }
        public string Name { get; }

        GridLineRef(bool isAuto, bool isSpan, int index, string name) {
            IsAuto = isAuto;
            IsSpan = isSpan;
            Index = index;
            Name = name;
        }

        public static readonly GridLineRef Auto = new GridLineRef(true, false, 0, null);
        public static GridLineRef IndexValue(int idx) => new GridLineRef(false, false, idx, null);
        public static GridLineRef NameValue(string name) => new GridLineRef(false, false, 0, name);
        // `<custom-ident> <integer>` per CSS Grid L1 §8: pick the Nth line with
        // that name (1-based). Negative idx counts from the end of the list.
        public static GridLineRef NameValue(string name, int idx) => new GridLineRef(false, false, idx, name);
        public static GridLineRef Span(int n) => new GridLineRef(false, true, n, null);
        public static GridLineRef SpanName(string name) => new GridLineRef(false, true, 0, name);
    }

    public readonly struct GridItemPlacement {
        public GridLineRef RowStart { get; }
        public GridLineRef RowEnd { get; }
        public GridLineRef ColumnStart { get; }
        public GridLineRef ColumnEnd { get; }
        public string AreaName { get; }

        public GridItemPlacement(GridLineRef rowStart, GridLineRef rowEnd,
                                 GridLineRef columnStart, GridLineRef columnEnd,
                                 string areaName) {
            RowStart = rowStart;
            RowEnd = rowEnd;
            ColumnStart = columnStart;
            ColumnEnd = columnEnd;
            AreaName = areaName;
        }

        public static readonly GridItemPlacement AllAuto =
            new GridItemPlacement(GridLineRef.Auto, GridLineRef.Auto, GridLineRef.Auto, GridLineRef.Auto, null);
    }
}
