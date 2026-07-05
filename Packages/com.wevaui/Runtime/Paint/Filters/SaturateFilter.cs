using System.Globalization;

namespace Weva.Paint.Filters {
    public sealed class SaturateFilter : FilterFunction {
        public double Amount { get; }

        public override FilterKind Kind => FilterKind.Saturate;

        public SaturateFilter(double amount) {
            // saturate may exceed 1.0 (oversaturated); only clamp negative.
            Amount = amount < 0 ? 0 : amount;
        }

        public override string ToText() {
            return "saturate(" + Amount.ToString("R", CultureInfo.InvariantCulture) + ")";
        }

        public override bool Equals(object obj) {
            return obj is SaturateFilter other && other.Amount == Amount;
        }

        public override int GetHashCode() => Amount.GetHashCode();
    }
}
