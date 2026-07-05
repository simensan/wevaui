using System.Globalization;

namespace Weva.Paint.Filters {
    public sealed class GrayscaleFilter : FilterFunction {
        public double Amount { get; }

        public override FilterKind Kind => FilterKind.Grayscale;

        public GrayscaleFilter(double amount) {
            // 0..1 range, clamped (documented: clamp not throw).
            if (amount < 0) amount = 0;
            if (amount > 1) amount = 1;
            Amount = amount;
        }

        public override string ToText() {
            return "grayscale(" + Amount.ToString("R", CultureInfo.InvariantCulture) + ")";
        }

        public override bool Equals(object obj) {
            return obj is GrayscaleFilter other && other.Amount == Amount;
        }

        public override int GetHashCode() => Amount.GetHashCode();
    }
}
