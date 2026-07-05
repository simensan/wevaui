using System.Globalization;

namespace Weva.Paint.Filters {
    public sealed class ContrastFilter : FilterFunction {
        public double Amount { get; }

        public override FilterKind Kind => FilterKind.Contrast;

        public ContrastFilter(double amount) {
            Amount = amount < 0 ? 0 : amount;
        }

        public override string ToText() {
            return "contrast(" + Amount.ToString("R", CultureInfo.InvariantCulture) + ")";
        }

        public override bool Equals(object obj) {
            return obj is ContrastFilter other && other.Amount == Amount;
        }

        public override int GetHashCode() => Amount.GetHashCode();
    }
}
