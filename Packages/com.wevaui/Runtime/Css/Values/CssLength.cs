using System;
using System.Globalization;

namespace Weva.Css.Values {
    public sealed class CssLength : CssValue {
        // Backing fields are mutable so CssValuePool can Reset() pooled instances
        // between layout passes without allocating a fresh wrapper. The public
        // surface remains read-only via the get-only properties; Reset is
        // internal, gated by the pool.
        double valueField;
        CssLengthUnit unitField;

        public double Value => valueField;
        public CssLengthUnit Unit => unitField;

        public override CssValueKind Kind => CssValueKind.Length;

        public CssLength(double value, CssLengthUnit unit) {
            valueField = value;
            unitField = unit;
            Raw = value.ToString("R", CultureInfo.InvariantCulture) + UnitSuffix(unit);
        }

        public CssLength(double value, CssLengthUnit unit, string raw) {
            valueField = value;
            unitField = unit;
            Raw = raw;
        }

        // Pool reset. Caller MUST hold the pool scope; rented CssLength values
        // must NOT be retained past CssValuePoolScope.Dispose() — the same
        // instance will be re-used in a future pass with different (value, unit).
        // Also used by the animation typed-transform overlay where instance
        // ownership is the animator's (not the pool); callers pass raw=null
        // and the lazy ToString below materialises on demand.
        internal void Reset(double value, CssLengthUnit unit, string raw) {
            valueField = value;
            unitField = unit;
            Raw = raw;
        }

        // Lazy raw-string materialisation. Mirrors CssAngle/CssNumber so the
        // animation typed overlay can Reset() with raw=null without the
        // downstream ComputedStyle.Get fallback returning an empty string.
        public override string ToString() {
            return Raw ?? (valueField.ToString("0.######", CultureInfo.InvariantCulture) + UnitSuffix(unitField));
        }

        // Interned constants. Zero/Auto/Empty are referenced from multiple
        // places (the parser fast path, layout fallbacks, animation seeding) so
        // returning the same instance is reference-stable across the whole
        // program lifetime — never reset, never returned to the pool.
        public static readonly CssLength Zero = new CssLength(0, CssLengthUnit.Px);
        public static readonly CssLength Empty = new CssLength(0, CssLengthUnit.Px);

        // Common-pixel cache for integer 0..256 in px. Hits in hot paths like
        // 1px borders, 2/4/8/16 padding, 100/200/etc. width literals. Identity-
        // equal across calls so consumers using ReferenceEquals can fast-path.
        // IMPORTANT: slot 0 aliases CssLength.Zero so parsing "0px" and using
        // CssLength.Zero converge on the same reference — code that compares
        // with ReferenceEquals(x, CssLength.Zero) relies on this.
        const int IntegerPxCacheMax = 256;
        static readonly CssLength[] IntegerPxCache = BuildIntegerPxCache();

        static CssLength[] BuildIntegerPxCache() {
            var arr = new CssLength[IntegerPxCacheMax + 1];
            arr[0] = Zero;
            for (int i = 1; i <= IntegerPxCacheMax; i++) {
                arr[i] = new CssLength(i, CssLengthUnit.Px);
            }
            return arr;
        }

        // Returns the interned 0..256 integer-pixel CssLength when value is
        // exactly representable; otherwise allocates a fresh instance. Useful
        // for code paths outside the pool scope that still want to amortize
        // common-case allocations.
        public static CssLength Px(double value) {
            if (value >= 0 && value <= IntegerPxCacheMax) {
                int i = (int)value;
                if ((double)i == value) return IntegerPxCache[i];
            }
            return new CssLength(value, CssLengthUnit.Px);
        }

        // Returns the interned instance for (value, unit) when one exists;
        // otherwise null. The parser uses this to short-circuit allocation when
        // an exact match is found before falling back to the pool.
        internal static CssLength TryIntern(double value, CssLengthUnit unit) {
            if (unit == CssLengthUnit.Px && value >= 0 && value <= IntegerPxCacheMax) {
                int i = (int)value;
                if ((double)i == value) return IntegerPxCache[i];
            }
            return null;
        }

        public double ToPixels(LengthContext ctx) {
            double dpi = ctx.DpiPixelsPerInch <= 0 ? 96 : ctx.DpiPixelsPerInch;
            switch (Unit) {
                case CssLengthUnit.Px: return Value;
                case CssLengthUnit.Em: return Value * ctx.BaseFontSizePx;
                case CssLengthUnit.Rem: return Value * ctx.RootFontSizePx;
                case CssLengthUnit.Percent:
                    if (!ctx.BasisPixels.HasValue) {
                        throw new InvalidOperationException("Cannot resolve percent length without BasisPixels in LengthContext");
                    }
                    return Value * 0.01 * ctx.BasisPixels.Value;
                case CssLengthUnit.Vh: return Value * 0.01 * ctx.ViewportHeightPx;
                case CssLengthUnit.Vw: return Value * 0.01 * ctx.ViewportWidthPx;
                case CssLengthUnit.Vmin: return Value * 0.01 * Math.Min(ctx.ViewportWidthPx, ctx.ViewportHeightPx);
                case CssLengthUnit.Vmax: return Value * 0.01 * Math.Max(ctx.ViewportWidthPx, ctx.ViewportHeightPx);
                case CssLengthUnit.Pt: return Value * (dpi / 72.0);
                case CssLengthUnit.Pc: return Value * 12.0 * (dpi / 72.0);
                case CssLengthUnit.In: return Value * dpi;
                case CssLengthUnit.Cm: return Value * (dpi / 2.54);
                case CssLengthUnit.Mm: return Value * (dpi / 25.4);
                case CssLengthUnit.Ch: return Value * 0.5 * ctx.BaseFontSizePx;
                case CssLengthUnit.Ex: return Value * 0.5 * ctx.BaseFontSizePx;
                case CssLengthUnit.Cap: return Value * 0.7 * ctx.BaseFontSizePx;
                case CssLengthUnit.Ic: return Value * ctx.BaseFontSizePx;
                case CssLengthUnit.Lh: return Value * (ctx.LineHeightPx > 0 ? ctx.LineHeightPx : ctx.BaseFontSizePx * 1.2);
                case CssLengthUnit.Rlh: return Value * (ctx.RootLineHeightPx > 0 ? ctx.RootLineHeightPx : ctx.RootFontSizePx * 1.2);
                case CssLengthUnit.Svw:
                case CssLengthUnit.Lvw:
                case CssLengthUnit.Dvw: return Value * 0.01 * ctx.ViewportWidthPx;
                case CssLengthUnit.Svh:
                case CssLengthUnit.Lvh:
                case CssLengthUnit.Dvh: return Value * 0.01 * ctx.ViewportHeightPx;
            }
            return Value;
        }

        public static bool TryParseUnit(string unit, out CssLengthUnit result) {
            switch (unit) {
                case "px": result = CssLengthUnit.Px; return true;
                case "em": result = CssLengthUnit.Em; return true;
                case "rem": result = CssLengthUnit.Rem; return true;
                case "vh": result = CssLengthUnit.Vh; return true;
                case "vw": result = CssLengthUnit.Vw; return true;
                case "vmin": result = CssLengthUnit.Vmin; return true;
                case "vmax": result = CssLengthUnit.Vmax; return true;
                case "pt": result = CssLengthUnit.Pt; return true;
                case "pc": result = CssLengthUnit.Pc; return true;
                case "in": result = CssLengthUnit.In; return true;
                case "cm": result = CssLengthUnit.Cm; return true;
                case "mm": result = CssLengthUnit.Mm; return true;
                case "ch": result = CssLengthUnit.Ch; return true;
                case "ex": result = CssLengthUnit.Ex; return true;
                case "cap": result = CssLengthUnit.Cap; return true;
                case "ic": result = CssLengthUnit.Ic; return true;
                case "lh": result = CssLengthUnit.Lh; return true;
                case "rlh": result = CssLengthUnit.Rlh; return true;
                case "svw": result = CssLengthUnit.Svw; return true;
                case "lvw": result = CssLengthUnit.Lvw; return true;
                case "dvw": result = CssLengthUnit.Dvw; return true;
                case "svh": result = CssLengthUnit.Svh; return true;
                case "lvh": result = CssLengthUnit.Lvh; return true;
                case "dvh": result = CssLengthUnit.Dvh; return true;
            }
            result = CssLengthUnit.Px;
            return false;
        }

        static string UnitSuffix(CssLengthUnit u) {
            switch (u) {
                case CssLengthUnit.Px: return "px";
                case CssLengthUnit.Em: return "em";
                case CssLengthUnit.Rem: return "rem";
                case CssLengthUnit.Percent: return "%";
                case CssLengthUnit.Vh: return "vh";
                case CssLengthUnit.Vw: return "vw";
                case CssLengthUnit.Vmin: return "vmin";
                case CssLengthUnit.Vmax: return "vmax";
                case CssLengthUnit.Pt: return "pt";
                case CssLengthUnit.Pc: return "pc";
                case CssLengthUnit.In: return "in";
                case CssLengthUnit.Cm: return "cm";
                case CssLengthUnit.Mm: return "mm";
                case CssLengthUnit.Ch: return "ch";
                case CssLengthUnit.Ex: return "ex";
                case CssLengthUnit.Cap: return "cap";
                case CssLengthUnit.Ic: return "ic";
                case CssLengthUnit.Lh: return "lh";
                case CssLengthUnit.Rlh: return "rlh";
                case CssLengthUnit.Svw: return "svw";
                case CssLengthUnit.Lvw: return "lvw";
                case CssLengthUnit.Dvw: return "dvw";
                case CssLengthUnit.Svh: return "svh";
                case CssLengthUnit.Lvh: return "lvh";
                case CssLengthUnit.Dvh: return "dvh";
            }
            return "";
        }
    }
}
