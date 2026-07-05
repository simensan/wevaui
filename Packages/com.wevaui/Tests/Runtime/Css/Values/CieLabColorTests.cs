using NUnit.Framework;
using Weva.Css.Values;

namespace Weva.Tests.Css.Values {
    public class CieLabColorTests {
        static CssColor ParseColor(string s) => (CssColor)CssValueParser.Parse(s);

        [Test]
        public void Lab_parses_basic_form() {
            var c = ParseColor("lab(50% 50 -30)");
            Assert.That(c.A, Is.EqualTo(1f));
            Assert.That((int)c.R + (int)c.G + (int)c.B, Is.GreaterThan(0));
        }

        [Test]
        public void Lab_parses_with_slash_alpha() {
            var c = ParseColor("lab(50 50 -30 / 0.5)");
            Assert.That(c.A, Is.EqualTo(0.5f).Within(1e-4));
        }

        [Test]
        public void Lab_neutral_grey_is_mid_srgb_grey() {
            // L=50, a=0, b=0 is D50 mid-grey. After Bradford D50->D65 and sRGB
            // encoding it should be a near-neutral grey at ~50% sRGB lightness
            // (~118-119 in 8-bit, per CSS Color 4 sample values).
            var c = ParseColor("lab(50% 0 0)");
            Assert.That((int)c.R, Is.EqualTo(118).Within(3));
            Assert.That((int)c.G, Is.EqualTo(118).Within(3));
            Assert.That((int)c.B, Is.EqualTo(118).Within(3));
            int avg = (c.R + c.G + c.B) / 3;
            Assert.That(System.Math.Abs(c.R - avg), Is.LessThanOrEqualTo(2));
            Assert.That(System.Math.Abs(c.G - avg), Is.LessThanOrEqualTo(2));
            Assert.That(System.Math.Abs(c.B - avg), Is.LessThanOrEqualTo(2));
        }

        [Test]
        public void Lch_parses_basic_form() {
            var c = ParseColor("lch(50% 50 270)");
            Assert.That(c.A, Is.EqualTo(1f));
            Assert.That((int)c.R + (int)c.G + (int)c.B, Is.GreaterThan(0));
        }

        [Test]
        public void Lch_accepts_deg_unit_on_hue() {
            var bare = ParseColor("lch(50 50 270)");
            var deg = ParseColor("lch(50 50 270deg)");
            Assert.That((int)deg.R, Is.EqualTo((int)bare.R));
            Assert.That((int)deg.G, Is.EqualTo((int)bare.G));
            Assert.That((int)deg.B, Is.EqualTo((int)bare.B));
        }

        [Test]
        public void Lch_accepts_other_angular_units() {
            // 0.5turn = 180deg, π rad = 180deg, 200grad = 180deg — same color.
            var deg = ParseColor("lch(60 40 180deg)");
            var turn = ParseColor("lch(60 40 0.5turn)");
            var rad = ParseColor("lch(60 40 3.14159265rad)");
            var grad = ParseColor("lch(60 40 200grad)");
            Assert.That((int)turn.R, Is.EqualTo((int)deg.R).Within(1));
            Assert.That((int)rad.R, Is.EqualTo((int)deg.R).Within(1));
            Assert.That((int)grad.R, Is.EqualTo((int)deg.R).Within(1));
        }

        [Test]
        public void Lch_270_deg_resolves_blueish() {
            // CIELCh hue 270 is blue-leaning — blue channel should dominate.
            var c = ParseColor("lch(50% 50 270)");
            Assert.That(c.B, Is.GreaterThan(c.R));
            Assert.That(c.B, Is.GreaterThan(c.G));
        }

        [Test]
        public void Lab_alpha_percentage_form() {
            var c = ParseColor("lab(50 0 0 / 50%)");
            Assert.That(c.A, Is.EqualTo(0.5f).Within(1e-4));
        }

        [Test]
        public void Lab_zero_lightness_is_black() {
            var c = ParseColor("lab(0 0 0)");
            Assert.That((int)c.R, Is.LessThanOrEqualTo(2));
            Assert.That((int)c.G, Is.LessThanOrEqualTo(2));
            Assert.That((int)c.B, Is.LessThanOrEqualTo(2));
        }

        [Test]
        public void Lab_full_lightness_is_white() {
            var c = ParseColor("lab(100 0 0)");
            Assert.That((int)c.R, Is.EqualTo(255));
            Assert.That((int)c.G, Is.EqualTo(255));
            Assert.That((int)c.B, Is.EqualTo(255));
        }

        [Test]
        public void Malformed_lab_missing_axis_throws() {
            Assert.Throws<CssValueParseException>(() => ParseColor("lab(50% 0)"));
        }

        [Test]
        public void Malformed_lch_missing_hue_throws() {
            Assert.Throws<CssValueParseException>(() => ParseColor("lch(50% 50)"));
        }

        [Test]
        public void Oklab_still_parses_regression() {
            // Guard: adding lab() must not have broken the existing oklab path.
            var c = ParseColor("oklab(0.5 0 0)");
            Assert.That(c.A, Is.EqualTo(1f));
            int avg = (c.R + c.G + c.B) / 3;
            Assert.That(System.Math.Abs(c.R - avg), Is.LessThanOrEqualTo(2));
            Assert.That(System.Math.Abs(c.G - avg), Is.LessThanOrEqualTo(2));
            Assert.That(System.Math.Abs(c.B - avg), Is.LessThanOrEqualTo(2));
        }

        [Test]
        public void Oklch_still_parses_regression() {
            var c = ParseColor("oklch(70% 0.15 270)");
            Assert.That(c.A, Is.EqualTo(1f));
            Assert.That(c.B, Is.GreaterThan(c.R));
        }
    }
}
