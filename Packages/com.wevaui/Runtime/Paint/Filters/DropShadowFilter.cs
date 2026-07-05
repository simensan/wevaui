using System.Globalization;

namespace Weva.Paint.Filters {
    public sealed class DropShadowFilter : FilterFunction {
        public double OffsetX { get; }
        public double OffsetY { get; }
        public double BlurRadius { get; }
        public LinearColor Color { get; }

        public override FilterKind Kind => FilterKind.DropShadow;

        public DropShadowFilter(double offsetX, double offsetY, double blurRadius, LinearColor color) {
            OffsetX = offsetX;
            OffsetY = offsetY;
            // Negative blur is illegal — clamp to 0.
            BlurRadius = blurRadius < 0 ? 0 : blurRadius;
            Color = color;
        }

        public override string ToText() {
            var inv = CultureInfo.InvariantCulture;
            return string.Format(inv,
                "drop-shadow({0}px {1}px {2}px rgba({3:R}, {4:R}, {5:R}, {6:R}))",
                OffsetX.ToString("R", inv),
                OffsetY.ToString("R", inv),
                BlurRadius.ToString("R", inv),
                Color.R, Color.G, Color.B, Color.A);
        }

        public override bool Equals(object obj) {
            return obj is DropShadowFilter other
                && other.OffsetX == OffsetX
                && other.OffsetY == OffsetY
                && other.BlurRadius == BlurRadius
                && other.Color == Color;
        }

        public override int GetHashCode() {
            unchecked {
                int h = OffsetX.GetHashCode();
                h = (h * 397) ^ OffsetY.GetHashCode();
                h = (h * 397) ^ BlurRadius.GetHashCode();
                h = (h * 397) ^ Color.GetHashCode();
                return h;
            }
        }
    }
}
