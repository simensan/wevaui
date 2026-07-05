using System.Globalization;

namespace Weva.Css.Values {
    public enum CssAngleUnit {
        Deg,
        Rad,
        Grad,
        Turn
    }

    // CSS Values 4 §6.1: <angle> dimension. Used by transform rotate / skew,
    // filter hue-rotate, conic-gradient starting angle, etc. Kept as a
    // separate type from CssLength because mixing degrees into a "length"
    // bucket forced every length consumer (BlockLayout, padding/margin
    // resolution, …) to either skip or special-case the angle units. Pool
    // doesn't manage CssAngle — the production rate is low (one rotate per
    // animation frame at most) and the type is immutable so caches can hold
    // refs across pool scopes without StableCopy concerns.
    public sealed class CssAngle : CssValue {
        // Backing fields are mutable for the in-place Reset() path used by
        // the animation overlay (see CssAnimationRunner). The public
        // accessors stay read-only; only the cascade owns the rebinding
        // contract.
        double valueField;
        CssAngleUnit unitField;
        public double Value => valueField;
        public CssAngleUnit Unit => unitField;

        public override CssValueKind Kind => CssValueKind.Angle;

        public CssAngle(double value, CssAngleUnit unit) {
            valueField = value;
            unitField = unit;
            Raw = value.ToString("R", CultureInfo.InvariantCulture) + UnitSuffix(unit);
        }

        public CssAngle(double value, CssAngleUnit unit, string raw) {
            valueField = value;
            unitField = unit;
            Raw = raw;
        }

        // In-place update for animation overlays. Callers passing raw=null
        // hand the lazy-materialisation contract to ComputedStyle.Get, which
        // reads .ToString() when a string consumer arrives. Designed for the
        // per-Tick transform-animation path where the angle changes every
        // frame but the surrounding CssFunctionCall stays stable.
        internal void Reset(double value, CssAngleUnit unit, string raw) {
            valueField = value;
            unitField = unit;
            Raw = raw;
        }

        public override string ToString() {
            return Raw ?? (valueField.ToString("0.######", CultureInfo.InvariantCulture) + UnitSuffix(unitField));
        }

        // Conversion factors per CSS Values 4 §6.1:
        //   1turn = 360deg, 1rad = 180/π deg, 1grad = 0.9deg.
        public double ToDegrees() {
            switch (Unit) {
                case CssAngleUnit.Deg: return Value;
                case CssAngleUnit.Rad: return Value * 180.0 / System.Math.PI;
                case CssAngleUnit.Grad: return Value * 0.9;
                case CssAngleUnit.Turn: return Value * 360.0;
            }
            return Value;
        }

        public static bool TryParseUnit(string lowercase, out CssAngleUnit result) {
            switch (lowercase) {
                case "deg": result = CssAngleUnit.Deg; return true;
                case "rad": result = CssAngleUnit.Rad; return true;
                case "grad": result = CssAngleUnit.Grad; return true;
                case "turn": result = CssAngleUnit.Turn; return true;
            }
            result = CssAngleUnit.Deg;
            return false;
        }

        static string UnitSuffix(CssAngleUnit u) {
            switch (u) {
                case CssAngleUnit.Deg: return "deg";
                case CssAngleUnit.Rad: return "rad";
                case CssAngleUnit.Grad: return "grad";
                case CssAngleUnit.Turn: return "turn";
            }
            return "";
        }
    }
}
