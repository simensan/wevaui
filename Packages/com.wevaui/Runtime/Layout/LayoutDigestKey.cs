namespace Weva.Layout {
    // Per-element layout-input fingerprint. Two boxes that share a digest are
    // guaranteed to produce identical layout output (same X/Y/Width/Height,
    // identical descendant geometry) provided their parent's containing block
    // and the layout context version match. The struct is a strict subset of
    // the inputs LayoutCacheKey carries, exposed at Box-granularity (not
    // Element-granularity) so the engine can short-circuit per-box during a
    // layout walk rather than only at Reconcile time.
    //
    // Why a separate type from LayoutCacheKey? LayoutCacheKey lives at the
    // engine's per-Element cache and is keyed on Element identity; this struct
    // is keyed on the Box itself (a freshly-constructed Box has no entry in
    // the engine's cache yet). Having a distinct type also lets the subtree-
    // skip path consult digests without going through the Dictionary lookup —
    // we read box.CachedDigest directly.
    //
    // Field order mirrors LayoutCacheKey for cache parity. `ParentContentWidth`
    // / `ParentContentHeight` are the parent's BORDER-BOX content dimensions
    // quantized to 1/1000-pixel buckets (matches QuantizeContainer in
    // LayoutEngine.Reconcile).
    public readonly struct LayoutDigestKey {
        public readonly long ElementVersion;
        public readonly long ComputedStyleVersion;
        public readonly long ParentContentWidth;
        public readonly long ParentContentHeight;
        public readonly long LayoutContextVersion;
        public readonly long ChildAggregateVersion;

        public LayoutDigestKey(
            long elementVersion,
            long computedStyleVersion,
            long parentContentWidth,
            long parentContentHeight,
            long layoutContextVersion,
            long childAggregateVersion
        ) {
            ElementVersion = elementVersion;
            ComputedStyleVersion = computedStyleVersion;
            ParentContentWidth = parentContentWidth;
            ParentContentHeight = parentContentHeight;
            LayoutContextVersion = layoutContextVersion;
            ChildAggregateVersion = childAggregateVersion;
        }

        public bool Equals(LayoutDigestKey other) {
            return ElementVersion == other.ElementVersion
                && ComputedStyleVersion == other.ComputedStyleVersion
                && ParentContentWidth == other.ParentContentWidth
                && ParentContentHeight == other.ParentContentHeight
                && LayoutContextVersion == other.LayoutContextVersion
                && ChildAggregateVersion == other.ChildAggregateVersion;
        }

        public override bool Equals(object obj) {
            return obj is LayoutDigestKey k && Equals(k);
        }

        public override int GetHashCode() {
            unchecked {
                int h = 17;
                h = h * 31 + ElementVersion.GetHashCode();
                h = h * 31 + ComputedStyleVersion.GetHashCode();
                h = h * 31 + ParentContentWidth.GetHashCode();
                h = h * 31 + ParentContentHeight.GetHashCode();
                h = h * 31 + LayoutContextVersion.GetHashCode();
                h = h * 31 + ChildAggregateVersion.GetHashCode();
                return h;
            }
        }

        public override string ToString() {
            return "LayoutDigestKey(elem=" + ElementVersion
                + ", style=" + ComputedStyleVersion
                + ", pw=" + ParentContentWidth
                + ", ph=" + ParentContentHeight
                + ", ctx=" + LayoutContextVersion
                + ", kids=" + ChildAggregateVersion + ")";
        }

        public static bool operator ==(LayoutDigestKey a, LayoutDigestKey b) => a.Equals(b);
        public static bool operator !=(LayoutDigestKey a, LayoutDigestKey b) => !a.Equals(b);

        public static readonly LayoutDigestKey Empty = default;
    }
}
