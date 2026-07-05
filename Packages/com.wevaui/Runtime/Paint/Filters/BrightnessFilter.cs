using System.Globalization;

namespace Weva.Paint.Filters {
    public sealed class BrightnessFilter : FilterFunction {
        public double Amount { get; }

        public override FilterKind Kind => FilterKind.Brightness;

        public BrightnessFilter(double amount) {
            // Per spec: brightness accepts any non-negative number; <0 is illegal. Clamp to 0.
            Amount = amount < 0 ? 0 : amount;
        }

        public override string ToText() {
            return "brightness(" + Amount.ToString("R", CultureInfo.InvariantCulture) + ")";
        }

        public override bool Equals(object obj) {
            return obj is BrightnessFilter other && other.Amount == Amount;
        }

        public override int GetHashCode() => Amount.GetHashCode();
    }
}
