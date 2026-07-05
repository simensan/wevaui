namespace Weva.Animation {
    public sealed class EaseInOutEasing : EasingFunction {
        public static EaseInOutEasing Instance { get; } = new EaseInOutEasing();
        static readonly CubicBezierEasing impl = new CubicBezierEasing(0.42, 0.0, 0.58, 1.0);

        public override double Evaluate(double t) => impl.Evaluate(t);
    }
}
