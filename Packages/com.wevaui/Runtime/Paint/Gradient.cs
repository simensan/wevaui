using System;
using System.Collections.Generic;
using Weva.Css.Values;

namespace Weva.Paint {
    public readonly struct GradientStop : IEquatable<GradientStop> {
        public LinearColor Color { get; }
        // Stop position. Normally a 0–1 fraction of the gradient line. When
        // IsAbsolutePx is true this holds an absolute pixel offset that hasn't
        // been resolved to a fraction yet (the gradient-line length isn't known
        // until the gradient is bound to a box/tile — see
        // BackgroundResolver.ResolveAbsoluteStops). Downstream sampling (GPU +
        // software) only ever sees resolved fractional stops.
        public double Position { get; }
        public bool IsAbsolutePx { get; }

        public GradientStop(LinearColor color, double position) {
            Color = color;
            Position = position;
            IsAbsolutePx = false;
        }

        public GradientStop(LinearColor color, double position, bool isAbsolutePx) {
            Color = color;
            Position = position;
            IsAbsolutePx = isAbsolutePx;
        }

        public bool Equals(GradientStop other) {
            return Color == other.Color && Position == other.Position
                && IsAbsolutePx == other.IsAbsolutePx;
        }

        public override bool Equals(object obj) => obj is GradientStop other && Equals(other);

        public override int GetHashCode() {
            unchecked {
                int h = (Color.GetHashCode() * 397) ^ Position.GetHashCode();
                return (h * 31) ^ IsAbsolutePx.GetHashCode();
            }
        }

        public static bool operator ==(GradientStop a, GradientStop b) => a.Equals(b);
        public static bool operator !=(GradientStop a, GradientStop b) => !a.Equals(b);
    }

    public abstract class Gradient {
        public IReadOnlyList<GradientStop> Stops { get; }
        public CssColorSpace InterpolationSpace { get; }
        public CssHueInterpolationMethod HueMethod { get; }

        // CSS gradients without an explicit `in <color-space>` interpolate
        // in sRGB. Authors can opt into linear/perceptual spaces with
        // `in <space>`.
        protected Gradient(IReadOnlyList<GradientStop> stops) : this(stops, CssColorSpace.Srgb, CssHueInterpolationMethod.Shorter) { }

        protected Gradient(IReadOnlyList<GradientStop> stops, CssColorSpace interpolationSpace)
            : this(stops, interpolationSpace, CssHueInterpolationMethod.Shorter) { }

        protected Gradient(IReadOnlyList<GradientStop> stops, CssColorSpace interpolationSpace, CssHueInterpolationMethod hueMethod) {
            Stops = stops ?? throw new ArgumentNullException(nameof(stops));
            InterpolationSpace = interpolationSpace;
            HueMethod = hueMethod;
        }

        // Sample the gradient at parametric position t (0-1, clamped). Honors InterpolationSpace
        // by lerping in the requested color space; sRGB/LinearRgb keep the existing fast path.
        public LinearColor Sample(double t) {
            if (Stops.Count == 0) return LinearColor.Transparent;
            if (Stops.Count == 1) return Stops[0].Color;
            if (t <= Stops[0].Position) return Stops[0].Color;
            if (t >= Stops[Stops.Count - 1].Position) return Stops[Stops.Count - 1].Color;
            for (int i = 0; i < Stops.Count - 1; i++) {
                var s0 = Stops[i];
                var s1 = Stops[i + 1];
                if (t >= s0.Position && t <= s1.Position) {
                    double span = s1.Position - s0.Position;
                    double local = span <= 0 ? 0 : (t - s0.Position) / span;
                    return GradientInterpolation.Interpolate(s0.Color, s1.Color, local, InterpolationSpace, HueMethod);
                }
            }
            return Stops[Stops.Count - 1].Color;
        }
    }

    public sealed class LinearGradient : Gradient {
        public double AngleDegrees { get; }
        // When true the gradient ramp tiles along its axis with period equal
        // to the largest stop position. CSS `repeating-linear-gradient(...)`
        // sets this; the regular `linear-gradient(...)` keeps the default
        // (false) so the existing 2-stop and multi-stop shader paths render
        // unchanged.
        public bool IsRepeating { get; }

        public LinearGradient(double angleDegrees, IReadOnlyList<GradientStop> stops) : base(stops) {
            AngleDegrees = angleDegrees;
            IsRepeating = false;
        }

        public LinearGradient(double angleDegrees, IReadOnlyList<GradientStop> stops, CssColorSpace space)
            : base(stops, space) {
            AngleDegrees = angleDegrees;
            IsRepeating = false;
        }

        public LinearGradient(double angleDegrees, IReadOnlyList<GradientStop> stops, CssColorSpace space, bool isRepeating)
            : base(stops, space) {
            AngleDegrees = angleDegrees;
            IsRepeating = isRepeating;
        }

        public LinearGradient(double angleDegrees, IReadOnlyList<GradientStop> stops, CssColorSpace space, bool isRepeating, CssHueInterpolationMethod hueMethod)
            : base(stops, space, hueMethod) {
            AngleDegrees = angleDegrees;
            IsRepeating = isRepeating;
        }
    }

    public sealed class ConicGradient : Gradient {
        public double FromAngleDegrees { get; }
        public double CenterX { get; }
        public double CenterY { get; }
        // CSS `repeating-conic-gradient(...)` sets this; mirrors
        // `LinearGradient.IsRepeating`. Sample/shader-side wrap is a
        // follow-up — today the flag only round-trips through the resolver.
        public bool IsRepeating { get; }

        public ConicGradient(double fromAngleDegrees, double centerX, double centerY, IReadOnlyList<GradientStop> stops)
            : base(stops) {
            FromAngleDegrees = fromAngleDegrees;
            CenterX = centerX;
            CenterY = centerY;
            IsRepeating = false;
        }

        public ConicGradient(double fromAngleDegrees, double centerX, double centerY,
                             IReadOnlyList<GradientStop> stops, CssColorSpace space) : base(stops, space) {
            FromAngleDegrees = fromAngleDegrees;
            CenterX = centerX;
            CenterY = centerY;
            IsRepeating = false;
        }

        public ConicGradient(double fromAngleDegrees, double centerX, double centerY,
                             IReadOnlyList<GradientStop> stops, CssColorSpace space, CssHueInterpolationMethod hueMethod)
            : base(stops, space, hueMethod) {
            FromAngleDegrees = fromAngleDegrees;
            CenterX = centerX;
            CenterY = centerY;
            IsRepeating = false;
        }

        public ConicGradient(double fromAngleDegrees, double centerX, double centerY,
                             IReadOnlyList<GradientStop> stops, CssColorSpace space,
                             CssHueInterpolationMethod hueMethod, bool isRepeating)
            : base(stops, space, hueMethod) {
            FromAngleDegrees = fromAngleDegrees;
            CenterX = centerX;
            CenterY = centerY;
            IsRepeating = isRepeating;
        }

        // Per-pixel sample for the software rasterizer: angle from center, normalized to [0,1)
        // starting at FromAngleDegrees. CSS conic-gradient progresses clockwise, hence the sign.
        public LinearColor SampleAtPixel(double px, double py) {
            double dx = px - CenterX;
            double dy = py - CenterY;
            double ang = Math.Atan2(dx, -dy) * (180.0 / Math.PI);
            ang = ang - FromAngleDegrees;
            ang = ((ang % 360.0) + 360.0) % 360.0;
            return Sample(ang / 360.0);
        }
    }

    public enum RadialGradientShape {
        Circle,
        Ellipse
    }

    // CSS Images 3 §3.7.1 radial-gradient sizing keywords.
    public enum RadialGradientSizing {
        FarthestCorner,
        ClosestSide,
        ClosestCorner,
        FarthestSide
    }

    public sealed class RadialGradient : Gradient {
        public double CenterX { get; }
        public double CenterY { get; }
        public double RadiusX { get; }
        public double RadiusY { get; }
        public RadialGradientShape Shape { get; }
        // CSS `repeating-radial-gradient(...)` sets this; mirrors
        // `LinearGradient.IsRepeating`. Sample/shader-side wrap is a
        // follow-up — today the flag only round-trips through the resolver.
        public bool IsRepeating { get; }

        public RadialGradient(double centerX, double centerY, double radiusX, double radiusY,
                              RadialGradientShape shape, IReadOnlyList<GradientStop> stops) : base(stops) {
            CenterX = centerX;
            CenterY = centerY;
            RadiusX = radiusX;
            RadiusY = radiusY;
            Shape = shape;
            IsRepeating = false;
        }

        public RadialGradient(double centerX, double centerY, double radiusX, double radiusY,
                              RadialGradientShape shape, IReadOnlyList<GradientStop> stops, CssColorSpace space)
            : base(stops, space) {
            CenterX = centerX;
            CenterY = centerY;
            RadiusX = radiusX;
            RadiusY = radiusY;
            Shape = shape;
            IsRepeating = false;
        }

        public RadialGradient(double centerX, double centerY, double radiusX, double radiusY,
                              RadialGradientShape shape, IReadOnlyList<GradientStop> stops, CssColorSpace space,
                              CssHueInterpolationMethod hueMethod)
            : base(stops, space, hueMethod) {
            CenterX = centerX;
            CenterY = centerY;
            RadiusX = radiusX;
            RadiusY = radiusY;
            Shape = shape;
            IsRepeating = false;
        }

        public RadialGradient(double centerX, double centerY, double radiusX, double radiusY,
                              RadialGradientShape shape, IReadOnlyList<GradientStop> stops, CssColorSpace space,
                              CssHueInterpolationMethod hueMethod, bool isRepeating)
            : base(stops, space, hueMethod) {
            CenterX = centerX;
            CenterY = centerY;
            RadiusX = radiusX;
            RadiusY = radiusY;
            Shape = shape;
            IsRepeating = isRepeating;
        }
    }
}
