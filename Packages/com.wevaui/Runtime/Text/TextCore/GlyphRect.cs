using System;

namespace Weva.Text.TextCore {
    public readonly struct GlyphRect : IEquatable<GlyphRect> {
        public readonly float U0;
        public readonly float V0;
        public readonly float U1;
        public readonly float V1;

        public GlyphRect(float u0, float v0, float u1, float v1) {
            U0 = u0;
            V0 = v0;
            U1 = u1;
            V1 = v1;
        }

        public float Width => U1 - U0;
        public float Height => V1 - V0;

        public bool Equals(GlyphRect other) {
            return U0 == other.U0 && V0 == other.V0 && U1 == other.U1 && V1 == other.V1;
        }

        public override bool Equals(object obj) => obj is GlyphRect other && Equals(other);

        public override int GetHashCode() {
            unchecked {
                int h = U0.GetHashCode();
                h = (h * 397) ^ V0.GetHashCode();
                h = (h * 397) ^ U1.GetHashCode();
                h = (h * 397) ^ V1.GetHashCode();
                return h;
            }
        }

        public static bool operator ==(GlyphRect a, GlyphRect b) => a.Equals(b);
        public static bool operator !=(GlyphRect a, GlyphRect b) => !a.Equals(b);

        public static GlyphRect Empty => new GlyphRect(0, 0, 0, 0);
    }
}
