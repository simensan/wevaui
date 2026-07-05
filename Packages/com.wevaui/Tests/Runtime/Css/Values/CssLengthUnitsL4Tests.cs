using NUnit.Framework;
using Weva.Css.Values;

namespace Weva.Tests.Css.Values {
    // Spec coverage for the CSS Values L3/L4 font-relative + viewport-variant units
    // registered in CssLength.ToPixels / TryParseUnit. v1 approximations:
    //   cap -> 0.7em, ic -> 1em, lh -> resolved line-height (fallback 1.2em),
    //   svw/lvw/dvw == vw, svh/lvh/dvh == vh (no dynamic UI chrome in this engine).
    public class CssLengthUnitsL4Tests {
        static CssLength ParseLength(string s) {
            return (CssLength)CssValueParser.Parse(s);
        }

        static LengthContext Ctx(double w = 1000, double h = 800, double fs = 20, double rfs = 16, double lh = 0, double rlh = 0) {
            var c = LengthContext.Default;
            c.BaseFontSizePx = fs;
            c.RootFontSizePx = rfs;
            c.ViewportWidthPx = w;
            c.ViewportHeightPx = h;
            c.DpiPixelsPerInch = 96;
            c.LineHeightPx = lh;
            c.RootLineHeightPx = rlh;
            return c;
        }

        [Test]
        public void Cap_parses_and_resolves_to_0_7_em() {
            var l = ParseLength("1cap");
            Assert.That(l.Unit, Is.EqualTo(CssLengthUnit.Cap));
            Assert.That(l.ToPixels(Ctx(fs: 20)), Is.EqualTo(14.0).Within(1e-9));
        }

        [Test]
        public void Ic_parses_and_resolves_to_em_for_latin_approximation() {
            var l = ParseLength("1ic");
            Assert.That(l.Unit, Is.EqualTo(CssLengthUnit.Ic));
            Assert.That(l.ToPixels(Ctx(fs: 20)), Is.EqualTo(20.0).Within(1e-9));
        }

        [Test]
        public void Lh_uses_box_line_height_when_set() {
            var l = ParseLength("1lh");
            Assert.That(l.Unit, Is.EqualTo(CssLengthUnit.Lh));
            Assert.That(l.ToPixels(Ctx(fs: 20, lh: 30)), Is.EqualTo(30.0).Within(1e-9));
        }

        [Test]
        public void Lh_falls_back_to_one_point_two_em_when_unset() {
            var l = new CssLength(1, CssLengthUnit.Lh);
            Assert.That(l.ToPixels(Ctx(fs: 20)), Is.EqualTo(24.0).Within(1e-9));
        }

        [Test]
        public void Rlh_uses_root_line_height_when_set() {
            var l = ParseLength("2rlh");
            Assert.That(l.Unit, Is.EqualTo(CssLengthUnit.Rlh));
            Assert.That(l.ToPixels(Ctx(rfs: 16, rlh: 24)), Is.EqualTo(48.0).Within(1e-9));
        }

        [Test]
        public void Rlh_falls_back_to_one_point_two_root_em_when_unset() {
            var l = new CssLength(1, CssLengthUnit.Rlh);
            Assert.That(l.ToPixels(Ctx(rfs: 16)), Is.EqualTo(19.2).Within(1e-9));
        }

        [Test]
        public void Svw_lvw_dvw_all_equal_vw_in_v1() {
            var ctx = Ctx(w: 1000, h: 800);
            double expected = new CssLength(100, CssLengthUnit.Vw).ToPixels(ctx);
            Assert.That(expected, Is.EqualTo(1000).Within(1e-9));
            Assert.That(ParseLength("100svw").ToPixels(ctx), Is.EqualTo(expected).Within(1e-9));
            Assert.That(ParseLength("100lvw").ToPixels(ctx), Is.EqualTo(expected).Within(1e-9));
            Assert.That(ParseLength("100dvw").ToPixels(ctx), Is.EqualTo(expected).Within(1e-9));
            Assert.That(ParseLength("100svw").Unit, Is.EqualTo(CssLengthUnit.Svw));
            Assert.That(ParseLength("100lvw").Unit, Is.EqualTo(CssLengthUnit.Lvw));
            Assert.That(ParseLength("100dvw").Unit, Is.EqualTo(CssLengthUnit.Dvw));
        }

        [Test]
        public void Svh_lvh_dvh_all_equal_vh_in_v1() {
            var ctx = Ctx(w: 1000, h: 800);
            double expected = new CssLength(100, CssLengthUnit.Vh).ToPixels(ctx);
            Assert.That(expected, Is.EqualTo(800).Within(1e-9));
            Assert.That(ParseLength("100svh").ToPixels(ctx), Is.EqualTo(expected).Within(1e-9));
            Assert.That(ParseLength("100lvh").ToPixels(ctx), Is.EqualTo(expected).Within(1e-9));
            Assert.That(ParseLength("100dvh").ToPixels(ctx), Is.EqualTo(expected).Within(1e-9));
            Assert.That(ParseLength("100svh").Unit, Is.EqualTo(CssLengthUnit.Svh));
            Assert.That(ParseLength("100lvh").Unit, Is.EqualTo(CssLengthUnit.Lvh));
            Assert.That(ParseLength("100dvh").Unit, Is.EqualTo(CssLengthUnit.Dvh));
        }

        [Test]
        public void Unit_keywords_are_case_insensitive() {
            Assert.That(ParseLength("1CAP").Unit, Is.EqualTo(CssLengthUnit.Cap));
            Assert.That(ParseLength("100SvW").Unit, Is.EqualTo(CssLengthUnit.Svw));
            Assert.That(ParseLength("1RLH").Unit, Is.EqualTo(CssLengthUnit.Rlh));
        }

        [Test]
        public void Existing_units_regression_still_resolve_correctly() {
            var ctx = Ctx(w: 1000, h: 800, fs: 20, rfs: 16);
            ctx.BasisPixels = 200;
            Assert.That(new CssLength(12, CssLengthUnit.Px).ToPixels(ctx), Is.EqualTo(12).Within(1e-9));
            Assert.That(new CssLength(2, CssLengthUnit.Em).ToPixels(ctx), Is.EqualTo(40).Within(1e-9));
            Assert.That(new CssLength(2, CssLengthUnit.Rem).ToPixels(ctx), Is.EqualTo(32).Within(1e-9));
            Assert.That(new CssLength(50, CssLengthUnit.Vw).ToPixels(ctx), Is.EqualTo(500).Within(1e-9));
            Assert.That(new CssLength(50, CssLengthUnit.Vh).ToPixels(ctx), Is.EqualTo(400).Within(1e-9));
            Assert.That(new CssLength(50, CssLengthUnit.Percent).ToPixels(ctx), Is.EqualTo(100).Within(1e-9));
        }
    }
}
