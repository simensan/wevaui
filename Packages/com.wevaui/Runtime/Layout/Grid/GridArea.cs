namespace Weva.Layout.Grid {
    // 1-based grid lines per CSS Grid Layout. End lines are exclusive: an item
    // with row-start=1 and row-end=2 occupies a single row (track index 0).
    // Auto/unresolved positions are represented as 0; spans use SpanEnd/SpanStart.
    public readonly struct GridArea {
        public int RowStart { get; }
        public int RowEnd { get; }
        public int ColumnStart { get; }
        public int ColumnEnd { get; }

        public GridArea(int rowStart, int rowEnd, int columnStart, int columnEnd) {
            RowStart = rowStart;
            RowEnd = rowEnd;
            ColumnStart = columnStart;
            ColumnEnd = columnEnd;
        }

        public int RowSpan => RowEnd - RowStart;
        public int ColumnSpan => ColumnEnd - ColumnStart;
    }
}
