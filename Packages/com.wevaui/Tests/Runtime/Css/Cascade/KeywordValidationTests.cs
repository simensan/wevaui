using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Syntax 3 §5 / CSS Cascade L5 §3 — per-property keyword validation.
    //
    // An invalid declaration (one whose value is not in the property's
    // allowed keyword set) is treated as if not specified: the cascade falls
    // through to the next-lower-priority matching declaration, or to the
    // initial value when no valid declaration exists.
    //
    // Enforced for keyword-only properties registered in
    // CssPropertyKeywordValidator (v1 set: animation-composition,
    // background-blend-mode, mix-blend-mode, visibility).
    // Properties without a keyword entry keep pass-through.
    //
    // Bypass invariants (never treated as invalid):
    //   - CSS-wide keywords (initial/inherit/unset/revert/revert-layer)
    //   - Values containing var()
    //   - Custom properties (--foo: anything)
    //   - Properties not in the v1 validation set
    public class KeywordValidationTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static Stylesheet Css(string s) => CssParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(Css(s));

        static ComputedStyle Compute(string css) {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("x"));
        }

        static ComputedStyle ComputeMultiple(params string[] cssBlocks) {
            var doc = Html("<div id=\"x\"></div>");
            var sheets = new OriginatedStylesheet[cssBlocks.Length];
            for (int i = 0; i < cssBlocks.Length; i++) {
                sheets[i] = Author(cssBlocks[i]);
            }
            var engine = new CascadeEngine(sheets);
            return engine.Compute(doc.GetElementById("x"));
        }

        // ── 1. Falls back to initial when no prior valid rule ─────────────────

        [Test]
        public void Invalid_animation_composition_keyword_falls_back_to_initial() {
            // A single invalid declaration leaves the property at initial `replace`.
            var cs = Compute("#x { animation-composition: bogus-mode; }");
            Assert.That(cs.Get("animation-composition"), Is.EqualTo("replace"),
                "invalid keyword must be dropped; initial value applies");
        }

        [Test]
        public void Invalid_background_blend_mode_falls_back_to_initial() {
            // A single invalid declaration leaves background-blend-mode at initial `normal`.
            var cs = Compute("#x { background-blend-mode: not-a-blend-mode; }");
            Assert.That(cs.Get("background-blend-mode"), Is.EqualTo("normal"),
                "invalid keyword must be dropped; initial value applies");
        }

        [Test]
        public void Invalid_mix_blend_mode_falls_back_to_initial() {
            // mix-blend-mode: initial is `normal`.
            var cs = Compute("#x { mix-blend-mode: invalid-blend; }");
            Assert.That(cs.Get("mix-blend-mode"), Is.EqualTo("normal"),
                "invalid mix-blend-mode keyword must be dropped; initial applies");
        }

        [Test]
        public void Invalid_visibility_keyword_falls_back_to_initial() {
            // visibility: initial is `visible`.
            var cs = Compute("#x { visibility: totally-invisible; }");
            Assert.That(cs.Get("visibility"), Is.EqualTo("visible"),
                "invalid visibility keyword must be dropped; initial applies");
        }

        // ── 2. Falls back to prior rule (cascade fallback) ─────────────────────

        [Test]
        public void Invalid_winner_cascades_to_lower_priority_valid_declaration() {
            // Two rules: div (lower specificity, valid) and #x (higher specificity,
            // invalid). The invalid #x declaration is dropped; div's valid value wins.
            var cs = Compute("div { animation-composition: add; } #x { animation-composition: not-valid; }");
            Assert.That(cs.Get("animation-composition"), Is.EqualTo("add"),
                "invalid high-specificity declaration must be dropped; valid lower-spec wins");
        }

        [Test]
        public void Invalid_winner_blend_mode_cascades_to_valid_lower_rule() {
            // Higher-specificity invalid blend-mode falls back to lower-spec valid.
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { background-blend-mode: multiply; } #x { background-blend-mode: not-valid; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("background-blend-mode"), Is.EqualTo("multiply"),
                "invalid high-spec declaration falls back to valid lower-spec multiply");
        }

        [Test]
        public void Invalid_source_order_winner_falls_back_to_earlier_valid_rule() {
            // Same specificity, later source order wins normally. When later is
            // invalid, the earlier one prevails.
            var cs = Compute("#x { animation-composition: replace; } #x { animation-composition: no-such-keyword; }");
            Assert.That(cs.Get("animation-composition"), Is.EqualTo("replace"),
                "invalid later-source-order declaration is dropped; valid earlier value wins");
        }

        // ── 3. Valid keywords are unaffected ───────────────────────────────────

        [Test]
        public void Valid_animation_composition_keywords_round_trip() {
            // All three valid keywords must survive (not be dropped by the validator).
            var cs1 = Compute("#x { animation-composition: replace; }");
            Assert.That(cs1.Get("animation-composition"), Is.EqualTo("replace"));

            var cs2 = Compute("#x { animation-composition: add; }");
            Assert.That(cs2.Get("animation-composition"), Is.EqualTo("add"));

            var cs3 = Compute("#x { animation-composition: accumulate; }");
            Assert.That(cs3.Get("animation-composition"), Is.EqualTo("accumulate"));
        }

        [Test]
        public void Valid_blend_mode_keywords_are_not_dropped() {
            // All major blend-mode keywords must survive validation.
            string[] modes = { "normal", "multiply", "screen", "overlay", "darken",
                               "lighten", "color-dodge", "color-burn", "hard-light",
                               "soft-light", "difference", "exclusion", "hue",
                               "saturation", "color", "luminosity",
                               "plus-darker", "plus-lighter" };
            foreach (var mode in modes) {
                var cs = Compute($"#x {{ background-blend-mode: {mode}; }}");
                Assert.That(cs.Get("background-blend-mode"), Is.EqualTo(mode),
                    $"Valid keyword '{mode}' must not be dropped by the validator");
            }
        }

        [Test]
        public void Keyword_validation_is_case_insensitive() {
            // CSS keywords are case-insensitive; `REPLACE` and `Replace` must
            // be accepted the same as `replace`.
            var cs1 = Compute("#x { animation-composition: REPLACE; }");
            Assert.That(cs1.Get("animation-composition"), Is.EqualTo("REPLACE"),
                "uppercase REPLACE is a valid keyword and must not be dropped");

            var cs2 = Compute("#x { animation-composition: Multiply; }");
            // NOTE: the value round-trips as the authored casing;
            // it is NOT dropped (case-insensitive match to valid "multiply").
            Assert.That(cs2.Get("animation-composition"), Is.EqualTo("replace"),
                "Multiply is not a valid animation-composition keyword; initial applies");

            // background-blend-mode with mixed case
            var cs3 = Compute("#x { background-blend-mode: Multiply; }");
            Assert.That(cs3.Get("background-blend-mode"), Is.EqualTo("Multiply"),
                "Multiply (mixed case) is a valid blend-mode keyword and must not be dropped");
        }

        // ── 4. CSS-wide keywords are never treated as invalid ─────────────────

        [Test]
        public void Css_wide_initial_keyword_always_valid_on_validated_property() {
            // `initial` resolves to the property's initial value via KeywordResolver;
            // the validator must not drop it before KeywordResolver sees it.
            var cs = Compute("#x { animation-composition: add; animation-composition: initial; }");
            Assert.That(cs.Get("animation-composition"), Is.EqualTo("replace"),
                "'initial' must be accepted by the validator and resolved to initial value");
        }

        [Test]
        public void Css_wide_inherit_keyword_always_valid_on_validated_property() {
            // `inherit` must bypass the keyword validator and reach KeywordResolver.
            var doc = Html("<div><span id=\"child\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { animation-composition: accumulate; } #child { animation-composition: inherit; }")
            });
            var cs = engine.Compute(doc.GetElementById("child"));
            Assert.That(cs.Get("animation-composition"), Is.EqualTo("accumulate"),
                "'inherit' must bypass validator and pull parent computed value");
        }

        [Test]
        public void Css_wide_unset_keyword_always_valid_on_validated_property() {
            // `unset` resolves to initial for non-inherited animation-composition.
            var cs = Compute("#x { animation-composition: add; animation-composition: unset; }");
            Assert.That(cs.Get("animation-composition"), Is.EqualTo("replace"),
                "'unset' must bypass validator and resolve to initial for non-inherited property");
        }

        [Test]
        public void Css_wide_revert_keyword_always_valid_on_validated_property() {
            // `revert` (no lower-origin rule) collapses to initial.
            var cs = Compute("#x { animation-composition: add; animation-composition: revert; }");
            Assert.That(cs.Get("animation-composition"), Is.EqualTo("replace"),
                "'revert' must bypass validator and resolve to initial");
        }

        // ── 5. var()-containing values bypass validation ──────────────────────

        [Test]
        public void Var_reference_bypasses_keyword_validation() {
            // A value containing var() defers validation to computed-value time.
            // The validator must NOT drop it even if the raw text looks invalid.
            var cs = Compute("* { --comp: replace; } #x { animation-composition: var(--comp); }");
            // After var() substitution, the value becomes "replace" (valid).
            // The test verifies the validator doesn't reject `var(--comp)` before
            // substitution happens.
            var v = cs.Get("animation-composition");
            Assert.That(v, Is.EqualTo("replace"),
                "var() reference must bypass keyword validation; substituted value applies");
        }

        [Test]
        public void Var_in_blend_mode_bypasses_keyword_validation() {
            // var() in background-blend-mode must not be pre-dropped.
            var cs = Compute("* { --bm: multiply; } #x { background-blend-mode: var(--bm); }");
            var v = cs.Get("background-blend-mode");
            Assert.That(v, Is.EqualTo("multiply"),
                "var() in blend-mode must bypass validator; resolved value applies");
        }

        // ── 6. Custom properties are unaffected ───────────────────────────────

        [Test]
        public void Custom_property_with_invalid_keyword_as_value_passes_through() {
            // Custom properties accept any value — the validator must not apply.
            var cs = Compute("#x { --my-mode: totally-fake-blend-mode; }");
            Assert.That(cs.Get("--my-mode"), Is.EqualTo("totally-fake-blend-mode"),
                "custom property value must never be rejected by keyword validation");
        }

        [Test]
        public void Custom_property_with_random_string_passes_through() {
            var cs = Compute("#x { --foo: hello world bar baz; }");
            Assert.That(cs.Get("--foo"), Is.EqualTo("hello world bar baz"),
                "custom property arbitrary value must pass through unchanged");
        }

        // ── 7. Unvalidated properties keep pass-through ───────────────────────

        [Test]
        public void Unvalidated_property_unknown_keyword_passes_through() {
            // `display` has no keyword entry in the v1 validator — any value
            // it receives passes through without validation (control case).
            var cs = Compute("#x { display: something-totally-made-up; }");
            Assert.That(cs.Get("display"), Is.EqualTo("something-totally-made-up"),
                "unvalidated property must not have its value rejected");
        }

        [Test]
        public void Unvalidated_color_property_non_keyword_passes_through() {
            // `color` has no keyword entry; any authored value passes through.
            var cs = Compute("#x { color: definitely-not-a-color; }");
            Assert.That(cs.Get("color"), Is.EqualTo("definitely-not-a-color"),
                "unvalidated property (color) must not drop unknown values");
        }

        // ── 8. !important invalid declaration also drops ─────────────────────

        [Test]
        public void Important_invalid_declaration_is_dropped() {
            // !important does NOT rescue an invalid keyword from validation.
            // The declaration is still invalid and must be skipped.
            var cs = Compute("#x { animation-composition: add; } * { animation-composition: bad-keyword !important; }");
            // The !important invalid declaration is dropped; the valid #x rule wins.
            Assert.That(cs.Get("animation-composition"), Is.EqualTo("add"),
                "!important does not rescue an invalid keyword; invalid declaration must be dropped");
        }

        [Test]
        public void Important_invalid_blend_mode_dropped_falls_to_valid_normal_rule() {
            // A !important invalid blend-mode from a lower-spec rule is dropped;
            // the valid higher-spec rule's value applies.
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("* { background-blend-mode: fake-mode !important; } #x { background-blend-mode: screen; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("background-blend-mode"), Is.EqualTo("screen"),
                "!important invalid declaration is dropped; valid declaration wins");
        }

        // ── 9. Multi-value list validation ───────────────────────────────────

        [Test]
        public void Multi_value_list_with_one_invalid_token_drops_whole_declaration() {
            // animation-composition: replace, INVALID, accumulate — the list
            // contains an invalid token so the entire declaration is dropped.
            var cs = Compute("#x { animation-composition: replace, bad-keyword, accumulate; }");
            Assert.That(cs.Get("animation-composition"), Is.EqualTo("replace"),
                "list with any invalid token drops the whole declaration; initial applies");
        }

        [Test]
        public void Multi_value_list_all_valid_tokens_passes() {
            // animation-composition: replace, add, accumulate — all valid.
            var cs = Compute("#x { animation-composition: replace, add, accumulate; }");
            Assert.That(cs.Get("animation-composition"), Is.EqualTo("replace, add, accumulate"),
                "all-valid comma list must pass validation unchanged");
        }

        [Test]
        public void Multi_value_blend_mode_with_invalid_token_drops_declaration() {
            // background-blend-mode: multiply, bogus — the bogus token
            // invalidates the whole declaration.
            var cs = Compute("#x { background-blend-mode: multiply, bogus-mode; }");
            Assert.That(cs.Get("background-blend-mode"), Is.EqualTo("normal"),
                "blend-mode list with invalid token drops whole declaration; initial applies");
        }

        // ── 10. Validator unit tests ──────────────────────────────────────────

        // ── 10a. attr() substitution-marker bypass (fast-path guard) ──────────
        // These tests pin the combined ContainsSubstitutionMarker logic:
        //   • No '(' → skip both scans (fast path)
        //   • '(' present but neither var( nor attr( → NOT a marker
        //   • var( present → bypass
        //   • attr( present → bypass
        //   • Both present → bypass

        [Test]
        public void Validator_IsValidValue_returns_true_for_attr_containing_value() {
            // attr() bypass must work identically to var(): defers validation
            // to computed-value time so the resolved attribute value can be valid.
            int id = CssProperties.AnimationCompositionId;
            Assert.That(CssPropertyKeywordValidator.IsValidValue(id, "attr(data-mode)"), Is.True,
                "attr() reference must bypass keyword validation");
            Assert.That(CssPropertyKeywordValidator.IsValidValue(id, "ATTR(data-x)"), Is.True,
                "attr() bypass is case-insensitive");
        }

        [Test]
        public void Validator_no_paren_fast_path_does_not_bypass_validation() {
            // A value with no '(' cannot contain var() or attr() — the fast-path
            // guard must not accidentally bypass validation for normal keyword values.
            int id = CssProperties.AnimationCompositionId;
            // "bogus-keyword" has no paren → fast-path exits early → normal validation
            // proceeds → value is rejected as invalid.
            Assert.That(CssPropertyKeywordValidator.IsValidValue(id, "bogus-keyword"), Is.False,
                "value with no '(' must not be treated as a substitution bypass");
            // "replace" has no paren → fast-path → validation proceeds → valid.
            Assert.That(CssPropertyKeywordValidator.IsValidValue(id, "replace"), Is.True,
                "valid keyword with no paren must still pass validation");
        }

        [Test]
        public void Validator_paren_present_but_not_var_or_attr_does_not_bypass() {
            // A value containing '(' that is NOT var( or attr( (e.g. something
            // authored like "foo(bar)") must NOT bypass validation — only the two
            // specific CSS substitution functions trigger the deferral.
            int id = CssProperties.AnimationCompositionId;
            // "calc(1)" has a paren but is not var() or attr() → NOT a marker →
            // validation proceeds → "calc(1)" is not in the keyword set → invalid.
            Assert.That(CssPropertyKeywordValidator.IsValidValue(id, "calc(1)"), Is.False,
                "calc() should not trigger substitution bypass; invalid keyword is rejected");
        }

        [Test]
        public void Validator_IsValidValue_returns_true_for_css_wide_keywords() {
            int id = CssProperties.AnimationCompositionId;
            Assert.That(CssPropertyKeywordValidator.IsValidValue(id, "initial"), Is.True);
            Assert.That(CssPropertyKeywordValidator.IsValidValue(id, "inherit"), Is.True);
            Assert.That(CssPropertyKeywordValidator.IsValidValue(id, "unset"), Is.True);
            Assert.That(CssPropertyKeywordValidator.IsValidValue(id, "revert"), Is.True);
            Assert.That(CssPropertyKeywordValidator.IsValidValue(id, "revert-layer"), Is.True);
        }

        [Test]
        public void Validator_IsValidValue_returns_true_for_var_containing_value() {
            int id = CssProperties.AnimationCompositionId;
            Assert.That(CssPropertyKeywordValidator.IsValidValue(id, "var(--x)"), Is.True,
                "var() bypass must work");
            Assert.That(CssPropertyKeywordValidator.IsValidValue(id, "VAR(--y)"), Is.True,
                "var() bypass is case-insensitive");
        }

        [Test]
        public void Validator_IsValidValue_returns_true_for_negative_property_id() {
            // id -1 = custom/unknown property → always valid.
            Assert.That(CssPropertyKeywordValidator.IsValidValue(-1, "anything"), Is.True);
        }

        [Test]
        public void Validator_IsValidValue_returns_false_for_invalid_keyword() {
            int id = CssProperties.AnimationCompositionId;
            Assert.That(CssPropertyKeywordValidator.IsValidValue(id, "bogus-keyword"), Is.False);
            Assert.That(CssPropertyKeywordValidator.IsValidValue(id, ""), Is.True, "empty is always valid");
        }

        [Test]
        public void Validator_IsValidValue_accepts_valid_animation_composition_keywords() {
            int id = CssProperties.AnimationCompositionId;
            Assert.That(CssPropertyKeywordValidator.IsValidValue(id, "replace"), Is.True);
            Assert.That(CssPropertyKeywordValidator.IsValidValue(id, "add"), Is.True);
            Assert.That(CssPropertyKeywordValidator.IsValidValue(id, "accumulate"), Is.True);
            // Case-insensitive
            Assert.That(CssPropertyKeywordValidator.IsValidValue(id, "REPLACE"), Is.True);
            Assert.That(CssPropertyKeywordValidator.IsValidValue(id, "Add"), Is.True);
        }
    }
}
