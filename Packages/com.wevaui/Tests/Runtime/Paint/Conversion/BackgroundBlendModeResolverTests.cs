using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Paint;
using Weva.Paint.Conversion;

namespace Weva.Tests.Paint.Conversion {
    // Unit tests for BackgroundBlendModeResolver: parsing, list-cycling, enum
    // mapping, and edge-case handling.
    //
    // These tests exercise the resolver in isolation via ComputedStyle, without
    // going through BoxToPaintConverter or the layout pipeline.
    //
    // NUnit constraint notes:
    //   - NEVER chain .Within() off Is.LessThan/GreaterThan.
    //   - Does.Not.Contain is substring-only; use Has.None.EqualTo for collections.
    public class BackgroundBlendModeResolverTests {
        static ComputedStyle Style(string blendMode = null) {
            var s = new ComputedStyle(new Element("div"));
            if (blendMode != null) s.Set("background-blend-mode", blendMode);
            return s;
        }

        // ── Null / absent property ────────────────────────────────────────────

        [Test]
        public void Null_style_returns_null() {
            Assert.That(BackgroundBlendModeResolver.Resolve(null), Is.Null);
        }

        [Test]
        public void Absent_property_returns_null() {
            // No background-blend-mode set → null (all-normal fast path).
            Assert.That(BackgroundBlendModeResolver.Resolve(Style()), Is.Null);
        }

        [Test]
        public void Single_normal_returns_null() {
            Assert.That(BackgroundBlendModeResolver.Resolve(Style("normal")), Is.Null,
                "single 'normal' keyword is the fast path and returns null");
        }

        // ── Mode keyword parsing ──────────────────────────────────────────────

        [Test]
        public void Multiply_resolves_to_Multiply() {
            var modes = BackgroundBlendModeResolver.Resolve(Style("multiply"));
            Assert.That(modes, Is.Not.Null);
            Assert.That(modes[0], Is.EqualTo(MixBlendMode.Multiply));
        }

        [Test]
        public void Screen_resolves_to_Screen() {
            var modes = BackgroundBlendModeResolver.Resolve(Style("screen"));
            Assert.That(modes, Is.Not.Null);
            Assert.That(modes[0], Is.EqualTo(MixBlendMode.Screen));
        }

        [Test]
        public void Overlay_resolves_to_Overlay() {
            var modes = BackgroundBlendModeResolver.Resolve(Style("overlay"));
            Assert.That(modes, Is.Not.Null);
            Assert.That(modes[0], Is.EqualTo(MixBlendMode.Overlay));
        }

        [Test]
        public void Darken_resolves_to_Darken() {
            var modes = BackgroundBlendModeResolver.Resolve(Style("darken"));
            Assert.That(modes, Is.Not.Null);
            Assert.That(modes[0], Is.EqualTo(MixBlendMode.Darken));
        }

        [Test]
        public void Lighten_resolves_to_Lighten() {
            var modes = BackgroundBlendModeResolver.Resolve(Style("lighten"));
            Assert.That(modes, Is.Not.Null);
            Assert.That(modes[0], Is.EqualTo(MixBlendMode.Lighten));
        }

        // ── Case insensitivity ────────────────────────────────────────────────

        [Test]
        public void Keywords_are_case_insensitive() {
            var modes = BackgroundBlendModeResolver.Resolve(Style("MULTIPLY"));
            Assert.That(modes, Is.Not.Null);
            Assert.That(modes[0], Is.EqualTo(MixBlendMode.Multiply));
        }

        // ── Multi-value parsing ───────────────────────────────────────────────

        [Test]
        public void Two_value_list_resolves_to_two_modes() {
            var modes = BackgroundBlendModeResolver.Resolve(Style("screen, multiply"));
            Assert.That(modes, Is.Not.Null);
            Assert.That(modes.Count, Is.EqualTo(2));
            Assert.That(modes[0], Is.EqualTo(MixBlendMode.Screen));
            Assert.That(modes[1], Is.EqualTo(MixBlendMode.Multiply));
        }

        [Test]
        public void Mixed_normal_and_non_normal_list_includes_normal_in_list() {
            // "normal, multiply" — first layer normal, second multiply.
            // The list must be returned (not null) so the caller can see that
            // layer-1 has a non-normal mode; LayerAt handles normal entries.
            var modes = BackgroundBlendModeResolver.Resolve(Style("normal, multiply"));
            Assert.That(modes, Is.Not.Null,
                "non-null list required when any layer has a non-normal mode");
            Assert.That(modes.Count, Is.EqualTo(2));
            Assert.That(modes[0], Is.EqualTo(MixBlendMode.Normal));
            Assert.That(modes[1], Is.EqualTo(MixBlendMode.Multiply));
        }

        // ── LayerAt cycling ───────────────────────────────────────────────────

        [Test]
        public void LayerAt_cycles_shorter_list() {
            // Two modes for three layers: layer 2 cycles back to modes[0].
            var modes = new List<MixBlendMode> { MixBlendMode.Screen, MixBlendMode.Multiply };
            Assert.That(BackgroundBlendModeResolver.LayerAt(modes, 0), Is.EqualTo(MixBlendMode.Screen));
            Assert.That(BackgroundBlendModeResolver.LayerAt(modes, 1), Is.EqualTo(MixBlendMode.Multiply));
            Assert.That(BackgroundBlendModeResolver.LayerAt(modes, 2), Is.EqualTo(MixBlendMode.Screen),
                "layer 2 cycles: 2 % 2 == 0 → Screen");
            Assert.That(BackgroundBlendModeResolver.LayerAt(modes, 3), Is.EqualTo(MixBlendMode.Multiply),
                "layer 3 cycles: 3 % 2 == 1 → Multiply");
        }

        [Test]
        public void LayerAt_with_null_list_returns_Normal() {
            Assert.That(BackgroundBlendModeResolver.LayerAt(null, 0), Is.EqualTo(MixBlendMode.Normal));
            Assert.That(BackgroundBlendModeResolver.LayerAt(null, 5), Is.EqualTo(MixBlendMode.Normal));
        }

        [Test]
        public void LayerAt_with_empty_list_returns_Normal() {
            var modes = new List<MixBlendMode>();
            Assert.That(BackgroundBlendModeResolver.LayerAt(modes, 0), Is.EqualTo(MixBlendMode.Normal));
        }

        // ── Unknown keywords ──────────────────────────────────────────────────

        [Test]
        public void Unknown_keyword_falls_back_to_Normal() {
            // CSS Compositing 1 §7: unknown values → treat as if property not set.
            // A lone unknown keyword resolves to Normal → returns null (all-normal).
            var modes = BackgroundBlendModeResolver.Resolve(Style("dissolve-42-invalid"));
            Assert.That(modes, Is.Null,
                "lone unknown keyword → all Normal → null (fast path)");
        }

        [Test]
        public void Mixed_known_and_unknown_in_list() {
            // "multiply, invalid-mode" → multiply, normal (unknown fallback).
            var modes = BackgroundBlendModeResolver.Resolve(Style("multiply, invalid-mode"));
            Assert.That(modes, Is.Not.Null);
            Assert.That(modes.Count, Is.EqualTo(2));
            Assert.That(modes[0], Is.EqualTo(MixBlendMode.Multiply));
            Assert.That(modes[1], Is.EqualTo(MixBlendMode.Normal));
        }

        // ── Full keyword matrix — all 16 CSS <blend-mode> values (CSS Compositing 1 §6) ──

        [Test]
        public void ColorDodge_resolves_to_ColorDodge() {
            var modes = BackgroundBlendModeResolver.Resolve(Style("color-dodge"));
            Assert.That(modes, Is.Not.Null);
            Assert.That(modes[0], Is.EqualTo(MixBlendMode.ColorDodge));
        }

        [Test]
        public void ColorBurn_resolves_to_ColorBurn() {
            var modes = BackgroundBlendModeResolver.Resolve(Style("color-burn"));
            Assert.That(modes, Is.Not.Null);
            Assert.That(modes[0], Is.EqualTo(MixBlendMode.ColorBurn));
        }

        [Test]
        public void HardLight_resolves_to_HardLight() {
            var modes = BackgroundBlendModeResolver.Resolve(Style("hard-light"));
            Assert.That(modes, Is.Not.Null);
            Assert.That(modes[0], Is.EqualTo(MixBlendMode.HardLight));
        }

        [Test]
        public void SoftLight_resolves_to_SoftLight() {
            var modes = BackgroundBlendModeResolver.Resolve(Style("soft-light"));
            Assert.That(modes, Is.Not.Null);
            Assert.That(modes[0], Is.EqualTo(MixBlendMode.SoftLight));
        }

        [Test]
        public void Difference_resolves_to_Difference() {
            var modes = BackgroundBlendModeResolver.Resolve(Style("difference"));
            Assert.That(modes, Is.Not.Null);
            Assert.That(modes[0], Is.EqualTo(MixBlendMode.Difference));
        }

        [Test]
        public void Exclusion_resolves_to_Exclusion() {
            var modes = BackgroundBlendModeResolver.Resolve(Style("exclusion"));
            Assert.That(modes, Is.Not.Null);
            Assert.That(modes[0], Is.EqualTo(MixBlendMode.Exclusion));
        }

        // ── Non-separable HSL-based modes (CSS Compositing 1 §11.5..§11.8) ────
        // Previously these fell back to Normal in v1; now fully mapped.

        [Test]
        public void Hue_resolves_to_Hue() {
            var modes = BackgroundBlendModeResolver.Resolve(Style("hue"));
            Assert.That(modes, Is.Not.Null);
            Assert.That(modes[0], Is.EqualTo(MixBlendMode.Hue));
        }

        [Test]
        public void Saturation_resolves_to_Saturation() {
            var modes = BackgroundBlendModeResolver.Resolve(Style("saturation"));
            Assert.That(modes, Is.Not.Null);
            Assert.That(modes[0], Is.EqualTo(MixBlendMode.Saturation));
        }

        [Test]
        public void Color_resolves_to_Color() {
            var modes = BackgroundBlendModeResolver.Resolve(Style("color"));
            Assert.That(modes, Is.Not.Null);
            Assert.That(modes[0], Is.EqualTo(MixBlendMode.Color));
        }

        [Test]
        public void Luminosity_resolves_to_Luminosity() {
            var modes = BackgroundBlendModeResolver.Resolve(Style("luminosity"));
            Assert.That(modes, Is.Not.Null);
            Assert.That(modes[0], Is.EqualTo(MixBlendMode.Luminosity));
        }

        [Test]
        public void Plus_lighter_is_not_a_blend_mode_falls_back_to_Normal() {
            // CSS Compositing 1 §9 accepts only <blend-mode> values; plus-lighter
            // is a compositing OPERATOR (§9.1), not a blend mode — it is not valid
            // for background-blend-mode. Must resolve to Normal.
            var modes = BackgroundBlendModeResolver.Resolve(Style("plus-lighter"));
            // Lone unknown (after treating plus-lighter as not a blend-mode) →
            // all Normal → null (fast path).
            Assert.That(modes, Is.Null,
                "plus-lighter is not a <blend-mode> and must fall back to Normal");
        }
    }
}
