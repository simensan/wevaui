using System;
using System.Collections.Generic;

namespace Weva.Animation {
    public sealed class PiecewiseLinearEasing : EasingFunction {
        // Construction of `Point` is internal to the easing parser pipeline;
        // authors interact with PiecewiseLinearEasing through EasingParser.Parse,
        // not by hand-building Point arrays.
        internal readonly struct Point {
            public double Output { get; }
            public double Input { get; }
            public Point(double output, double input) {
                Output = output;
                Input = input;
            }
        }

        readonly Point[] _points;

        // Points must be pre-sorted by Input (ascending). Inputs are in [0,1].
        internal PiecewiseLinearEasing(IReadOnlyList<Point> points) {
            if (points == null) throw new ArgumentNullException(nameof(points));
            if (points.Count < 2) {
                throw new ArgumentException("PiecewiseLinearEasing requires at least 2 points", nameof(points));
            }
            _points = new Point[points.Count];
            for (int i = 0; i < points.Count; i++) _points[i] = points[i];
        }

        internal IReadOnlyList<Point> Points => _points;

        public override double Evaluate(double t) {
            // CSS Easing L2 §3.2: clamp output to the endpoint outputs when t is outside the input domain.
            if (t <= _points[0].Input) return _points[0].Output;
            int last = _points.Length - 1;
            if (t >= _points[last].Input) return _points[last].Output;

            // Linear scan is fine — typical control-point counts are small (<10).
            for (int i = 1; i <= last; i++) {
                var b = _points[i];
                if (t <= b.Input) {
                    var a = _points[i - 1];
                    double span = b.Input - a.Input;
                    // Adjacent points with identical inputs form a step: prefer the later output.
                    if (span <= 0) return b.Output;
                    double k = (t - a.Input) / span;
                    return a.Output + k * (b.Output - a.Output);
                }
            }
            return _points[last].Output;
        }
    }
}
