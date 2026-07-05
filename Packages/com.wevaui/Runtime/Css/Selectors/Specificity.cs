using System;

namespace Weva.Css.Selectors {
    public readonly struct Specificity : IEquatable<Specificity>, IComparable<Specificity> {
        public int A { get; }
        public int B { get; }
        public int C { get; }

        public Specificity(int a, int b, int c) {
            A = a;
            B = b;
            C = c;
        }

        public static Specificity Zero => new(0, 0, 0);

        public static Specificity Add(Specificity x, Specificity y)
            => new(x.A + y.A, x.B + y.B, x.C + y.C);

        public static Specificity Max(Specificity x, Specificity y)
            => x.CompareTo(y) >= 0 ? x : y;

        public int CompareTo(Specificity other) {
            if (A != other.A) return A.CompareTo(other.A);
            if (B != other.B) return B.CompareTo(other.B);
            return C.CompareTo(other.C);
        }

        public bool Equals(Specificity other) => A == other.A && B == other.B && C == other.C;
        public override bool Equals(object obj) => obj is Specificity s && Equals(s);
        public override int GetHashCode() => (A * 397 ^ B) * 397 ^ C;

        public static bool operator ==(Specificity x, Specificity y) => x.Equals(y);
        public static bool operator !=(Specificity x, Specificity y) => !x.Equals(y);
        public static bool operator <(Specificity x, Specificity y) => x.CompareTo(y) < 0;
        public static bool operator >(Specificity x, Specificity y) => x.CompareTo(y) > 0;
        public static bool operator <=(Specificity x, Specificity y) => x.CompareTo(y) <= 0;
        public static bool operator >=(Specificity x, Specificity y) => x.CompareTo(y) >= 0;

        public override string ToString() => $"({A},{B},{C})";
    }
}
