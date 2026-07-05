using System;
using System.Globalization;
using Weva.Css.Values;

namespace Weva.Css.Cascade.Shorthands {
    // Classification helpers for shorthand tokens. These are deliberately permissive
    // and string-based; the cascade just needs to route tokens to the correct longhand.
    internal static class ShorthandTokens {
        public static bool IsLength(string s) {
            if (string.IsNullOrEmpty(s)) return false;
            if (s == "0") return true;
            if (s.Length > 0 && (s[0] == '-' || s[0] == '+' || char.IsDigit(s[0]) || s[0] == '.')) {
                int i = 0;
                if (s[0] == '+' || s[0] == '-') i++;
                bool sawDigit = false;
                while (i < s.Length && char.IsDigit(s[i])) { sawDigit = true; i++; }
                if (i < s.Length && s[i] == '.') {
                    i++;
                    while (i < s.Length && char.IsDigit(s[i])) { sawDigit = true; i++; }
                }
                if (!sawDigit) return false;
                if (i == s.Length) return false;
                // Span-based unit match: avoid the per-call Substring +
                // lowered-string allocation. Each EqualsIgnoreCase call
                // walks at most 4 chars over the slice, no allocations.
                var unit = s.AsSpan(i);
                return CssStringUtil.EqualsIgnoreCase(unit, "px")
                    || CssStringUtil.EqualsIgnoreCase(unit, "em")
                    || CssStringUtil.EqualsIgnoreCase(unit, "rem")
                    || CssStringUtil.EqualsIgnoreCase(unit, "%")
                    || CssStringUtil.EqualsIgnoreCase(unit, "vh")
                    || CssStringUtil.EqualsIgnoreCase(unit, "vw")
                    || CssStringUtil.EqualsIgnoreCase(unit, "vmin")
                    || CssStringUtil.EqualsIgnoreCase(unit, "vmax")
                    || CssStringUtil.EqualsIgnoreCase(unit, "pt")
                    || CssStringUtil.EqualsIgnoreCase(unit, "pc")
                    || CssStringUtil.EqualsIgnoreCase(unit, "in")
                    || CssStringUtil.EqualsIgnoreCase(unit, "cm")
                    || CssStringUtil.EqualsIgnoreCase(unit, "mm")
                    || CssStringUtil.EqualsIgnoreCase(unit, "ch")
                    || CssStringUtil.EqualsIgnoreCase(unit, "ex");
            }
            return false;
        }

        public static bool IsZeroNumber(string s) {
            return s == "0";
        }

        public static bool IsNumber(string s) {
            if (string.IsNullOrEmpty(s)) return false;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
        }

        // Span overload so callers slicing off a unit suffix (e.g. IsPercentage
        // dropping the trailing `%`, IsTime dropping `s`/`ms`) avoid the
        // Substring allocation that the string overload would force.
        public static bool IsNumber(ReadOnlySpan<char> s) {
            if (s.Length == 0) return false;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
        }

        public static bool IsPercentage(string s) {
            return !string.IsNullOrEmpty(s) && s.EndsWith("%") && IsNumber(s.AsSpan(0, s.Length - 1));
        }

        public static bool IsLengthOrPercentage(string s) {
            return IsLength(s) || IsPercentage(s);
        }

        public static bool IsCalc(string s) {
            // Despite the name (kept for callsite stability), this accepts
            // any CSS math function — calc/clamp/min/max — so paddings and
            // other shorthands can carry `clamp(6px, .8vmin, 10px)` etc.
            // without being silently dropped by the per-edge validator.
            if (string.IsNullOrEmpty(s)) return false;
            return s.StartsWith("calc(", StringComparison.OrdinalIgnoreCase)
                || s.StartsWith("clamp(", StringComparison.OrdinalIgnoreCase)
                || s.StartsWith("min(", StringComparison.OrdinalIgnoreCase)
                || s.StartsWith("max(", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsTime(string s) {
            if (string.IsNullOrEmpty(s)) return false;
            string lower = CssStringUtil.ToLowerInvariantOrSame(s);
            if (lower.EndsWith("ms")) return IsNumber(lower.AsSpan(0, lower.Length - 2));
            if (lower.EndsWith("s")) return IsNumber(lower.AsSpan(0, lower.Length - 1));
            return false;
        }

        public static bool IsBorderStyle(string s) {
            switch (CssStringUtil.ToLowerInvariantOrSame(s)) {
                case "none": case "hidden": case "dotted": case "dashed": case "solid":
                case "double": case "groove": case "ridge": case "inset": case "outset":
                    return true;
                default:
                    return false;
            }
        }

        public static bool IsBorderWidthKeyword(string s) {
            switch (CssStringUtil.ToLowerInvariantOrSame(s)) {
                case "thin": case "medium": case "thick":
                    return true;
                default:
                    return false;
            }
        }

        public static bool IsColorFunction(string s) {
            if (string.IsNullOrEmpty(s)) return false;
            string lower = CssStringUtil.ToLowerInvariantOrSame(s);
            return lower.StartsWith("rgb(") || lower.StartsWith("rgba(") ||
                   lower.StartsWith("hsl(") || lower.StartsWith("hsla(") ||
                   lower.StartsWith("hwb(") || lower.StartsWith("oklab(") ||
                   lower.StartsWith("oklch(") || lower.StartsWith("lab(") ||
                   lower.StartsWith("lch(") || lower.StartsWith("color(") ||
                   lower.StartsWith("color-mix(");
        }

        public static bool IsHexColor(string s) {
            if (string.IsNullOrEmpty(s) || s[0] != '#') return false;
            for (int i = 1; i < s.Length; i++) {
                char c = s[i];
                if (!(char.IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
            }
            int len = s.Length - 1;
            return len == 3 || len == 4 || len == 6 || len == 8;
        }

        public static bool IsCurrentColor(string s) {
            // D8: delegates to the shared CssStringUtil helper so the token
            // check lives in exactly one place across the codebase.
            return CssStringUtil.IsCurrentColor(s);
        }

        public static bool IsTransparent(string s) {
            return string.Equals(s, "transparent", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsColor(string s) {
            if (IsHexColor(s)) return true;
            if (IsColorFunction(s)) return true;
            if (IsCurrentColor(s)) return true;
            if (IsTransparent(s)) return true;
            // Named color (best effort): recognise common ones via CssNamedColors.
            // CssNamedColors uses an OrdinalIgnoreCase dictionary, so passing the
            // raw token directly avoids the lowercased-string allocation.
            return CssColor.TryFromName(s, out _);
        }

        public static bool IsImageValue(string s) {
            if (string.IsNullOrEmpty(s)) return false;
            string lower = CssStringUtil.ToLowerInvariantOrSame(s);
            return lower.StartsWith("url(") ||
                   lower.StartsWith("linear-gradient(") ||
                   lower.StartsWith("radial-gradient(") ||
                   lower.StartsWith("conic-gradient(") ||
                   lower.StartsWith("repeating-linear-gradient(") ||
                   lower.StartsWith("repeating-radial-gradient(");
        }

        public static bool IsRepeatKeyword(string s) {
            switch (CssStringUtil.ToLowerInvariantOrSame(s)) {
                case "repeat": case "no-repeat": case "repeat-x": case "repeat-y":
                case "round": case "space":
                    return true;
                default:
                    return false;
            }
        }

        public static bool IsAttachmentKeyword(string s) {
            switch (CssStringUtil.ToLowerInvariantOrSame(s)) {
                case "scroll": case "fixed": case "local":
                    return true;
                default:
                    return false;
            }
        }

        public static bool IsBoxKeyword(string s) {
            switch (CssStringUtil.ToLowerInvariantOrSame(s)) {
                case "border-box": case "padding-box": case "content-box":
                    return true;
                default:
                    return false;
            }
        }

        public static bool IsPositionKeyword(string s) {
            switch (CssStringUtil.ToLowerInvariantOrSame(s)) {
                case "left": case "right": case "top": case "bottom": case "center":
                    return true;
                default:
                    return false;
            }
        }

        public static bool IsBackgroundSizeKeyword(string s) {
            switch (CssStringUtil.ToLowerInvariantOrSame(s)) {
                case "cover": case "contain": case "auto":
                    return true;
                default:
                    return false;
            }
        }

        public static bool IsFontStyleKeyword(string s) {
            switch (CssStringUtil.ToLowerInvariantOrSame(s)) {
                case "normal": case "italic": case "oblique":
                    return true;
                default:
                    return false;
            }
        }

        public static bool IsFontVariantKeyword(string s) {
            switch (CssStringUtil.ToLowerInvariantOrSame(s)) {
                case "normal": case "small-caps":
                    return true;
                default:
                    return false;
            }
        }

        public static bool IsFontWeightKeyword(string s) {
            switch (CssStringUtil.ToLowerInvariantOrSame(s)) {
                case "normal": case "bold": case "bolder": case "lighter":
                    return true;
            }
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) {
                return n >= 1 && n <= 1000;
            }
            return false;
        }

        public static bool IsAbsoluteFontSizeKeyword(string s) {
            switch (CssStringUtil.ToLowerInvariantOrSame(s)) {
                case "xx-small": case "x-small": case "small": case "medium":
                case "large": case "x-large": case "xx-large":
                    return true;
                default:
                    return false;
            }
        }

        public static bool IsRelativeFontSizeKeyword(string s) {
            switch (CssStringUtil.ToLowerInvariantOrSame(s)) {
                case "smaller": case "larger":
                    return true;
                default:
                    return false;
            }
        }

        public static bool IsTimingFunctionKeyword(string s) {
            switch (CssStringUtil.ToLowerInvariantOrSame(s)) {
                case "linear": case "ease": case "ease-in": case "ease-out":
                case "ease-in-out": case "step-start": case "step-end":
                    return true;
                default:
                    return false;
            }
        }

        public static bool IsTimingFunction(string s) {
            if (IsTimingFunctionKeyword(s)) return true;
            if (string.IsNullOrEmpty(s)) return false;
            string lower = CssStringUtil.ToLowerInvariantOrSame(s);
            // `linear(` is the CSS Easing L2 piecewise-linear form; recognise it
            // here so the shorthand path doesn't silently drop it (H1b). The
            // shorthand tokenizer already keeps the balanced-paren body intact,
            // so the whole `linear(...)` arg is handed to the longhand parser.
            return lower.StartsWith("cubic-bezier(")
                || lower.StartsWith("steps(")
                || lower.StartsWith("linear(");
        }

        public static bool IsAnimationDirection(string s) {
            switch (CssStringUtil.ToLowerInvariantOrSame(s)) {
                case "normal": case "reverse": case "alternate": case "alternate-reverse":
                    return true;
                default:
                    return false;
            }
        }

        public static bool IsAnimationFillMode(string s) {
            switch (CssStringUtil.ToLowerInvariantOrSame(s)) {
                case "none": case "forwards": case "backwards": case "both":
                    return true;
                default:
                    return false;
            }
        }

        public static bool IsAnimationPlayState(string s) {
            switch (CssStringUtil.ToLowerInvariantOrSame(s)) {
                case "running": case "paused":
                    return true;
                default:
                    return false;
            }
        }

        public static bool IsIterationCount(string s) {
            if (string.Equals(s, "infinite", StringComparison.OrdinalIgnoreCase)) return true;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0;
        }

        public static bool IsTextDecorationLine(string s) {
            switch (CssStringUtil.ToLowerInvariantOrSame(s)) {
                case "none": case "underline": case "overline": case "line-through": case "blink":
                    return true;
                default:
                    return false;
            }
        }

        public static bool IsTextDecorationStyle(string s) {
            switch (CssStringUtil.ToLowerInvariantOrSame(s)) {
                case "solid": case "double": case "dotted": case "dashed": case "wavy":
                    return true;
                default:
                    return false;
            }
        }
    }
}
