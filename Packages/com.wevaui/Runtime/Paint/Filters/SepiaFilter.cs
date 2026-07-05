using System.Globalization;

namespace Weva.Paint.Filters {
    public sealed class SepiaFilter : FilterFunction {
        public double Amount { get; }

        public override FilterKind Kind => FilterKind.Sepia;

        public SepiaFilter(double amount) {
            if (amount < 0) amount = 0;
            if (amount > 1) amount = 1;
            Amount = amount;
        }

        public override string ToText() {
            return "sepia(" + Amount.ToString("R", CultureInfo.InvariantCulture) + ")";
        }

        public override bool Equals(object obj) {
            return obj is SepiaFilter other && other.Amount == Amount;
        }

        public override int GetHashCode() => Amount.GetHashCode();
    }
}
