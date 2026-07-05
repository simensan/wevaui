using NUnit.Framework;
using Weva.Css.Values;

namespace Weva.Tests.Css.Values {
    public class CssColorTests {
        static CssColor ParseColor(string s) => (CssColor)CssValueParser.Parse(s);

        [Test]
        public void Hex3_expands_to_rrggbb() {
            var c = ParseColor("#fff");
            Assert.That(c.R, Is.EqualTo(255));
            Assert.That(c.G, Is.EqualTo(255));
            Assert.That(c.B, Is.EqualTo(255));
            Assert.That(c.A, Is.EqualTo(1f));
        }

        [Test]
        public void Hex3_uppercase_same_as_lowercase() {
            var a = ParseColor("#FFF");
            var b = ParseColor("#fff");
            Assert.That(a.R, Is.EqualTo(b.R));
            Assert.That(a.G, Is.EqualTo(b.G));
            Assert.That(a.B, Is.EqualTo(b.B));
        }

        [Test]
        public void Hex4_expands_with_alpha() {
            var c = ParseColor("#f00f");
            Assert.That(c.R, Is.EqualTo(255));
            Assert.That(c.G, Is.EqualTo(0));
            Assert.That(c.B, Is.EqualTo(0));
            Assert.That(c.A, Is.EqualTo(1f));
        }

        [Test]
        public void Hex4_zero_alpha() {
            var c = ParseColor("#f000");
            Assert.That(c.A, Is.EqualTo(0f).Within(0.01));
        }

        [Test]
        public void Hex6_parses() {
            var c = ParseColor("#ff8800");
            Assert.That(c.R, Is.EqualTo(255));
            Assert.That(c.G, Is.EqualTo(136));
            Assert.That(c.B, Is.EqualTo(0));
        }

        [Test]
        public void Hex8_parses_with_alpha() {
            var c = ParseColor("#ff000080");
            Assert.That(c.R, Is.EqualTo(255));
            Assert.That(c.A, Is.EqualTo(128f / 255f).Within(1e-4));
        }

        [Test]
        public void Rgb_integer_legacy_comma() {
            var c = ParseColor("rgb(255, 0, 0)");
            Assert.That(c.R, Is.EqualTo(255));
            Assert.That(c.G, Is.EqualTo(0));
            Assert.That(c.B, Is.EqualTo(0));
        }

        [Test]
        public void Rgb_integer_modern_space() {
            var c = ParseColor("rgb(255 0 0)");
            Assert.That(c.R, Is.EqualTo(255));
            Assert.That(c.G, Is.EqualTo(0));
            Assert.That(c.B, Is.EqualTo(0));
        }

        [Test]
        public void Rgb_percent_red() {
            var c = ParseColor("rgb(100% 0% 0%)");
            Assert.That(c.R, Is.EqualTo(255));
            Assert.That(c.G, Is.EqualTo(0));
        }

        [Test]
        public void Rgb_modern_with_slash_alpha() {
            var c = ParseColor("rgb(255 0 0 / 50%)");
            Assert.That(c.R, Is.EqualTo(255));
            Assert.That(c.A, Is.EqualTo(0.5f).Within(1e-4));
        }

        [Test]
        public void Rgba_legacy_with_alpha() {
            var c = ParseColor("rgba(255, 0, 0, 0.25)");
            Assert.That(c.R, Is.EqualTo(255));
            Assert.That(c.A, Is.EqualTo(0.25f).Within(1e-4));
        }

        [Test]
        public void Hsl_legacy_pure_red() {
            var c = ParseColor("hsl(0, 100%, 50%)");
            Assert.That(c.R, Is.EqualTo(255));
            Assert.That(c.G, Is.EqualTo(0));
            Assert.That(c.B, Is.EqualTo(0));
        }

        [Test]
        public void Hsl_modern_pure_green() {
            var c = ParseColor("hsl(120 100% 50%)");
            Assert.That(c.R, Is.EqualTo(0));
            Assert.That(c.G, Is.EqualTo(255));
            Assert.That(c.B, Is.EqualTo(0));
        }

        [Test]
        public void Hsl_unitless_hue_is_degrees() {
            var c = ParseColor("hsl(240, 100%, 50%)");
            Assert.That(c.B, Is.EqualTo(255));
        }

        [Test]
        public void Hsl_turn_hue() {
            var c = ParseColor("hsl(0.5turn, 100%, 50%)");
            Assert.That(c.G, Is.EqualTo(255));
            Assert.That(c.B, Is.EqualTo(255));
        }

        [Test]
        public void Hsl_rad_hue() {
            var c = ParseColor("hsl(3.14159rad, 100%, 50%)");
            Assert.That(c.G, Is.GreaterThan(200));
        }

        [Test]
        public void Hsl_grad_hue() {
            var c = ParseColor("hsl(200grad, 100%, 50%)");
            Assert.That(c.G, Is.EqualTo(255));
            Assert.That(c.B, Is.EqualTo(255));
        }

        [Test]
        public void Hsla_with_slash_alpha() {
            var c = ParseColor("hsl(0 100% 50% / 0.5)");
            Assert.That(c.A, Is.EqualTo(0.5f).Within(1e-4));
        }

        [Test]
        public void Named_red() {
            var c = ParseColor("red");
            Assert.That(c.R, Is.EqualTo(255));
        }

        [Test]
        public void Named_blue() {
            var c = ParseColor("blue");
            Assert.That(c.B, Is.EqualTo(255));
        }

        [Test]
        public void Named_rebeccapurple() {
            var c = ParseColor("rebeccapurple");
            Assert.That(c.R, Is.EqualTo(102));
            Assert.That(c.G, Is.EqualTo(51));
            Assert.That(c.B, Is.EqualTo(153));
        }

        [Test]
        public void Transparent_is_zero_alpha() {
            var c = ParseColor("transparent");
            Assert.That(c.A, Is.EqualTo(0f));
        }

        [Test]
        public void CurrentColor_is_keyword_sentinel() {
            var v = CssValueParser.Parse("currentColor");
            Assert.That(v, Is.InstanceOf<CssKeyword>());
            Assert.That(((CssKeyword)v).Identifier, Is.EqualTo("currentcolor"));
        }

        [Test]
        public void Bad_hex_digit_throws() {
            Assert.Throws<CssValueParseException>(() => ParseColor("#xyz"));
        }

        [Test]
        public void Bad_hex_length_throws() {
            Assert.Throws<CssValueParseException>(() => ParseColor("#ff"));
        }

        [Test]
        public void Negative_hue_wraps() {
            var c = ParseColor("hsl(-120, 100%, 50%)");
            Assert.That(c.B, Is.EqualTo(255));
        }

        [Test]
        public void Hue_over_360_wraps() {
            var c = ParseColor("hsl(480, 100%, 50%)");
            Assert.That(c.G, Is.EqualTo(255));
        }

        [Test]
        public void Rgb_missing_arg_throws() {
            Assert.Throws<CssValueParseException>(() => ParseColor("rgb(255, 0)"));
        }

        [Test]
        public void TryFromName_red() {
            Assert.That(CssColor.TryFromName("red", out var c), Is.True);
            Assert.That(c.R, Is.EqualTo(255));
        }

        [Test]
        public void TryFromName_unknown_returns_false() {
            Assert.That(CssColor.TryFromName("notacolor", out _), Is.False);
        }

        // ----- D2 regression: CssColor.FromHsl / FromHwb now delegate to
        // the consolidated ColorMath.HslToRgb01 (LA6). The public wrappers
        // still accept CSS-spec 0..100 inputs for saturation / lightness /
        // white / black; we pin those samples here so a future refactor
        // can't drop the /100 scaling without breaking the byte output.

        [Test]
        public void D2_FromHsl_pure_red_0_100_100_50_matches_ColorMath() {
            // hsl(0, 100%, 50%) -> sRGB red. Independently compute via
            // ColorMath on the 0..1 scale and assert the public 0..100
            // wrapper packs to the same bytes.
            ColorMath.HslToRgb01(0, 100.0 / 100.0, 50.0 / 100.0, out double r, out double g, out double b);
            var c = CssColor.FromHsl(0, 100, 50, 1.0, "hsl(0,100%,50%)");
            Assert.That(c.R, Is.EqualTo((byte)System.Math.Round(r * 255.0)));
            Assert.That(c.G, Is.EqualTo((byte)System.Math.Round(g * 255.0)));
            Assert.That(c.B, Is.EqualTo((byte)System.Math.Round(b * 255.0)));
            Assert.That(c.R, Is.EqualTo(255));
            Assert.That(c.G, Is.EqualTo(0));
            Assert.That(c.B, Is.EqualTo(0));
        }

        [Test]
        public void D2_FromHsl_pure_green_120_100_50_matches_ColorMath() {
            ColorMath.HslToRgb01(120, 100.0 / 100.0, 50.0 / 100.0, out double r, out double g, out double b);
            var c = CssColor.FromHsl(120, 100, 50, 1.0, "hsl(120,100%,50%)");
            Assert.That(c.R, Is.EqualTo((byte)System.Math.Round(r * 255.0)));
            Assert.That(c.G, Is.EqualTo((byte)System.Math.Round(g * 255.0)));
            Assert.That(c.B, Is.EqualTo((byte)System.Math.Round(b * 255.0)));
            Assert.That(c.G, Is.EqualTo(255));
        }

        [Test]
        public void D2_FromHsl_partial_saturation_240_50_25_matches_ColorMath() {
            // hsl(240, 50%, 25%) — non-trivial S/L on the 0..100 scale.
            ColorMath.HslToRgb01(240, 50.0 / 100.0, 25.0 / 100.0, out double r, out double g, out double b);
            var c = CssColor.FromHsl(240, 50, 25, 1.0, "hsl(240,50%,25%)");
            Assert.That(c.R, Is.EqualTo((byte)System.Math.Round(r * 255.0)));
            Assert.That(c.G, Is.EqualTo((byte)System.Math.Round(g * 255.0)));
            Assert.That(c.B, Is.EqualTo((byte)System.Math.Round(b * 255.0)));
        }

        [Test]
        public void D2_FromHsl_negative_hue_wraps_through_ColorMath() {
            // -120 should wrap to 240 (blue) per CSS Color 4 §6.
            ColorMath.HslToRgb01(-120, 100.0 / 100.0, 50.0 / 100.0, out double r, out double g, out double b);
            var c = CssColor.FromHsl(-120, 100, 50, 1.0, "hsl(-120,100%,50%)");
            Assert.That(c.R, Is.EqualTo((byte)System.Math.Round(r * 255.0)));
            Assert.That(c.G, Is.EqualTo((byte)System.Math.Round(g * 255.0)));
            Assert.That(c.B, Is.EqualTo((byte)System.Math.Round(b * 255.0)));
            Assert.That(c.B, Is.EqualTo(255));
        }

        [Test]
        public void D2_FromHwb_partial_120_20_30_matches_ColorMath_baseline() {
            // FromHwb computes a pure-hue baseline via HslToRgb01(h, 1, 0.5)
            // and then folds in W/Bk. Pin against the same ColorMath call
            // the production code now uses.
            const double hue = 120, whitePct = 20, blackPct = 30;
            double w = whitePct / 100.0;
            double bk = blackPct / 100.0;
            ColorMath.HslToRgb01(hue, 1.0, 0.5, out double rD, out double gD, out double bD);
            rD = rD * (1 - w - bk) + w;
            gD = gD * (1 - w - bk) + w;
            bD = bD * (1 - w - bk) + w;
            byte expectedR = (byte)System.Math.Round(rD * 255.0);
            byte expectedG = (byte)System.Math.Round(gD * 255.0);
            byte expectedB = (byte)System.Math.Round(bD * 255.0);
            var c = CssColor.FromHwb(hue, whitePct, blackPct, 1.0, "hwb(120 20% 30%)");
            Assert.That(c.R, Is.EqualTo(expectedR));
            Assert.That(c.G, Is.EqualTo(expectedG));
            Assert.That(c.B, Is.EqualTo(expectedB));
        }
    }
}
