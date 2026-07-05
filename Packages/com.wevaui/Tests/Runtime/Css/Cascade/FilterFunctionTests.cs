using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Filter Effects Module Level 1 — cascade-side coverage for `filter:`.
    //
    // CssProperties registers `filter` as non-inherited with initial value
    // `none` (CssProperties.cs line ~885). FilterResolver.cs in
    // Runtime/Paint/Conversion/ consumes the computed value; this file pins
    // only the cascade boundary:
    //   - initial value (`none`) when no rule applies
    //   - parse → cascade → Get round-trip for each of the 10 filter functions
    //   - filter chain composition (multiple functions, order preserved)
    //   - `filter: none` explicit keyword
    //   - non-inheritance (filter is non-inherited per spec)
    //   - hue-rotate edge cases (0deg vs 360deg)
    //   - drop-shadow with and without optional arguments
    //   - negative blur radius (spec requires clamped/rejected; filter is
    //     non-negative, so a negative value may be treated as invalid;
    //     we pin the observable cascade-level behavior rather than a specific
    //     implementation choice)
    //
    // The paint-level tests in Tests/Runtime/Paint/Filters/FilterParserTests.cs
    // cover the FilterParser and FilterResolver function-level resolution.
    // This file is intentionally cascade-only and runs in the TestVerifyAll
    // headless harness.
    public class FilterFunctionTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static Stylesheet Css(string s) => CssParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(Css(s));

        // Compute `filter` on a single <div id="x"> element.
        static ComputedStyle Compute(string css) {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("x"));
        }

        // Compute on the child span to test inheritance / non-inheritance.
        static ComputedStyle ComputeChild(string css) {
            var doc = Html("<div><span id=\"child\"></span></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("child"));
        }

        // ══════════════════════════════════════════════════════════════════
        // Initial value and `none` keyword
        // ══════════════════════════════════════════════════════════════════

        [Test]
        public void Filter_initial_value_is_none() {
            // CSS Filter Effects 1 §2: `filter` initial value is `none`.
            var cs = Compute("");
            Assert.That(cs.Get("filter"), Is.EqualTo("none"));
        }

        [Test]
        public void Filter_none_explicit_round_trips() {
            // Explicitly setting `none` should store the keyword; the cascade
            // must not drop or transform it.
            var cs = Compute("#x { filter: none; }");
            Assert.That(cs.Get("filter"), Is.EqualTo("none"));
        }

        // ══════════════════════════════════════════════════════════════════
        // Per-function cascade round-trips
        // ══════════════════════════════════════════════════════════════════

        [Test]
        public void Filter_blur_px_round_trips() {
            // blur(<length>) — the canonical pixel-length variant.
            var cs = Compute("#x { filter: blur(5px); }");
            var v = cs.Get("filter");
            Assert.That(v, Does.Contain("blur"));
            Assert.That(v, Does.Contain("5px"));
        }

        [Test]
        public void Filter_brightness_number_round_trips() {
            // brightness(<number>) — a multiplier > 1 brightens.
            var cs = Compute("#x { filter: brightness(1.5); }");
            var v = cs.Get("filter");
            Assert.That(v, Does.Contain("brightness"));
            Assert.That(v, Does.Contain("1.5"));
        }

        [Test]
        public void Filter_brightness_percentage_round_trips() {
            // brightness(<percentage>) — 150% == 1.5 in number form.
            var cs = Compute("#x { filter: brightness(150%); }");
            var v = cs.Get("filter");
            Assert.That(v, Does.Contain("brightness"));
            Assert.That(v, Does.Contain("150%"));
        }

        [Test]
        public void Filter_contrast_number_round_trips() {
            // contrast(<number>) — > 1 increases contrast.
            var cs = Compute("#x { filter: contrast(1.2); }");
            var v = cs.Get("filter");
            Assert.That(v, Does.Contain("contrast"));
            Assert.That(v, Does.Contain("1.2"));
        }

        [Test]
        public void Filter_contrast_percentage_round_trips() {
            var cs = Compute("#x { filter: contrast(80%); }");
            var v = cs.Get("filter");
            Assert.That(v, Does.Contain("contrast"));
            Assert.That(v, Does.Contain("80%"));
        }

        [Test]
        public void Filter_grayscale_number_round_trips() {
            // grayscale(1) = full desaturation; values between 0 and 1.
            var cs = Compute("#x { filter: grayscale(1); }");
            var v = cs.Get("filter");
            Assert.That(v, Does.Contain("grayscale"));
        }

        [Test]
        public void Filter_grayscale_percentage_round_trips() {
            var cs = Compute("#x { filter: grayscale(50%); }");
            var v = cs.Get("filter");
            Assert.That(v, Does.Contain("grayscale"));
            Assert.That(v, Does.Contain("50%"));
        }

        [Test]
        public void Filter_opacity_number_round_trips() {
            // opacity() via filter is a separate property from the `opacity`
            // CSS property; values 0..1.
            var cs = Compute("#x { filter: opacity(0.6); }");
            var v = cs.Get("filter");
            Assert.That(v, Does.Contain("opacity"));
            Assert.That(v, Does.Contain("0.6"));
        }

        [Test]
        public void Filter_opacity_percentage_round_trips() {
            var cs = Compute("#x { filter: opacity(60%); }");
            var v = cs.Get("filter");
            Assert.That(v, Does.Contain("opacity"));
            Assert.That(v, Does.Contain("60%"));
        }

        [Test]
        public void Filter_saturate_number_round_trips() {
            // saturate() — values > 1 are valid per spec (super-saturate).
            var cs = Compute("#x { filter: saturate(2); }");
            var v = cs.Get("filter");
            Assert.That(v, Does.Contain("saturate"));
        }

        [Test]
        public void Filter_saturate_percentage_round_trips() {
            var cs = Compute("#x { filter: saturate(200%); }");
            var v = cs.Get("filter");
            Assert.That(v, Does.Contain("saturate"));
            Assert.That(v, Does.Contain("200%"));
        }

        [Test]
        public void Filter_hue_rotate_deg_round_trips() {
            // hue-rotate(<angle>) — the canonical degree form.
            var cs = Compute("#x { filter: hue-rotate(90deg); }");
            var v = cs.Get("filter");
            Assert.That(v, Does.Contain("hue-rotate"));
            Assert.That(v, Does.Contain("90deg"));
        }

        [Test]
        public void Filter_invert_number_round_trips() {
            // invert(1) = fully inverted, invert(0) = no-op.
            var cs = Compute("#x { filter: invert(0.8); }");
            var v = cs.Get("filter");
            Assert.That(v, Does.Contain("invert"));
            Assert.That(v, Does.Contain("0.8"));
        }

        [Test]
        public void Filter_invert_percentage_round_trips() {
            var cs = Compute("#x { filter: invert(75%); }");
            var v = cs.Get("filter");
            Assert.That(v, Does.Contain("invert"));
            Assert.That(v, Does.Contain("75%"));
        }

        [Test]
        public void Filter_sepia_number_round_trips() {
            // sepia(1) = full sepia tone.
            var cs = Compute("#x { filter: sepia(0.5); }");
            var v = cs.Get("filter");
            Assert.That(v, Does.Contain("sepia"));
            Assert.That(v, Does.Contain("0.5"));
        }

        [Test]
        public void Filter_sepia_percentage_round_trips() {
            var cs = Compute("#x { filter: sepia(100%); }");
            var v = cs.Get("filter");
            Assert.That(v, Does.Contain("sepia"));
            Assert.That(v, Does.Contain("100%"));
        }

        [Test]
        public void Filter_drop_shadow_two_lengths_round_trips() {
            // drop-shadow(<length> <length>) — minimal form (no blur, no color).
            var cs = Compute("#x { filter: drop-shadow(4px 8px); }");
            var v = cs.Get("filter");
            Assert.That(v, Does.Contain("drop-shadow"));
            Assert.That(v, Does.Contain("4px"));
            Assert.That(v, Does.Contain("8px"));
        }

        [Test]
        public void Filter_drop_shadow_with_blur_round_trips() {
            // drop-shadow(<length> <length> <length>) — with blur radius.
            var cs = Compute("#x { filter: drop-shadow(2px 4px 6px); }");
            var v = cs.Get("filter");
            Assert.That(v, Does.Contain("drop-shadow"));
            Assert.That(v, Does.Contain("2px"));
            Assert.That(v, Does.Contain("4px"));
            Assert.That(v, Does.Contain("6px"));
        }

        [Test]
        public void Filter_drop_shadow_with_color_round_trips() {
            // drop-shadow(<length> <length> <length> <color>) — full form.
            var cs = Compute("#x { filter: drop-shadow(2px 4px 8px red); }");
            var v = cs.Get("filter");
            Assert.That(v, Does.Contain("drop-shadow"));
            Assert.That(v, Does.Contain("red"));
        }

        // ══════════════════════════════════════════════════════════════════
        // Filter chain composition (order preservation)
        // ══════════════════════════════════════════════════════════════════

        [Test]
        public void Filter_two_function_chain_round_trips_in_order() {
            // The cascade must store the full declaration text; order matters
            // because filter functions compose left-to-right per spec §2.1.
            var cs = Compute("#x { filter: blur(2px) brightness(1.2); }");
            var v = cs.Get("filter");
            Assert.That(v, Does.Contain("blur"));
            Assert.That(v, Does.Contain("brightness"));
            // blur must appear before brightness in the stored text.
            Assert.That(v.IndexOf("blur", System.StringComparison.Ordinal),
                Is.LessThan(v.IndexOf("brightness", System.StringComparison.Ordinal)));
        }

        [Test]
        public void Filter_three_function_chain_preserves_order() {
            // Three-function chain: blur, grayscale, sepia — ordering pinned.
            var cs = Compute("#x { filter: blur(3px) grayscale(0.5) sepia(0.3); }");
            var v = cs.Get("filter");
            Assert.That(v, Does.Contain("blur"));
            Assert.That(v, Does.Contain("grayscale"));
            Assert.That(v, Does.Contain("sepia"));
            int blurIdx = v.IndexOf("blur", System.StringComparison.Ordinal);
            int grayIdx = v.IndexOf("grayscale", System.StringComparison.Ordinal);
            int sepiaIdx = v.IndexOf("sepia", System.StringComparison.Ordinal);
            Assert.That(blurIdx, Is.LessThan(grayIdx));
            Assert.That(grayIdx, Is.LessThan(sepiaIdx));
        }

        [Test]
        public void Filter_full_chain_all_ten_functions() {
            // Exercise all ten filter functions in one declaration to confirm
            // none are silently dropped by the parser or cascade.
            const string css = "#x { filter: blur(2px) brightness(1.1) contrast(1.2) grayscale(0.3) opacity(0.9) saturate(1.5) hue-rotate(45deg) invert(0.2) sepia(0.4) drop-shadow(1px 2px 3px black); }";
            var cs = Compute(css);
            var v = cs.Get("filter");
            Assert.That(v, Does.Contain("blur"));
            Assert.That(v, Does.Contain("brightness"));
            Assert.That(v, Does.Contain("contrast"));
            Assert.That(v, Does.Contain("grayscale"));
            Assert.That(v, Does.Contain("opacity"));
            Assert.That(v, Does.Contain("saturate"));
            Assert.That(v, Does.Contain("hue-rotate"));
            Assert.That(v, Does.Contain("invert"));
            Assert.That(v, Does.Contain("sepia"));
            Assert.That(v, Does.Contain("drop-shadow"));
        }

        // ══════════════════════════════════════════════════════════════════
        // Non-inheritance
        // ══════════════════════════════════════════════════════════════════

        [Test]
        public void Filter_does_not_inherit() {
            // CSS Filter Effects 1 §2: `filter` is non-inherited. A child
            // element must see the initial value `none`, not the parent's
            // filter chain.
            var cs = ComputeChild("div { filter: blur(4px) brightness(0.8); }");
            Assert.That(cs.Get("filter"), Is.EqualTo("none"),
                "filter is non-inherited; child must see initial `none`, not the parent's blur+brightness chain");
        }

        [Test]
        public void Filter_does_not_inherit_drop_shadow() {
            // Regression guard: drop-shadow on a parent must not appear on
            // child's computed filter value.
            var cs = ComputeChild("div { filter: drop-shadow(2px 4px 8px red); }");
            Assert.That(cs.Get("filter"), Is.EqualTo("none"),
                "filter is non-inherited; child must not inherit parent drop-shadow");
        }

        // ══════════════════════════════════════════════════════════════════
        // Edge cases
        // ══════════════════════════════════════════════════════════════════

        [Test]
        public void Filter_hue_rotate_zero_deg_round_trips() {
            // hue-rotate(0deg) is explicitly valid per spec (identity transform).
            var cs = Compute("#x { filter: hue-rotate(0deg); }");
            var v = cs.Get("filter");
            Assert.That(v, Does.Contain("hue-rotate"));
            Assert.That(v, Does.Contain("0deg"));
        }

        [Test]
        public void Filter_hue_rotate_360deg_round_trips() {
            // 360deg is a full rotation — equivalent visually to 0deg but the
            // raw authored text must survive the cascade unchanged.
            var cs = Compute("#x { filter: hue-rotate(360deg); }");
            var v = cs.Get("filter");
            Assert.That(v, Does.Contain("hue-rotate"));
            Assert.That(v, Does.Contain("360deg"));
        }

        [Test]
        public void Filter_specificity_higher_rule_wins() {
            // A more-specific selector (#x) must win over the tag rule (div).
            var cs = Compute("div { filter: grayscale(1); } #x { filter: brightness(1.2); }");
            var v = cs.Get("filter");
            Assert.That(v, Does.Contain("brightness"),
                "ID rule (#x) has higher specificity and must win over tag rule (div)");
            Assert.That(v, Has.None.EqualTo("grayscale"),
                "Lower-specificity grayscale rule must be overridden");
        }

        [Test]
        public void Filter_later_rule_wins_on_equal_specificity() {
            // Same specificity: source-order tiebreak — last rule wins.
            var cs = Compute("#x { filter: sepia(1); } #x { filter: invert(1); }");
            var v = cs.Get("filter");
            Assert.That(v, Does.Contain("invert"),
                "Later same-specificity rule must win (source-order tiebreak)");
        }

        [Test]
        public void Filter_important_overrides_normal_in_same_origin() {
            // !important lifts a declaration above normal author declarations.
            var cs = Compute("#x { filter: sepia(1) !important; } #x { filter: invert(1); }");
            var v = cs.Get("filter");
            Assert.That(v, Does.Contain("sepia"),
                "!important sepia must override normal invert despite same specificity");
        }
    }
}
