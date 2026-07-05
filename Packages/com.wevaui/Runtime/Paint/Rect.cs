using System;
using System.Globalization;

namespace Weva.Paint {
    public readonly struct Rect : IEquatable<Rect> {
        public double X { get; }
        public double Y { get; }
        public double Width { get; }
        public double Height { get; }

        public Rect(double x, double y, double width, double height) {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        public double Right => X + Width;
        public double Bottom => Y + Height;

        public bool IsEmpty => Width <= 0 || Height <= 0;

        public static Rect Empty => new Rect(0, 0, 0, 0);

        public bool Contains(double px, double py) {
            return px >= X && px < Right && py >= Y && py < Bottom;
        }

        public Rect Intersect(Rect other) {
            double x1 = Math.Max(X, other.X);
            double y1 = Math.Max(Y, other.Y);
            double x2 = Math.Min(Right, other.Right);
            double y2 = Math.Min(Bottom, other.Bottom);
            if (x2 <= x1 || y2 <= y1) return Empty;
            return new Rect(x1, y1, x2 - x1, y2 - y1);
        }

        public bool Equals(Rect other) {
            return X == other.X && Y == other.Y && Width == other.Width && Height == other.Height;
        }

        public override bool Equals(object obj) => obj is Rect other && Equals(other);

        public override int GetHashCode() {
            unchecked {
                int h = X.GetHashCode();
                h = (h * 397) ^ Y.GetHashCode();
                h = (h * 397) ^ Width.GetHashCode();
                h = (h * 397) ^ Height.GetHashCode();
                return h;
            }
        }

        public static bool operator ==(Rect a, Rect b) => a.Equals(b);
        public static bool operator !=(Rect a, Rect b) => !a.Equals(b);

        public override string ToString() {
            return string.Format(CultureInfo.InvariantCulture, "Rect({0:R}, {1:R}, {2:R}, {3:R})", X, Y, Width, Height);
        }
    }
}
