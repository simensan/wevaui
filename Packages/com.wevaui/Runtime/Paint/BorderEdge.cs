using System;

namespace Weva.Paint {
    public readonly struct BorderEdge : IEquatable<BorderEdge> {
        public BorderStyle Style { get; }
        public double Width { get; }
        public LinearColor Color { get; }

        public BorderEdge(BorderStyle style, double width, LinearColor color) {
            Style = style;
            Width = width;
            Color = color;
        }

        public static BorderEdge None => new BorderEdge(BorderStyle.None, 0, LinearColor.Transparent);

        public bool Equals(BorderEdge other) {
            return Style == other.Style && Width == other.Width && Color == other.Color;
        }

        public override bool Equals(object obj) => obj is BorderEdge other && Equals(other);

        public override int GetHashCode() {
            unchecked {
                int h = (int)Style;
                h = (h * 397) ^ Width.GetHashCode();
                h = (h * 397) ^ Color.GetHashCode();
                return h;
            }
        }

        public static bool operator ==(BorderEdge a, BorderEdge b) => a.Equals(b);
        public static bool operator !=(BorderEdge a, BorderEdge b) => !a.Equals(b);
    }
}
