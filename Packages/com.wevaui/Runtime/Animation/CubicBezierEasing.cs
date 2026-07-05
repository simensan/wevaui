using System;

namespace Weva.Animation {
    public sealed class CubicBezierEasing : EasingFunction {
        public double X1 { get; }
        public double Y1 { get; }
        public double X2 { get; }
        public double Y2 { get; }

        public CubicBezierEasing(double x1, double y1, double x2, double y2) {
            X1 = x1;
            Y1 = y1;
            X2 = x2;
            Y2 = y2;
        }

        public override double Evaluate(double t) {
            if (t <= 0) return 0;
            if (t >= 1) return 1;
            // Identity bezier (P1 = P2 = (0,0)/(1,1) corresponds to linear; also handle exact match).
            if (X1 == Y1 && X2 == Y2) return t;
            double param = SolveForT(t);
            return Bezier(param, Y1, Y2);
        }

        // Standard CSS approach: the input 't' is x along the curve; solve for the curve parameter,
        // then evaluate y. Newton-Raphson with bisection fallback for stability.
        double SolveForT(double x) {
            double param = x;
            for (int i = 0; i < 8; i++) {
                double currentX = Bezier(param, X1, X2) - x;
                if (Math.Abs(currentX) < 1e-7) return param;
                double slope = BezierDerivative(param, X1, X2);
                if (Math.Abs(slope) < 1e-7) break;
                param -= currentX / slope;
            }
            // Bisection fallback.
            double lo = 0, hi = 1;
            double t2 = x;
            for (int i = 0; i < 32; i++) {
                double currentX = Bezier(t2, X1, X2);
                if (Math.Abs(currentX - x) < 1e-7) return t2;
                if (currentX < x) lo = t2;
                else hi = t2;
                t2 = (lo + hi) * 0.5;
            }
            return t2;
        }

        static double Bezier(double t, double a, double b) {
            // P0 = 0, P3 = 1; cubic Bezier with control points (a, b) on the relevant axis:
            //   B(t) = 3(1-t)^2*t*a + 3(1-t)*t^2*b + t^3
            double mt = 1 - t;
            return 3 * mt * mt * t * a + 3 * mt * t * t * b + t * t * t;
        }

        static double BezierDerivative(double t, double a, double b) {
            double mt = 1 - t;
            return 3 * mt * mt * a + 6 * mt * t * (b - a) + 3 * t * t * (1 - b);
        }
    }
}
