using System.Globalization;

namespace Weva.Paint.Filters {
    public sealed class InvertFilter : FilterFunction {
        public double Amount { get; }

        public override FilterKind Kind => FilterKind.Invert;

        public InvertFilter(double amount) {
            if (amount < 0) amount = 0;
            if (amount > 1) amount = 1;
            Amount = amount;
        }

        public override string ToText() {
            return "invert(" + Amount.ToString("R", CultureInfo.InvariantCulture) + ")";
        }

        public override bool Equals(object obj) {
            return obj is InvertFilter other && other.Amount == Amount;
        }

        public override int GetHashCode() => Amount.GetHashCode();
    }
}
