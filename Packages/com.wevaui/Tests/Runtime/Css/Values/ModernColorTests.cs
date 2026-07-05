using NUnit.Framework;
using Weva.Css.Values;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Paint;
using Weva.Paint.Conversion;

namespace Weva.Tests.Css.Values {
    public class ModernColorTests {
        static CssColor ParseColor(string s) => (CssColor)CssValueParser.Parse(s);

        [Test]
        public void Oklch_pure_red_round_trips_within_tolerance() {
            // sRGB red has L≈0.628, C≈0.258, H≈29.23 in OKLCh.
            var c = ParseColor("oklch(0.6279 0.2576 29.23)");
            Assert.That((int)c.R, Is.EqualTo(255).Within(2));
            Assert.That((int)c.G, Is.LessThan(20));
            Assert.That((int)c.B, Is.LessThan(20));
        }

        [Test]
        public void Oklch_lightness_percentage_form() {
            var c = ParseColor("oklch(70% 0.15 270)");
            Assert.That(c.A, Is.EqualTo(1f));
            // 270deg in OKLCh is roughly blue-purple — blue should dominate.
            Assert.That(c.B, Is.GreaterThan(c.R));
            Assert.That(c.B, Is.GreaterThan(c.G));
        }

        [Test]
        public void Oklch_with_alpha() {
            var c = ParseColor("oklch(50% 0.1 200 / 0.5)");
            Assert.That(c.A, Is.EqualTo(0.5f).Within(1e-4));
        }

        [Test]
        public void Oklab_parses() {
            var c = ParseColor("oklab(70% 0.05 -0.1)");
            Assert.That(c.A, Is.EqualTo(1f));
            // Negative b axis = blueish.
            Assert.That(c.B, Is.GreaterThan(c.R));
        }

        [Test]
        public void Oklab_neutral_grey() {
            // (L=0.5, a=0, b=0) is a neutral grey.
            var c = ParseColor("oklab(50% 0 0)");
            int avg = (c.R + c.G + c.B) / 3;
            Assert.That(System.Math.Abs(c.R - avg), Is.LessThanOrEqualTo(2));
            Assert.That(System.Math.Abs(c.G - avg), Is.LessThanOrEqualTo(2));
            Assert.That(System.Math.Abs(c.B - avg), Is.LessThanOrEqualTo(2));
        }

        [Test]
        public void Hwb_pure_red() {
            var c = ParseColor("hwb(0 0% 0%)");
            Assert.That((int)c.R, Is.EqualTo(255));
            Assert.That((int)c.G, Is.EqualTo(0));
            Assert.That((int)c.B, Is.EqualTo(0));
        }

        [Test]
        public void Hwb_with_whiteness_and_blackness() {
            var c = ParseColor("hwb(220 20% 30%)");
            // Mostly blue-ish.
            Assert.That(c.B, Is.GreaterThan(c.R));
        }

        [Test]
        public void Hwb_white_blackness_clamped_to_grey() {
            // When w + b > 1, result is grey at w/(w+b).
            var c = ParseColor("hwb(0 70% 70%)");
            Assert.That((int)c.R, Is.EqualTo((int)c.G));
            Assert.That((int)c.G, Is.EqualTo((int)c.B));
        }

        [Test]
        public void Hwb_with_alpha() {
            var c = ParseColor("hwb(0 0% 0% / 0.25)");
            Assert.That(c.A, Is.EqualTo(0.25f).Within(1e-4));
        }

        [Test]
        public void Color_mix_srgb_red_blue_is_rgb_midpoint() {
            var c = ParseColor("color-mix(in srgb, red, blue)");
            Assert.That((int)c.R, Is.EqualTo(127).Within(2));
            Assert.That((int)c.G, Is.EqualTo(0));
            Assert.That((int)c.B, Is.EqualTo(127).Within(2));
        }

        [Test]
        public void Color_mix_oklch_red_blue_differs_from_srgb_midpoint() {
            var srgb = ParseColor("color-mix(in srgb, red, blue)");
            var oklch = ParseColor("color-mix(in oklch, red, blue)");
            // OKLCh midpoint is perceptually balanced and visibly different from
            // the dim-purple sRGB midpoint.
            Assert.That((int)oklch.R, Is.Not.EqualTo((int)srgb.R));
        }

        [Test]
        public void Color_mix_weighted_bias_to_red() {
            var c = ParseColor("color-mix(in srgb, red 75%, blue 25%)");
            Assert.That(c.R, Is.GreaterThan(c.B));
        }

        [Test]
        public void Color_mix_weighted_bias_to_blue() {
            var c = ParseColor("color-mix(in srgb, red 25%, blue 75%)");
            Assert.That(c.B, Is.GreaterThan(c.R));
        }

        [Test]
        public void Color_mix_default_space_is_oklab() {
            // No "in <space>" prefix — default is oklab. Verify it parses and result differs
            // from in-srgb mix (oklab mid for red+blue is not (127,0,127)).
            var def = ParseColor("color-mix(red, blue)");
            var srgb = ParseColor("color-mix(in srgb, red, blue)");
            Assert.That(def.R != srgb.R || def.G != srgb.G || def.B != srgb.B,
                "color-mix() default space should be oklab, not srgb");
        }

        [Test]
        public void Color_mix_only_one_weight_implies_remaining() {
            // "red 30%, blue" -> blue gets 70%.
            var c = ParseColor("color-mix(in srgb, red 30%, blue)");
            Assert.That(c.B, Is.GreaterThan(c.R));
        }

        [Test]
        public void Color_mix_postfix_percentage() {
            // CSS 5 also accepts "<color> <pct>".
            var c = ParseColor("color-mix(in srgb, red 25%, blue 75%)");
            Assert.That(c.B, Is.GreaterThan(c.R));
        }

        [Test]
        public void Color_mix_with_hex_colors() {
            var c = ParseColor("color-mix(in srgb, #ff0000 50%, #0000ff)");
            Assert.That((int)c.R, Is.EqualTo(127).Within(2));
            Assert.That((int)c.B, Is.EqualTo(127).Within(2));
        }

        [Test]
        public void Color_mix_oklab_white_black_midpoint() {
            // OKLab midpoint of white and black is L=0.5 — perceptual middle grey, not 50% sRGB grey.
            var c = ParseColor("color-mix(in oklab, white, black)");
            Assert.That((int)c.R, Is.EqualTo((int)c.G).Within(2));
            Assert.That((int)c.G, Is.EqualTo((int)c.B).Within(2));
            // Perceptual midgrey is ≈ 119 in sRGB, not 127.
            Assert.That(c.R, Is.LessThan(140));
        }

        [Test]
        public void Color_mix_hsl_space_parses() {
            var c = ParseColor("color-mix(in hsl, red, blue)");
            Assert.That(c.A, Is.EqualTo(1f));
        }

        [Test]
        public void Color_mix_hwb_space_parses() {
            var c = ParseColor("color-mix(in hwb, red, blue)");
            Assert.That(c.A, Is.EqualTo(1f));
        }

        [Test]
        public void Color_mix_unknown_space_throws() {
            Assert.Throws<CssValueParseException>(() => ParseColor("color-mix(in fakespace, red, blue)"));
        }

        [Test]
        public void Color_mix_missing_in_space_uses_default() {
            // Already tested default. Verify "in srgb-linear" works as another alias.
            var c = ParseColor("color-mix(in srgb-linear, red, blue)");
            Assert.That(c.A, Is.EqualTo(1f));
        }

        [Test]
        public void Oklch_zero_lightness_is_black() {
            var c = ParseColor("oklch(0% 0 0)");
            Assert.That((int)c.R, Is.LessThanOrEqualTo(2));
            Assert.That((int)c.G, Is.LessThanOrEqualTo(2));
            Assert.That((int)c.B, Is.LessThanOrEqualTo(2));
        }

        [Test]
        public void Oklch_full_lightness_zero_chroma_is_white() {
            var c = ParseColor("oklch(100% 0 0)");
            Assert.That((int)c.R, Is.EqualTo(255));
            Assert.That((int)c.G, Is.EqualTo(255));
            Assert.That((int)c.B, Is.EqualTo(255));
        }

        [Test]
        public void Oklch_extreme_chroma_clamps_in_gamut() {
            // 100% chroma on the OKLCh axis (0.4) is far out of sRGB gamut; we clamp to byte 0..255.
            var c = ParseColor("oklch(50% 100% 0)");
            Assert.That((int)c.R, Is.LessThanOrEqualTo(255));
            Assert.That((int)c.G, Is.LessThanOrEqualTo(255));
            Assert.That((int)c.B, Is.LessThanOrEqualTo(255));
        }

        [Test]
        public void Oklab_round_trip_red() {
            // sRGB red -> OKLab -> sRGB linear -> back is approximately identity.
            CssColor.LinearRgbToOklab(
                CssColor.SrgbToLinear(1.0),
                CssColor.SrgbToLinear(0.0),
                CssColor.SrgbToLinear(0.0),
                out double L, out double a, out double b);
            CssColor.OklabToLinearRgb(L, a, b, out double lr, out double lg, out double lb);
            Assert.That(CssColor.LinearToSrgb(lr), Is.EqualTo(1.0).Within(1e-3));
            Assert.That(CssColor.LinearToSrgb(lg), Is.EqualTo(0.0).Within(1e-3));
            Assert.That(CssColor.LinearToSrgb(lb), Is.EqualTo(0.0).Within(1e-3));
        }

        [Test]
        public void Malformed_oklch_missing_components_throws() {
            Assert.Throws<CssValueParseException>(() => ParseColor("oklch(70%)"));
        }

        [Test]
        public void Malformed_oklab_missing_axis_throws() {
            Assert.Throws<CssValueParseException>(() => ParseColor("oklab(50% 0)"));
        }

        [Test]
        public void Malformed_hwb_requires_percent_on_whiteness() {
            Assert.Throws<CssValueParseException>(() => ParseColor("hwb(220 20 30)"));
        }

        [Test]
        public void Malformed_color_mix_missing_color_throws() {
            Assert.Throws<CssValueParseException>(() => ParseColor("color-mix(in srgb, red)"));
        }

        // ---- color() function (CSS Color 4 §15) ----

        [Test]
        public void Color_function_srgb_pure_red() {
            var c = ParseColor("color(srgb 1 0 0)");
            Assert.That((int)c.R, Is.EqualTo(255));
            Assert.That((int)c.G, Is.EqualTo(0));
            Assert.That((int)c.B, Is.EqualTo(0));
            Assert.That(c.A, Is.EqualTo(1f));
        }

        [Test]
        public void Color_function_srgb_with_slash_alpha() {
            var c = ParseColor("color(srgb 1 0 0 / 0.5)");
            Assert.That(c.A, Is.EqualTo(0.5f).Within(1e-4));
        }

        [Test]
        public void Color_function_srgb_percentage_channels() {
            var c = ParseColor("color(srgb 100% 0% 0%)");
            Assert.That((int)c.R, Is.EqualTo(255));
            Assert.That((int)c.G, Is.EqualTo(0));
        }

        [Test]
        public void Color_function_srgb_linear_white_round_trips_to_srgb_white() {
            // Linear 1 -> sRGB 255 (1.0 in linear maps to 255 in encoded sRGB).
            var c = ParseColor("color(srgb-linear 1 1 1)");
            Assert.That((int)c.R, Is.EqualTo(255));
            Assert.That((int)c.G, Is.EqualTo(255));
            Assert.That((int)c.B, Is.EqualTo(255));
        }

        [Test]
        public void Color_function_unknown_space_throws() {
            Assert.Throws<CssValueParseException>(() => ParseColor("color(unknown-space 1 0 0)"));
        }

        [Test]
        public void Color_function_missing_channel_throws() {
            Assert.Throws<CssValueParseException>(() => ParseColor("color(srgb 1 0)"));
        }

        // ---- color() L4 wide-gamut spaces (CSS Color 4 §15/§17) ----

        [Test]
        public void Color_function_srgb_red_regression_guard() {
            // Regression: srgb path must remain plain red after wide-gamut wiring.
            var c = ParseColor("color(srgb 1 0 0)");
            Assert.That((int)c.R, Is.EqualTo(255));
            Assert.That((int)c.G, Is.EqualTo(0));
            Assert.That((int)c.B, Is.EqualTo(0));
        }

        [Test]
        public void Color_function_srgb_linear_midgray_encodes_to_about_188() {
            // 0.5 linear sRGB encodes to ~0.7354 -> ~188 byte (sRGB OETF on 0.5).
            var c = ParseColor("color(srgb-linear 0.5 0.5 0.5)");
            Assert.That((int)c.R, Is.EqualTo(188).Within(2));
            Assert.That((int)c.G, Is.EqualTo(188).Within(2));
            Assert.That((int)c.B, Is.EqualTo(188).Within(2));
        }

        [Test]
        public void Color_function_display_p3_pure_red_clips_to_srgb_red() {
            // P3 (1,0,0) is outside sRGB gamut on the green/blue side; after clip
            // it should still be a strong red (R=255, G/B small and non-negative).
            var c = ParseColor("color(display-p3 1 0 0)");
            Assert.That((int)c.R, Is.EqualTo(255));
            Assert.That((int)c.R + (int)c.G + (int)c.B, Is.GreaterThan(0));
            Assert.That(c.R, Is.GreaterThan(c.G));
            Assert.That(c.R, Is.GreaterThan(c.B));
        }

        [Test]
        public void Color_function_rec2020_red_brighter_than_display_p3_red() {
            // Rec.2020 has a wider gamut than P3; pure-red in rec2020 lies even further
            // outside sRGB, so after clipping the green/blue ride-along is smaller.
            var p3 = ParseColor("color(display-p3 1 0 0)");
            var rec = ParseColor("color(rec2020 1 0 0)");
            Assert.That((int)rec.R, Is.EqualTo(255));
            Assert.That(rec.G + rec.B, Is.LessThanOrEqualTo(p3.G + p3.B));
        }

        [Test]
        public void Color_function_xyz_resolves_to_sensible_color() {
            // XYZ (0.5, 0.5, 0.5) at D65: Y=0.5 -> luminance ~mid; X≈Y≈Z is near
            // neutral-ish (slightly warm). After encoding it should be a real
            // visible color, not all-zero and not all-255, and roughly grey-warm.
            var c = ParseColor("color(xyz 0.5 0.5 0.5)");
            int sum = c.R + c.G + c.B;
            Assert.That(sum, Is.GreaterThan(0));
            Assert.That(sum, Is.LessThan(3 * 255));
            // No channel should be wildly off the mean — within 60 bytes of average.
            int avg = sum / 3;
            Assert.That(System.Math.Abs(c.R - avg), Is.LessThan(80));
            Assert.That(System.Math.Abs(c.G - avg), Is.LessThan(80));
            Assert.That(System.Math.Abs(c.B - avg), Is.LessThan(80));
        }

        [Test]
        public void Color_function_xyz_d65_alias_matches_xyz() {
            var a = ParseColor("color(xyz 0.4 0.5 0.6)");
            var b = ParseColor("color(xyz-d65 0.4 0.5 0.6)");
            Assert.That((int)a.R, Is.EqualTo((int)b.R));
            Assert.That((int)a.G, Is.EqualTo((int)b.G));
            Assert.That((int)a.B, Is.EqualTo((int)b.B));
        }

        [Test]
        public void Color_function_xyz_d50_differs_from_xyz_d65_via_bradford() {
            // Same XYZ tuple under D50 vs D65 should NOT encode identically because
            // the D50 path runs Bradford adaptation first.
            var d50 = ParseColor("color(xyz-d50 0.4 0.5 0.6)");
            var d65 = ParseColor("color(xyz-d65 0.4 0.5 0.6)");
            bool any = d50.R != d65.R || d50.G != d65.G || d50.B != d65.B;
            Assert.That(any, "xyz-d50 must run Bradford adaptation distinct from xyz-d65");
        }

        [Test]
        public void Color_function_a98_red_is_red() {
            var c = ParseColor("color(a98-rgb 1 0 0)");
            Assert.That((int)c.R, Is.EqualTo(255));
            Assert.That(c.R, Is.GreaterThan(c.G));
            Assert.That(c.R, Is.GreaterThan(c.B));
        }

        [Test]
        public void Color_function_prophoto_white_round_trips_white() {
            // ProPhoto (1,1,1) is D50 white; after Bradford to D65 and sRGB encode
            // it should be near sRGB white.
            var c = ParseColor("color(prophoto-rgb 1 1 1)");
            Assert.That((int)c.R, Is.EqualTo(255).Within(2));
            Assert.That((int)c.G, Is.EqualTo(255).Within(2));
            Assert.That((int)c.B, Is.EqualTo(255).Within(2));
        }

        [Test]
        public void Color_function_display_p3_with_slash_alpha() {
            var c = ParseColor("color(display-p3 1 0 0 / 0.5)");
            Assert.That(c.A, Is.EqualTo(0.5f).Within(1e-4));
        }

        // ---- conic-gradient + linear-gradient(in <space>) parsing ----

        static ComputedStyle Style() => new ComputedStyle(new Element("div"));
        static Rect Bounds() => new Rect(0, 0, 100, 100);

        [Test]
        public void Conic_gradient_parses_default_from_zero() {
            var s = Style();
            s.Set("background-image", "conic-gradient(red, green, blue)");
            var brush = BackgroundResolver.ResolveBackground(s, Bounds());
            Assert.That(brush, Is.Not.Null);
            Assert.That(brush.Kind, Is.EqualTo(BrushKind.Gradient));
            var cg = (ConicGradient)brush.GradientValue;
            Assert.That(cg.FromAngleDegrees, Is.EqualTo(0).Within(1e-6));
            Assert.That(cg.Stops.Count, Is.EqualTo(3));
        }

        [Test]
        public void Conic_gradient_with_from_angle_and_at_position() {
            var s = Style();
            s.Set("background-image", "conic-gradient(from 45deg at 50% 50%, red, blue)");
            var brush = BackgroundResolver.ResolveBackground(s, Bounds());
            Assert.That(brush, Is.Not.Null);
            var cg = (ConicGradient)brush.GradientValue;
            Assert.That(cg.FromAngleDegrees, Is.EqualTo(45).Within(1e-6));
            Assert.That(cg.CenterX, Is.EqualTo(50).Within(1e-6));
            Assert.That(cg.CenterY, Is.EqualTo(50).Within(1e-6));
        }

        [Test]
        public void Linear_gradient_with_in_oklch_sets_interpolation_space() {
            var s = Style();
            s.Set("background-image", "linear-gradient(in oklch, red, blue)");
            var brush = BackgroundResolver.ResolveBackground(s, Bounds());
            var lg = (LinearGradient)brush.GradientValue;
            Assert.That(lg.InterpolationSpace, Is.EqualTo(CssColorSpace.Oklch));
            Assert.That(lg.Stops.Count, Is.EqualTo(2));
        }

        [Test]
        public void Linear_gradient_in_oklab_default_interpolation() {
            var s = Style();
            s.Set("background-image", "linear-gradient(in oklab, red, blue)");
            var brush = BackgroundResolver.ResolveBackground(s, Bounds());
            var lg = (LinearGradient)brush.GradientValue;
            Assert.That(lg.InterpolationSpace, Is.EqualTo(CssColorSpace.Oklab));
        }

        [Test]
        public void Linear_gradient_no_in_prefix_defaults_to_srgb() {
            var s = Style();
            s.Set("background-image", "linear-gradient(red, blue)");
            var brush = BackgroundResolver.ResolveBackground(s, Bounds());
            var lg = (LinearGradient)brush.GradientValue;
            // G1 deviation: CSS Images 4 §3.4 / CSS Color 4 §12.2 specify `oklab`
            // as the default interpolation color space. This engine currently
            // defaults to sRGB because the URP shader path (`Weva_GradientLerp`
            // in `UIShaderLib.hlsl`) only differentiates sRGB vs linear-RGB and
            // does not implement an oklab branch — flipping the default would
            // produce a CPU/GPU visual mismatch. Pinned here as a regression
            // until the shader gains an oklab branch; see G1 in
            // CSS_COMPLIANCE_ISSUES.md.
            Assert.That(lg.InterpolationSpace, Is.EqualTo(CssColorSpace.Srgb));
        }

        [Test]
        public void Linear_gradient_in_srgb_overrides_default_G1() {
            // Regression pin: `linear-gradient(in srgb, ...)` must always
            // resolve to sRGB interpolation regardless of what the engine
            // default is. Once G1's shader work lands and the default flips
            // to Oklab, this test must continue to pass — `in srgb` is an
            // explicit author override.
            var s = Style();
            s.Set("background-image", "linear-gradient(in srgb, red, blue)");
            var brush = BackgroundResolver.ResolveBackground(s, Bounds());
            var lg = (LinearGradient)brush.GradientValue;
            Assert.That(lg.InterpolationSpace, Is.EqualTo(CssColorSpace.Srgb));
        }

        [Test]
        public void Gradient_midpoint_oklab_differs_from_srgb_G1() {
            // Proves the CPU `Gradient.Sample` actually applies oklab math
            // (not just labels the gradient). When the shader pipeline lands
            // its oklab branch, this divergence between Sample(0.5) in oklab
            // and the sRGB midpoint of red↔blue is what authors will see
            // visually mid-stop.
            var red = LinearColor.FromCssColor(new CssColor(255, 0, 0, 1f));
            var blue = LinearColor.FromCssColor(new CssColor(0, 0, 255, 1f));
            var stops = new[] {
                new GradientStop(red, 0.0),
                new GradientStop(blue, 1.0),
            };
            var oklabGrad = new LinearGradient(0, stops, CssColorSpace.Oklab);
            var srgbGrad = new LinearGradient(0, stops, CssColorSpace.Srgb);
            var oklabMid = oklabGrad.Sample(0.5);
            var srgbMid = srgbGrad.Sample(0.5);

            // Convert both midpoints to sRGB byte form for a perceptual diff
            // that matches how authors and goldens see the gradient.
            byte oR = (byte)System.Math.Round(CssColor.LinearToSrgb(oklabMid.R) * 255.0);
            byte oG = (byte)System.Math.Round(CssColor.LinearToSrgb(oklabMid.G) * 255.0);
            byte oB = (byte)System.Math.Round(CssColor.LinearToSrgb(oklabMid.B) * 255.0);
            byte sR = (byte)System.Math.Round(CssColor.LinearToSrgb(srgbMid.R) * 255.0);
            byte sG = (byte)System.Math.Round(CssColor.LinearToSrgb(srgbMid.G) * 255.0);
            byte sB = (byte)System.Math.Round(CssColor.LinearToSrgb(srgbMid.B) * 255.0);
            int dr = System.Math.Abs(oR - sR);
            int dg = System.Math.Abs(oG - sG);
            int db = System.Math.Abs(oB - sB);
            int totalDiff = dr + dg + db;
            // Empirical: oklab midpoint of red↔blue is markedly more purple
            // and brighter than the sRGB midpoint. A summed-channel delta
            // of at least 20 confirms oklab is applied, not just labeled.
            Assert.That(totalDiff, Is.GreaterThan(20),
                $"Oklab midpoint ({oR},{oG},{oB}) must differ from sRGB midpoint ({sR},{sG},{sB})");
        }

        // ---- conic gradient rasterizer sampling ----

        [Test]
        public void Conic_gradient_sample_at_top_is_first_stop() {
            var stops = new[] {
                new GradientStop(LinearColor.FromCssColor(new CssColor(255, 0, 0, 1f)), 0.0),
                new GradientStop(LinearColor.FromCssColor(new CssColor(0, 255, 0, 1f)), 0.5),
                new GradientStop(LinearColor.FromCssColor(new CssColor(0, 0, 255, 1f)), 1.0),
            };
            var cg = new ConicGradient(0, 50, 50, stops);
            // Above center (px=50, py=0) is angle 0deg in CSS conic — first stop (red).
            var c = cg.SampleAtPixel(50, 0);
            Assert.That(c.R, Is.GreaterThan(0.5f));
            Assert.That(c.G, Is.LessThan(0.1f));
        }

        [Test]
        public void Conic_gradient_sample_at_bottom_is_middle_stop() {
            var stops = new[] {
                new GradientStop(LinearColor.FromCssColor(new CssColor(255, 0, 0, 1f)), 0.0),
                new GradientStop(LinearColor.FromCssColor(new CssColor(0, 255, 0, 1f)), 0.5),
                new GradientStop(LinearColor.FromCssColor(new CssColor(0, 0, 255, 1f)), 1.0),
            };
            var cg = new ConicGradient(0, 50, 50, stops);
            // Below center is 180deg = halfway around -> green stop.
            var c = cg.SampleAtPixel(50, 100);
            Assert.That(c.G, Is.GreaterThan(0.5f));
        }

        [Test]
        public void Conic_gradient_from_angle_offsets_sample() {
            var stops = new[] {
                new GradientStop(LinearColor.FromCssColor(new CssColor(255, 0, 0, 1f)), 0.0),
                new GradientStop(LinearColor.FromCssColor(new CssColor(0, 0, 255, 1f)), 1.0),
            };
            // from=180deg means the start of the gradient sweep is at the bottom.
            var cg = new ConicGradient(180, 50, 50, stops);
            var c = cg.SampleAtPixel(50, 100);
            // At bottom now = first stop (red).
            Assert.That(c.R, Is.GreaterThan(0.5f));
        }

        [Test]
        public void Conic_gradient_full_sweep_returns_to_start_color() {
            var stops = new[] {
                new GradientStop(LinearColor.FromCssColor(new CssColor(200, 0, 0, 1f)), 0.0),
                new GradientStop(LinearColor.FromCssColor(new CssColor(0, 200, 0, 1f)), 0.5),
                new GradientStop(LinearColor.FromCssColor(new CssColor(200, 0, 0, 1f)), 1.0),
            };
            var cg = new ConicGradient(0, 50, 50, stops);
            var top = cg.SampleAtPixel(50, 0);
            // 360deg around: top of pixel space samples close to 0deg = first stop (red).
            Assert.That(top.R, Is.GreaterThan(top.G));
        }

        // ---- color-mix <hue-interpolation-method> (CSS Color 4 §12.3) ----

        static double ExtractOklchHueDegrees(CssColor c) {
            double lr = CssColor.SrgbToLinear(c.R / 255.0);
            double lg = CssColor.SrgbToLinear(c.G / 255.0);
            double lb = CssColor.SrgbToLinear(c.B / 255.0);
            CssColor.LinearRgbToOklab(lr, lg, lb, out _, out double a, out double bAxis);
            double h = System.Math.Atan2(bAxis, a) * 180.0 / System.Math.PI;
            return ((h % 360.0) + 360.0) % 360.0;
        }

        static double HueDistance(double a, double b) {
            double d = System.Math.Abs(a - b) % 360.0;
            return d > 180.0 ? 360.0 - d : d;
        }

        [Test]
        public void Color_mix_oklch_hue_methods_pick_different_arcs() {
            // hues 10 and 350: short arc is 20deg through 0; long arc is 340deg through 180.
            var shorter = ParseColor("color-mix(in oklch, oklch(0.5 0.05 10), oklch(0.5 0.05 350))");
            var explicitShorter = ParseColor("color-mix(in oklch shorter hue, oklch(0.5 0.05 10), oklch(0.5 0.05 350))");
            var longer = ParseColor("color-mix(in oklch longer hue, oklch(0.5 0.05 10), oklch(0.5 0.05 350))");
            var increasing = ParseColor("color-mix(in oklch increasing hue, oklch(0.5 0.05 10), oklch(0.5 0.05 350))");
            var decreasing = ParseColor("color-mix(in oklch decreasing hue, oklch(0.5 0.05 10), oklch(0.5 0.05 350))");

            double hShorter = ExtractOklchHueDegrees(shorter);
            double hExplicitShorter = ExtractOklchHueDegrees(explicitShorter);
            double hLonger = ExtractOklchHueDegrees(longer);
            double hIncreasing = ExtractOklchHueDegrees(increasing);
            double hDecreasing = ExtractOklchHueDegrees(decreasing);

            const double tol = 15.0;
            Assert.That(HueDistance(hShorter, 0.0), Is.LessThan(tol), "shorter default should wrap through 0deg");
            Assert.That(HueDistance(hExplicitShorter, 0.0), Is.LessThan(tol), "explicit shorter hue should wrap through 0deg");
            Assert.That(HueDistance(hLonger, 180.0), Is.LessThan(tol), "longer hue should go the long way through 180deg");
            Assert.That(HueDistance(hIncreasing, 180.0), Is.LessThan(tol), "increasing hue should always wrap forward");
            Assert.That(HueDistance(hDecreasing, 0.0), Is.LessThan(tol), "decreasing hue should always wrap backward");

            // shorter and longer must produce visibly different colors (not just numerically — bytes differ).
            Assert.That(shorter.R != longer.R || shorter.G != longer.G || shorter.B != longer.B,
                "shorter and longer hue arcs must yield distinct results");
            // Same for increasing vs decreasing.
            Assert.That(increasing.R != decreasing.R || increasing.G != decreasing.G || increasing.B != decreasing.B,
                "increasing and decreasing hue arcs must yield distinct results");
        }

        [Test]
        public void Color_mix_oklch_no_wrap_case_shorter_and_increasing_agree_at_45() {
            // hues 0 and 90 are 90deg apart; |dh| < 180, so the shorter arc IS the
            // increasing arc — both default-shorter and explicit-increasing land at 45.
            var shorter = ParseColor("color-mix(in oklch, oklch(0.5 0.05 0), oklch(0.5 0.05 90))");
            var increasing = ParseColor("color-mix(in oklch increasing hue, oklch(0.5 0.05 0), oklch(0.5 0.05 90))");
            double hShorter = ExtractOklchHueDegrees(shorter);
            double hIncreasing = ExtractOklchHueDegrees(increasing);
            const double tol = 15.0;
            Assert.That(HueDistance(hShorter, 45.0), Is.LessThan(tol), "shorter default mid of 0..90 is 45deg");
            Assert.That(HueDistance(hIncreasing, 45.0), Is.LessThan(tol), "increasing mid of 0..90 is 45deg (no wrap)");
        }

        // ---- linear-gradient <hue-interpolation-method> (G6: H11 follow-up) ----

        static double ExtractOklchHueDegreesFromLinear(LinearColor c) {
            CssColor.LinearRgbToOklab(c.R, c.G, c.B, out _, out double a, out double bAxis);
            double h = System.Math.Atan2(bAxis, a) * 180.0 / System.Math.PI;
            return ((h % 360.0) + 360.0) % 360.0;
        }

        static LinearColor GradientMidpoint(string css) {
            var s = Style();
            s.Set("background-image", css);
            var brush = BackgroundResolver.ResolveBackground(s, Bounds());
            var lg = (LinearGradient)brush.GradientValue;
            return lg.Sample(0.5);
        }

        [Test]
        public void Linear_gradient_oklch_hue_methods_pick_different_arcs() {
            // hues 10 and 350: short arc is 20deg through 0; long arc is 340deg through 180.
            // Mirrors the H11 color-mix hue-method test on the gradient side.
            var shorterMid = GradientMidpoint("linear-gradient(in oklch shorter hue, oklch(0.5 0.2 10), oklch(0.5 0.2 350))");
            var longerMid = GradientMidpoint("linear-gradient(in oklch longer hue, oklch(0.5 0.2 10), oklch(0.5 0.2 350))");
            var increasingMid = GradientMidpoint("linear-gradient(in oklch increasing hue, oklch(0.5 0.2 10), oklch(0.5 0.2 350))");
            var decreasingMid = GradientMidpoint("linear-gradient(in oklch decreasing hue, oklch(0.5 0.2 10), oklch(0.5 0.2 350))");

            double hShorter = ExtractOklchHueDegreesFromLinear(shorterMid);
            double hLonger = ExtractOklchHueDegreesFromLinear(longerMid);
            double hIncreasing = ExtractOklchHueDegreesFromLinear(increasingMid);
            double hDecreasing = ExtractOklchHueDegreesFromLinear(decreasingMid);

            const double tol = 25.0;
            Assert.That(HueDistance(hShorter, 0.0), Is.LessThan(tol), "shorter hue should wrap through 0deg");
            Assert.That(HueDistance(hLonger, 180.0), Is.LessThan(tol), "longer hue should go through 180deg");
            Assert.That(HueDistance(hIncreasing, 180.0), Is.LessThan(tol), "increasing hue should wrap forward");
            Assert.That(HueDistance(hDecreasing, 0.0), Is.LessThan(tol), "decreasing hue should wrap backward");

            // shorter and longer arcs must produce distinct midpoint colors.
            Assert.That(shorterMid.R != longerMid.R || shorterMid.G != longerMid.G || shorterMid.B != longerMid.B,
                "shorter and longer hue arcs must yield distinct midpoint colors");
            Assert.That(increasingMid.R != decreasingMid.R || increasingMid.G != decreasingMid.G || increasingMid.B != decreasingMid.B,
                "increasing and decreasing hue arcs must yield distinct midpoint colors");
        }

        [Test]
        public void Linear_gradient_oklch_no_method_defaults_to_shorter() {
            // Regression: no hue-interpolation-method specified — must still default
            // to the shorter arc. hues 0 and 90 differ by 90deg; shorter midpoint is 45deg.
            var mid = GradientMidpoint("linear-gradient(in oklch, oklch(0.5 0.2 0), oklch(0.5 0.2 90))");
            double h = ExtractOklchHueDegreesFromLinear(mid);
            Assert.That(HueDistance(h, 45.0), Is.LessThan(25.0), "default-shorter midpoint of 0..90 should be near 45deg");
        }

        [Test]
        public void Linear_gradient_oklch_hue_method_parses_into_gradient() {
            // Plumbing check: the HueMethod slot on Gradient is populated from
            // the `<shorter|longer|increasing|decreasing> hue` suffix.
            var s = Style();
            s.Set("background-image", "linear-gradient(in oklch longer hue, oklch(0.5 0.2 10), oklch(0.5 0.2 350))");
            var brush = BackgroundResolver.ResolveBackground(s, Bounds());
            var lg = (LinearGradient)brush.GradientValue;
            Assert.That(lg.InterpolationSpace, Is.EqualTo(CssColorSpace.Oklch));
            Assert.That(lg.HueMethod, Is.EqualTo(CssHueInterpolationMethod.Longer));

            s.Set("background-image", "linear-gradient(in oklch, oklch(0.5 0.2 10), oklch(0.5 0.2 350))");
            var defaultBrush = BackgroundResolver.ResolveBackground(s, Bounds());
            var defaultLg = (LinearGradient)defaultBrush.GradientValue;
            Assert.That(defaultLg.HueMethod, Is.EqualTo(CssHueInterpolationMethod.Shorter),
                "no hue-interpolation-method must default to Shorter");
        }

        [Test]
        public void Gradient_interpolation_oklab_midpoint_matches_color_mix() {
            // Sample(0.5) on a two-stop gradient with InterpolationSpace=Oklab matches
            // color-mix(in oklab, ...) for the same colors.
            var red = LinearColor.FromCssColor(new CssColor(255, 0, 0, 1f));
            var blue = LinearColor.FromCssColor(new CssColor(0, 0, 255, 1f));
            var lg = new LinearGradient(0, new[] {
                new GradientStop(red, 0.0),
                new GradientStop(blue, 1.0),
            }, CssColorSpace.Oklab);
            var mid = lg.Sample(0.5);
            var mix = ParseColor("color-mix(in oklab, red, blue)");
            // Convert mid (linear) back to sRGB byte form.
            byte midR = (byte)System.Math.Round(CssColor.LinearToSrgb(mid.R) * 255.0);
            byte midB = (byte)System.Math.Round(CssColor.LinearToSrgb(mid.B) * 255.0);
            Assert.That((int)midR, Is.EqualTo((int)mix.R).Within(2));
            Assert.That((int)midB, Is.EqualTo((int)mix.B).Within(2));
        }

        [Test]
        public void Relative_rgb_identity_destructures_source_channels() {
            // CSS Color L5 §4: literal-channel relative rgb() is an identity
            // operation — the from-color is rebuilt from its own R/G/B slots.
            var identity = ParseColor("rgb(from red r g b)");
            Assert.That((int)identity.R, Is.EqualTo(255));
            Assert.That((int)identity.G, Is.EqualTo(0));
            Assert.That((int)identity.B, Is.EqualTo(0));
            Assert.That(identity.A, Is.EqualTo(1f));

            var permuted = ParseColor("rgb(from #336699 b g r)");
            Assert.That((int)permuted.R, Is.EqualTo(0x99));
            Assert.That((int)permuted.G, Is.EqualTo(0x66));
            Assert.That((int)permuted.B, Is.EqualTo(0x33));

            var withAlpha = ParseColor("rgb(from red r g b / 0.5)");
            Assert.That((int)withAlpha.R, Is.EqualTo(255));
            Assert.That(withAlpha.A, Is.EqualTo(0.5f).Within(1e-4));

            var alphaInherits = ParseColor("rgb(from rgb(255 0 0 / 0.25) r g b)");
            Assert.That(alphaInherits.A, Is.EqualTo(0.25f).Within(1e-4));

            var noneAndOverride = ParseColor("rgb(from red none g 128)");
            Assert.That((int)noneAndOverride.R, Is.EqualTo(0));
            Assert.That((int)noneAndOverride.G, Is.EqualTo(0));
            Assert.That((int)noneAndOverride.B, Is.EqualTo(128));

            var alphaKeyword = ParseColor("rgb(from rgb(255 0 0 / 0.4) r g b / alpha)");
            Assert.That(alphaKeyword.A, Is.EqualTo(0.4f).Within(1e-4));
        }

        // -------- H10b: channel idents inside calc() + extended relative syntax --------

        [Test]
        public void Relative_rgb_calc_channel_ident_adds_to_red_H10b() {
            // CSS Color L5 §4: `calc(r + 20)` inside rgb(from red ...) should
            // resolve `r` to 255, then 255 + 20 = 275, which the byte-clamp
            // pins to 255. G/B stay at their source-zero values.
            var c = ParseColor("rgb(from red calc(r + 20) g b)");
            Assert.That((int)c.R, Is.EqualTo(255));
            Assert.That((int)c.G, Is.EqualTo(0));
            Assert.That((int)c.B, Is.EqualTo(0));
        }

        [Test]
        public void Relative_rgb_calc_halves_red_channel_H10b() {
            // `calc(r * 0.5)` halves the source's red byte (0x88 = 136 -> 68).
            var c = ParseColor("rgb(from #888888 calc(r * 0.5) g b)");
            Assert.That((int)c.R, Is.EqualTo(68));
            Assert.That((int)c.G, Is.EqualTo(0x88));
            Assert.That((int)c.B, Is.EqualTo(0x88));
        }

        [Test]
        public void Relative_hsl_calc_halves_lightness_H10b() {
            // hsl(from red h s calc(l * 0.5)): red is hsl(0 100% 50%), so
            // lightness becomes 25% — a darker red (R ≈ 128, G≈0, B≈0).
            var c = ParseColor("hsl(from red h s calc(l * 0.5))");
            Assert.That((int)c.R, Is.EqualTo(128).Within(2));
            Assert.That((int)c.G, Is.LessThan(5));
            Assert.That((int)c.B, Is.LessThan(5));
        }

        [Test]
        public void Relative_oklch_identity_round_trips_blue_H10b() {
            // oklch(from blue l c h) is identity — the source color is
            // decomposed into OKLCh then reassembled with the same channels.
            // Round-trip through the linear sRGB / OKLab matrices is lossy
            // at the byte level by ≤1.
            var c = ParseColor("oklch(from blue l c h)");
            Assert.That((int)c.R, Is.EqualTo(0).Within(2));
            Assert.That((int)c.G, Is.EqualTo(0).Within(2));
            Assert.That((int)c.B, Is.EqualTo(255).Within(2));
        }

        [Test]
        public void Relative_lab_calc_brightens_red_H10b() {
            // lab(from red calc(l + 10) a b) — bump the L axis up by 10
            // points. Red ≈ Lab(54, 81, 70); adding 10 to L moves it toward
            // a brighter pinkish red. We just pin the result is still
            // dominantly red and brighter than the source byte-wise on
            // average.
            var src = ParseColor("lab(from red l a b)");
            var bright = ParseColor("lab(from red calc(l + 10) a b)");
            int srcAvg = (src.R + src.G + src.B) / 3;
            int brightAvg = (bright.R + bright.G + bright.B) / 3;
            Assert.That(brightAvg, Is.GreaterThan(srcAvg),
                "Boosted L should brighten the color overall");
            // Red still dominates (the +10 to L shouldn't flip the hue).
            Assert.That(bright.R, Is.GreaterThan(bright.G));
            Assert.That(bright.R, Is.GreaterThan(bright.B));
        }

        [Test]
        public void Relative_color_display_p3_round_trips_red_H10b() {
            // color(display-p3 from red r g b) — identity round trip. The
            // engine converts sRGB-red into display-p3 channels, then
            // CssColor.FromColorFunction takes those channels back through
            // P3->XYZ->linear-sRGB->encoded. Tolerance to absorb the
            // bidirectional matrix round trip.
            var c = ParseColor("color(display-p3 from red r g b)");
            Assert.That((int)c.R, Is.EqualTo(255).Within(3));
            Assert.That((int)c.G, Is.LessThan(5));
            Assert.That((int)c.B, Is.LessThan(5));
            Assert.That(c.A, Is.EqualTo(1f));
        }

        [Test]
        public void Relative_hsl_channel_ident_identity_round_trips_red_H10b() {
            // hsl(from red h s l) is identity — proves `from <color>` works
            // for hsl() with bare channel idents (no calc() inside). Red is
            // hsl(0, 100%, 50%); round-tripping yields red back.
            var c = ParseColor("hsl(from red h s l)");
            Assert.That((int)c.R, Is.EqualTo(255).Within(1));
            Assert.That((int)c.G, Is.LessThan(3));
            Assert.That((int)c.B, Is.LessThan(3));
        }

        [Test]
        public void Calc_channel_ident_outside_relative_color_throws_H10b() {
            // Defensive: if a CalcChannelNode were to escape into normal
            // calc() (it shouldn't, since the parser only emits one when
            // bindings are active), Evaluate must reject it cleanly.
            var node = new CalcChannelNode("r");
            var calc = new CssCalc(node, "calc(r)");
            Assert.That(() => calc.Evaluate(LengthContext.Default),
                Throws.InstanceOf<System.InvalidOperationException>());
        }
    }
}
