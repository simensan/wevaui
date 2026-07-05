using System.Collections.Generic;
using Weva.Css.Cascade;
using Weva.Css.Values;

namespace Weva.Paint.Conversion {
    // CSS Compositing 1 §9 — resolves the `background-blend-mode` property into
    // a per-layer list of MixBlendMode values.
    //
    // The property is a comma-separated list of <blend-mode> keywords. When the
    // list is shorter than the number of background layers, it repeats cyclically
    // (same list-matching rule as background-repeat, background-position, etc. —
    // CSS Backgrounds 3 §3.10). When the list is longer the extras are ignored.
    //
    // Spec note (CSS Compositing 1 §9): background-blend-mode blends each layer
    // with the element's OWN background layers below it and the element's
    // background-color. It does NOT involve the page backdrop — blending is
    // element-local and fully determined at paint time.
    //
    // All 16 CSS <blend-mode> keywords are now mapped (CSS Compositing 1 §6):
    // the separable modes (multiply, screen, overlay, darken, lighten,
    // color-dodge, color-burn, hard-light, soft-light, difference, exclusion)
    // and the HSL-based non-separable modes (hue, saturation, color, luminosity).
    // `plus-lighter` is a compositing operator, NOT a <blend-mode>, and is
    // therefore not valid for background-blend-mode.
    // Unknown keywords fall back to Normal (CSS Compositing 1 §7: "UA must treat
    // an unknown value as if the property had not been specified").
    internal static class BackgroundBlendModeResolver {
        // Returns null when the property is absent or all-normal (fast path: no
        // wrapping needed). Otherwise returns a list of MixBlendMode values, one
        // per declared mode keyword. The caller must use LayerAt(list, layerIndex)
        // to read a specific layer's mode with cycling, matching the n-layer count.
        public static List<MixBlendMode> Resolve(ComputedStyle style) {
            if (style == null) return null;
            string raw = style.Get(CssProperties.BackgroundBlendModeId);
            if (string.IsNullOrEmpty(raw)
                || CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "normal")) {
                return null;
            }
            // Split on top-level commas (handles pathological whitespace too).
            var parts = RawValueParser.SplitTopLevelCommas(raw);
            if (parts == null || parts.Count == 0) return null;
            List<MixBlendMode> result = null;
            for (int i = 0; i < parts.Count; i++) {
                var mode = ParseMode(parts[i]);
                // Lazily allocate only when a non-Normal entry is found.
                if (mode != MixBlendMode.Normal || result != null) {
                    if (result == null) {
                        result = new List<MixBlendMode>(parts.Count);
                        // Back-fill Normal entries before this first non-Normal.
                        for (int j = 0; j < i; j++) result.Add(MixBlendMode.Normal);
                    }
                    result.Add(mode);
                }
            }
            // If every mode was Normal the result list is never created — caller
            // treats null as "all normal, skip blending".
            return result;
        }

        // Per-layer accessor with cycling. layerIndex is 0-based and counts from
        // the TOP of the background stack (index 0 = topmost image layer, just like
        // BackgroundResolver.ResolveBackgroundLayersInto). Returns Normal when
        // modes is null, the list is empty, or the indexed slot resolves to Normal.
        public static MixBlendMode LayerAt(List<MixBlendMode> modes, int layerIndex) {
            if (modes == null || modes.Count == 0) return MixBlendMode.Normal;
            return modes[layerIndex % modes.Count];
        }

        // Returns the number of background-image layer slots (including "none"
        // slots). Used by BoxToPaintConverter to determine which layers in
        // BackgroundLayers are subject to mode cycling (all image layers, indices
        // 0..imageLayerCount-1). The background-color entry (if present, always
        // last) is NOT an image layer and always gets Normal.
        // Returns 0 when background-image is absent or a single "none".
        public static int CountImageLayers(ComputedStyle style) {
            if (style == null) return 0;
            string raw = style.Get(CssProperties.BackgroundImageId);
            if (string.IsNullOrEmpty(raw)) return 0;
            raw = raw.Trim();
            if (CssStringUtil.EqualsIgnoreCase(raw, "none")) return 0;
            // Count top-level commas + 1 = layer count.
            var parts = RawValueParser.SplitTopLevelCommas(raw);
            if (parts == null || parts.Count == 0) return 0;
            return parts.Count;
        }

        static MixBlendMode ParseMode(string raw) {
            if (string.IsNullOrEmpty(raw)) return MixBlendMode.Normal;
            string s = raw.Trim();
            // CSS Compositing 1 §6 — all 16 valid <blend-mode> keywords.
            // Ordinals are locked in MixBlendMode enum; the shader dispatches
            // on them per-fragment in Weva_FinishFragment.
            // Separable modes (§11.1..§11.12):
            if (CssStringUtil.EqualsIgnoreCase(s, "normal"))       return MixBlendMode.Normal;
            if (CssStringUtil.EqualsIgnoreCase(s, "multiply"))     return MixBlendMode.Multiply;
            if (CssStringUtil.EqualsIgnoreCase(s, "screen"))       return MixBlendMode.Screen;
            if (CssStringUtil.EqualsIgnoreCase(s, "overlay"))      return MixBlendMode.Overlay;
            if (CssStringUtil.EqualsIgnoreCase(s, "darken"))       return MixBlendMode.Darken;
            if (CssStringUtil.EqualsIgnoreCase(s, "lighten"))      return MixBlendMode.Lighten;
            if (CssStringUtil.EqualsIgnoreCase(s, "color-dodge"))  return MixBlendMode.ColorDodge;
            if (CssStringUtil.EqualsIgnoreCase(s, "color-burn"))   return MixBlendMode.ColorBurn;
            if (CssStringUtil.EqualsIgnoreCase(s, "hard-light"))   return MixBlendMode.HardLight;
            if (CssStringUtil.EqualsIgnoreCase(s, "soft-light"))   return MixBlendMode.SoftLight;
            if (CssStringUtil.EqualsIgnoreCase(s, "difference"))   return MixBlendMode.Difference;
            if (CssStringUtil.EqualsIgnoreCase(s, "exclusion"))    return MixBlendMode.Exclusion;
            // Non-separable HSL-based modes (CSS Compositing 1 §11.5..§11.8).
            // These are valid <blend-mode> keywords; the shader already handles
            // them via Weva_BlendHue/Saturation/Color/Luminosity helpers.
            if (CssStringUtil.EqualsIgnoreCase(s, "hue"))          return MixBlendMode.Hue;
            if (CssStringUtil.EqualsIgnoreCase(s, "saturation"))   return MixBlendMode.Saturation;
            if (CssStringUtil.EqualsIgnoreCase(s, "color"))        return MixBlendMode.Color;
            if (CssStringUtil.EqualsIgnoreCase(s, "luminosity"))   return MixBlendMode.Luminosity;
            // Note: `plus-lighter` (ordinal 12) is a compositing operator, NOT a
            // <blend-mode>. It is not valid for background-blend-mode (CSS
            // Compositing 1 §9 only accepts <blend-mode> values).
            // Unknown keywords → Normal (CSS Compositing 1 §7).
            return MixBlendMode.Normal;
        }
    }
}
