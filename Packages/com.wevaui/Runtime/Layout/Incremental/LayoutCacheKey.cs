namespace Weva.Layout.Incremental {
    public readonly struct LayoutCacheKey {
        public readonly long ElementVersion;
        public readonly long ComputedStyleVersion;
        public readonly long ContainerWidthQuantized;
        public readonly long ContainerHeightQuantized;
        public readonly long LayoutContextVersion;
        public readonly long ChildAggregateVersion;

        public LayoutCacheKey(
            long elementVersion,
            long computedStyleVersion,
            long containerWidthQuantized,
            long containerHeightQuantized,
            long layoutContextVersion,
            long childAggregateVersion
        ) {
            ElementVersion = elementVersion;
            ComputedStyleVersion = computedStyleVersion;
            ContainerWidthQuantized = containerWidthQuantized;
            ContainerHeightQuantized = containerHeightQuantized;
            LayoutContextVersion = layoutContextVersion;
            ChildAggregateVersion = childAggregateVersion;
        }

        public bool Equals(LayoutCacheKey other) {
            return ElementVersion == other.ElementVersion
                && ComputedStyleVersion == other.ComputedStyleVersion
                && ContainerWidthQuantized == other.ContainerWidthQuantized
                && ContainerHeightQuantized == other.ContainerHeightQuantized
                && LayoutContextVersion == other.LayoutContextVersion
                && ChildAggregateVersion == other.ChildAggregateVersion;
        }

        public override bool Equals(object obj) {
            return obj is LayoutCacheKey k && Equals(k);
        }

        public override int GetHashCode() {
            unchecked {
                int h = 17;
                h = h * 31 + ElementVersion.GetHashCode();
                h = h * 31 + ComputedStyleVersion.GetHashCode();
                h = h * 31 + ContainerWidthQuantized.GetHashCode();
                h = h * 31 + ContainerHeightQuantized.GetHashCode();
                h = h * 31 + LayoutContextVersion.GetHashCode();
                h = h * 31 + ChildAggregateVersion.GetHashCode();
                return h;
            }
        }

        public override string ToString() {
            return "LayoutCacheKey(elem=" + ElementVersion
                + ", style=" + ComputedStyleVersion
                + ", cw=" + ContainerWidthQuantized
                + ", ch=" + ContainerHeightQuantized
                + ", ctx=" + LayoutContextVersion
                + ", kids=" + ChildAggregateVersion + ")";
        }

        public static bool operator ==(LayoutCacheKey a, LayoutCacheKey b) => a.Equals(b);
        public static bool operator !=(LayoutCacheKey a, LayoutCacheKey b) => !a.Equals(b);
    }
}
