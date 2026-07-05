using NUnit.Framework;
using Weva.Css.Values;

namespace Weva.Tests.Css.Values {
    // CSS Color Level 4 §12.3 — <hue-interpolation-method> in color-mix().
    //
    // Spec: https://www.w3.org/TR/css-color-4/#hue-interpolation
    //
    // Cylindrical color spaces (oklch, lch, hsl, hwb) support four hue
    // interpolation methods that differ in which arc of the hue wheel is
    // traversed:
    //
    //   shorter   — the smaller of the two arcs (default per §12.3)
    //   longer    — the larger arc
    //   increasing — always travel in the positive (clockwise) direction
    //   decreasing — always travel in the negative direction
    //
    // The method keyword appears between the space name and the colors:
    //   color-mix(in oklch shorter hue, red, blue)
    //
    // ColorMixer.cs implements the four methods via LerpHueRadians /
    // LerpHueDegrees; CssValueParser.ParseColorMix calls
    // ColorMixer.TryParseHueMethod to read the optional keyword before
    // the first comma.
    public class ColorMixHueMethodTests {
        static CssColor Parse(string s) => (CssColor)CssValueParser.Parse(s);

        // ── Parse-level smoke tests ──────────────────────────────────────────

        [Test]
        public void Shorter_hue_keyword_parses_in_oklch() {
            // CSS Color 4 §12.3: explicit `shorter hue` is permitted and
            // must produce the same result as omitting the method keyword,
            // since shorter is the default.
            var explicit_ = Parse("color-mix(in oklch shorter hue, red, blue)");
            var default_  = Parse("color-mix(in oklch, red, blue)");
            Assert.That(explicit_.A, Is.EqualTo(1f), "shorter hue result must be opaque");
            // Explicit shorter == default shorter (within rounding).
            Assert.That((int)explicit_.R, Is.EqualTo((int)default_.R).Within(2));
            Assert.That((int)explicit_.G, Is.EqualTo((int)default_.G).Within(2));
            Assert.That((int)explicit_.B, Is.EqualTo((int)default_.B).Within(2));
        }

        [Test]
        public void Longer_hue_keyword_parses_in_oklch() {
            // CSS Color 4 §12.3: `longer hue` must parse and return a color.
            var c = Parse("color-mix(in oklch longer hue, red, blue)");
            Assert.That(c.A, Is.EqualTo(1f), "longer hue result must be opaque");
        }

        [Test]
        public void Increasing_hue_keyword_parses_in_oklch() {
            // CSS Color 4 §12.3: `increasing hue` must parse and return a color.
            var c = Parse("color-mix(in oklch increasing hue, red, blue)");
            Assert.That(c.A, Is.EqualTo(1f));
        }

        [Test]
        public void Decreasing_hue_keyword_parses_in_oklch() {
            // CSS Color 4 §12.3: `decreasing hue` must parse and return a color.
            var c = Parse("color-mix(in oklch decreasing hue, red, blue)");
            Assert.That(c.A, Is.EqualTo(1f));
        }

        // ── Shorter vs Longer behavioral divergence ──────────────────────────

        [Test]
        public void Shorter_and_longer_produce_different_results_in_oklch() {
            // Red (hue ≈ 29°) and blue (hue ≈ 264°) in OKLCh.
            // The shorter arc goes ≈ -125° (through pink/magenta);
            // the longer arc goes ≈ +235° (through yellow/green).
            // The two midpoints must differ visually.
            var shorter  = Parse("color-mix(in oklch shorter hue, red, blue)");
            var longer   = Parse("color-mix(in oklch longer hue, red, blue)");
            // They must not be the same colour.
            bool different = (int)shorter.R != (int)longer.R
                          || (int)shorter.G != (int)longer.G
                          || (int)shorter.B != (int)longer.B;
            Assert.That(different, Is.True,
                "shorter hue and longer hue must traverse different arcs and produce distinct mid-colours");
        }

        [Test]
        public void Shorter_hue_red_blue_midpoint_is_pink_not_green() {
            // Red→Blue shorter arc passes through magenta/pink territory.
            // The green channel should be low relative to red and blue.
            var c = Parse("color-mix(in oklch shorter hue, red, blue)");
            // A pink/magenta has elevated R+B and low G.
            int rPlusB = (int)c.R + (int)c.B;
            Assert.That(c.G, Is.LessThan(rPlusB / 2 + 30),
                "shorter arc red-to-blue should pass through pink/magenta, not green");
        }

        [Test]
        public void Longer_hue_red_blue_midpoint_has_elevated_green() {
            // Red→Blue longer arc passes through yellow/green territory.
            // The green channel should be higher than the shorter-arc midpoint.
            var shorter = Parse("color-mix(in oklch shorter hue, red, blue)");
            var longer  = Parse("color-mix(in oklch longer hue, red, blue)");
            Assert.That(longer.G, Is.GreaterThan(shorter.G),
                "longer arc traverses yellow/green; G should exceed the shorter-arc midpoint");
        }

        // ── Increasing vs Decreasing behavioral divergence ───────────────────

        [Test]
        public void Increasing_and_decreasing_produce_different_results_in_hsl() {
            // In HSL, green (hue 120°) to red (hue 0° / 360°):
            // increasing hue travels 120→360 (+240°, through cyan/blue);
            // decreasing hue travels 120→0 (-120°, through yellow).
            // Midpoints must differ.
            var inc = Parse("color-mix(in hsl increasing hue, green, red)");
            var dec = Parse("color-mix(in hsl decreasing hue, green, red)");
            bool different = (int)inc.R != (int)dec.R
                          || (int)inc.G != (int)dec.G
                          || (int)inc.B != (int)dec.B;
            Assert.That(different, Is.True,
                "increasing hue and decreasing hue must traverse different arcs in HSL");
        }

        [Test]
        public void Decreasing_hue_green_to_red_passes_through_yellow() {
            // Decreasing from 120° toward 0° traverses the yellow range (60°).
            // The 50% midpoint hue should be ≈ 60° (yellow).
            var c = Parse("color-mix(in hsl decreasing hue, green, red)");
            // A yellow-ish colour has high R, high G, low B.
            Assert.That(c.R, Is.GreaterThan(100), "hue 60° midpoint should have high R");
            Assert.That(c.G, Is.GreaterThan(100), "hue 60° midpoint should have high G");
            Assert.That(c.B, Is.LessThan(80),     "hue 60° midpoint should have low B");
        }

        [Test]
        public void Increasing_hue_green_to_red_passes_through_cyan_or_blue() {
            // Increasing from 120° toward 360° traverses cyan/blue (180°–270°).
            // The 50% midpoint hue should be near 240° (blue direction).
            var c = Parse("color-mix(in hsl increasing hue, green, red)");
            // A cyan/blue colour has low-to-mid R and elevated B.
            Assert.That(c.B, Is.GreaterThan(80), "hue 240° midpoint should have elevated B");
        }

        // ── Method symmetry for achromatic inputs ────────────────────────────

        [Test]
        public void All_hue_methods_agree_for_achromatic_colors_in_oklch() {
            // CSS Color 4 §12.3 powerless-hue rule: when both endpoints have
            // near-zero chroma, the hue is undefined and all methods must
            // produce the same neutral result (a grey).
            var shorter  = Parse("color-mix(in oklch shorter hue, white, black)");
            var longer   = Parse("color-mix(in oklch longer hue, white, black)");
            var inc      = Parse("color-mix(in oklch increasing hue, white, black)");
            var dec      = Parse("color-mix(in oklch decreasing hue, white, black)");
            // All four should be neutral grey (R ≈ G ≈ B).
            foreach (var c in new[] { shorter, longer, inc, dec }) {
                Assert.That(System.Math.Abs(c.R - c.G), Is.LessThanOrEqualTo(5),
                    "achromatic mix should be neutral grey regardless of hue method");
                Assert.That(System.Math.Abs(c.R - c.B), Is.LessThanOrEqualTo(5),
                    "achromatic mix should be neutral grey regardless of hue method");
            }
        }

        // ── HWB cylindrical space supports hue methods ───────────────────────

        [Test]
        public void Shorter_hue_parses_in_hwb() {
            // CSS Color 4 §12.1: hwb is a cylindrical space and therefore
            // supports hue interpolation methods.
            var c = Parse("color-mix(in hwb shorter hue, red, blue)");
            Assert.That(c.A, Is.EqualTo(1f));
        }

        [Test]
        public void Longer_hue_parses_in_hwb() {
            var c = Parse("color-mix(in hwb longer hue, red, blue)");
            Assert.That(c.A, Is.EqualTo(1f));
        }

        [Test]
        public void Shorter_and_longer_differ_in_hwb() {
            var shorter = Parse("color-mix(in hwb shorter hue, red, blue)");
            var longer  = Parse("color-mix(in hwb longer hue, red, blue)");
            bool different = (int)shorter.R != (int)longer.R
                          || (int)shorter.G != (int)longer.G
                          || (int)shorter.B != (int)longer.B;
            Assert.That(different, Is.True,
                "shorter and longer hue methods must differ in HWB for red/blue endpoints");
        }

        // ── LCh cylindrical space supports hue methods ───────────────────────

        [Test]
        public void Shorter_hue_parses_in_lch() {
            // CSS Color 4 §12.1: lch is a cylindrical space (hue ∈ [0°,360°)).
            var c = Parse("color-mix(in lch shorter hue, red, blue)");
            Assert.That(c.A, Is.EqualTo(1f));
        }

        [Test]
        public void Shorter_and_longer_differ_in_lch() {
            var shorter = Parse("color-mix(in lch shorter hue, red, blue)");
            var longer  = Parse("color-mix(in lch longer hue, red, blue)");
            bool different = (int)shorter.R != (int)longer.R
                          || (int)shorter.G != (int)longer.G
                          || (int)shorter.B != (int)longer.B;
            Assert.That(different, Is.True,
                "shorter and longer hue methods must differ in LCh for red/blue endpoints");
        }
    }
}
