using System;

namespace Weva.Paint {
    public readonly struct CornerRadius : IEquatable<CornerRadius> {
        public double XRadius { get; }
        public double YRadius { get; }

        public CornerRadius(double radius) {
            XRadius = radius;
            YRadius = radius;
        }

        public CornerRadius(double xRadius, double yRadius) {
            XRadius = xRadius;
            YRadius = yRadius;
        }

        public bool IsZero => XRadius <= 0 && YRadius <= 0;

        public bool Equals(CornerRadius other) {
            return XRadius == other.XRadius && YRadius == other.YRadius;
        }

        public override bool Equals(object obj) => obj is CornerRadius other && Equals(other);

        public override int GetHashCode() {
            unchecked {
                return (XRadius.GetHashCode() * 397) ^ YRadius.GetHashCode();
            }
        }

        public static bool operator ==(CornerRadius a, CornerRadius b) => a.Equals(b);
        public static bool operator !=(CornerRadius a, CornerRadius b) => !a.Equals(b);
    }

    public readonly struct BorderRadii : IEquatable<BorderRadii> {
        public CornerRadius TopLeft { get; }
        public CornerRadius TopRight { get; }
        public CornerRadius BottomRight { get; }
        public CornerRadius BottomLeft { get; }

        public BorderRadii(CornerRadius topLeft, CornerRadius topRight, CornerRadius bottomRight, CornerRadius bottomLeft) {
            TopLeft = topLeft;
            TopRight = topRight;
            BottomRight = bottomRight;
            BottomLeft = bottomLeft;
        }

        public static BorderRadii Zero => new BorderRadii();

        public static BorderRadii Uniform(double radius) {
            var c = new CornerRadius(radius);
            return new BorderRadii(c, c, c, c);
        }

        public bool IsZero => TopLeft.IsZero && TopRight.IsZero && BottomRight.IsZero && BottomLeft.IsZero;

        public bool Equals(BorderRadii other) {
            return TopLeft == other.TopLeft
                && TopRight == other.TopRight
                && BottomRight == other.BottomRight
                && BottomLeft == other.BottomLeft;
        }

        public override bool Equals(object obj) => obj is BorderRadii other && Equals(other);

        public override int GetHashCode() {
            unchecked {
                int h = TopLeft.GetHashCode();
                h = (h * 397) ^ TopRight.GetHashCode();
                h = (h * 397) ^ BottomRight.GetHashCode();
                h = (h * 397) ^ BottomLeft.GetHashCode();
                return h;
            }
        }

        public static bool operator ==(BorderRadii a, BorderRadii b) => a.Equals(b);
        public static bool operator !=(BorderRadii a, BorderRadii b) => !a.Equals(b);
    }
}
