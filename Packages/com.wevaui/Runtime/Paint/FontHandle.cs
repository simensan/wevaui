using System;

namespace Weva.Paint {
    public enum FontStyle {
        Normal,
        Italic,
        Oblique
    }

    public readonly struct FontHandle : IEquatable<FontHandle> {
        public string Family { get; }
        public double Size { get; }
        public int Weight { get; }
        public FontStyle Style { get; }

        public FontHandle(string family, double size, int weight, FontStyle style) {
            Family = family;
            Size = size;
            Weight = weight;
            Style = style;
        }

        public bool Equals(FontHandle other) {
            return Family == other.Family && Size == other.Size && Weight == other.Weight && Style == other.Style;
        }

        public override bool Equals(object obj) => obj is FontHandle other && Equals(other);

        public override int GetHashCode() {
            unchecked {
                int h = Family != null ? Family.GetHashCode() : 0;
                h = (h * 397) ^ Size.GetHashCode();
                h = (h * 397) ^ Weight;
                h = (h * 397) ^ (int)Style;
                return h;
            }
        }

        public static bool operator ==(FontHandle a, FontHandle b) => a.Equals(b);
        public static bool operator !=(FontHandle a, FontHandle b) => !a.Equals(b);
    }
}
