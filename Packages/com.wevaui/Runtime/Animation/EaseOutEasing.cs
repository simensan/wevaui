namespace Weva.Animation {
    public sealed class EaseOutEasing : EasingFunction {
        public static EaseOutEasing Instance { get; } = new EaseOutEasing();
        static readonly CubicBezierEasing impl = new CubicBezierEasing(0.0, 0.0, 0.58, 1.0);

        public override double Evaluate(double t) => impl.Evaluate(t);
    }
}
