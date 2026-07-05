using NUnit.Framework;
using Weva.Css.Values;
using Weva.Paint;
using Weva.Paint.Filters;

namespace Weva.Tests.Paint.Filters {
    public class FilterParserTests {
        const double Eps = 1e-6;
        static LengthContext Ctx() => LengthContext.Default;

        [Test]
        public void Null_returns_empty() {
            Assert.That(FilterParser.Parse(null, Ctx()).IsEmpty, Is.True);
        }

        [Test]
        public void Whitespace_returns_empty() {
            Assert.That(FilterParser.Parse("   ", Ctx()).IsEmpty, Is.True);
        }

        [Test]
        public void None_returns_empty() {
            Assert.That(FilterParser.Parse("none", Ctx()).IsEmpty, Is.True);
            Assert.That(FilterParser.Parse("NONE", Ctx()).IsEmpty, Is.True);
        }

        [Test]
        public void Blur_with_pixels() {
            var c = FilterParser.Parse("blur(5px)", Ctx());
            Assert.That(c.Functions.Count, Is.EqualTo(1));
            var b = (BlurFilter)c.Functions[0];
            Assert.That(b.RadiusPx, Is.EqualTo(5).Within(Eps));
        }

        [Test]
        public void Blur_with_em_resolves_via_context() {
            var ctx = LengthContext.Default;
            ctx.BaseFontSizePx = 20;
            var c = FilterParser.Parse("blur(0.5em)", ctx);
            var b = (BlurFilter)c.Functions[0];
            Assert.That(b.RadiusPx, Is.EqualTo(10).Within(Eps));
        }

        [Test]
        public void Brightness_number() {
            var c = FilterParser.Parse("brightness(1.2)", Ctx());
            var b = (BrightnessFilter)c.Functions[0];
            Assert.That(b.Amount, Is.EqualTo(1.2).Within(Eps));
        }

        [Test]
        public void Brightness_50pct_parses_as_half() {
            var c = FilterParser.Parse("brightness(50%)", Ctx());
            var b = (BrightnessFilter)c.Functions[0];
            Assert.That(b.Amount, Is.EqualTo(0.5).Within(Eps));
        }

        [Test]
        public void Contrast_number() {
            var c = FilterParser.Parse("contrast(1.5)", Ctx());
            var f = (ContrastFilter)c.Functions[0];
            Assert.That(f.Amount, Is.EqualTo(1.5).Within(Eps));
        }

        [Test]
        public void Grayscale_clamped() {
            var c = FilterParser.Parse("grayscale(1)", Ctx());
            var f = (GrayscaleFilter)c.Functions[0];
            Assert.That(f.Amount, Is.EqualTo(1));
        }

        [Test]
        public void Opacity_filter_parses() {
            var c = FilterParser.Parse("opacity(0.4)", Ctx());
            var f = (OpacityFilter)c.Functions[0];
            Assert.That(f.Amount, Is.EqualTo(0.4).Within(Eps));
        }

        [Test]
        public void Saturate_above_one_allowed() {
            var c = FilterParser.Parse("saturate(2)", Ctx());
            var f = (SaturateFilter)c.Functions[0];
            Assert.That(f.Amount, Is.EqualTo(2));
        }

        [Test]
        public void Invert_percentage() {
            var c = FilterParser.Parse("invert(75%)", Ctx());
            var f = (InvertFilter)c.Functions[0];
            Assert.That(f.Amount, Is.EqualTo(0.75).Within(Eps));
        }

        [Test]
        public void Sepia_default_zero() {
            var c = FilterParser.Parse("sepia(0)", Ctx());
            var f = (SepiaFilter)c.Functions[0];
            Assert.That(f.Amount, Is.EqualTo(0));
        }

        [Test]
        public void HueRotate_deg() {
            var c = FilterParser.Parse("hue-rotate(180deg)", Ctx());
            var f = (HueRotateFilter)c.Functions[0];
            Assert.That(f.DegreesNormalized, Is.EqualTo(180).Within(Eps));
        }

        [Test]
        public void HueRotate_turn() {
            var c = FilterParser.Parse("hue-rotate(0.5turn)", Ctx());
            var f = (HueRotateFilter)c.Functions[0];
            Assert.That(f.DegreesNormalized, Is.EqualTo(180).Within(Eps));
        }

        [Test]
        public void HueRotate_rad() {
            var c = FilterParser.Parse("hue-rotate(3.14159265rad)", Ctx());
            var f = (HueRotateFilter)c.Functions[0];
            // 180° (mod 360 stays 180)
            Assert.That(f.DegreesNormalized, Is.EqualTo(180).Within(1e-3));
        }

        [Test]
        public void HueRotate_grad() {
            // 200grad = 180°
            var c = FilterParser.Parse("hue-rotate(200grad)", Ctx());
            var f = (HueRotateFilter)c.Functions[0];
            Assert.That(f.DegreesNormalized, Is.EqualTo(180).Within(Eps));
        }

        [Test]
        public void HueRotate_unitless_treated_as_degrees() {
            var c = FilterParser.Parse("hue-rotate(90)", Ctx());
            var f = (HueRotateFilter)c.Functions[0];
            Assert.That(f.DegreesNormalized, Is.EqualTo(90).Within(Eps));
        }

        [Test]
        public void DropShadow_minimum_offsets() {
            var c = FilterParser.Parse("drop-shadow(2px 4px)", Ctx());
            var f = (DropShadowFilter)c.Functions[0];
            Assert.That(f.OffsetX, Is.EqualTo(2));
            Assert.That(f.OffsetY, Is.EqualTo(4));
            Assert.That(f.BlurRadius, Is.EqualTo(0));
        }

        [Test]
        public void DropShadow_with_blur_and_named_color() {
            var c = FilterParser.Parse("drop-shadow(2px 4px 8px red)", Ctx());
            var f = (DropShadowFilter)c.Functions[0];
            Assert.That(f.OffsetX, Is.EqualTo(2));
            Assert.That(f.OffsetY, Is.EqualTo(4));
            Assert.That(f.BlurRadius, Is.EqualTo(8));
            Assert.That(f.Color.R, Is.GreaterThan(0.5f));
            Assert.That(f.Color.G, Is.LessThan(0.1f));
            Assert.That(f.Color.B, Is.LessThan(0.1f));
        }

        [Test]
        public void DropShadow_with_rgba_function() {
            var c = FilterParser.Parse("drop-shadow(0 4px 8px rgba(0, 0, 0, 0.5))", Ctx());
            var f = (DropShadowFilter)c.Functions[0];
            Assert.That(f.OffsetX, Is.EqualTo(0));
            Assert.That(f.OffsetY, Is.EqualTo(4));
            Assert.That(f.BlurRadius, Is.EqualTo(8));
            Assert.That(f.Color.A, Is.EqualTo(0.5f).Within(0.01));
        }

        [Test]
        public void DropShadow_with_hex_color() {
            var c = FilterParser.Parse("drop-shadow(0 0 4px #ff0000)", Ctx());
            var f = (DropShadowFilter)c.Functions[0];
            Assert.That(f.Color.R, Is.GreaterThan(0.5f));
            Assert.That(f.Color.G, Is.LessThan(0.1f));
        }

        [Test]
        public void Multiple_filters_chained() {
            var c = FilterParser.Parse("blur(5px) brightness(1.2)", Ctx());
            Assert.That(c.Functions.Count, Is.EqualTo(2));
            Assert.That(c.Functions[0], Is.InstanceOf<BlurFilter>());
            Assert.That(c.Functions[1], Is.InstanceOf<BrightnessFilter>());
        }

        [Test]
        public void Three_filters_chain_preserves_order() {
            var c = FilterParser.Parse("blur(5px) brightness(0.8) contrast(1.2)", Ctx());
            Assert.That(c.Functions.Count, Is.EqualTo(3));
            Assert.That(c.Functions[0], Is.InstanceOf<BlurFilter>());
            Assert.That(c.Functions[1], Is.InstanceOf<BrightnessFilter>());
            Assert.That(c.Functions[2], Is.InstanceOf<ContrastFilter>());
        }

        [Test]
        public void Whitespace_tolerance() {
            var c = FilterParser.Parse("  blur( 5px )   brightness( 1.2 )  ", Ctx());
            Assert.That(c.Functions.Count, Is.EqualTo(2));
            Assert.That(((BlurFilter)c.Functions[0]).RadiusPx, Is.EqualTo(5).Within(Eps));
        }

        [Test]
        public void Nested_function_args_parse_cleanly() {
            // rgba() is nested inside drop-shadow(...) — paren depth must be tracked.
            var c = FilterParser.Parse("drop-shadow(1px 2px 3px rgba(255, 128, 0, 0.5))", Ctx());
            Assert.That(c.Functions.Count, Is.EqualTo(1));
            var f = (DropShadowFilter)c.Functions[0];
            Assert.That(f.Color.A, Is.EqualTo(0.5f).Within(0.01));
        }

        [Test]
        public void Unknown_function_throws() {
            Assert.Throws<FilterParseException>(() => FilterParser.Parse("unsharp-mask(2px)", Ctx()));
        }

        [Test]
        public void Missing_arg_throws() {
            Assert.Throws<FilterParseException>(() => FilterParser.Parse("blur()", Ctx()));
        }

        [Test]
        public void Bad_unit_in_blur_throws() {
            Assert.Throws<FilterParseException>(() => FilterParser.Parse("blur(5)", Ctx()));
        }

        [Test]
        public void Bad_unit_in_hue_rotate_throws() {
            Assert.Throws<FilterParseException>(() => FilterParser.Parse("hue-rotate(5px)", Ctx()));
        }

        [Test]
        public void DropShadow_missing_offset_throws() {
            Assert.Throws<FilterParseException>(() => FilterParser.Parse("drop-shadow(2px)", Ctx()));
        }

        [Test]
        public void DropShadow_uses_provided_currentColor_when_omitted() {
            var ctx = LengthContext.Default;
            var blue = new LinearColor(0f, 0f, 1f, 1f);
            var c = FilterParser.Parse("drop-shadow(2px 4px)", ctx, blue);
            var f = (DropShadowFilter)c.Functions[0];
            Assert.That(f.Color, Is.EqualTo(blue));
        }

        [Test]
        public void Blur_zero_is_legal() {
            var c = FilterParser.Parse("blur(0)", Ctx());
            Assert.That(c.Functions.Count, Is.EqualTo(1));
            Assert.That(((BlurFilter)c.Functions[0]).RadiusPx, Is.EqualTo(0));
        }
    }
}
