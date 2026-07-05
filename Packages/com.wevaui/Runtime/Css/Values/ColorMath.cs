using System;

namespace Weva.Css.Values {
    // Shared cylindrical-color-space conversion math for HSL and HWB.
    // Single source of truth — both ColorMixer (color-mix() in CSS Color 5)
    // and Paint.GradientInterpolation (linear-gradient() interpolation per
    // CSS Images 4 §3.5) delegate here. Prior to LA6 each module held a
    // byte-identical private copy and the two could silently drift.
    //
    // All routines operate on sRGB 0..1 components. They are NOT alpha-aware;
    // callers handle alpha (and gamma encode/decode) outside.
    //
    // Spec citations:
    //   - HSL <-> RGB: CSS Color 4 §6 (and the WHATWG/Smith 1978 algorithm).
    //   - HWB <-> RGB: CSS Color 4 §7. HWB hue equals HSL hue; W and Bk are
    //     just min(R,G,B) and 1-max(R,G,B).
    //
    // Edge-case behavior (preserved verbatim from the historical copies so
    // this is a pure consolidation — no semantic drift):
    //   - Achromatic input (max-min < 1e-9) yields H=0, S=0 instead of NaN.
    //     CSS's "powerless hue" handling is left to the caller (ColorMixer
    //     and GradientInterpolation each apply their own dead-zone before
    //     calling LerpHue).
    //   - HslToRgb01 wraps H into [0,360) and clamps S,L to [0,1] before
    //     evaluating the standard 6-sector piecewise form.
    //   - HwbToRgb01 collapses to a grey ramp (w/(w+bk)) when w+bk >= 1,
    //     matching CSS Color 4 §7's normative algorithm.
    public static class ColorMath {
        // sRGB (0..1) -> HSL. H in degrees [0,360), S and L in [0,1].
        // Achromatic returns H=0, S=0.
        public static void RgbToHsl(double r, double g, double b, out double h, out double s, out double l) {
            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            l = (max + min) / 2.0;
            double d = max - min;
            if (d < 1e-9) { h = 0; s = 0; return; }
            s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
            double hh;
            if (max == r) hh = (g - b) / d + (g < b ? 6 : 0);
            else if (max == g) hh = (b - r) / d + 2;
            else hh = (r - g) / d + 4;
            h = hh * 60.0;
        }

        // HSL -> sRGB (0..1). H wraps to [0,360); S and L clamped to [0,1].
        public static void HslToRgb01(double h, double s, double l, out double r, out double g, out double b) {
            h = ((h % 360) + 360) % 360;
            if (s < 0) s = 0; if (s > 1) s = 1;
            if (l < 0) l = 0; if (l > 1) l = 1;
            double c = (1 - Math.Abs(2 * l - 1)) * s;
            double hp = h / 60.0;
            double x = c * (1 - Math.Abs((hp % 2) - 1));
            double r1 = 0, g1 = 0, b1 = 0;
            if (hp < 1) { r1 = c; g1 = x; b1 = 0; }
            else if (hp < 2) { r1 = x; g1 = c; b1 = 0; }
            else if (hp < 3) { r1 = 0; g1 = c; b1 = x; }
            else if (hp < 4) { r1 = 0; g1 = x; b1 = c; }
            else if (hp < 5) { r1 = x; g1 = 0; b1 = c; }
            else { r1 = c; g1 = 0; b1 = x; }
            double m = l - c / 2.0;
            r = r1 + m; g = g1 + m; b = b1 + m;
        }

        // sRGB (0..1) -> HWB. H in degrees [0,360); W = min channel; Bk = 1-max channel.
        public static void RgbToHwb(double r, double g, double b, out double h, out double w, out double bk) {
            RgbToHsl(r, g, b, out h, out _, out _);
            w = Math.Min(r, Math.Min(g, b));
            bk = 1.0 - Math.Max(r, Math.Max(g, b));
        }

        // HWB -> sRGB (0..1). When W+Bk >= 1 the color is achromatic; we collapse
        // to W/(W+Bk) grey per CSS Color 4 §7.
        public static void HwbToRgb01(double h, double w, double bk, out double r, out double g, out double b) {
            if (w + bk >= 1) { double grey = w / (w + bk); r = g = b = grey; return; }
            HslToRgb01(h, 1, 0.5, out r, out g, out b);
            r = r * (1 - w - bk) + w;
            g = g * (1 - w - bk) + w;
            b = b * (1 - w - bk) + w;
        }
    }
}
