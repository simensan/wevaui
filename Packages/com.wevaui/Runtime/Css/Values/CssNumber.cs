using System.Globalization;

namespace Weva.Css.Values {
    public sealed class CssNumber : CssValue {
        // Mutable backing so the pool can Reset() between layout passes.
        // Public surface stays read-only via the property.
        double valueField;

        public double Value => valueField;

        public override CssValueKind Kind => CssValueKind.Number;

        public CssNumber(double value) {
            valueField = value;
            Raw = value.ToString("R", CultureInfo.InvariantCulture);
        }

        public CssNumber(double value, string raw) {
            valueField = value;
            Raw = raw;
        }

        // Pool reset. Caller MUST hold the pool scope; rented CssNumber values
        // must NOT be retained past CssValuePoolScope.Dispose() — the same
        // instance will be re-used in a future pass with different value.
        // Also used by the AnimationInstance opacity-typed overlay (where the
        // instance lifetime is owned by the animator, not the pool) — calls
        // pass raw=null and rely on the lazy ToString below for stringification.
        internal void Reset(double value, string raw) {
            valueField = value;
            Raw = raw;
        }

        // Lazy raw-string materialisation: when the animation overlay mutates
        // valueField via Reset(value, null), ComputedStyle.Get falls back to
        // calling ToString() to fill its raw-string cache. We format from the
        // mutable backing field rather than the (possibly stale) Raw slot.
        public override string ToString() {
            return Raw ?? valueField.ToString("0.######", CultureInfo.InvariantCulture);
        }

        // Interned constants (always reference-stable; never reset).
        public static readonly CssNumber Zero = new CssNumber(0);
        public static readonly CssNumber One = new CssNumber(1);

        // Returns the interned instance for `value` when one exists; otherwise
        // null. The parser uses this to short-circuit allocation for the
        // common 0/1 numeric literals.
        internal static CssNumber TryIntern(double value) {
            if (value == 0) return Zero;
            if (value == 1) return One;
            return null;
        }
    }
}
