using Weva.Layout.Boxes;

namespace Weva.Layout.Grid {
    public sealed class GridBox : BlockBox {
        public bool IsInline { get; internal set; }
        public GridContainerProperties ResolvedProperties { get; internal set; }

        // Set by GridLayout when this GridBox is itself an item of a parent grid.
        // Carries the 1-based row/column extents the parent grid placed it
        // into, so a subgrid child can slice the parent's tracks against this
        // area. Default-zero (not placed by a parent grid). See PLAN §11
        // subgrid notes.
        public GridArea ParentPlacement { get; internal set; }
        public bool HasParentPlacement { get; internal set; }

        internal override void ResetForPool() {
            base.ResetForPool();
            IsInline = false;
            ResolvedProperties = default;
            ParentPlacement = default;
            HasParentPlacement = false;
        }
    }
}
