using System;

namespace Weva.Text.TextCore {
    // FaceMetrics is the per-face, units-per-em-normalized metrics struct returned
    // by ITextCoreBackend.LoadFace. Multiplying any of these scalars by
    // (fontSize / UnitsPerEm) yields pixels at the requested point size.
    public readonly struct FaceMetrics : IEquatable<FaceMetrics> {
        public readonly double UnitsPerEm;
        public readonly double Ascent;
        public readonly double Descent;
        public readonly double LineGap;
        public readonly double LineHeight;

        public FaceMetrics(double unitsPerEm, double ascent, double descent, double lineGap, double lineHeight) {
            UnitsPerEm = unitsPerEm <= 0 ? 1024 : unitsPerEm;
            Ascent = ascent;
            Descent = descent;
            LineGap = lineGap;
            LineHeight = lineHeight;
        }

        public bool Equals(FaceMetrics other) {
            return UnitsPerEm == other.UnitsPerEm
                && Ascent == other.Ascent
                && Descent == other.Descent
                && LineGap == other.LineGap
                && LineHeight == other.LineHeight;
        }

        public override bool Equals(object obj) => obj is FaceMetrics other && Equals(other);

        public override int GetHashCode() {
            unchecked {
                int h = UnitsPerEm.GetHashCode();
                h = (h * 397) ^ Ascent.GetHashCode();
                h = (h * 397) ^ Descent.GetHashCode();
                h = (h * 397) ^ LineGap.GetHashCode();
                h = (h * 397) ^ LineHeight.GetHashCode();
                return h;
            }
        }

        public static bool operator ==(FaceMetrics a, FaceMetrics b) => a.Equals(b);
        public static bool operator !=(FaceMetrics a, FaceMetrics b) => !a.Equals(b);
    }
}
