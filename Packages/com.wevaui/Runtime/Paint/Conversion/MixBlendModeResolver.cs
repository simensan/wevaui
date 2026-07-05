using Weva.Css.Cascade;
using Weva.Css.Values;

namespace Weva.Paint.Conversion {
    // CSS Compositing 1 §6.1 — resolves the cascaded `mix-blend-mode` keyword
    // to a typed MixBlendMode enum. Single-property grammar (one of 17 idents,
    // initial = `normal`) so the dispatch is a straight string-equals on the
    // CssKeyword identifier.
    //
    // All 17 spec modes are now first-class: the 13 separable RGB modes plus
    // the four HSL-based modes (`hue` / `saturation` / `color` / `luminosity`)
    // implemented in HLSL per CSS Compositing 1 §11.5..§11.8 via the
    // sRGB->HSL helper chain in Weva-Quad.shader (tracker B3c).
    internal static class MixBlendModeResolver {
        public static MixBlendMode Resolve(ComputedStyle style) {
            if (style == null) return MixBlendMode.Normal;
            var parsed = style.GetParsed(CssProperties.MixBlendModeId);
            string name = null;
            if (parsed is CssKeyword k) name = k.Identifier;
            else if (parsed is CssIdentifier id) name = id.Name;
            if (string.IsNullOrEmpty(name)) {
                // Fall through to the raw string path for un-parsed slots.
                name = style.Get(CssProperties.MixBlendModeId);
                if (string.IsNullOrEmpty(name)) return MixBlendMode.Normal;
            }
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(name, "normal")) return MixBlendMode.Normal;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(name, "multiply")) return MixBlendMode.Multiply;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(name, "screen")) return MixBlendMode.Screen;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(name, "overlay")) return MixBlendMode.Overlay;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(name, "darken")) return MixBlendMode.Darken;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(name, "lighten")) return MixBlendMode.Lighten;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(name, "color-dodge")) return MixBlendMode.ColorDodge;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(name, "color-burn")) return MixBlendMode.ColorBurn;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(name, "hard-light")) return MixBlendMode.HardLight;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(name, "soft-light")) return MixBlendMode.SoftLight;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(name, "difference")) return MixBlendMode.Difference;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(name, "exclusion")) return MixBlendMode.Exclusion;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(name, "plus-lighter")) return MixBlendMode.PlusLighter;
            // HSL-based modes — CSS Compositing 1 §11.5..§11.8. Implemented in
            // HLSL via SetLum/SetSat/ClipColor helpers in Weva-Quad.shader.
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(name, "hue")) return MixBlendMode.Hue;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(name, "saturation")) return MixBlendMode.Saturation;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(name, "color")) return MixBlendMode.Color;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(name, "luminosity")) return MixBlendMode.Luminosity;
            // Unknown keyword. Per CSS Compositing 1, invalid values are
            // treated as initial (`normal`). Stay silent to avoid spamming on
            // typo'd author styles — the cascade already drops obvious garbage.
            return MixBlendMode.Normal;
        }
    }
}
