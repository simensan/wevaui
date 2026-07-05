using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Layout.Boxes;

namespace Weva.Layout.Floats {
    public static class FloatExtensions {
        public static FloatType ReadFloatType(this Box box) {
            return ParseFloatType(box?.Style?.Get(CssProperties.FloatId));
        }

        // Allocation-free keyword dispatch. `raw.Trim().ToLowerInvariant()`
        // copied the string twice every time these were called — once per box
        // per layout pass for ReadFloatType / ReadClearType, which adds up on
        // float-heavy layouts. CssStringUtil.EqualsIgnoreCaseTrimmed compares
        // against a known lowercase literal without trimming or copying.
        public static FloatType ParseFloatType(string raw) {
            if (string.IsNullOrEmpty(raw)) return FloatType.None;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "left")) return FloatType.Left;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "right")) return FloatType.Right;
            // CSS Logical Properties L1 §4.1: `inline-start` / `inline-end`
            // alias to left/right in horizontal-tb LTR (the only writing
            // mode we support).
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "inline-start")) return FloatType.Left;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "inline-end")) return FloatType.Right;
            return FloatType.None;
        }

        public static ClearType ReadClearType(this Box box) {
            return ParseClearType(box?.Style?.Get(CssProperties.ClearId));
        }

        public static ClearType ParseClearType(string raw) {
            if (string.IsNullOrEmpty(raw)) return ClearType.None;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "left")) return ClearType.Left;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "right")) return ClearType.Right;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "both")) return ClearType.Both;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "inline-start")) return ClearType.Left;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "inline-end")) return ClearType.Right;
            return ClearType.None;
        }
    }
}
