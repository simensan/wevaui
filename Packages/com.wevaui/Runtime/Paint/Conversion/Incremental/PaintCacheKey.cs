namespace Weva.Paint.Conversion.Incremental {
    public readonly struct PaintCacheKey {
        public readonly long BoxVersion;
        public readonly long StyleVersion;
        public readonly long ContextVersion;

        public PaintCacheKey(long boxVersion, long styleVersion, long contextVersion) {
            BoxVersion = boxVersion;
            StyleVersion = styleVersion;
            ContextVersion = contextVersion;
        }

        public bool Equals(PaintCacheKey other) {
            return BoxVersion == other.BoxVersion
                && StyleVersion == other.StyleVersion
                && ContextVersion == other.ContextVersion;
        }

        public override bool Equals(object obj) {
            return obj is PaintCacheKey k && Equals(k);
        }

        public override int GetHashCode() {
            unchecked {
                int h = 17;
                h = h * 31 + BoxVersion.GetHashCode();
                h = h * 31 + StyleVersion.GetHashCode();
                h = h * 31 + ContextVersion.GetHashCode();
                return h;
            }
        }

        public override string ToString() {
            return "PaintCacheKey(box=" + BoxVersion
                + ", style=" + StyleVersion
                + ", ctx=" + ContextVersion + ")";
        }

        public static bool operator ==(PaintCacheKey a, PaintCacheKey b) => a.Equals(b);
        public static bool operator !=(PaintCacheKey a, PaintCacheKey b) => !a.Equals(b);
    }
}
