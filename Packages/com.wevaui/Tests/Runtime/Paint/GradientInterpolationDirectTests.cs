using System;
using NUnit.Framework;
using Weva.Css.Values;
using Weva.Paint;

namespace Weva.Tests.Paint {
    // Direct unit coverage for GradientInterpolation (TG5 from CODE_AUDIT_FINDINGS.md).
    // The class implements six color-space paths (Srgb / LinearRgb / Oklab / Oklch /
    // Hsl / Hwb) and four hue-interpolation methods (Shorter / Longer / Increasing /
    // Decreasing). Its math overlaps with the canonical ColorMixer per D2/D3, so the
    // last test in this file pins byte-for-byte parity between the two so drift can't
    // pass undetected.
    public class GradientInterpolationDirectTests {
        const float Eps = 1e-3f;
        const float ByteEps = 1.5f / 255f; // ~one byte step in linear-light scale
        // Looser eps for HSL/HWB round-trips that go sRGB->HSL->sRGB->linear.
        const float HslEps = 6f / 255f;

        static LinearColor Red() => new LinearColor(1f, 0f, 0f, 1f);
        static LinearColor Blue() => new LinearColor(0f, 0f, 1f, 1f);

        // Build a LinearColor from sRGB 0-1 components.
        static LinearColor FromSrgb01(double r, double g, double b, float a = 1f) {
            return new LinearColor(
                (float)CssColor.SrgbToLinear(r),
                (float)CssColor.SrgbToLinear(g),
                (float)CssColor.SrgbToLinear(b),
                a);
        }

        // Sample chroma in sRGB-display terms (max - min of the sRGB-encoded mid).
        static double SrgbChroma(LinearColor c) {
            double r = CssColor.LinearToSrgb(c.R);
            double g = CssColor.LinearToSrgb(c.G);
            double b = CssColor.LinearToSrgb(c.B);
            return Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
        }

        // ---------- Color-space midpoints ----------

        [Test]
        public void Srgb_midpoint_of_red_blue_is_canonical_purple_128_0_128() {
            // Per CSS Color 4 §12.2, mixing in sRGB averages the sRGB-encoded
            // channels — red+blue at t=0.5 yields sRGB (128, 0, 128), the
            // brownish purple that webdevs recognise as the gamma-aware mid.
            var mid = GradientInterpolation.Interpolate(Red(), Blue(), 0.5, CssColorSpace.Srgb);
            double midR = CssColor.LinearToSrgb(mid.R);
            double midG = CssColor.LinearToSrgb(mid.G);
            double midB = CssColor.LinearToSrgb(mid.B);
            Assert.That(midR, Is.EqualTo(0.5).Within(0.005),  "sRGB R mid");
            Assert.That(midG, Is.EqualTo(0.0).Within(0.005),  "sRGB G mid");
            Assert.That(midB, Is.EqualTo(0.5).Within(0.005),  "sRGB B mid");
            // The corresponding LinearColor mid is SrgbToLinear(0.5) ≈ 0.2140
            // on both R and B — this is what the rest of the pipeline sees.
            Assert.That(mid.R, Is.EqualTo(0.2140f).Within(Eps));
            Assert.That(mid.B, Is.EqualTo(0.2140f).Within(Eps));
            Assert.That(mid.G, Is.EqualTo(0f).Within(Eps));
            Assert.That(mid.A, Is.EqualTo(1f).Within(Eps));
        }

        [Test]
        public void LinearRgb_midpoint_differs_from_Srgb_midpoint_proving_path_is_separate() {
            // Linear-RGB mid of red+blue is (0.5, 0, 0.5) in linear space;
            // sRGB mid in linear space is (~0.214, 0, ~0.214). If the wires
            // ever cross, both numbers collapse to the same value.
            var midLin = GradientInterpolation.Interpolate(Red(), Blue(), 0.5, CssColorSpace.LinearRgb);
            var midSrgb = GradientInterpolation.Interpolate(Red(), Blue(), 0.5, CssColorSpace.Srgb);
            Assert.That(midLin.R, Is.EqualTo(0.5f).Within(Eps));
            Assert.That(midLin.B, Is.EqualTo(0.5f).Within(Eps));
            Assert.That(midLin.G, Is.EqualTo(0f).Within(Eps));
            Assert.That(Math.Abs(midLin.R - midSrgb.R), Is.GreaterThan(0.1f),
                "Linear-RGB and sRGB midpoints must not collide");
        }

        [Test]
        public void Oklab_midpoint_differs_from_Srgb_midpoint_and_has_nonzero_blue_channel() {
            // Oklab is designed for perceptually uniform interpolation, not maximum
            // chroma. For red->blue, the Oklab path passes through a blue-violet
            // region that is perceptually distinct from the sRGB (0.5, 0, 0.5) mid.
            // The Oklab mid has a meaningful blue component (it leans blue-purple)
            // and is observably different from the sRGB mid.
            // Computed Oklab mid ≈ LinearColor(0.264, 0.086, 0.365) from the
            // Björn Ottosson Oklab formulae.
            var midOklab = GradientInterpolation.Interpolate(Red(), Blue(), 0.5, CssColorSpace.Oklab);
            var midSrgb = GradientInterpolation.Interpolate(Red(), Blue(), 0.5, CssColorSpace.Srgb);
            // Oklab mid must not collapse to sRGB mid — distinct interpolation path.
            Assert.That(midOklab.B, Is.GreaterThan(midSrgb.B + 0.05f),
                $"Oklab mid B ({midOklab.B:F3}) should exceed sRGB mid B ({midSrgb.B:F3}) — Oklab leans blue-purple");
            Assert.That(midOklab.R, Is.LessThan(midSrgb.R + 0.1f),
                $"Oklab mid R ({midOklab.R:F3}) should not greatly exceed sRGB mid R ({midSrgb.R:F3})");
            Assert.That(midOklab.A, Is.EqualTo(1f).Within(Eps));
        }

        [Test]
        public void Hsl_midpoint_of_red_blue_takes_shorter_hue_path_by_default() {
            // Red HSL hue = 0, Blue HSL hue = 240. Shorter arc is via 0/360
            // (60deg total in the negative direction), so the mid lands at
            // hue 300 — magenta-ish (sRGB approx (1, 0, 1)). The longer arc
            // would go via 120 (green); we must not see green.
            var mid = GradientInterpolation.Interpolate(Red(), Blue(), 0.5, CssColorSpace.Hsl);
            Assert.That(mid.R, Is.EqualTo(1f).Within(HslEps));
            Assert.That(mid.B, Is.EqualTo(1f).Within(HslEps));
            Assert.That(mid.G, Is.LessThan(0.05f), "Shorter arc must not pass through green");
        }

        [Test]
        public void Hwb_midpoint_round_trips_along_shorter_hue_path() {
            // Same red->blue check in HWB: hue takes the shorter arc, W/B
            // average to zero, so the mid is a pure hue at 300deg — magenta.
            var mid = GradientInterpolation.Interpolate(Red(), Blue(), 0.5, CssColorSpace.Hwb);
            Assert.That(mid.R, Is.EqualTo(1f).Within(HslEps));
            Assert.That(mid.B, Is.EqualTo(1f).Within(HslEps));
            Assert.That(mid.G, Is.LessThan(0.05f), "HWB shorter arc must not pass through green");
        }

        [Test]
        public void Oklch_midpoint_passes_through_purple_not_grey() {
            // Oklch decomposes Oklab into chroma+hue, so the midpoint of red
            // and blue should preserve chroma rather than collapsing through
            // achromatic grey. The mid is purple-ish — red and blue should
            // be roughly balanced and clearly above green.
            var mid = GradientInterpolation.Interpolate(Red(), Blue(), 0.5, CssColorSpace.Oklch);
            Assert.That(mid.R, Is.GreaterThan(mid.G + 0.05f),
                $"Oklch mid R ({mid.R}) should exceed G ({mid.G})");
            Assert.That(mid.B, Is.GreaterThan(mid.G + 0.05f),
                $"Oklch mid B ({mid.B}) should exceed G ({mid.G})");
            Assert.That(mid.A, Is.EqualTo(1f).Within(Eps));
        }

        // ---------- Hue-interpolation methods (LerpHueDegrees through HSL) ----------
        // 30deg = sRGB(1, 0.5, 0) (orange-red), 330deg = sRGB(1, 0, 0.5) (pink).
        // Both have s=1, l=0.5, so the midpoint is a pure-hue mix that exposes
        // which arc the algorithm walked.

        static LinearColor Hue30() => FromSrgb01(1.0, 0.5, 0.0);   // hue=30, s=1, l=0.5
        static LinearColor Hue330() => FromSrgb01(1.0, 0.0, 0.5);  // hue=330, s=1, l=0.5

        [Test]
        public void HueInterp_Shorter_30_to_330_goes_via_zero() {
            // diff = 300 -> wrap to -60 -> mid lands at hue 0 (pure red).
            // Expected mid sRGB ≈ (1, 0, 0).
            var mid = GradientInterpolation.Interpolate(Hue30(), Hue330(), 0.5,
                CssColorSpace.Hsl, CssHueInterpolationMethod.Shorter);
            double r = CssColor.LinearToSrgb(mid.R);
            double g = CssColor.LinearToSrgb(mid.G);
            double b = CssColor.LinearToSrgb(mid.B);
            Assert.That(r, Is.EqualTo(1.0).Within(0.02), "R should be max at hue 0");
            Assert.That(g, Is.EqualTo(0.0).Within(0.02), "G should be 0 at hue 0");
            Assert.That(b, Is.EqualTo(0.0).Within(0.02), "B should be 0 at hue 0");
        }

        [Test]
        public void HueInterp_Longer_30_to_330_goes_via_180() {
            // diff = 300 -> shorter would be -60, longer flips to +300 ->
            // mid lands at hue 180 (cyan). Expected sRGB ≈ (0, 1, 1).
            var mid = GradientInterpolation.Interpolate(Hue30(), Hue330(), 0.5,
                CssColorSpace.Hsl, CssHueInterpolationMethod.Longer);
            double r = CssColor.LinearToSrgb(mid.R);
            double g = CssColor.LinearToSrgb(mid.G);
            double b = CssColor.LinearToSrgb(mid.B);
            Assert.That(r, Is.EqualTo(0.0).Within(0.02), "R should be 0 at hue 180");
            Assert.That(g, Is.EqualTo(1.0).Within(0.02), "G should be max at hue 180");
            Assert.That(b, Is.EqualTo(1.0).Within(0.02), "B should be max at hue 180");
        }

        [Test]
        public void HueInterp_Increasing_30_to_330_goes_via_180() {
            // Increasing forces +diff -> 300 -> mid at hue 180 (cyan).
            var mid = GradientInterpolation.Interpolate(Hue30(), Hue330(), 0.5,
                CssColorSpace.Hsl, CssHueInterpolationMethod.Increasing);
            double r = CssColor.LinearToSrgb(mid.R);
            double g = CssColor.LinearToSrgb(mid.G);
            double b = CssColor.LinearToSrgb(mid.B);
            Assert.That(r, Is.EqualTo(0.0).Within(0.02), "Increasing should pass through cyan");
            Assert.That(g, Is.EqualTo(1.0).Within(0.02));
            Assert.That(b, Is.EqualTo(1.0).Within(0.02));
        }

        [Test]
        public void HueInterp_Decreasing_30_to_330_goes_via_zero() {
            // Decreasing forces -diff -> -60 -> mid at hue 0 (red).
            var mid = GradientInterpolation.Interpolate(Hue30(), Hue330(), 0.5,
                CssColorSpace.Hsl, CssHueInterpolationMethod.Decreasing);
            double r = CssColor.LinearToSrgb(mid.R);
            double g = CssColor.LinearToSrgb(mid.G);
            double b = CssColor.LinearToSrgb(mid.B);
            Assert.That(r, Is.EqualTo(1.0).Within(0.02), "Decreasing should pass through red");
            Assert.That(g, Is.EqualTo(0.0).Within(0.02));
            Assert.That(b, Is.EqualTo(0.0).Within(0.02));
        }

        // ---------- t-bounds ----------

        [Test]
        public void Interpolate_clamps_t_to_endpoints() {
            var a = Red();
            var b = Blue();
            Assert.That(GradientInterpolation.Interpolate(a, b, 0.0, CssColorSpace.Oklab), Is.EqualTo(a),
                "t<=0 returns endpoint a verbatim");
            Assert.That(GradientInterpolation.Interpolate(a, b, 1.0, CssColorSpace.Oklab), Is.EqualTo(b),
                "t>=1 returns endpoint b verbatim");
            Assert.That(GradientInterpolation.Interpolate(a, b, -0.25, CssColorSpace.Hsl), Is.EqualTo(a));
            Assert.That(GradientInterpolation.Interpolate(a, b, 1.5, CssColorSpace.Hsl), Is.EqualTo(b));
        }

        // ---------- Cross-implementation parity (D2/D3 drift guard) ----------

        [Test]
        public void Oklab_midpoint_matches_ColorMixer_Oklab_byte_for_byte() {
            // GradientInterpolation.LerpOklab and ColorMixer.Mix(..., Oklab,...)
            // are two independent copies of the same CSS Color 4 §12 math.
            // If they drift, gradients and color-mix() silently diverge and
            // only one side has tests today (ColorMixer). This pins both to
            // the same byte output so a future PR is forced to decide whether
            // to unify them.
            var redCss = new CssColor((byte)255, (byte)0, (byte)0, 1f);
            var blueCss = new CssColor((byte)0, (byte)0, (byte)255, 1f);
            var mixed = ColorMixer.Mix(redCss, blueCss, CssColorSpace.Oklab, 0.5, 0.5, "test");

            var midLinear = GradientInterpolation.Interpolate(
                LinearColor.FromCssColor(redCss),
                LinearColor.FromCssColor(blueCss),
                0.5,
                CssColorSpace.Oklab);
            byte giR = (byte)Math.Round(CssColor.LinearToSrgb(midLinear.R) * 255.0);
            byte giG = (byte)Math.Round(CssColor.LinearToSrgb(midLinear.G) * 255.0);
            byte giB = (byte)Math.Round(CssColor.LinearToSrgb(midLinear.B) * 255.0);

            // Tolerate at most one byte of rounding slop on each channel (both
            // sides round via Math.Round on the same intermediate floats, so
            // exact agreement is the spec — anything bigger is true drift).
            int dr = Math.Abs(giR - mixed.R);
            int dg = Math.Abs(giG - mixed.G);
            int dbz = Math.Abs(giB - mixed.B);
            string msg = $"GradientInterpolation Oklab mid = ({giR},{giG},{giB}), " +
                $"ColorMixer Oklab mid = ({mixed.R},{mixed.G},{mixed.B}). " +
                "If these drift, decide whether GradientInterpolation should consume ColorMixer's implementation.";
            Assert.That(dr, Is.LessThanOrEqualTo(1), msg);
            Assert.That(dg, Is.LessThanOrEqualTo(1), msg);
            Assert.That(dbz, Is.LessThanOrEqualTo(1), msg);
        }

        [Test]
        public void Oklch_midpoint_matches_ColorMixer_Oklch_within_byte() {
            // Same drift guard for the cylindrical Oklch path — both
            // implementations route through atan2/hue blending and share
            // the Shorter default. ColorMixer additionally has a powerless-
            // hue dead-zone (kHueDeadzone) that GradientInterpolation lacks;
            // red/blue are both fully chromatic so that branch is inert here.
            var redCss = new CssColor((byte)255, (byte)0, (byte)0, 1f);
            var blueCss = new CssColor((byte)0, (byte)0, (byte)255, 1f);
            var mixed = ColorMixer.Mix(redCss, blueCss, CssColorSpace.Oklch, 0.5, 0.5, "test");

            var midLinear = GradientInterpolation.Interpolate(
                LinearColor.FromCssColor(redCss),
                LinearColor.FromCssColor(blueCss),
                0.5,
                CssColorSpace.Oklch);
            byte giR = (byte)Math.Round(CssColor.LinearToSrgb(midLinear.R) * 255.0);
            byte giG = (byte)Math.Round(CssColor.LinearToSrgb(midLinear.G) * 255.0);
            byte giB = (byte)Math.Round(CssColor.LinearToSrgb(midLinear.B) * 255.0);

            int dr = Math.Abs(giR - mixed.R);
            int dg = Math.Abs(giG - mixed.G);
            int dbz = Math.Abs(giB - mixed.B);
            string msg = $"GradientInterpolation Oklch mid = ({giR},{giG},{giB}), " +
                $"ColorMixer Oklch mid = ({mixed.R},{mixed.G},{mixed.B}).";
            Assert.That(dr, Is.LessThanOrEqualTo(1), msg);
            Assert.That(dg, Is.LessThanOrEqualTo(1), msg);
            Assert.That(dbz, Is.LessThanOrEqualTo(1), msg);
        }
    }
}
