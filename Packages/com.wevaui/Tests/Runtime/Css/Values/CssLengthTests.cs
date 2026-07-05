using NUnit.Framework;
using Weva.Css.Values;

namespace Weva.Tests.Css.Values {
    public class CssLengthTests {
        static CssLength ParseLength(string s) {
            var v = CssValueParser.Parse(s);
            return (CssLength)v;
        }

        static LengthContext Ctx(double basis = 200) {
            var c = LengthContext.Default;
            c.BaseFontSizePx = 16;
            c.RootFontSizePx = 16;
            c.ViewportWidthPx = 1000;
            c.ViewportHeightPx = 800;
            c.DpiPixelsPerInch = 96;
            c.BasisPixels = basis;
            return c;
        }

        [Test]
        public void Px_unit_parses() {
            var l = ParseLength("12px");
            Assert.That(l.Value, Is.EqualTo(12));
            Assert.That(l.Unit, Is.EqualTo(CssLengthUnit.Px));
        }

        [Test]
        public void Em_unit_parses() {
            var l = ParseLength("1.5em");
            Assert.That(l.Value, Is.EqualTo(1.5));
            Assert.That(l.Unit, Is.EqualTo(CssLengthUnit.Em));
        }

        [Test]
        public void Rem_unit_parses() {
            var l = ParseLength("1rem");
            Assert.That(l.Unit, Is.EqualTo(CssLengthUnit.Rem));
        }

        [Test]
        public void Percent_parses_as_percentage_value() {
            var v = CssValueParser.Parse("50%");
            Assert.That(v, Is.InstanceOf<CssPercentage>());
            Assert.That(((CssPercentage)v).Value, Is.EqualTo(50));
        }

        [Test]
        public void Vh_unit_parses() {
            var l = ParseLength("100vh");
            Assert.That(l.Unit, Is.EqualTo(CssLengthUnit.Vh));
        }

        [Test]
        public void Vw_unit_parses() {
            var l = ParseLength("100vw");
            Assert.That(l.Unit, Is.EqualTo(CssLengthUnit.Vw));
        }

        [Test]
        public void Vmin_unit_parses() {
            var l = ParseLength("50vmin");
            Assert.That(l.Unit, Is.EqualTo(CssLengthUnit.Vmin));
        }

        [Test]
        public void Vmax_unit_parses() {
            var l = ParseLength("50vmax");
            Assert.That(l.Unit, Is.EqualTo(CssLengthUnit.Vmax));
        }

        [Test]
        public void Pt_unit_parses() {
            var l = ParseLength("12pt");
            Assert.That(l.Unit, Is.EqualTo(CssLengthUnit.Pt));
        }

        [Test]
        public void Pc_unit_parses() {
            var l = ParseLength("1pc");
            Assert.That(l.Unit, Is.EqualTo(CssLengthUnit.Pc));
        }

        [Test]
        public void In_unit_parses() {
            var l = ParseLength("1in");
            Assert.That(l.Unit, Is.EqualTo(CssLengthUnit.In));
        }

        [Test]
        public void Cm_unit_parses() {
            var l = ParseLength("2cm");
            Assert.That(l.Unit, Is.EqualTo(CssLengthUnit.Cm));
        }

        [Test]
        public void Mm_unit_parses() {
            var l = ParseLength("5mm");
            Assert.That(l.Unit, Is.EqualTo(CssLengthUnit.Mm));
        }

        [Test]
        public void Ch_unit_parses() {
            var l = ParseLength("2ch");
            Assert.That(l.Unit, Is.EqualTo(CssLengthUnit.Ch));
        }

        [Test]
        public void Ex_unit_parses() {
            var l = ParseLength("1ex");
            Assert.That(l.Unit, Is.EqualTo(CssLengthUnit.Ex));
        }

        [Test]
        public void Negative_signed_value() {
            var l = ParseLength("-5px");
            Assert.That(l.Value, Is.EqualTo(-5));
            Assert.That(l.Unit, Is.EqualTo(CssLengthUnit.Px));
        }

        [Test]
        public void Positive_signed_value() {
            var l = ParseLength("+2em");
            Assert.That(l.Value, Is.EqualTo(2));
            Assert.That(l.Unit, Is.EqualTo(CssLengthUnit.Em));
        }

        [Test]
        public void Decimal_value() {
            var l = ParseLength("1.5px");
            Assert.That(l.Value, Is.EqualTo(1.5));
        }

        [Test]
        public void Leading_decimal_value() {
            var l = ParseLength(".5em");
            Assert.That(l.Value, Is.EqualTo(0.5));
        }

        [Test]
        public void Px_to_pixels_is_identity() {
            var l = new CssLength(12, CssLengthUnit.Px);
            Assert.That(l.ToPixels(Ctx()), Is.EqualTo(12).Within(1e-9));
        }

        [Test]
        public void Em_to_pixels_uses_base_font() {
            var l = new CssLength(2, CssLengthUnit.Em);
            Assert.That(l.ToPixels(Ctx()), Is.EqualTo(32).Within(1e-9));
        }

        [Test]
        public void Rem_to_pixels_uses_root_font() {
            var ctx = Ctx();
            ctx.RootFontSizePx = 20;
            var l = new CssLength(2, CssLengthUnit.Rem);
            Assert.That(l.ToPixels(ctx), Is.EqualTo(40).Within(1e-9));
        }

        [Test]
        public void Vh_to_pixels_uses_viewport_height() {
            var l = new CssLength(50, CssLengthUnit.Vh);
            Assert.That(l.ToPixels(Ctx()), Is.EqualTo(400).Within(1e-9));
        }

        [Test]
        public void Vw_to_pixels_uses_viewport_width() {
            var l = new CssLength(50, CssLengthUnit.Vw);
            Assert.That(l.ToPixels(Ctx()), Is.EqualTo(500).Within(1e-9));
        }

        [Test]
        public void Vmin_uses_smaller_viewport_axis() {
            var l = new CssLength(50, CssLengthUnit.Vmin);
            Assert.That(l.ToPixels(Ctx()), Is.EqualTo(400).Within(1e-9));
        }

        [Test]
        public void Vmax_uses_larger_viewport_axis() {
            var l = new CssLength(50, CssLengthUnit.Vmax);
            Assert.That(l.ToPixels(Ctx()), Is.EqualTo(500).Within(1e-9));
        }

        [Test]
        public void Pt_to_pixels_at_96dpi() {
            var l = new CssLength(72, CssLengthUnit.Pt);
            Assert.That(l.ToPixels(Ctx()), Is.EqualTo(96).Within(1e-9));
        }

        [Test]
        public void Pc_to_pixels_is_12pt() {
            var l = new CssLength(1, CssLengthUnit.Pc);
            Assert.That(l.ToPixels(Ctx()), Is.EqualTo(16).Within(1e-9));
        }

        [Test]
        public void In_to_pixels_at_96dpi() {
            var l = new CssLength(1, CssLengthUnit.In);
            Assert.That(l.ToPixels(Ctx()), Is.EqualTo(96).Within(1e-9));
        }

        [Test]
        public void Cm_to_pixels_at_96dpi() {
            var l = new CssLength(2.54, CssLengthUnit.Cm);
            Assert.That(l.ToPixels(Ctx()), Is.EqualTo(96).Within(1e-9));
        }

        [Test]
        public void Mm_to_pixels_at_96dpi() {
            var l = new CssLength(25.4, CssLengthUnit.Mm);
            Assert.That(l.ToPixels(Ctx()), Is.EqualTo(96).Within(1e-9));
        }

        [Test]
        public void Ch_approximation_is_half_em() {
            var l = new CssLength(2, CssLengthUnit.Ch);
            Assert.That(l.ToPixels(Ctx()), Is.EqualTo(16).Within(1e-9));
        }

        [Test]
        public void Ex_approximation_is_half_em() {
            var l = new CssLength(2, CssLengthUnit.Ex);
            Assert.That(l.ToPixels(Ctx()), Is.EqualTo(16).Within(1e-9));
        }

        [Test]
        public void Percent_to_pixels_with_basis() {
            var l = new CssLength(50, CssLengthUnit.Percent);
            Assert.That(l.ToPixels(Ctx(200)), Is.EqualTo(100).Within(1e-9));
        }

        [Test]
        public void Percent_throws_without_basis() {
            var ctx = LengthContext.Default;
            ctx.BasisPixels = null;
            var l = new CssLength(50, CssLengthUnit.Percent);
            Assert.Throws<System.InvalidOperationException>(() => l.ToPixels(ctx));
        }

        [Test]
        public void Bare_number_is_not_length() {
            var v = CssValueParser.Parse("12");
            Assert.That(v, Is.InstanceOf<CssNumber>());
        }

        [Test]
        public void Unknown_unit_throws() {
            Assert.Throws<CssValueParseException>(() => CssValueParser.Parse("12foo"));
        }
    }
}
