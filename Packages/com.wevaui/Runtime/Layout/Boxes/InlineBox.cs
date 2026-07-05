namespace Weva.Layout.Boxes {
    public class InlineBox : Box {
        // Set on the SECOND and subsequent InlineBox fragments produced for a
        // span that wraps across multiple lines (CSS 2.1 §9.4.2 / CSS Inline 3).
        // The line-1 fragment is the original element-keyed InlineBox; line-2+
        // are pool-allocated clones that share Element / Style. We mark the
        // clones so LayoutEngine.Reconcile / IsCacheable treats them like
        // anonymous boxes — without the flag, the engine's per-Element layout
        // cache collapses every fragment back to the canonical InlineBox
        // (because they all key off the same Element), leaving multiple
        // LineBoxes pointing at a single InlineBox child.
        public bool IsLineFragment { get; internal set; }

        // Set on the LAST InlineBox fragment for a wrapped span (the fragment
        // on the final line). A single-line span has only one fragment, so the
        // original InlineBox is BOTH first (IsLineFragment=false) and last
        // (IsLastFragment=true). Paint uses this to suppress the break-edge
        // border sides under box-decoration-break: slice — specifically, the
        // right edge of non-last fragments and the left edge of non-first ones.
        public bool IsLastFragment { get; internal set; }

        // CSS Fragmentation L3 §6.1 — inline-axis PBM (padding + border + margin)
        // reserved on the START edge (left in LTR) and END edge (right in LTR).
        // Resolved by InlineLayout.CollectInlineInner from the element's computed
        // style. Used by AttachInlineFragmentsToLines to expand fragment rects so
        // the box includes its PBM area, and by FinishLine to account for the
        // per-line PBM contribution in line width.
        //
        // Under slice (initial value): start PBM on the first fragment only;
        // end PBM on the last fragment only. These values represent one side only
        // (not the sum) — the caller decides which side to apply per fragment.
        // Under clone: both InlinePbmStart and InlinePbmEnd apply to every fragment.
        internal double InlinePbmStart { get; set; }
        internal double InlinePbmEnd   { get; set; }

        internal override void ResetForPool() {
            base.ResetForPool();
            IsLineFragment = false;
            IsLastFragment = false;
            InlinePbmStart = 0;
            InlinePbmEnd   = 0;
        }
    }
}
