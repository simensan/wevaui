namespace Weva.Css.Cascade {
    public readonly struct IncrementalCacheKey {
        public readonly long ElementVersion;
        public readonly long ParentStyleVersion;
        public readonly long MediaContextVersion;
        // v0.4 stored the global IElementStateProvider.Version here; v0.5 changed
        // this slot to a per-element state DIGEST computed by
        // CascadeEngine.ResolveStateDigest. Two cache keys with identical digests
        // are guaranteed to produce identical cascade matches even when the
        // provider's global Version differs, so a `:hover` flip on element X no
        // longer invalidates the cached style for element Y. Field name is kept
        // for source compatibility; semantics are documented here.
        public readonly long StateVersion;
        public readonly int StateProviderId;

        public IncrementalCacheKey(long elementVersion, long parentStyleVersion, long mediaContextVersion, long stateVersion, int stateProviderId) {
            ElementVersion = elementVersion;
            ParentStyleVersion = parentStyleVersion;
            MediaContextVersion = mediaContextVersion;
            StateVersion = stateVersion;
            StateProviderId = stateProviderId;
        }

        public bool Equals(IncrementalCacheKey other) {
            return ElementVersion == other.ElementVersion
                && ParentStyleVersion == other.ParentStyleVersion
                && MediaContextVersion == other.MediaContextVersion
                && StateVersion == other.StateVersion
                && StateProviderId == other.StateProviderId;
        }

        public override bool Equals(object obj) {
            return obj is IncrementalCacheKey k && Equals(k);
        }

        public override int GetHashCode() {
            unchecked {
                int h = 17;
                h = h * 31 + ElementVersion.GetHashCode();
                h = h * 31 + ParentStyleVersion.GetHashCode();
                h = h * 31 + MediaContextVersion.GetHashCode();
                h = h * 31 + StateVersion.GetHashCode();
                h = h * 31 + StateProviderId;
                return h;
            }
        }

        public override string ToString() {
            return "IncrementalCacheKey(elem=" + ElementVersion
                + ", parent=" + ParentStyleVersion
                + ", media=" + MediaContextVersion
                + ", state=" + StateVersion
                + ", providerId=" + StateProviderId + ")";
        }

        public static bool operator ==(IncrementalCacheKey a, IncrementalCacheKey b) => a.Equals(b);
        public static bool operator !=(IncrementalCacheKey a, IncrementalCacheKey b) => !a.Equals(b);
    }
}
