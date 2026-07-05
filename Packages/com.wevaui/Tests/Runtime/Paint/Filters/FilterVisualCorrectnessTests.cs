using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css.Values;
using Weva.Paint;
using Weva.Paint.Filters;

// §3.3 Filter visual correctness — drop-shadow edge cases, hue-rotate wrapping,
// brightness/contrast/saturate at extremes, opacity semantics, blur identity,
// chain ordering, multiple drop-shadows, filter:none, and invalid-token pin.
// GAME_UI_COVERAGE_PLAN.md §3.3 row.
namespace Weva.Tests.Paint.Filters {
    public class FilterVisualCorrectnessTests {
        const double Eps = 1e-6;
        static LengthContext Ctx() => LengthContext.Default;

        // ── drop-shadow: negative offsets ──────────────────────────────────────
        [Test]
        public void DropShadow_negative_offsets_stored_verbatim() {
            var f = new DropShadowFilter(-5, -5, 4, LinearColor.Black);
            Assert.That(f.OffsetX, Is.EqualTo(-5));
            Assert.That(f.OffsetY, Is.EqualTo(-5));
            Assert.That(f.BlurRadius, Is.EqualTo(4));
        }

        [Test]
        public void Parser_negative_offsets_roundtrip() {
            var c = FilterParser.Parse("drop-shadow(-5px -5px 4px black)", Ctx());
            var f = (DropShadowFilter)c.Functions[0];
            Assert.That(f.OffsetX, Is.EqualTo(-5).Within(Eps));
            Assert.That(f.OffsetY, Is.EqualTo(-5).Within(Eps));
            Assert.That(f.BlurRadius, Is.EqualTo(4).Within(Eps));
            // black: R≈0, G≈0, B≈0
            Assert.That(f.Color.R, Is.LessThan(0.05f));
            Assert.That(f.Color.G, Is.LessThan(0.05f));
            Assert.That(f.Color.B, Is.LessThan(0.05f));
        }

        // ── drop-shadow: zero blur (sharp shadow) ─────────────────────────────
        [Test]
        public void DropShadow_zero_blur_is_sharp_shadow() {
            var c = FilterParser.Parse("drop-shadow(2px 2px 0 red)", Ctx());
            var f = (DropShadowFilter)c.Functions[0];
            Assert.That(f.OffsetX, Is.EqualTo(2).Within(Eps));
            Assert.That(f.OffsetY, Is.EqualTo(2).Within(Eps));
            Assert.That(f.BlurRadius, Is.EqualTo(0).Within(Eps));
            Assert.That(f.Color.R, Is.GreaterThan(0.5f));
        }

        [Test]
        public void DropShadow_omitted_blur_defaults_to_zero() {
            // "drop-shadow(2px 4px)" — no blur token; default is 0.
            var c = FilterParser.Parse("drop-shadow(2px 4px)", Ctx());
            var f = (DropShadowFilter)c.Functions[0];
            Assert.That(f.BlurRadius, Is.EqualTo(0));
        }

        // ── drop-shadow: very large blur ───────────────────────────────────────
        [Test]
        public void DropShadow_large_blur_stored_unchanged() {
            var c = FilterParser.Parse("drop-shadow(0 0 100px black)", Ctx());
            var f = (DropShadowFilter)c.Functions[0];
            Assert.That(f.OffsetX, Is.EqualTo(0).Within(Eps));
            Assert.That(f.OffsetY, Is.EqualTo(0).Within(Eps));
            Assert.That(f.BlurRadius, Is.EqualTo(100).Within(Eps));
        }

        // ── drop-shadow: color BEFORE lengths ─────────────────────────────────
        // Per CSS Filter Effects L1 spec the <color> may appear before OR after
        // the length tokens.  Current parser walks left-to-right and falls through
        // to TryConsumeColor for non-length tokens, so it must handle this.
        [Test]
        public void DropShadow_color_before_lengths_parses_correctly() {
            var c = FilterParser.Parse("drop-shadow(blue 2px 4px 6px)", Ctx());
            var f = (DropShadowFilter)c.Functions[0];
            Assert.That(f.OffsetX, Is.EqualTo(2).Within(Eps));
            Assert.That(f.OffsetY, Is.EqualTo(4).Within(Eps));
            Assert.That(f.BlurRadius, Is.EqualTo(6).Within(Eps));
            Assert.That(f.Color.B, Is.GreaterThan(0.5f));
            Assert.That(f.Color.R, Is.LessThan(0.1f));
        }

        // ── drop-shadow: mixed offsets (positive + negative) ──────────────────
        [Test]
        public void DropShadow_mixed_sign_offsets_stored() {
            var f = new DropShadowFilter(3, -7, 2, LinearColor.White);
            Assert.That(f.OffsetX, Is.EqualTo(3));
            Assert.That(f.OffsetY, Is.EqualTo(-7));
        }

        // ── multiple drop-shadows in one filter chain ──────────────────────────
        [Test]
        public void Multiple_drop_shadows_both_emitted() {
            var c = FilterParser.Parse(
                "drop-shadow(2px 2px 0 red) drop-shadow(-2px -2px 0 blue)", Ctx());
            Assert.That(c.Functions.Count, Is.EqualTo(2));
            Assert.That(c.Functions[0], Is.InstanceOf<DropShadowFilter>());
            Assert.That(c.Functions[1], Is.InstanceOf<DropShadowFilter>());
            var ds0 = (DropShadowFilter)c.Functions[0];
            var ds1 = (DropShadowFilter)c.Functions[1];
            Assert.That(ds0.OffsetX, Is.EqualTo(2).Within(Eps));
            Assert.That(ds1.OffsetX, Is.EqualTo(-2).Within(Eps));
            // Colors differ: first is red-ish, second is blue-ish.
            Assert.That(ds0.Color.R, Is.GreaterThan(0.5f));
            Assert.That(ds1.Color.B, Is.GreaterThan(0.5f));
        }

        // ── hue-rotate: identity values ────────────────────────────────────────
        [Test]
        public void HueRotate_zero_deg_is_identity() {
            var c = FilterParser.Parse("hue-rotate(0deg)", Ctx());
            var f = (HueRotateFilter)c.Functions[0];
            Assert.That(f.DegreesNormalized, Is.EqualTo(0).Within(Eps));
        }

        [Test]
        public void HueRotate_360deg_normalizes_to_zero() {
            var c = FilterParser.Parse("hue-rotate(360deg)", Ctx());
            var f = (HueRotateFilter)c.Functions[0];
            // 360 mod 360 == 0 — identity rotation.
            Assert.That(f.DegreesNormalized, Is.EqualTo(0).Within(Eps));
        }

        [Test]
        public void HueRotate_540deg_normalizes_to_180() {
            var c = FilterParser.Parse("hue-rotate(540deg)", Ctx());
            var f = (HueRotateFilter)c.Functions[0];
            Assert.That(f.DegreesNormalized, Is.EqualTo(180).Within(Eps));
        }

        [Test]
        public void HueRotate_negative_90deg_normalizes_to_270() {
            // -90 mod 360 == 270 after adding 360.
            var c = FilterParser.Parse("hue-rotate(-90deg)", Ctx());
            var f = (HueRotateFilter)c.Functions[0];
            Assert.That(f.DegreesNormalized, Is.EqualTo(270).Within(Eps));
        }

        [Test]
        public void HueRotate_1turn_equals_360deg_normalizes_to_zero() {
            var c = FilterParser.Parse("hue-rotate(1turn)", Ctx());
            var f = (HueRotateFilter)c.Functions[0];
            Assert.That(f.DegreesNormalized, Is.EqualTo(0).Within(1e-9));
        }

        [Test]
        public void HueRotate_constructor_normalizes_directly() {
            Assert.That(new HueRotateFilter(0).DegreesNormalized, Is.EqualTo(0));
            Assert.That(new HueRotateFilter(360).DegreesNormalized, Is.EqualTo(0));
            Assert.That(new HueRotateFilter(540).DegreesNormalized, Is.EqualTo(180));
            Assert.That(new HueRotateFilter(-90).DegreesNormalized, Is.EqualTo(270));
        }

        // ── brightness/contrast/saturate at 0 ────────────────────────────────
        [Test]
        public void Brightness_zero_percent_is_full_darken() {
            var c = FilterParser.Parse("brightness(0%)", Ctx());
            var f = (BrightnessFilter)c.Functions[0];
            Assert.That(f.Amount, Is.EqualTo(0).Within(Eps));
        }

        [Test]
        public void Contrast_zero_percent_flattens_contrast() {
            var c = FilterParser.Parse("contrast(0%)", Ctx());
            var f = (ContrastFilter)c.Functions[0];
            Assert.That(f.Amount, Is.EqualTo(0).Within(Eps));
        }

        [Test]
        public void Saturate_zero_desaturates_fully() {
            var c = FilterParser.Parse("saturate(0%)", Ctx());
            var f = (SaturateFilter)c.Functions[0];
            Assert.That(f.Amount, Is.EqualTo(0).Within(Eps));
        }

        // ── opacity filter semantics ───────────────────────────────────────────
        [Test]
        public void Opacity_filter_half_stored_correctly() {
            var c = FilterParser.Parse("opacity(0.5)", Ctx());
            var f = (OpacityFilter)c.Functions[0];
            Assert.That(f.Amount, Is.EqualTo(0.5).Within(Eps));
        }

        [Test]
        public void Opacity_filter_clamped_at_zero() {
            // Negative opacity should clamp to 0.
            var f = new OpacityFilter(-1);
            Assert.That(f.Amount, Is.EqualTo(0));
        }

        [Test]
        public void Opacity_filter_clamped_at_one() {
            // Values > 1 should clamp to 1.
            var f = new OpacityFilter(2.5);
            Assert.That(f.Amount, Is.EqualTo(1));
        }

        [Test]
        public void Opacity_filter_100pct_is_one() {
            var c = FilterParser.Parse("opacity(100%)", Ctx());
            var f = (OpacityFilter)c.Functions[0];
            Assert.That(f.Amount, Is.EqualTo(1).Within(Eps));
        }

        // ── blur(0px) — identity / no-op ──────────────────────────────────────
        [Test]
        public void Blur_zero_px_is_identity_noop() {
            var c = FilterParser.Parse("blur(0px)", Ctx());
            var f = (BlurFilter)c.Functions[0];
            Assert.That(f.RadiusPx, Is.EqualTo(0));
            Assert.That(f.Kind, Is.EqualTo(FilterKind.Blur));
        }

        // ── filter chain ordering ─────────────────────────────────────────────
        [Test]
        public void Chain_blur_then_brightness_preserves_order() {
            var c = FilterParser.Parse("blur(4px) brightness(2)", Ctx());
            Assert.That(c.Functions.Count, Is.EqualTo(2));
            Assert.That(c.Functions[0], Is.InstanceOf<BlurFilter>());
            Assert.That(c.Functions[1], Is.InstanceOf<BrightnessFilter>());
        }

        [Test]
        public void Chain_brightness_then_blur_preserves_order() {
            var c = FilterParser.Parse("brightness(2) blur(4px)", Ctx());
            Assert.That(c.Functions.Count, Is.EqualTo(2));
            Assert.That(c.Functions[0], Is.InstanceOf<BrightnessFilter>());
            Assert.That(c.Functions[1], Is.InstanceOf<BlurFilter>());
        }

        [Test]
        public void Chain_order_not_commutative_equality() {
            // blur(4px) brightness(2) != brightness(2) blur(4px) — order matters per spec.
            var chainA = FilterParser.Parse("blur(4px) brightness(2)", Ctx());
            var chainB = FilterParser.Parse("brightness(2) blur(4px)", Ctx());
            Assert.That(chainA, Is.Not.EqualTo(chainB));
        }

        [Test]
        public void Chain_four_functions_preserves_declaration_order() {
            var c = FilterParser.Parse(
                "blur(2px) brightness(0.8) hue-rotate(90deg) drop-shadow(1px 1px 0 black)", Ctx());
            Assert.That(c.Functions.Count, Is.EqualTo(4));
            Assert.That(c.Functions[0], Is.InstanceOf<BlurFilter>());
            Assert.That(c.Functions[1], Is.InstanceOf<BrightnessFilter>());
            Assert.That(c.Functions[2], Is.InstanceOf<HueRotateFilter>());
            Assert.That(c.Functions[3], Is.InstanceOf<DropShadowFilter>());
        }

        // ── filter: none clears chain ─────────────────────────────────────────
        [Test]
        public void None_keyword_returns_empty_chain() {
            var c = FilterParser.Parse("none", Ctx());
            Assert.That(c.IsEmpty, Is.True);
            Assert.That(c.Functions.Count, Is.EqualTo(0));
        }

        [Test]
        public void None_keyword_case_insensitive() {
            Assert.That(FilterParser.Parse("NONE", Ctx()).IsEmpty, Is.True);
            Assert.That(FilterParser.Parse("None", Ctx()).IsEmpty, Is.True);
        }

        // ── invalid token in chain — current behaviour pin ────────────────────
        // The parser throws FilterParseException for unknown function names.
        // This pins the documented v1 behaviour (throw, not swallow-and-skip).
        [Test]
        public void Unknown_function_in_chain_throws_FilterParseException() {
            Assert.Throws<FilterParseException>(
                () => FilterParser.Parse("blur(4px) wobble(50%)", Ctx()));
        }

        [Test]
        public void Unknown_function_alone_throws_FilterParseException() {
            Assert.Throws<FilterParseException>(
                () => FilterParser.Parse("skew(45deg)", Ctx()));
        }

        // ── ToText round-trips ────────────────────────────────────────────────
        [Test]
        public void HueRotate_ToText_outputs_normalized_degrees() {
            // 540deg normalizes to 180 — round-trip through ToText.
            var f = new HueRotateFilter(540);
            var text = f.ToText();
            Assert.That(text, Does.StartWith("hue-rotate("));
            Assert.That(text, Does.Contain("180"));
        }

        [Test]
        public void DropShadow_negative_offsets_ToText_contains_negative_values() {
            var f = new DropShadowFilter(-5, -8, 3, LinearColor.Black);
            var text = f.ToText();
            Assert.That(text, Does.StartWith("drop-shadow("));
            Assert.That(text, Does.Contain("-5"));
            Assert.That(text, Does.Contain("-8"));
        }

        // ── FilterChain with multiple drop-shadows equality ────────────────────
        [Test]
        public void FilterChain_with_two_drop_shadows_equality() {
            var ds1 = new DropShadowFilter(2, 2, 0, LinearColor.Black);
            var ds2 = new DropShadowFilter(-2, -2, 0, LinearColor.White);
            var a = new FilterChain(new List<FilterFunction> { ds1, ds2 });
            var b = new FilterChain(new List<FilterFunction> { ds1, ds2 });
            Assert.That(a, Is.EqualTo(b));
        }

        [Test]
        public void FilterChain_reversed_drop_shadows_not_equal() {
            var ds1 = new DropShadowFilter(2, 2, 0, LinearColor.Black);
            var ds2 = new DropShadowFilter(-2, -2, 0, LinearColor.White);
            var a = new FilterChain(new List<FilterFunction> { ds1, ds2 });
            var b = new FilterChain(new List<FilterFunction> { ds2, ds1 });
            Assert.That(a, Is.Not.EqualTo(b));
        }
    }
}
