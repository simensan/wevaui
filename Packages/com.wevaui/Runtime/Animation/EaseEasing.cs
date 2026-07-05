namespace Weva.Animation {
    public sealed class EaseEasing : EasingFunction {
        public static EaseEasing Instance { get; } = new EaseEasing();
        static readonly CubicBezierEasing impl = new CubicBezierEasing(0.25, 0.1, 0.25, 1.0);

        public override double Evaluate(double t) => impl.Evaluate(t);
    }
}
