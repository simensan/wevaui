using System;
using Weva.Paint;

namespace Weva.Animation {
    public interface IInterpolator<T> {
        T Lerp(T a, T b, double t);
    }

    public static class Interpolator {
        public static double Lerp(double a, double b, double t) {
            if (t <= 0) return a;
            if (t >= 1) return b;
            return a + (b - a) * t;
        }

        // Color lerp is performed in linear space (PLAN.md §9 locked decision).
        public static LinearColor LerpColor(LinearColor a, LinearColor b, double t) {
            if (t <= 0) return a;
            if (t >= 1) return b;
            float ft = (float)t;
            return new LinearColor(
                a.R + (b.R - a.R) * ft,
                a.G + (b.G - a.G) * ft,
                a.B + (b.B - a.B) * ft,
                a.A + (b.A - a.A) * ft);
        }

        public static Rect LerpRect(Rect a, Rect b, double t) {
            if (t <= 0) return a;
            if (t >= 1) return b;
            return new Rect(
                Lerp(a.X, b.X, t),
                Lerp(a.Y, b.Y, t),
                Lerp(a.Width, b.Width, t),
                Lerp(a.Height, b.Height, t));
        }

        public static double[] LerpArray(double[] a, double[] b, double t) {
            if (a == null) throw new ArgumentNullException(nameof(a));
            if (b == null) throw new ArgumentNullException(nameof(b));
            if (a.Length != b.Length) throw new ArgumentException("Array lengths must match");
            var result = new double[a.Length];
            for (int i = 0; i < a.Length; i++) {
                result[i] = Lerp(a[i], b[i], t);
            }
            return result;
        }

        public static IInterpolator<double> Double { get; } = new DoubleInterpolator();
        public static IInterpolator<LinearColor> Color { get; } = new ColorInterpolator();
        public static IInterpolator<Rect> RectInterpolator { get; } = new RectInterp();

        sealed class DoubleInterpolator : IInterpolator<double> {
            public double Lerp(double a, double b, double t) => Interpolator.Lerp(a, b, t);
        }

        sealed class ColorInterpolator : IInterpolator<LinearColor> {
            public LinearColor Lerp(LinearColor a, LinearColor b, double t) => LerpColor(a, b, t);
        }

        sealed class RectInterp : IInterpolator<Rect> {
            public Rect Lerp(Rect a, Rect b, double t) => LerpRect(a, b, t);
        }
    }
}
