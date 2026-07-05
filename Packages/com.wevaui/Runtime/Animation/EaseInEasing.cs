namespace Weva.Animation {
    public sealed class EaseInEasing : EasingFunction {
        public static EaseInEasing Instance { get; } = new EaseInEasing();
        static readonly CubicBezierEasing impl = new CubicBezierEasing(0.42, 0.0, 1.0, 1.0);

        public override double Evaluate(double t) => impl.Evaluate(t);
    }
}
