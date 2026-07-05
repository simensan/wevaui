using System;

namespace Weva.Paint {
    public readonly struct BoxShadow : IEquatable<BoxShadow> {
        public double OffsetX { get; }
        public double OffsetY { get; }
        public double BlurRadius { get; }
        public double SpreadRadius { get; }
        public LinearColor Color { get; }
        public bool Inset { get; }

        public BoxShadow(double offsetX, double offsetY, double blurRadius, double spreadRadius, LinearColor color, bool inset) {
            OffsetX = offsetX;
            OffsetY = offsetY;
            BlurRadius = blurRadius;
            SpreadRadius = spreadRadius;
            Color = color;
            Inset = inset;
        }

        public bool Equals(BoxShadow other) {
            return OffsetX == other.OffsetX
                && OffsetY == other.OffsetY
                && BlurRadius == other.BlurRadius
                && SpreadRadius == other.SpreadRadius
                && Color == other.Color
                && Inset == other.Inset;
        }

        public override bool Equals(object obj) => obj is BoxShadow other && Equals(other);

        public override int GetHashCode() {
            unchecked {
                int h = OffsetX.GetHashCode();
                h = (h * 397) ^ OffsetY.GetHashCode();
                h = (h * 397) ^ BlurRadius.GetHashCode();
                h = (h * 397) ^ SpreadRadius.GetHashCode();
                h = (h * 397) ^ Color.GetHashCode();
                h = (h * 397) ^ Inset.GetHashCode();
                return h;
            }
        }

        public static bool operator ==(BoxShadow a, BoxShadow b) => a.Equals(b);
        public static bool operator !=(BoxShadow a, BoxShadow b) => !a.Equals(b);
    }
}
