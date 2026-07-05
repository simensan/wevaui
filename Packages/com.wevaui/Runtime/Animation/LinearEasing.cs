namespace Weva.Animation {
    public sealed class LinearEasing : EasingFunction {
        public static LinearEasing Instance { get; } = new LinearEasing();

        public override double Evaluate(double t) {
            if (t < 0) return 0;
            if (t > 1) return 1;
            return t;
        }
    }
}
