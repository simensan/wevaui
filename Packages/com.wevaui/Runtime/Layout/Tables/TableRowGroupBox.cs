using Weva.Layout.Boxes;

namespace Weva.Layout.Tables {
    // Wrapper for <thead> / <tbody> / <tfoot>. Per CSS Tables L3 §3, row
    // groups don't introduce a separate formatting context — they're a layout
    // hint for the table to know which rows belong together. v1 honours this
    // by laying out row groups as transparent containers: their children
    // (rows) flow directly inside the table at row-group-derived Y offsets,
    // and the row group itself receives an X/Y/Width/Height that wraps its
    // rows for paint / hit-test purposes.
    public sealed class TableRowGroupBox : BlockBox {
        // "header" | "body" | "footer". Used by TableLayout to order groups:
        // header rows first, then body, then footer, regardless of source
        // position (CSS Tables L3 §3.6).
        public string GroupKind { get; internal set; } = "body";

        internal override void ResetForPool() {
            base.ResetForPool();
            GroupKind = "body";
        }
    }
}
