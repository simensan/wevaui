using System;
using NUnit.Framework;
using Weva.Css.Values;

namespace Weva.Tests.Css.Values {
    // Coverage for the consolidated ColorMath helpers (LA6 — extracted from
    // byte-identical private copies in ColorMixer and GradientInterpolation).
    // Pins the spec-mandated canonical cases (pure hues at H=0/120/240,
    // achromatic HWB collapse, RgbToHsl round-trip) so the consolidated
    // implementation can't silently drift from CSS Color 4 §6/§7.
    public class ColorMathTests {
        const double Eps = 1e-6;

        // ----- HslToRgb01: canonical pure hues -----

        [Test]
        public void HslToRgb01_pure_red_at_hue_0() {
            // CSS Color 4 §6: hsl(0, 100%, 50%) == sRGB red (1,0,0).
            ColorMath.HslToRgb01(0, 1, 0.5, out double r, out double g, out double b);
            Assert.That(r, Is.EqualTo(1.0).Within(Eps));
            Assert.That(g, Is.EqualTo(0.0).Within(Eps));
            Assert.That(b, Is.EqualTo(0.0).Within(Eps));
        }

        [Test]
        public void HslToRgb01_pure_green_at_hue_120() {
            // hsl(120, 100%, 50%) == sRGB green (0,1,0).
            ColorMath.HslToRgb01(120, 1, 0.5, out double r, out double g, out double b);
            Assert.That(r, Is.EqualTo(0.0).Within(Eps));
            Assert.That(g, Is.EqualTo(1.0).Within(Eps));
            Assert.That(b, Is.EqualTo(0.0).Within(Eps));
        }

        [Test]
        public void HslToRgb01_pure_blue_at_hue_240() {
            // hsl(240, 100%, 50%) == sRGB blue (0,0,1).
            ColorMath.HslToRgb01(240, 1, 0.5, out double r, out double g, out double b);
            Assert.That(r, Is.EqualTo(0.0).Within(Eps));
            Assert.That(g, Is.EqualTo(0.0).Within(Eps));
            Assert.That(b, Is.EqualTo(1.0).Within(Eps));
        }

        [Test]
        public void HslToRgb01_wraps_negative_hue() {
            // -360deg should be the same as 0deg (pure red at S=1, L=0.5).
            ColorMath.HslToRgb01(-360, 1, 0.5, out double r, out double g, out double b);
            Assert.That(r, Is.EqualTo(1.0).Within(Eps));
            Assert.That(g, Is.EqualTo(0.0).Within(Eps));
            Assert.That(b, Is.EqualTo(0.0).Within(Eps));
        }

        // ----- HwbToRgb01: achromatic collapse -----

        [Test]
        public void HwbToRgb01_achromatic_when_w_plus_bk_equals_one_collapses_to_grey() {
            // CSS Color 4 §7: when w+bk >= 1, color is grey w/(w+bk) and hue
            // is powerless. hwb(180 30% 70%) -> grey 0.3.
            ColorMath.HwbToRgb01(180, 0.3, 0.7, out double r, out double g, out double b);
            Assert.That(r, Is.EqualTo(0.3).Within(Eps));
            Assert.That(g, Is.EqualTo(0.3).Within(Eps));
            Assert.That(b, Is.EqualTo(0.3).Within(Eps));
        }

        [Test]
        public void HwbToRgb01_achromatic_grey_independent_of_hue() {
            // For w+bk>=1, R,G,B must be equal regardless of hue.
            ColorMath.HwbToRgb01(0, 0.5, 0.5, out double r0, out double g0, out double b0);
            ColorMath.HwbToRgb01(120, 0.5, 0.5, out double r1, out double g1, out double b1);
            ColorMath.HwbToRgb01(240, 0.5, 0.5, out double r2, out double g2, out double b2);
            Assert.That(r0, Is.EqualTo(g0).Within(Eps));
            Assert.That(g0, Is.EqualTo(b0).Within(Eps));
            Assert.That(r0, Is.EqualTo(r1).Within(Eps));
            Assert.That(r0, Is.EqualTo(r2).Within(Eps));
        }

        // ----- RgbToHsl: round-trip via HslToRgb01 -----

        static void AssertRoundTrip(double r, double g, double b) {
            ColorMath.RgbToHsl(r, g, b, out double h, out double s, out double l);
            ColorMath.HslToRgb01(h, s, l, out double r2, out double g2, out double b2);
            Assert.That(r2, Is.EqualTo(r).Within(1e-9), $"R round-trip ({r},{g},{b})");
            Assert.That(g2, Is.EqualTo(g).Within(1e-9), $"G round-trip ({r},{g},{b})");
            Assert.That(b2, Is.EqualTo(b).Within(1e-9), $"B round-trip ({r},{g},{b})");
        }

        [Test]
        public void RgbToHsl_round_trips_canonical_colors() {
            AssertRoundTrip(1, 0, 0);     // red
            AssertRoundTrip(0, 1, 0);     // green
            AssertRoundTrip(0, 0, 1);     // blue
            AssertRoundTrip(1, 1, 0);     // yellow
            AssertRoundTrip(0, 1, 1);     // cyan
            AssertRoundTrip(1, 0, 1);     // magenta
            AssertRoundTrip(0.25, 0.5, 0.75); // arbitrary mid
            AssertRoundTrip(0.7, 0.3, 0.1);   // arbitrary warm
        }

        [Test]
        public void RgbToHsl_achromatic_yields_zero_hue_zero_saturation() {
            // Grey input: hue is undefined; we use the well-known (h=0,s=0) convention
            // (preserved verbatim from the historical private copies).
            ColorMath.RgbToHsl(0.5, 0.5, 0.5, out double h, out double s, out double l);
            Assert.That(h, Is.EqualTo(0).Within(Eps));
            Assert.That(s, Is.EqualTo(0).Within(Eps));
            Assert.That(l, Is.EqualTo(0.5).Within(Eps));
        }

        // ----- RgbToHwb: spec definition -----

        [Test]
        public void RgbToHwb_definition_w_is_min_bk_is_one_minus_max() {
            // CSS Color 4 §7: W = min(R,G,B); Bk = 1 - max(R,G,B).
            ColorMath.RgbToHwb(0.6, 0.2, 0.4, out double h, out double w, out double bk);
            Assert.That(w, Is.EqualTo(0.2).Within(Eps));
            Assert.That(bk, Is.EqualTo(1.0 - 0.6).Within(Eps));
            // Sanity: hue is the same as the HSL hue.
            ColorMath.RgbToHsl(0.6, 0.2, 0.4, out double hslH, out _, out _);
            Assert.That(h, Is.EqualTo(hslH).Within(Eps));
        }

        // ----- Cross-implementation parity check (mirrors TG5 intent for HSL math) -----
        //
        // Independent re-derivation of the canonical HSL->RGB algorithm in this test
        // file, run against ColorMath. If anyone ever "optimises" the production code
        // and changes a constant, this will catch it. This is the byte-identity check
        // the audit asks for, but expressed as an oracle rather than a shim against
        // private state.

        static void OracleHslToRgb01(double h, double s, double l, out double r, out double g, out double b) {
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

        [Test]
        public void HslToRgb01_matches_independent_oracle_across_hue_sweep() {
            // Sweep hue every 15° at three lightness/saturation samples and assert
            // the production code matches an in-file reimplementation byte-for-byte
            // (within FP determinism on the same arithmetic path).
            double[] sats = { 0.25, 0.6, 1.0 };
            double[] lights = { 0.25, 0.5, 0.75 };
            for (int hi = 0; hi < 24; hi++) {
                double h = hi * 15.0;
                foreach (var s in sats) {
                    foreach (var l in lights) {
                        ColorMath.HslToRgb01(h, s, l, out double pr, out double pg, out double pb);
                        OracleHslToRgb01(h, s, l, out double er, out double eg, out double eb);
                        Assert.That(pr, Is.EqualTo(er).Within(1e-12), $"R mismatch h={h} s={s} l={l}");
                        Assert.That(pg, Is.EqualTo(eg).Within(1e-12), $"G mismatch h={h} s={s} l={l}");
                        Assert.That(pb, Is.EqualTo(eb).Within(1e-12), $"B mismatch h={h} s={s} l={l}");
                    }
                }
            }
        }
    }
}
