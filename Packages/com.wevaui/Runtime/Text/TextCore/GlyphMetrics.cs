using System;

namespace Weva.Text.TextCore {
    public readonly struct GlyphMetrics : IEquatable<GlyphMetrics> {
        public readonly double AdvanceX;
        public readonly double BearingX;
        public readonly double BearingY;
        public readonly double Width;
        public readonly double Height;

        public GlyphMetrics(double advanceX, double bearingX, double bearingY, double width, double height) {
            AdvanceX = advanceX;
            BearingX = bearingX;
            BearingY = bearingY;
            Width = width;
            Height = height;
        }

        public bool Equals(GlyphMetrics other) {
            return AdvanceX == other.AdvanceX
                && BearingX == other.BearingX
                && BearingY == other.BearingY
                && Width == other.Width
                && Height == other.Height;
        }

        public override bool Equals(object obj) => obj is GlyphMetrics other && Equals(other);

        public override int GetHashCode() {
            unchecked {
                int h = AdvanceX.GetHashCode();
                h = (h * 397) ^ BearingX.GetHashCode();
                h = (h * 397) ^ BearingY.GetHashCode();
                h = (h * 397) ^ Width.GetHashCode();
                h = (h * 397) ^ Height.GetHashCode();
                return h;
            }
        }

        public static bool operator ==(GlyphMetrics a, GlyphMetrics b) => a.Equals(b);
        public static bool operator !=(GlyphMetrics a, GlyphMetrics b) => !a.Equals(b);

        public static GlyphMetrics Zero => new GlyphMetrics(0, 0, 0, 0, 0);
    }
}
