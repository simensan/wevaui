using System.Globalization;

namespace Weva.Paint.Filters {
    public sealed class HueRotateFilter : FilterFunction {
        public double DegreesNormalized { get; }

        public override FilterKind Kind => FilterKind.HueRotate;

        public HueRotateFilter(double degrees) {
            // Normalize to [0,360).
            double d = degrees % 360.0;
            if (d < 0) d += 360.0;
            DegreesNormalized = d;
        }

        public override string ToText() {
            return "hue-rotate(" + DegreesNormalized.ToString("R", CultureInfo.InvariantCulture) + "deg)";
        }

        public override bool Equals(object obj) {
            return obj is HueRotateFilter other && other.DegreesNormalized == DegreesNormalized;
        }

        public override int GetHashCode() => DegreesNormalized.GetHashCode();
    }
}
