using System.Globalization;
using Weva.Css.Cascade;
using Weva.Css.Values;

namespace Weva.Paint.Conversion {
    internal static class OpacityResolver {
        public static float ResolveOpacity(ComputedStyle style) {
            if (style == null) return 1f;
            // Hot path: read the cached parse tree directly via per-style
            // GetParsed instead of pulling the raw string + going through
            // CssValue.TryParse on every call. Falls through to a raw-
            // string float.TryParse only when the parse tree wasn't a
            // CssNumber / CssPercentage (rare; opacity is essentially
            // always a number per CSS Color L3 §3).
            var v = style.GetParsed(CssProperties.OpacityId);
            float value = 1f;
            if (v is CssNumber n) {
                value = (float)n.Value;
            } else if (v is CssPercentage p) {
                value = (float)(p.Value * 0.01);
            } else {
                string raw = style.Get(CssProperties.OpacityId);
                if (string.IsNullOrEmpty(raw)) return 1f;
                if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) value = 1f;
            }
            if (value < 0f) value = 0f;
            if (value > 1f) value = 1f;
            return value;
        }
    }
}
