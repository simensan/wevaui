using System;

namespace Weva.Paint {
    // CSS Text Decoration Module Level 3 — `text-shadow`. Per-character
    // offset+blur+color drop shadow drawn beneath the glyph layer. No
    // `inset` (inset shadows aren't meaningful for glyphs) and no
    // `spread-radius` (CSS spec only defines four lengths for box-shadow,
    // not text-shadow).
    public readonly struct TextShadow : IEquatable<TextShadow> {
        public double OffsetX { get; }
        public double OffsetY { get; }
        public double BlurRadius { get; }
        public LinearColor Color { get; }

        public TextShadow(double offsetX, double offsetY, double blurRadius, LinearColor color) {
            OffsetX = offsetX;
            OffsetY = offsetY;
            BlurRadius = blurRadius;
            Color = color;
        }

        public bool Equals(TextShadow other) {
            return OffsetX == other.OffsetX
                && OffsetY == other.OffsetY
                && BlurRadius == other.BlurRadius
                && Color == other.Color;
        }

        public override bool Equals(object obj) => obj is TextShadow other && Equals(other);

        public override int GetHashCode() {
            unchecked {
                int h = OffsetX.GetHashCode();
                h = (h * 397) ^ OffsetY.GetHashCode();
                h = (h * 397) ^ BlurRadius.GetHashCode();
                h = (h * 397) ^ Color.GetHashCode();
                return h;
            }
        }

        public static bool operator ==(TextShadow a, TextShadow b) => a.Equals(b);
        public static bool operator !=(TextShadow a, TextShadow b) => !a.Equals(b);
    }
}
