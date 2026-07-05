using NUnit.Framework;
using Weva.Css.Animation;
using Weva.Css.Values;

namespace Weva.Tests.Css.Animation {
    public class ValueInterpolatorTests {
        static LengthContext Ctx() => LengthContext.Default;

        [Test]
        public void Length_lerp_same_unit() {
            var v = ValueInterpolator.Interpolate("10px", "20px", 0.5, PropertyKind.Length, Ctx());
            Assert.That(v, Is.EqualTo("15px"));
        }

        [Test]
        public void Length_lerp_at_start() {
            var v = ValueInterpolator.Interpolate("10px", "20px", 0, PropertyKind.Length, Ctx());
            Assert.That(v, Is.EqualTo("10px"));
        }

        [Test]
        public void Length_lerp_at_end() {
            var v = ValueInterpolator.Interpolate("10px", "20px", 1, PropertyKind.Length, Ctx());
            Assert.That(v, Is.EqualTo("20px"));
        }

        [Test]
        public void Length_lerp_with_mixed_units_resolves_to_pixels() {
            // 16px (1em with default 16px) -> 32px at t=0.5 -> 24px.
            var v = ValueInterpolator.Interpolate("1em", "32px", 0.5, PropertyKind.Length, Ctx());
            Assert.That(v, Is.EqualTo("24px"));
        }

        [Test]
        public void Color_lerp_black_to_white_midpoint_is_oklab_grey() {
            // CSS Color L4 §12: default color interpolation is oklab. Black→white at
            // t=0.5 in oklab is L=0.5,a=0,b=0 — the perceptually-uniform mid grey,
            // which round-trips back through linear-RGB → sRGB to ~rgb(99,99,99).
            // (Linear-RGB lerp would give ~rgb(188,…); naive sRGB byte lerp would
            // give rgb(128,…). Bracket the oklab answer well clear of both.)
            var v = ValueInterpolator.Interpolate("#000", "#fff", 0.5, PropertyKind.Color, Ctx());
            Assert.That(v, Does.Match("rgb\\((\\d+), \\1, \\1\\)"));
            int open = v.IndexOf('(') + 1;
            int comma = v.IndexOf(',');
            int channel = int.Parse(v.Substring(open, comma - open));
            Assert.That(channel, Is.InRange(80, 120));
        }

        [Test]
        public void Number_lerp() {
            var v = ValueInterpolator.Interpolate("0", "1", 0.5, PropertyKind.Number, Ctx());
            Assert.That(v, Is.EqualTo("0.5"));
        }

        [Test]
        public void Number_lerp_at_zero() {
            var v = ValueInterpolator.Interpolate("0", "1", 0, PropertyKind.Number, Ctx());
            Assert.That(v, Is.EqualTo("0"));
        }

        [Test]
        public void Percentage_lerp_keeps_unit() {
            var v = ValueInterpolator.Interpolate("0%", "100%", 0.5, PropertyKind.Percentage, Ctx());
            Assert.That(v, Is.EqualTo("50%"));
        }

        [Test]
        public void Discrete_switches_at_half() {
            Assert.That(ValueInterpolator.Interpolate("block", "inline", 0.4, PropertyKind.Discrete, Ctx()),
                Is.EqualTo("block"));
            Assert.That(ValueInterpolator.Interpolate("block", "inline", 0.5, PropertyKind.Discrete, Ctx()),
                Is.EqualTo("inline"));
            Assert.That(ValueInterpolator.Interpolate("block", "inline", 0.6, PropertyKind.Discrete, Ctx()),
                Is.EqualTo("inline"));
        }

        [Test]
        public void Transform_translate_lerp_per_component() {
            var v = ValueInterpolator.Interpolate("translate(0, 0)", "translate(10px, 20px)", 0.5,
                PropertyKind.Transform, Ctx());
            Assert.That(v, Does.StartWith("translate("));
            Assert.That(v, Does.Contain("5"));
            Assert.That(v, Does.Contain("10"));
        }

        [Test]
        public void Transform_mismatched_function_lists_use_matrix_decomposition_G9() {
            // CSS Transforms L1 §17 / G9: mismatched shapes used to step
            // discretely at t=0.5; we now collapse both sides to a 2D matrix
            // and lerp the decomposed components. translate(0,0) is the
            // identity, so the result is a pure rotation matrix at the lerp
            // fraction. Exhaustive numeric assertions live in
            // TransformMatrixDecompositionTests; the assertions here only
            // pin the contract change (no longer discrete-stepped).
            var midLow = ValueInterpolator.Interpolate("translate(0, 0)", "rotate(45deg)", 0.4,
                PropertyKind.Transform, Ctx());
            Assert.That(midLow, Does.StartWith("matrix("));
            var midHigh = ValueInterpolator.Interpolate("translate(0, 0)", "rotate(45deg)", 0.6,
                PropertyKind.Transform, Ctx());
            Assert.That(midHigh, Does.StartWith("matrix("));
        }

        [Test]
        public void Transform_rotate_in_degrees() {
            var v = ValueInterpolator.Interpolate("rotate(0deg)", "rotate(90deg)", 0.5,
                PropertyKind.Transform, Ctx());
            Assert.That(v, Is.EqualTo("rotate(45deg)"));
        }

        [Test]
        public void Filter_brightness_lerps_matching_function() {
            var v = ValueInterpolator.Interpolate("brightness(1)", "brightness(1.5)", 0.5,
                PropertyKind.Filter, Ctx());
            Assert.That(v, Is.EqualTo("brightness(1.25)"));
        }

        [Test]
        public void Filter_none_lerps_from_identity_function_list() {
            var v = ValueInterpolator.Interpolate("none", "blur(10px) saturate(2)", 0.5,
                PropertyKind.Filter, Ctx());
            Assert.That(v, Does.Contain("blur(5px)"));
            Assert.That(v, Does.Contain("saturate(1.5)"));
        }

        [Test]
        public void Filter_mismatched_function_lists_fall_back_to_discrete() {
            var low = ValueInterpolator.Interpolate("brightness(1)", "saturate(2)", 0.4,
                PropertyKind.Filter, Ctx());
            var high = ValueInterpolator.Interpolate("brightness(1)", "saturate(2)", 0.6,
                PropertyKind.Filter, Ctx());
            Assert.That(low, Is.EqualTo("brightness(1)"));
            Assert.That(high, Is.EqualTo("saturate(2)"));
        }

        [Test]
        public void T_below_zero_clamps_to_from() {
            var v = ValueInterpolator.Interpolate("10px", "20px", -0.5, PropertyKind.Length, Ctx());
            Assert.That(v, Is.EqualTo("10px"));
        }

        [Test]
        public void T_above_one_clamps_to_to() {
            var v = ValueInterpolator.Interpolate("10px", "20px", 1.5, PropertyKind.Length, Ctx());
            Assert.That(v, Is.EqualTo("20px"));
        }

        [Test]
        public void Length_with_both_zero_works() {
            var v = ValueInterpolator.Interpolate("0", "10px", 0.5, PropertyKind.Length, Ctx());
            // 0 is parseable as a unitless 0 length.
            Assert.That(v, Does.EndWith("px"));
        }

        [Test]
        public void Color_named_to_hex_blends() {
            var v = ValueInterpolator.Interpolate("black", "white", 0.5, PropertyKind.Color, Ctx());
            Assert.That(v, Does.StartWith("rgb("));
        }

        [Test]
        public void Color_with_alpha_emits_rgba() {
            var v = ValueInterpolator.Interpolate("rgba(0, 0, 0, 0)", "rgba(255, 255, 255, 1)", 0.5,
                PropertyKind.Color, Ctx());
            Assert.That(v, Does.StartWith("rgba("));
        }

        [Test]
        public void Number_unparseable_falls_back_to_discrete() {
            var v = ValueInterpolator.Interpolate("auto", "200", 0.4, PropertyKind.Number, Ctx());
            Assert.That(v, Is.EqualTo("auto"));
            var w = ValueInterpolator.Interpolate("auto", "200", 0.6, PropertyKind.Number, Ctx());
            Assert.That(w, Is.EqualTo("200"));
        }

        [Test]
        public void Length_unparseable_falls_back_to_discrete() {
            var v = ValueInterpolator.Interpolate("auto", "10px", 0.4, PropertyKind.Length, Ctx());
            Assert.That(v, Is.EqualTo("auto"));
        }

        // --- H14: color interpolation in oklab (CSS Color L4 §12) ---

        // Parses an "rgb(R, G, B)" / "rgba(R, G, B, A)" emitted by the interpolator
        // back into byte channels + alpha. Tests below need to inspect the numeric
        // output; the formatter contract is locked at "rgb(r, g, b)" with comma+space
        // separators (see InterpolateColorTyped).
        static void ParseRgbOut(string text, out int r, out int g, out int b, out double alpha) {
            string body = text.Substring(text.IndexOf('(') + 1);
            body = body.Substring(0, body.IndexOf(')'));
            string[] parts = body.Split(',');
            r = int.Parse(parts[0].Trim(), System.Globalization.CultureInfo.InvariantCulture);
            g = int.Parse(parts[1].Trim(), System.Globalization.CultureInfo.InvariantCulture);
            b = int.Parse(parts[2].Trim(), System.Globalization.CultureInfo.InvariantCulture);
            alpha = parts.Length >= 4
                ? double.Parse(parts[3].Trim(), System.Globalization.CultureInfo.InvariantCulture)
                : 1.0;
        }

        [Test]
        public void Color_red_to_blue_midpoint_uses_oklab_not_linear_rgb() {
            // CSS Color L4 §12 default is oklab. Linear-RGB midpoint of red→blue is
            // (R=0.5,G=0,B=0.5) linear → sRGB byte ~188 on both R and B (a pinkish
            // grey-magenta). Oklab midpoint passes through a perceptually-uniform
            // purple with markedly different green-axis tilt and a much lower red
            // channel. Any reasonable threshold separates them — we assert the red
            // channel differs from the linear-RGB answer (~188) by at least 20
            // bytes, which proves the lerp went through oklab rather than linear.
            var oklabMid = ValueInterpolator.Interpolate(
                "rgb(255, 0, 0)", "rgb(0, 0, 255)", 0.5, PropertyKind.Color, Ctx());
            ParseRgbOut(oklabMid, out int r, out int g, out int b, out double _);
            // Linear-RGB lerp would give R ≈ 188. Oklab lerp gives R ≈ 142 (the
            // perceptually-balanced purple). Use a 20-byte buffer; the actual gap
            // is larger but this guards against floating-point drift.
            int diffFromLinearR = System.Math.Abs(r - 188);
            Assert.That(diffFromLinearR, Is.GreaterThanOrEqualTo(20),
                "oklab red channel should differ from linear-RGB midpoint (~188) by at least 20 bytes; got rgb(" + r + "," + g + "," + b + ")");
            // Both endpoints have zero green; an oklab interpolation between two
            // colors that share zero in a linear-RGB channel typically still passes
            // through nonzero green at the perceptually-balanced midpoint (the
            // chromaticity arc bends through the green region). Linear-RGB lerp
            // gives G = 0 exactly. Asserting G > 0 is a second wire-proof.
            Assert.That(g, Is.GreaterThan(0),
                "oklab midpoint between red and blue should have nonzero green channel");
        }

        [Test]
        public void Color_identity_lerp_red_to_red_returns_red() {
            // Identity lerp must round-trip through oklab without drift. Red is
            // out-of-gamut-adjacent in oklab (sits near the LMS cone edge), so
            // accumulated float error from the LinearRgbToOklab / OklabToLinearRgb
            // roundtrip is the worst-case for this test.
            var v = ValueInterpolator.Interpolate(
                "rgb(255, 0, 0)", "rgb(255, 0, 0)", 0.5, PropertyKind.Color, Ctx());
            ParseRgbOut(v, out int r, out int g, out int b, out double alpha);
            Assert.That(r, Is.EqualTo(255));
            Assert.That(g, Is.EqualTo(0));
            Assert.That(b, Is.EqualTo(0));
            Assert.That(alpha, Is.EqualTo(1.0));
        }

        [Test]
        public void Color_alpha_midpoint_lerps_linearly_in_component_space() {
            // CSS Color L4 §12.1: alpha is interpolated linearly in component-space
            // (i.e. straight-alpha, not premultiplied with the color channels). For
            // rgba(255,0,0,1) → rgba(0,0,255,0) at t=0.5, the alpha component must be
            // exactly 0.5 regardless of which color space the RGB channels lerp in.
            var v = ValueInterpolator.Interpolate(
                "rgba(255, 0, 0, 1)", "rgba(0, 0, 255, 0)", 0.5, PropertyKind.Color, Ctx());
            Assert.That(v, Does.StartWith("rgba("));
            ParseRgbOut(v, out int _, out int _, out int _, out double alpha);
            Assert.That(alpha, Is.EqualTo(0.5).Within(0.01));
        }
    }
}
