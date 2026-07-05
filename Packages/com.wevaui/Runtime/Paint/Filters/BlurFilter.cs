using System.Globalization;

namespace Weva.Paint.Filters {
    public sealed class BlurFilter : FilterFunction {
        public double RadiusPx { get; }

        public override FilterKind Kind => FilterKind.Blur;

        public BlurFilter(double radiusPx) {
            // Negative blur is illegal per spec; clamp to 0 (documented v1 choice — clamp not throw).
            RadiusPx = radiusPx < 0 ? 0 : radiusPx;
        }

        public override string ToText() {
            return "blur(" + RadiusPx.ToString("R", CultureInfo.InvariantCulture) + "px)";
        }

        public override bool Equals(object obj) {
            return obj is BlurFilter other && other.RadiusPx == RadiusPx;
        }

        public override int GetHashCode() {
            return RadiusPx.GetHashCode();
        }
    }
}
