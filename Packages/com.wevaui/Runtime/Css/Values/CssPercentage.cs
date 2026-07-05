using System.Globalization;

namespace Weva.Css.Values {
    public sealed class CssPercentage : CssValue {
        // Mutable backing so the pool can Reset() between layout passes.
        // Public surface stays read-only via the property.
        double valueField;

        public double Value => valueField;

        public override CssValueKind Kind => CssValueKind.Percentage;

        public CssPercentage(double value) {
            valueField = value;
            Raw = value.ToString("R", CultureInfo.InvariantCulture) + "%";
        }

        public CssPercentage(double value, string raw) {
            valueField = value;
            Raw = raw;
        }

        // Pool reset. Caller MUST hold the pool scope; rented CssPercentage
        // values must NOT be retained past CssValuePoolScope.Dispose() — the
        // same instance will be re-used in a future pass with different value.
        internal void Reset(double value, string raw) {
            valueField = value;
            Raw = raw;
        }

        // Interned constants (always reference-stable; never reset).
        public static readonly CssPercentage Zero = new CssPercentage(0);
        public static readonly CssPercentage Hundred = new CssPercentage(100);
        public static readonly CssPercentage Fifty = new CssPercentage(50);

        // Returns the interned instance for `value` when one exists; otherwise
        // null. The parser uses this to short-circuit allocation for common
        // 0% / 50% / 100% literals.
        internal static CssPercentage TryIntern(double value) {
            if (value == 0) return Zero;
            if (value == 100) return Hundred;
            if (value == 50) return Fifty;
            return null;
        }

        public double ToPixels(LengthContext ctx) {
            if (!ctx.BasisPixels.HasValue) {
                throw new System.InvalidOperationException("Cannot resolve percentage without BasisPixels in LengthContext");
            }
            return Value * 0.01 * ctx.BasisPixels.Value;
        }
    }
}
