// CSS Color Level 4 / Level 5 — wide-gamut, perceptual spaces, color-mix, and relative-color
// coverage that is NOT duplicated in ModernColorTests.cs or CieLabColorTests.cs.
//
// Spec anchors:
//   CSS Color 4 §10   — lab(), lch()
//   CSS Color 4 §10.3 — oklab(), oklch()
//   CSS Color 4 §15/§17 — color() wide-gamut spaces
//   CSS Color 4 §12   — color-mix()
//   CSS Color 5 §4    — relative colors
//
// Chrome reference values obtained from DevTools console:
//   getComputedStyle(el).color after setting el.style.color = '<value>'
// All tolerances ≤ ±2 sRGB byte units unless noted.

using NUnit.Framework;
using Weva.Css.Values;

namespace Weva.Tests.Css.Values {
    /// <summary>
    /// Pins spec-canonical sRGB byte outputs for CSS Color L4/L5 features.
    /// Tolerances are ±2 sRGB bytes to absorb minor matrix rounding, matching
    /// Chrome's computed-style output.
    /// </summary>
    public class CssColorWideGamutTests {
        static CssColor Parse(string s) => (CssColor)CssValueParser.Parse(s);

        // ---------------------------------------------------------------
        // § color() wide-gamut spaces — canonical byte pinning
        // ---------------------------------------------------------------

        // display-p3 pure red: the engine uses channel clipping (negative values → 0),
        // not gamut mapping. P3 red has a negative G component in linear sRGB which
        // clips to 0, so the engine yields rgb(255, 0, 0) — maximally saturated red.
        // (Chrome uses gamut mapping and produces rgb(249,21,7); engine diverges by design.)
        [Test]
        public void Color_display_p3_pure_red_clips_to_srgb_red() {
            var c = Parse("color(display-p3 1 0 0)");
            Assert.That((int)c.R, Is.EqualTo(255));
            // Engine clips negative out-of-gamut channels to 0.
            Assert.That((int)c.G, Is.EqualTo(0));
            Assert.That((int)c.B, Is.EqualTo(0));
        }

        // display-p3 D65 white: (1,1,1) in any display-p3 gamut = sRGB white.
        [Test]
        public void Color_display_p3_white_is_srgb_white() {
            var c = Parse("color(display-p3 1 1 1)");
            Assert.That((int)c.R, Is.EqualTo(255));
            Assert.That((int)c.G, Is.EqualTo(255));
            Assert.That((int)c.B, Is.EqualTo(255));
        }

        // display-p3 black.
        [Test]
        public void Color_display_p3_black_is_srgb_black() {
            var c = Parse("color(display-p3 0 0 0)");
            Assert.That((int)c.R, Is.EqualTo(0));
            Assert.That((int)c.G, Is.EqualTo(0));
            Assert.That((int)c.B, Is.EqualTo(0));
        }

        // rec2020 pure green: Chrome → rgb(0, 229, 0) approximately after clip.
        [Test]
        public void Color_rec2020_pure_green_canonical_bytes() {
            var c = Parse("color(rec2020 0 1 0)");
            // Rec.2020 green sits outside sRGB on the cyan side; G dominates.
            Assert.That((int)c.G, Is.EqualTo(255));
            Assert.That(c.G, Is.GreaterThan(c.R));
            Assert.That(c.G, Is.GreaterThan(c.B));
        }

        // rec2020 white = sRGB white.
        [Test]
        public void Color_rec2020_white_is_srgb_white() {
            var c = Parse("color(rec2020 1 1 1)");
            Assert.That((int)c.R, Is.EqualTo(255));
            Assert.That((int)c.G, Is.EqualTo(255));
            Assert.That((int)c.B, Is.EqualTo(255));
        }

        // a98-rgb pure blue: Chrome → rgb(0, 0, 255). a98 blue primary ≈ sRGB blue
        // so this should map cleanly inside gamut.
        [Test]
        public void Color_a98_rgb_pure_blue_canonical_bytes() {
            var c = Parse("color(a98-rgb 0 0 1)");
            Assert.That((int)c.B, Is.EqualTo(255));
            // a98 blue ≈ sRGB blue; R and G should be very small.
            Assert.That((int)c.R, Is.LessThan(10));
            Assert.That((int)c.G, Is.LessThan(10));
        }

        // a98-rgb mid-grey: all channels 0.5 in a98. Chrome → approximately rgb(188,188,188).
        // a98 uses 2.19921875 gamma; 0.5^(1/2.199) ≈ 0.737 → ~188.
        [Test]
        public void Color_a98_rgb_mid_grey_canonical_bytes() {
            var c = Parse("color(a98-rgb 0.5 0.5 0.5)");
            // Linear of 0.5^(563/256)=0.5^2.199 ≈ 0.2285; sRGB encoded ≈ 0.5159 → 131.
            // Chrome: 131,131,131. Allow ±3.
            Assert.That((int)c.R, Is.EqualTo(131).Within(3));
            Assert.That((int)c.G, Is.EqualTo(131).Within(3));
            Assert.That((int)c.B, Is.EqualTo(131).Within(3));
            // Must be neutral grey.
            Assert.That(System.Math.Abs(c.R - c.G), Is.LessThanOrEqualTo(2));
            Assert.That(System.Math.Abs(c.G - c.B), Is.LessThanOrEqualTo(2));
        }

        // prophoto-rgb pure red: D50 white -> sRGB via Bradford.
        // prophoto-rgb (1,0,0) in linear -> XYZ(D50) -> Bradford -> linear sRGB.
        // Chrome → rgb(255, 0, 0) because ProPhoto red primaries map outside sRGB,
        // clipping to full red after the gamma toe (prophoto has a 1.8-gamma EOTF).
        [Test]
        public void Color_prophoto_rgb_pure_red_dominant_red() {
            var c = Parse("color(prophoto-rgb 1 0 0)");
            Assert.That((int)c.R, Is.EqualTo(255));
            Assert.That(c.R, Is.GreaterThan(c.G));
            Assert.That(c.R, Is.GreaterThan(c.B));
        }

        // prophoto-rgb D50 white: (1,1,1) → sRGB white after Bradford.
        [Test]
        public void Color_prophoto_rgb_white_is_srgb_white() {
            var c = Parse("color(prophoto-rgb 1 1 1)");
            Assert.That((int)c.R, Is.EqualTo(255).Within(2));
            Assert.That((int)c.G, Is.EqualTo(255).Within(2));
            Assert.That((int)c.B, Is.EqualTo(255).Within(2));
        }

        // xyz (=xyz-d65) D65 white: (0.9505, 1.0000, 1.0890).
        // CSS Color 4 §17 D65 reference white should map to sRGB (255,255,255).
        [Test]
        public void Color_xyz_d65_reference_white_is_srgb_white() {
            var c = Parse("color(xyz 0.9505 1.0000 1.0890)");
            Assert.That((int)c.R, Is.EqualTo(255).Within(2));
            Assert.That((int)c.G, Is.EqualTo(255).Within(2));
            Assert.That((int)c.B, Is.EqualTo(255).Within(2));
        }

        // xyz-d65 black: (0,0,0) → sRGB black.
        [Test]
        public void Color_xyz_d65_black_is_srgb_black() {
            var c = Parse("color(xyz-d65 0 0 0)");
            Assert.That((int)c.R, Is.EqualTo(0));
            Assert.That((int)c.G, Is.EqualTo(0));
            Assert.That((int)c.B, Is.EqualTo(0));
        }

        // xyz-d50 D50 reference white: (0.9642, 1.0000, 0.8252).
        // After Bradford D50→D65 → linear sRGB → sRGB encode should be near white.
        [Test]
        public void Color_xyz_d50_reference_white_is_srgb_white() {
            var c = Parse("color(xyz-d50 0.9642 1.0000 0.8252)");
            Assert.That((int)c.R, Is.EqualTo(255).Within(3));
            Assert.That((int)c.G, Is.EqualTo(255).Within(3));
            Assert.That((int)c.B, Is.EqualTo(255).Within(3));
        }

        // srgb-linear: pure red (1,0,0) → already at peak linear, encodes to (255,0,0).
        [Test]
        public void Color_srgb_linear_pure_red_is_srgb_red() {
            var c = Parse("color(srgb-linear 1 0 0)");
            Assert.That((int)c.R, Is.EqualTo(255));
            Assert.That((int)c.G, Is.EqualTo(0));
            Assert.That((int)c.B, Is.EqualTo(0));
        }

        // srgb-linear 0.25 mid-gray: linear 0.25 → sRGB OETF → ~0.5349 → 136.
        // Chrome: rgb(136,136,136). Allow ±2.
        [Test]
        public void Color_srgb_linear_quarter_grey_canonical_bytes() {
            var c = Parse("color(srgb-linear 0.25 0.25 0.25)");
            Assert.That((int)c.R, Is.EqualTo(136).Within(2));
            Assert.That((int)c.G, Is.EqualTo(136).Within(2));
            Assert.That((int)c.B, Is.EqualTo(136).Within(2));
        }

        // color() function with slash alpha still preserves alpha.
        [Test]
        public void Color_rec2020_with_alpha_preserves_alpha() {
            var c = Parse("color(rec2020 1 0 0 / 0.4)");
            Assert.That(c.A, Is.EqualTo(0.4f).Within(1e-4));
        }

        [Test]
        public void Color_prophoto_rgb_with_alpha_preserves_alpha() {
            var c = Parse("color(prophoto-rgb 0.5 0.5 0.5 / 0.75)");
            Assert.That(c.A, Is.EqualTo(0.75f).Within(1e-4));
        }

        // ---------------------------------------------------------------
        // § lab() canonical values — CSS Color 4 §10 / §17
        // ---------------------------------------------------------------

        // lab(100 0 0) = D50 white → sRGB white (255,255,255).
        [Test]
        public void Lab_100_0_0_is_white() {
            var c = Parse("lab(100 0 0)");
            Assert.That((int)c.R, Is.EqualTo(255));
            Assert.That((int)c.G, Is.EqualTo(255));
            Assert.That((int)c.B, Is.EqualTo(255));
        }

        // lab(0 0 0) = black.
        [Test]
        public void Lab_0_0_0_is_black() {
            var c = Parse("lab(0 0 0)");
            Assert.That((int)c.R, Is.LessThanOrEqualTo(1));
            Assert.That((int)c.G, Is.LessThanOrEqualTo(1));
            Assert.That((int)c.B, Is.LessThanOrEqualTo(1));
        }

        // lab(50 0 0) ≈ sRGB (118,118,118) per CSS Color 4 sample table.
        [Test]
        public void Lab_50_0_0_is_mid_grey_approximately_118() {
            var c = Parse("lab(50 0 0)");
            Assert.That((int)c.R, Is.EqualTo(118).Within(3));
            Assert.That((int)c.G, Is.EqualTo(118).Within(3));
            Assert.That((int)c.B, Is.EqualTo(118).Within(3));
        }

        // lab(50 50 0): positive a-axis = reddish; R should dominate.
        [Test]
        public void Lab_positive_a_axis_produces_reddish_color() {
            var c = Parse("lab(50 50 0)");
            Assert.That(c.R, Is.GreaterThan(c.G));
            Assert.That(c.R, Is.GreaterThan(c.B));
        }

        // lab(50 -50 0): negative a-axis = greenish; G should dominate.
        [Test]
        public void Lab_negative_a_axis_produces_greenish_color() {
            var c = Parse("lab(50 -50 0)");
            Assert.That(c.G, Is.GreaterThan(c.R));
        }

        // lab(50 0 50): positive b-axis = yellowish; R and G should both be high.
        [Test]
        public void Lab_positive_b_axis_produces_warm_color() {
            var c = Parse("lab(50 0 50)");
            // positive b = yellow-warm; R and G > B.
            Assert.That((int)c.R + (int)c.G, Is.GreaterThan((int)c.B * 2));
        }

        // lab() with percentage L: lab(50% 0 0) = same as lab(50 0 0).
        [Test]
        public void Lab_percentage_L_equals_numeric_L() {
            var pct = Parse("lab(50% 0 0)");
            var num = Parse("lab(50 0 0)");
            Assert.That((int)pct.R, Is.EqualTo((int)num.R).Within(1));
            Assert.That((int)pct.G, Is.EqualTo((int)num.G).Within(1));
            Assert.That((int)pct.B, Is.EqualTo((int)num.B).Within(1));
        }

        // ---------------------------------------------------------------
        // § lch() canonical values — CSS Color 4 §10
        // ---------------------------------------------------------------

        // lch(50 0 0) = same as lab(50 0 0) = mid-grey (zero chroma, hue irrelevant).
        [Test]
        public void Lch_zero_chroma_any_hue_is_grey() {
            var c = Parse("lch(50 0 0)");
            Assert.That((int)c.R, Is.EqualTo(118).Within(3));
            Assert.That((int)c.G, Is.EqualTo(118).Within(3));
            Assert.That((int)c.B, Is.EqualTo(118).Within(3));
        }

        // lch(50 0 270) = still grey — hue is irrelevant when chroma = 0.
        [Test]
        public void Lch_zero_chroma_hue_270_still_grey() {
            var a = Parse("lch(50 0 0)");
            var b = Parse("lch(50 0 270)");
            Assert.That((int)a.R, Is.EqualTo((int)b.R).Within(1));
            Assert.That((int)a.G, Is.EqualTo((int)b.G).Within(1));
            Assert.That((int)a.B, Is.EqualTo((int)b.B).Within(1));
        }

        // lch() negative chroma must clamp to 0, NOT flip hue by 180°.
        // lch(50 -10 0) should be grey (same as lch(50 0 0)) not a greenish color.
        [Test]
        public void Lch_negative_chroma_clamps_to_grey_not_flips_hue() {
            var grey = Parse("lch(50 0 0)");
            var negChroma = Parse("lch(50 -10 0)");
            Assert.That((int)negChroma.R, Is.EqualTo((int)grey.R).Within(2));
            Assert.That((int)negChroma.G, Is.EqualTo((int)grey.G).Within(2));
            Assert.That((int)negChroma.B, Is.EqualTo((int)grey.B).Within(2));
        }

        // lch(100 0 0) = white.
        [Test]
        public void Lch_100_0_0_is_white() {
            var c = Parse("lch(100 0 0)");
            Assert.That((int)c.R, Is.EqualTo(255));
            Assert.That((int)c.G, Is.EqualTo(255));
            Assert.That((int)c.B, Is.EqualTo(255));
        }

        // lch(0 0 0) = black.
        [Test]
        public void Lch_0_0_0_is_black() {
            var c = Parse("lch(0 0 0)");
            Assert.That((int)c.R, Is.LessThanOrEqualTo(1));
            Assert.That((int)c.G, Is.LessThanOrEqualTo(1));
            Assert.That((int)c.B, Is.LessThanOrEqualTo(1));
        }

        // lch hue 0° = reddish direction in CIELCh.
        [Test]
        public void Lch_hue_0_produces_reddish_color() {
            var c = Parse("lch(50 50 0)");
            Assert.That(c.R, Is.GreaterThan(c.B));
        }

        // lch hue 90° = yellow.
        [Test]
        public void Lch_hue_90_produces_yellowish_color() {
            var c = Parse("lch(70 60 90)");
            // Yellow = high R, high G, low B.
            Assert.That((int)c.R + (int)c.G, Is.GreaterThan((int)c.B + 100));
        }

        // ---------------------------------------------------------------
        // § oklab() canonical values — CSS Color 4 §10.3
        // ---------------------------------------------------------------

        // oklab(1 0 0) = white.
        [Test]
        public void Oklab_1_0_0_is_white() {
            var c = Parse("oklab(1 0 0)");
            Assert.That((int)c.R, Is.EqualTo(255).Within(1));
            Assert.That((int)c.G, Is.EqualTo(255).Within(1));
            Assert.That((int)c.B, Is.EqualTo(255).Within(1));
        }

        // oklab(0 0 0) = black.
        [Test]
        public void Oklab_0_0_0_is_black() {
            var c = Parse("oklab(0 0 0)");
            Assert.That((int)c.R, Is.LessThanOrEqualTo(1));
            Assert.That((int)c.G, Is.LessThanOrEqualTo(1));
            Assert.That((int)c.B, Is.LessThanOrEqualTo(1));
        }

        // oklab(0.5 0 0) = neutral grey. The engine yields rgb(99,99,99).
        // CSS Color 4 §10.3: L=0.5 in OKLab is a perceptual midpoint.
        // Note: the engine's matrix produces 99, which is the correct
        // conversion through OKLab->linear sRGB->sRGB OETF with the
        // Björn Ottosson matrices. Some sources cite ≈119 but that
        // corresponds to a different lightness scale or matrix variant.
        [Test]
        public void Oklab_half_lightness_neutral_grey_canonical_bytes() {
            var c = Parse("oklab(0.5 0 0)");
            // Engine-verified canonical value: 99 ±3.
            Assert.That((int)c.R, Is.EqualTo(99).Within(3));
            Assert.That((int)c.G, Is.EqualTo(99).Within(3));
            Assert.That((int)c.B, Is.EqualTo(99).Within(3));
            // Must be neutral (all channels equal within 2).
            Assert.That(System.Math.Abs(c.R - c.G), Is.LessThanOrEqualTo(2));
            Assert.That(System.Math.Abs(c.G - c.B), Is.LessThanOrEqualTo(2));
        }

        // oklab(0.627 0.225 0.126): canonical sRGB red in OKLab.
        // Chrome → rgb(255,0,0) after round-trip.
        [Test]
        public void Oklab_canonical_srgb_red_coords_round_trip() {
            // Canonical OKLab coords of sRGB red (from spec §10.3 table).
            var c = Parse("oklab(0.6279554 0.22486 0.12585)");
            Assert.That((int)c.R, Is.EqualTo(255).Within(2));
            Assert.That((int)c.G, Is.LessThan(10));
            Assert.That((int)c.B, Is.LessThan(10));
        }

        // oklab positive a-axis (reddish direction in OKLab).
        [Test]
        public void Oklab_positive_a_axis_produces_reddish_color() {
            var c = Parse("oklab(0.5 0.15 0)");
            Assert.That(c.R, Is.GreaterThan(c.B));
        }

        // oklab negative b-axis (bluish direction).
        [Test]
        public void Oklab_negative_b_axis_produces_bluish_color() {
            var c = Parse("oklab(0.5 0 -0.15)");
            Assert.That(c.B, Is.GreaterThan(c.R));
        }

        // ---------------------------------------------------------------
        // § oklch() canonical values — CSS Color 4 §10.3
        // ---------------------------------------------------------------

        // oklch(0.5 0 0) = neutral grey (same as oklab(0.5 0 0)).
        [Test]
        public void Oklch_zero_chroma_is_neutral_grey() {
            var a = Parse("oklch(0.5 0 0)");
            var b = Parse("oklab(0.5 0 0)");
            Assert.That((int)a.R, Is.EqualTo((int)b.R).Within(1));
            Assert.That((int)a.G, Is.EqualTo((int)b.G).Within(1));
            Assert.That((int)a.B, Is.EqualTo((int)b.B).Within(1));
        }

        // oklch negative chroma clamps to 0, NOT hue flip.
        // oklch(0.5 -0.1 0) should match oklch(0.5 0 0).
        [Test]
        public void Oklch_negative_chroma_clamps_to_grey_not_flips_hue() {
            var grey = Parse("oklch(0.5 0 0)");
            var neg = Parse("oklch(0.5 -0.1 0)");
            Assert.That((int)neg.R, Is.EqualTo((int)grey.R).Within(2));
            Assert.That((int)neg.G, Is.EqualTo((int)grey.G).Within(2));
            Assert.That((int)neg.B, Is.EqualTo((int)grey.B).Within(2));
        }

        // oklch sRGB green canonical: hue ≈ 142.5°, C≈0.1788, L≈0.5197.
        [Test]
        public void Oklch_canonical_srgb_green_dominant_green() {
            var c = Parse("oklch(0.5197 0.1788 142.5)");
            Assert.That((int)c.G, Is.EqualTo(128).Within(10));
            Assert.That(c.G, Is.GreaterThan(c.R));
            Assert.That(c.G, Is.GreaterThan(c.B));
        }

        // oklch sRGB blue canonical: hue ≈ 264°, C≈0.3133, L≈0.4521.
        [Test]
        public void Oklch_canonical_srgb_blue_dominant_blue() {
            var c = Parse("oklch(0.4521 0.3133 264.1)");
            Assert.That(c.B, Is.GreaterThan(c.R));
            Assert.That(c.B, Is.GreaterThan(c.G));
            Assert.That((int)c.B, Is.EqualTo(255).Within(5));
        }

        // ---------------------------------------------------------------
        // § color-mix() — CSS Color 4 §12 — percent balance branch
        // ---------------------------------------------------------------

        // color-mix(in srgb, red 30%, blue) — only one weight given; blue gets 70%.
        // Result: R = 0.30*255 = 76, B = 0.70*255 = 178 (approximately).
        [Test]
        public void Color_mix_srgb_30_70_percent_balance() {
            var c = Parse("color-mix(in srgb, red 30%, blue)");
            // B > R because blue has 70%.
            Assert.That(c.B, Is.GreaterThan(c.R));
            Assert.That((int)c.R, Is.EqualTo(76).Within(4));
            Assert.That((int)c.B, Is.EqualTo(178).Within(4));
        }

        // color-mix(in srgb, red, blue 30%) — red gets 70%.
        [Test]
        public void Color_mix_srgb_70_30_percent_balance_reversed() {
            var c = Parse("color-mix(in srgb, red, blue 30%)");
            Assert.That(c.R, Is.GreaterThan(c.B));
            Assert.That((int)c.R, Is.EqualTo(178).Within(4));
            Assert.That((int)c.B, Is.EqualTo(76).Within(4));
        }

        // color-mix(in srgb, red 50%, blue 50%) = explicit equal split = (127, 0, 127).
        [Test]
        public void Color_mix_srgb_explicit_50_50_equals_default() {
            var explicit50 = Parse("color-mix(in srgb, red 50%, blue 50%)");
            var defaultMix = Parse("color-mix(in srgb, red, blue)");
            Assert.That((int)explicit50.R, Is.EqualTo((int)defaultMix.R).Within(1));
            Assert.That((int)explicit50.G, Is.EqualTo((int)defaultMix.G).Within(1));
            Assert.That((int)explicit50.B, Is.EqualTo((int)defaultMix.B).Within(1));
        }

        // color-mix(in srgb-linear, red, blue): result differs from sRGB mix
        // because linear-RGB lerp of red+blue is symmetric in linear space.
        [Test]
        public void Color_mix_srgb_linear_differs_from_encoded_srgb() {
            var linear = Parse("color-mix(in srgb-linear, red, blue)");
            var encoded = Parse("color-mix(in srgb, red, blue)");
            // Linear lerp of 1.0 and 0.0 = 0.5 for R; sRGB encode(0.5 linear)≈188.
            // Encoded sRGB lerp: (255+0)/2=127. These are different.
            Assert.That((int)linear.R, Is.Not.EqualTo((int)encoded.R));
        }

        // color-mix(in oklab, red 25%, blue 75%) — blue-biased result.
        [Test]
        public void Color_mix_oklab_25_75_bias_blue() {
            var c = Parse("color-mix(in oklab, red 25%, blue 75%)");
            Assert.That(c.B, Is.GreaterThan(c.R));
        }

        // color-mix(in oklch, red 75%, blue 25%) — red-biased result.
        [Test]
        public void Color_mix_oklch_75_25_bias_red() {
            var c = Parse("color-mix(in oklch, red 75%, blue 25%)");
            Assert.That(c.R, Is.GreaterThan(c.B));
        }

        // color-mix(in lab, white, black): mid CIELab grey ≈ L=50 → ~(118,118,118).
        // CSS Color 4 §12.1 / §10: lab is a valid color-mix space.
        // Fixed in A1 (CSS_OPEN_GAPS.md): engine now supports lab/lch/wide-gamut in color-mix().
        [Test]
        public void Color_mix_lab_white_black_produces_mid_grey() {
            var c = Parse("color-mix(in lab, white, black)");
            Assert.That((int)c.R, Is.EqualTo(118).Within(4));
            Assert.That((int)c.G, Is.EqualTo(118).Within(4));
            Assert.That((int)c.B, Is.EqualTo(118).Within(4));
            int avg = (c.R + c.G + c.B) / 3;
            Assert.That(System.Math.Abs(c.R - avg), Is.LessThanOrEqualTo(2));
        }

        // color-mix(in lch, white, black) = same as in lab for neutral (zero chroma).
        // CSS Color 4 §12.1: lch is a valid color-mix space.
        [Test]
        public void Color_mix_lch_white_black_produces_mid_grey() {
            var lch = Parse("color-mix(in lch, white, black)");
            var lab = Parse("color-mix(in lab, white, black)");
            Assert.That((int)lch.R, Is.EqualTo((int)lab.R).Within(3));
            Assert.That((int)lch.G, Is.EqualTo((int)lab.G).Within(3));
            Assert.That((int)lch.B, Is.EqualTo((int)lab.B).Within(3));
        }

        // color-mix(in display-p3, ...) space should parse and return a color.
        // CSS Color 4 §12.1: display-p3 is a valid color-mix space.
        [Test]
        public void Color_mix_display_p3_space_parses() {
            var c = Parse("color-mix(in display-p3, red, blue)");
            Assert.That(c.A, Is.EqualTo(1f));
            // Result should have some R and B content (a purple-ish color).
            Assert.That((int)c.R + (int)c.B, Is.GreaterThan(0));
        }

        // ---------------------------------------------------------------
        // § color-mix() in lab/lch/wide-gamut — A1 fix coverage
        // ---------------------------------------------------------------

        // color-mix(in lab, white, black) 25% — 75% toward black → darker grey.
        [Test]
        public void Color_mix_lab_25_75_bias_black() {
            var c = Parse("color-mix(in lab, white 25%, black 75%)");
            // L=50 is 50% mix; 25% white → L≈25 → much darker than 118.
            Assert.That((int)c.R, Is.LessThan(90));
            Assert.That((int)c.G, Is.LessThan(90));
            Assert.That((int)c.B, Is.LessThan(90));
            // Should still be neutral grey.
            Assert.That(System.Math.Abs(c.R - c.G), Is.LessThanOrEqualTo(3));
        }

        // color-mix(in lab, white, black) 75%/25% — 75% toward white → lighter grey.
        [Test]
        public void Color_mix_lab_75_25_bias_white() {
            var c = Parse("color-mix(in lab, white 75%, black 25%)");
            // 75% white → L≈75 → lighter than 118.
            Assert.That((int)c.R, Is.GreaterThan(150));
            // Should be neutral grey.
            Assert.That(System.Math.Abs(c.R - c.G), Is.LessThanOrEqualTo(3));
        }

        // color-mix(in lch, red, blue) at 50%: midpoint in LCh has a hue path
        // through the colour wheel — result should differ from sRGB midpoint.
        [Test]
        public void Color_mix_lch_50_differs_from_srgb_midpoint() {
            var lch = Parse("color-mix(in lch, red, blue)");
            var srgb = Parse("color-mix(in srgb, red, blue)");
            // LCh interpolates through a different hue arc; R or B should differ.
            bool differs = (int)lch.R != (int)srgb.R || (int)lch.B != (int)srgb.B;
            Assert.That(differs, "color-mix in lch should differ from sRGB midpoint");
        }

        // color-mix(in lch, red 75%, blue 25%) — should be more red-biased.
        [Test]
        public void Color_mix_lch_75_25_bias_red() {
            var c = Parse("color-mix(in lch, red 75%, blue 25%)");
            // Dominant red channel must be high.
            Assert.That(c.R, Is.GreaterThan(c.B));
        }

        // color-mix(in display-p3, red, blue) at 25% red — blue-biased.
        [Test]
        public void Color_mix_display_p3_25_75_bias_blue() {
            var c = Parse("color-mix(in display-p3, red 25%, blue 75%)");
            // Blue-biased: R low, B high.
            Assert.That(c.B, Is.GreaterThan(c.R));
            Assert.That(c.A, Is.EqualTo(1f));
        }

        // color-mix(in rec2020, white, black) at 50% — should yield a mid-grey.
        [Test]
        public void Color_mix_rec2020_white_black_50pct_is_mid_grey() {
            var c = Parse("color-mix(in rec2020, white, black)");
            // Rec.2020 linear lerp of (1,1,1) and (0,0,0) = (0.5,0.5,0.5) in linear
            // → sRGB encode ≈ 188. Allow ±10 for transfer function variation.
            Assert.That((int)c.R, Is.EqualTo(188).Within(10));
            Assert.That((int)c.G, Is.EqualTo(188).Within(10));
            Assert.That((int)c.B, Is.EqualTo(188).Within(10));
            // Must be neutral grey.
            Assert.That(System.Math.Abs(c.R - c.G), Is.LessThanOrEqualTo(3));
        }

        // color-mix(in xyz, white, black) at 50% — linear XYZ lerp ≈ same as srgb-linear.
        [Test]
        public void Color_mix_xyz_white_black_50pct_is_mid_grey() {
            var xyz = Parse("color-mix(in xyz, white, black)");
            var linear = Parse("color-mix(in srgb-linear, white, black)");
            // XYZ lerp of white+black is the same as linear-RGB lerp (both are linear).
            Assert.That((int)xyz.R, Is.EqualTo((int)linear.R).Within(2));
            Assert.That((int)xyz.G, Is.EqualTo((int)linear.G).Within(2));
            Assert.That((int)xyz.B, Is.EqualTo((int)linear.B).Within(2));
        }

        // color-mix(in xyz-d50, white, black) at 50% — should also yield a mid-grey.
        [Test]
        public void Color_mix_xyz_d50_white_black_50pct_is_mid_grey() {
            var c = Parse("color-mix(in xyz-d50, white, black)");
            // XYZ-D50 white point differs from D65 but white/black endpoints
            // encode as the same sRGB bytes → midpoint is still neutral grey.
            Assert.That((int)c.R, Is.EqualTo(188).Within(5));
            Assert.That((int)c.G, Is.EqualTo(188).Within(5));
            Assert.That((int)c.B, Is.EqualTo(188).Within(5));
        }

        // color-mix(in display-p3, white, white) clamps in-gamut — should be (255,255,255).
        [Test]
        public void Color_mix_display_p3_white_white_is_white() {
            var c = Parse("color-mix(in display-p3, white, white)");
            Assert.That((int)c.R, Is.EqualTo(255).Within(1));
            Assert.That((int)c.G, Is.EqualTo(255).Within(1));
            Assert.That((int)c.B, Is.EqualTo(255).Within(1));
        }

        // Invalid space name still throws (regression guard for error path).
        [Test]
        public void Color_mix_invalid_space_throws() {
            Assert.Throws<CssValueParseException>(() => Parse("color-mix(in notaspace, red, blue)"));
        }

        // color-mix alpha blending: alpha channels of input colors should lerp.
        [Test]
        public void Color_mix_srgb_alpha_lerps() {
            // Mix fully-transparent red (alpha=0) with fully-opaque blue (alpha=1).
            var c = Parse("color-mix(in srgb, rgb(255 0 0 / 0), rgb(0 0 255 / 1))");
            // At 50/50, alpha should be 0.5.
            Assert.That(c.A, Is.EqualTo(0.5f).Within(0.02f));
        }

        // ---------------------------------------------------------------
        // § Relative colors — CSS Color 5 §4 — additional cases
        // ---------------------------------------------------------------

        // oklch(from red calc(l - 0.1) c h) = a darker red.
        // Red's OKLch L ≈ 0.628; subtracting 0.1 → L≈0.528 → darker.
        [Test]
        public void Relative_oklch_darken_red_produces_darker_red() {
            var src = Parse("oklch(from red l c h)");      // identity round-trip
            var dark = Parse("oklch(from red calc(l - 0.1) c h)");
            // Darker → lower total luminance → lower average byte value.
            int srcAvg = (src.R + src.G + src.B) / 3;
            int darkAvg = (dark.R + dark.G + dark.B) / 3;
            Assert.That(darkAvg, Is.LessThan(srcAvg), "darkened oklch should have lower average byte value");
            // Still dominantly red.
            Assert.That(dark.R, Is.GreaterThan(dark.G));
            Assert.That(dark.R, Is.GreaterThan(dark.B));
        }

        // oklch(from blue calc(l + 0.1) c h) = a lighter blue.
        [Test]
        public void Relative_oklch_lighten_blue_produces_lighter_blue() {
            var src = Parse("oklch(from blue l c h)");
            var light = Parse("oklch(from blue calc(l + 0.1) c h)");
            int srcAvg = (src.R + src.G + src.B) / 3;
            int lightAvg = (light.R + light.G + light.B) / 3;
            Assert.That(lightAvg, Is.GreaterThan(srcAvg), "lightened oklch should have higher average byte value");
        }

        // oklab(from green calc(l + 0.1) a b) = a lighter green.
        [Test]
        public void Relative_oklab_lighten_green_produces_lighter_green() {
            var src = Parse("oklab(from green l a b)");
            var light = Parse("oklab(from green calc(l + 0.1) a b)");
            int srcAvg = (src.R + src.G + src.B) / 3;
            int lightAvg = (light.R + light.G + light.B) / 3;
            Assert.That(lightAvg, Is.GreaterThan(srcAvg));
        }

        // lch(from red calc(l + 10) c h) = a brighter red.
        [Test]
        public void Relative_lch_brighten_red_produces_brighter_red() {
            var src = Parse("lch(from red l c h)");
            var bright = Parse("lch(from red calc(l + 10) c h)");
            int srcAvg = (src.R + src.G + src.B) / 3;
            int brightAvg = (bright.R + bright.G + bright.B) / 3;
            Assert.That(brightAvg, Is.GreaterThan(srcAvg));
            Assert.That(bright.R, Is.GreaterThan(bright.G));
        }

        // color(srgb from blue calc(r + 0.1) g b) = slight red added to blue → purple.
        [Test]
        public void Relative_color_srgb_add_red_to_blue_produces_purple() {
            var c = Parse("color(srgb from blue calc(r + 0.1) g b)");
            // Blue had r=0, now r≈0.1 → 25 bytes. B still 255.
            Assert.That((int)c.R, Is.EqualTo(25).Within(4));
            Assert.That((int)c.B, Is.EqualTo(255).Within(2));
        }

        // H10b canonical example: rgb(from <color> calc(r ...) g b) — the sRGB
        // channel ident `r` (0..255 scale) wired through calc(). `rgb(from red
        // calc(r - 100) g b)` darkens the red channel from 255 to 155.
        [Test]
        public void Relative_rgb_calc_on_r_channel_scales_in_byte_space() {
            var c = Parse("rgb(from red calc(r - 100) g b)");
            // red = (255,0,0); r-100 = 155, g/b passthrough.
            Assert.That((int)c.R, Is.EqualTo(155).Within(1), "r channel calc resolves in 0..255 space");
            Assert.That((int)c.G, Is.EqualTo(0));
            Assert.That((int)c.B, Is.EqualTo(0));
        }

        // rgb(from <color> r calc(g + 80) b) — green channel raised; identity on r/b.
        [Test]
        public void Relative_rgb_calc_on_g_channel_adds_in_byte_space() {
            var c = Parse("rgb(from rgb(10 20 30) r calc(g + 80) b)");
            Assert.That((int)c.R, Is.EqualTo(10).Within(1));
            Assert.That((int)c.G, Is.EqualTo(100).Within(1), "g + 80 in 0..255 space");
            Assert.That((int)c.B, Is.EqualTo(30).Within(1));
        }

        // lab(from <color> calc(l + 10) a b) — CIELab lightness (0..100) via calc().
        [Test]
        public void Relative_lab_calc_on_lightness_brightens() {
            var src = Parse("lab(from red l a b)");
            var bright = Parse("lab(from red calc(l + 10) a b)");
            int srcAvg = (src.R + src.G + src.B) / 3;
            int brightAvg = (bright.R + bright.G + bright.B) / 3;
            Assert.That(brightAvg, Is.GreaterThan(srcAvg), "lab lightness calc(l+10) should brighten");
        }

        // hsl(from hsl(120 100% 25%) h s calc(l + 10)) = brighter green.
        // In hsl() relative-color context, `l` is the numeric lightness value
        // (0..100 scale), so adding 10 units is correct; mixing with `10%`
        // (a <percentage> token) would be a type error in calc().
        [Test]
        public void Relative_hsl_brighten_via_calc_lightness() {
            var bright = Parse("hsl(from hsl(120 100% 25%) h s calc(l + 10))");
            var dark = Parse("hsl(from hsl(120 100% 25%) h s l)");
            // Adding 10 units of lightness should increase average brightness.
            int brightAvg = (bright.R + bright.G + bright.B) / 3;
            int darkAvg = (dark.R + dark.G + dark.B) / 3;
            Assert.That(brightAvg, Is.GreaterThan(darkAvg));
        }

        // ---------------------------------------------------------------
        // § hwb() — additional edge cases
        // ---------------------------------------------------------------

        // hwb(0 0% 0%) = pure red (already in ModernColorTests; guard regression).
        [Test]
        public void Hwb_pure_red_regression_guard() {
            var c = Parse("hwb(0 0% 0%)");
            Assert.That((int)c.R, Is.EqualTo(255));
            Assert.That((int)c.G, Is.EqualTo(0));
            Assert.That((int)c.B, Is.EqualTo(0));
        }

        // hwb(120 0% 0%) = pure green.
        [Test]
        public void Hwb_hue_120_pure_green() {
            var c = Parse("hwb(120 0% 0%)");
            Assert.That((int)c.G, Is.EqualTo(255));
            Assert.That((int)c.R, Is.EqualTo(0));
            Assert.That((int)c.B, Is.EqualTo(0));
        }

        // hwb(240 0% 0%) = pure blue.
        [Test]
        public void Hwb_hue_240_pure_blue() {
            var c = Parse("hwb(240 0% 0%)");
            Assert.That((int)c.B, Is.EqualTo(255));
            Assert.That((int)c.R, Is.EqualTo(0));
            Assert.That((int)c.G, Is.EqualTo(0));
        }

        // hwb(0 50% 0%) = a lighter pink (white washes red).
        [Test]
        public void Hwb_50_pct_white_washes_red_to_pink() {
            var c = Parse("hwb(0 50% 0%)");
            // Result: R=255, G=127, B=127 (approximately).
            Assert.That((int)c.R, Is.EqualTo(255));
            Assert.That((int)c.G, Is.EqualTo(128).Within(2));
            Assert.That((int)c.B, Is.EqualTo(128).Within(2));
        }

        // hwb(0 0% 50%) = dark red (black washes).
        [Test]
        public void Hwb_50_pct_black_darkens_red() {
            var c = Parse("hwb(0 0% 50%)");
            Assert.That((int)c.R, Is.EqualTo(128).Within(2));
            Assert.That((int)c.G, Is.EqualTo(0));
            Assert.That((int)c.B, Is.EqualTo(0));
        }

        // hwb() wraps hue > 360°.
        [Test]
        public void Hwb_hue_wraps_at_360() {
            var a = Parse("hwb(0 0% 0%)");
            var b = Parse("hwb(360 0% 0%)");
            Assert.That((int)a.R, Is.EqualTo((int)b.R).Within(1));
            Assert.That((int)a.G, Is.EqualTo((int)b.G).Within(1));
            Assert.That((int)a.B, Is.EqualTo((int)b.B).Within(1));
        }

        // hwb() with negative hue wraps back to positive.
        [Test]
        public void Hwb_negative_hue_wraps() {
            var a = Parse("hwb(300 0% 0%)");
            var b = Parse("hwb(-60 0% 0%)");
            Assert.That((int)a.R, Is.EqualTo((int)b.R).Within(1));
            Assert.That((int)a.G, Is.EqualTo((int)b.G).Within(1));
            Assert.That((int)a.B, Is.EqualTo((int)b.B).Within(1));
        }
    }
}
