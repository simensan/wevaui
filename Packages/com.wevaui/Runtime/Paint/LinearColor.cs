using System;
using System.Globalization;
using Weva.Css.Values;

namespace Weva.Paint {
    // sRGB -> linear conversion uses the proper IEC 61966-2-1 piecewise curve, not gamma 2.2,
    // because the cascade emits 8-bit sRGB byte channels and we want a single, well-defined
    // round trip. PLAN.md §9 locks linear color space.
    public readonly struct LinearColor : IEquatable<LinearColor> {
        public float R { get; }
        public float G { get; }
        public float B { get; }
        public float A { get; }

        public LinearColor(float r, float g, float b, float a) {
            R = r;
            G = g;
            B = b;
            A = a;
        }

        public static LinearColor Transparent => new LinearColor(0f, 0f, 0f, 0f);
        public static LinearColor Black => new LinearColor(0f, 0f, 0f, 1f);
        public static LinearColor White => new LinearColor(1f, 1f, 1f, 1f);

        public static LinearColor FromCssColor(CssColor color) {
            if (color == null) throw new ArgumentNullException(nameof(color));
            float r = SrgbByteToLinear(color.R);
            float g = SrgbByteToLinear(color.G);
            float b = SrgbByteToLinear(color.B);
            return new LinearColor(r, g, b, color.A);
        }

        public LinearColor Premultiplied() {
            return new LinearColor(R * A, G * A, B * A, A);
        }

        public bool Equals(LinearColor other) {
            return R == other.R && G == other.G && B == other.B && A == other.A;
        }

        public override bool Equals(object obj) {
            return obj is LinearColor other && Equals(other);
        }

        public override int GetHashCode() {
            unchecked {
                int h = R.GetHashCode();
                h = (h * 397) ^ G.GetHashCode();
                h = (h * 397) ^ B.GetHashCode();
                h = (h * 397) ^ A.GetHashCode();
                return h;
            }
        }

        public static bool operator ==(LinearColor a, LinearColor b) => a.Equals(b);
        public static bool operator !=(LinearColor a, LinearColor b) => !a.Equals(b);

        public override string ToString() {
            return string.Format(CultureInfo.InvariantCulture, "LinearColor({0:R}, {1:R}, {2:R}, {3:R})", R, G, B, A);
        }

        static float SrgbByteToLinear(byte b) {
            // Endpoints are exact in linear space — avoid float drift from the
            // pow() path (e.g. 255 -> 1.00000012f) so White round-trips to 1.0.
            if (b == 0) return 0f;
            if (b == 255) return 1f;
            float v = b / 255f;
            if (v <= 0.04045f) return v / 12.92f;
            return (float)Math.Pow((v + 0.055f) / 1.055f, 2.4f);
        }
    }
}
