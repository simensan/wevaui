using System;

namespace Weva.Paint {
    public readonly struct Borders : IEquatable<Borders> {
        public BorderEdge Top { get; }
        public BorderEdge Right { get; }
        public BorderEdge Bottom { get; }
        public BorderEdge Left { get; }

        public Borders(BorderEdge top, BorderEdge right, BorderEdge bottom, BorderEdge left) {
            Top = top;
            Right = right;
            Bottom = bottom;
            Left = left;
        }

        public static Borders None => new Borders(BorderEdge.None, BorderEdge.None, BorderEdge.None, BorderEdge.None);

        public static Borders Uniform(BorderEdge edge) {
            return new Borders(edge, edge, edge, edge);
        }

        public bool IsNone =>
            IsInvisibleStyle(Top.Style) && IsInvisibleStyle(Right.Style) &&
            IsInvisibleStyle(Bottom.Style) && IsInvisibleStyle(Left.Style);

        // Both None and Hidden are invisible in the separate-borders model
        // (Hidden only wins in collapsed-border conflict resolution; the
        // resulting winning edge's style is set to Hidden to suppress drawing).
        static bool IsInvisibleStyle(BorderStyle s) => s == BorderStyle.None || s == BorderStyle.Hidden;

        public bool Equals(Borders other) {
            return Top == other.Top && Right == other.Right && Bottom == other.Bottom && Left == other.Left;
        }

        public override bool Equals(object obj) => obj is Borders other && Equals(other);

        public override int GetHashCode() {
            unchecked {
                int h = Top.GetHashCode();
                h = (h * 397) ^ Right.GetHashCode();
                h = (h * 397) ^ Bottom.GetHashCode();
                h = (h * 397) ^ Left.GetHashCode();
                return h;
            }
        }

        public static bool operator ==(Borders a, Borders b) => a.Equals(b);
        public static bool operator !=(Borders a, Borders b) => !a.Equals(b);
    }
}
