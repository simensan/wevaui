using System.Linq;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Fonts L4 §6 — cascade coverage for font-variant-* longhands,
    // font-feature-settings, and font-size-adjust.
    //
    // REGISTRATION STATE (verified against CssProperties.BuildRegistry()):
    //   font-variant            registered, inherited=true,  initial="normal"   (shorthand)
    //   font-variant-numeric    registered, inherited=true,  initial="normal"
    //   font-feature-settings   registered, inherited=true,  initial="normal"
    //   font-size-adjust        registered, inherited=true,  initial="none"
    //   font-synthesis          registered, inherited=true,  initial="weight style small-caps"
    //   font-synthesis-weight   registered, inherited=true,  initial="auto"
    //   font-synthesis-style    registered, inherited=true,  initial="auto"
    //   font-synthesis-small-caps  registered, inherited=true, initial="auto"
    //   font-synthesis-position    registered, inherited=true, initial="auto"
    //
    //   font-variant-ligatures  NOT registered — spills to customProps;
    //                           does NOT inherit (bitmask path skips it).
    //                           Spec: CSS Fonts L4 §6.1, inherited=yes.
    //                           Tracked as CSS_OPEN_GAPS.md §A (see below).
    //   font-variant-position   NOT registered — same caveat.
    //   font-variant-caps       NOT registered — same caveat.
    //   font-variant-alternates NOT registered — same caveat.
    //   font-variant-east-asian NOT registered — same caveat.
    //   font-variant-emoji      NOT registered — same caveat.
    //
    // Tests for unregistered longhands are marked with [Ignore] where spec-
    // correct behaviour diverges from current engine behaviour (inheritance
    // gap). Current-behaviour round-trip tests are NOT ignored so the
    // harness still exercises the parser/customProps path.
    public class FontVariantFeatureSettingsSizeAdjustTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static Stylesheet ParseCss(string s) => CssParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(ParseCss(s));

        // Single-element convenience: <div id="x"></div>
        static ComputedStyle Compute(string css) {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("x"));
        }

        // Parent-child convenience: parent sets css; returns child's computed style.
        static ComputedStyle ComputeInherited(string parentCss, string childCss = "") {
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author($"#p {{ {parentCss} }} #c {{ {childCss} }}")
            });
            return engine.Compute(doc.GetElementById("c"));
        }

        // ══════════════════════════════════════════════════════════════════
        // font-variant-numeric (registered, inherited=true, initial="normal")
        // CSS Fonts L4 §6.7 / InheritanceFlagSweepTests pins the flag.
        // TextPropertyIntegrationTests has basic keyword checks; these tests
        // add multi-token, !important, CSS-wide keyword coverage.
        // ══════════════════════════════════════════════════════════════════

        [Test]
        public void FontVariantNumeric_initial_value_is_normal() {
            // §6.7: initial="normal". No rule set → should resolve to "normal".
            var cs = Compute("");
            Assert.That(cs.Get("font-variant-numeric"), Is.EqualTo("normal"));
        }

        [Test]
        public void FontVariantNumeric_tabular_nums_round_trips() {
            var cs = Compute("#x { font-variant-numeric: tabular-nums; }");
            Assert.That(cs.Get("font-variant-numeric"), Is.EqualTo("tabular-nums"));
        }

        [Test]
        public void FontVariantNumeric_proportional_nums_round_trips() {
            var cs = Compute("#x { font-variant-numeric: proportional-nums; }");
            Assert.That(cs.Get("font-variant-numeric"), Is.EqualTo("proportional-nums"));
        }

        [Test]
        public void FontVariantNumeric_lining_nums_round_trips() {
            var cs = Compute("#x { font-variant-numeric: lining-nums; }");
            Assert.That(cs.Get("font-variant-numeric"), Is.EqualTo("lining-nums"));
        }

        [Test]
        public void FontVariantNumeric_oldstyle_nums_round_trips() {
            var cs = Compute("#x { font-variant-numeric: oldstyle-nums; }");
            Assert.That(cs.Get("font-variant-numeric"), Is.EqualTo("oldstyle-nums"));
        }

        [Test]
        public void FontVariantNumeric_stacked_fractions_round_trips() {
            var cs = Compute("#x { font-variant-numeric: stacked-fractions; }");
            Assert.That(cs.Get("font-variant-numeric"), Is.EqualTo("stacked-fractions"));
        }

        [Test]
        public void FontVariantNumeric_diagonal_fractions_round_trips() {
            var cs = Compute("#x { font-variant-numeric: diagonal-fractions; }");
            Assert.That(cs.Get("font-variant-numeric"), Is.EqualTo("diagonal-fractions"));
        }

        [Test]
        public void FontVariantNumeric_ordinal_round_trips() {
            var cs = Compute("#x { font-variant-numeric: ordinal; }");
            Assert.That(cs.Get("font-variant-numeric"), Is.EqualTo("ordinal"));
        }

        [Test]
        public void FontVariantNumeric_slashed_zero_round_trips() {
            var cs = Compute("#x { font-variant-numeric: slashed-zero; }");
            Assert.That(cs.Get("font-variant-numeric"), Is.EqualTo("slashed-zero"));
        }

        [Test]
        public void FontVariantNumeric_normal_keyword_round_trips() {
            var cs = Compute("#x { font-variant-numeric: normal; }");
            Assert.That(cs.Get("font-variant-numeric"), Is.EqualTo("normal"));
        }

        [Test]
        public void FontVariantNumeric_inherits_from_parent() {
            // §6.7: inherited=yes — child without own rule gets parent's value.
            var child = ComputeInherited("font-variant-numeric: tabular-nums;");
            Assert.That(child.Get("font-variant-numeric"), Is.EqualTo("tabular-nums"));
        }

        [Test]
        public void FontVariantNumeric_child_overrides_parent() {
            // Child's own rule wins over inheritance.
            var child = ComputeInherited(
                "font-variant-numeric: tabular-nums;",
                "font-variant-numeric: normal;");
            Assert.That(child.Get("font-variant-numeric"), Is.EqualTo("normal"));
        }

        [Test]
        public void FontVariantNumeric_important_wins_over_later_non_important() {
            // !important cascade ordering.
            var cs = Compute("#x { font-variant-numeric: tabular-nums !important; font-variant-numeric: normal; }");
            Assert.That(cs.Get("font-variant-numeric"), Is.EqualTo("tabular-nums"));
        }

        [Test]
        public void FontVariantNumeric_initial_keyword_resets_to_normal() {
            // CSS Cascade L5 §7.1 — `initial` resolves to the property's spec
            // initial value regardless of parent.
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#p { font-variant-numeric: tabular-nums; } #c { font-variant-numeric: initial; }")
            });
            var child = engine.Compute(doc.GetElementById("c"));
            Assert.That(child.Get("font-variant-numeric"), Is.EqualTo("normal"),
                "`initial` must resolve to the spec initial `normal`, not inherit parent's `tabular-nums`");
        }

        [Test]
        public void FontVariantNumeric_inherit_keyword_pulls_parent_value() {
            // CSS Cascade L5 §7.2 — explicit `inherit`.
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#p { font-variant-numeric: tabular-nums; } #c { font-variant-numeric: inherit; }")
            });
            var child = engine.Compute(doc.GetElementById("c"));
            Assert.That(child.Get("font-variant-numeric"), Is.EqualTo("tabular-nums"));
        }

        [Test]
        public void FontVariantNumeric_unset_on_inherited_property_acts_as_inherit() {
            // CSS Cascade L5 §7.3 — `unset` on an inherited property = inherit.
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#p { font-variant-numeric: lining-nums; } #c { font-variant-numeric: unset; }")
            });
            var child = engine.Compute(doc.GetElementById("c"));
            Assert.That(child.Get("font-variant-numeric"), Is.EqualTo("lining-nums"));
        }

        // ══════════════════════════════════════════════════════════════════
        // font-feature-settings (registered, inherited=true, initial="normal")
        // CSS Fonts L4 §6.4
        // Note: TextPropertyIntegrationTests has stale comments saying this
        // property is "NOT REGISTERED" — that was v1-era; it IS registered now.
        // These tests exercise the full registered-property code path.
        // ══════════════════════════════════════════════════════════════════

        [Test]
        public void FontFeatureSettings_initial_value_is_normal() {
            // §6.4: initial="normal" (no feature overrides).
            var cs = Compute("");
            Assert.That(cs.Get("font-feature-settings"), Is.EqualTo("normal"));
        }

        [Test]
        public void FontFeatureSettings_normal_keyword_round_trips() {
            var cs = Compute("#x { font-feature-settings: normal; }");
            Assert.That(cs.Get("font-feature-settings"), Is.EqualTo("normal"));
        }

        [Test]
        public void FontFeatureSettings_single_tag_with_on_value() {
            // `"liga" 1` — enable ligatures.
            var cs = Compute("#x { font-feature-settings: \"liga\" 1; }");
            var v = cs.Get("font-feature-settings");
            Assert.That(v, Is.Not.Null);
            Assert.That(v, Does.Contain("liga"));
        }

        [Test]
        public void FontFeatureSettings_single_tag_with_off_value() {
            // `"kern" 0` — disable kerning.
            var cs = Compute("#x { font-feature-settings: \"kern\" 0; }");
            var v = cs.Get("font-feature-settings");
            Assert.That(v, Is.Not.Null);
            Assert.That(v, Does.Contain("kern"));
        }

        [Test]
        public void FontFeatureSettings_tag_alone_defaults_to_on() {
            // §6.4: a bare tag `"smcp"` is shorthand for `"smcp" 1`.
            // The cascade must preserve enough information that the tag is reachable.
            var cs = Compute("#x { font-feature-settings: \"smcp\"; }");
            var v = cs.Get("font-feature-settings");
            Assert.That(v, Is.Not.Null);
            Assert.That(v, Does.Contain("smcp"));
        }

        [Test]
        public void FontFeatureSettings_multi_tag_comma_list_round_trips() {
            // Comma-separated list of tags. Both must survive the cascade.
            var cs = Compute("#x { font-feature-settings: \"liga\" 1, \"kern\" 0; }");
            var v = cs.Get("font-feature-settings");
            Assert.That(v, Is.Not.Null);
            Assert.That(v, Does.Contain("liga"));
            Assert.That(v, Does.Contain("kern"));
        }

        [Test]
        public void FontFeatureSettings_three_tag_list_round_trips() {
            var cs = Compute("#x { font-feature-settings: \"liga\" 1, \"smcp\" 1, \"kern\" 0; }");
            var v = cs.Get("font-feature-settings");
            Assert.That(v, Is.Not.Null);
            Assert.That(v, Does.Contain("liga"));
            Assert.That(v, Does.Contain("smcp"));
            Assert.That(v, Does.Contain("kern"));
        }

        [Test]
        public void FontFeatureSettings_inherits_from_parent() {
            // §6.4: inherited=yes.
            var child = ComputeInherited("font-feature-settings: \"smcp\" 1;");
            var v = child.Get("font-feature-settings");
            Assert.That(v, Is.Not.Null);
            Assert.That(v, Does.Contain("smcp"));
        }

        [Test]
        public void FontFeatureSettings_child_overrides_parent() {
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#p { font-feature-settings: \"smcp\" 1; } #c { font-feature-settings: normal; }")
            });
            var child = engine.Compute(doc.GetElementById("c"));
            Assert.That(child.Get("font-feature-settings"), Is.EqualTo("normal"));
        }

        [Test]
        public void FontFeatureSettings_important_wins_cascade() {
            var cs = Compute("#x { font-feature-settings: \"liga\" 1 !important; font-feature-settings: normal; }");
            var v = cs.Get("font-feature-settings");
            Assert.That(v, Is.Not.Null);
            Assert.That(v, Does.Contain("liga"));
        }

        [Test]
        public void FontFeatureSettings_initial_keyword_resets_to_normal() {
            // CSS Cascade L5 §7.1.
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#p { font-feature-settings: \"liga\" 1; } #c { font-feature-settings: initial; }")
            });
            var child = engine.Compute(doc.GetElementById("c"));
            Assert.That(child.Get("font-feature-settings"), Is.EqualTo("normal"));
        }

        [Test]
        public void FontFeatureSettings_inherit_keyword_pulls_parent_value() {
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#p { font-feature-settings: \"liga\" 1; } #c { font-feature-settings: inherit; }")
            });
            var child = engine.Compute(doc.GetElementById("c"));
            var v = child.Get("font-feature-settings");
            Assert.That(v, Is.Not.Null);
            Assert.That(v, Does.Contain("liga"));
        }

        [Test]
        public void FontFeatureSettings_unset_on_inherited_property_acts_as_inherit() {
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#p { font-feature-settings: \"smcp\" 1; } #c { font-feature-settings: unset; }")
            });
            var child = engine.Compute(doc.GetElementById("c"));
            var v = child.Get("font-feature-settings");
            Assert.That(v, Is.Not.Null);
            Assert.That(v, Does.Contain("smcp"));
        }

        // ══════════════════════════════════════════════════════════════════
        // font-size-adjust (registered, inherited=true, initial="none")
        // CSS Fonts L4 §3.4
        // ══════════════════════════════════════════════════════════════════

        [Test]
        public void FontSizeAdjust_initial_value_is_none() {
            // §3.4: initial="none" (no metric-adaptive sizing).
            var cs = Compute("");
            Assert.That(cs.Get("font-size-adjust"), Is.EqualTo("none"));
        }

        [Test]
        public void FontSizeAdjust_none_keyword_round_trips() {
            var cs = Compute("#x { font-size-adjust: none; }");
            Assert.That(cs.Get("font-size-adjust"), Is.EqualTo("none"));
        }

        [Test]
        public void FontSizeAdjust_number_value_round_trips() {
            // §3.4: <number> specifies the target ex-height-to-font-size ratio.
            var cs = Compute("#x { font-size-adjust: 0.5; }");
            Assert.That(cs.Get("font-size-adjust"), Is.EqualTo("0.5"));
        }

        [Test]
        public void FontSizeAdjust_from_font_keyword_round_trips() {
            // §3.4: `from-font` uses the first available font's metric.
            var cs = Compute("#x { font-size-adjust: from-font; }");
            Assert.That(cs.Get("font-size-adjust"), Is.EqualTo("from-font"));
        }

        [Test]
        public void FontSizeAdjust_ex_height_from_font_round_trips() {
            // §3.4: two-token form <metric-key> from-font.
            var cs = Compute("#x { font-size-adjust: ex-height from-font; }");
            var v = cs.Get("font-size-adjust");
            Assert.That(v, Is.Not.Null);
            Assert.That(v, Does.Contain("ex-height"));
        }

        [Test]
        public void FontSizeAdjust_cap_height_number_round_trips() {
            // §3.4: two-token form <metric-key> <number>.
            var cs = Compute("#x { font-size-adjust: cap-height 0.7; }");
            var v = cs.Get("font-size-adjust");
            Assert.That(v, Is.Not.Null);
            Assert.That(v, Does.Contain("cap-height"));
        }

        [Test]
        public void FontSizeAdjust_ch_width_metric_round_trips() {
            var cs = Compute("#x { font-size-adjust: ch-width 0.4; }");
            var v = cs.Get("font-size-adjust");
            Assert.That(v, Is.Not.Null);
            Assert.That(v, Does.Contain("ch-width"));
        }

        [Test]
        public void FontSizeAdjust_inherits_from_parent() {
            // §3.4: inherited=yes.
            var child = ComputeInherited("font-size-adjust: 0.5;");
            Assert.That(child.Get("font-size-adjust"), Is.EqualTo("0.5"));
        }

        [Test]
        public void FontSizeAdjust_child_overrides_parent() {
            var child = ComputeInherited(
                "font-size-adjust: 0.5;",
                "font-size-adjust: 0.3;");
            Assert.That(child.Get("font-size-adjust"), Is.EqualTo("0.3"));
        }

        [Test]
        public void FontSizeAdjust_important_wins_cascade() {
            var cs = Compute("#x { font-size-adjust: 0.5 !important; font-size-adjust: none; }");
            Assert.That(cs.Get("font-size-adjust"), Is.EqualTo("0.5"));
        }

        [Test]
        public void FontSizeAdjust_initial_keyword_resets_to_none() {
            // CSS Cascade L5 §7.1.
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#p { font-size-adjust: 0.5; } #c { font-size-adjust: initial; }")
            });
            var child = engine.Compute(doc.GetElementById("c"));
            Assert.That(child.Get("font-size-adjust"), Is.EqualTo("none"));
        }

        [Test]
        public void FontSizeAdjust_inherit_keyword_pulls_parent_value() {
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#p { font-size-adjust: 0.5; } #c { font-size-adjust: inherit; }")
            });
            var child = engine.Compute(doc.GetElementById("c"));
            Assert.That(child.Get("font-size-adjust"), Is.EqualTo("0.5"));
        }

        [Test]
        public void FontSizeAdjust_unset_on_inherited_property_acts_as_inherit() {
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#p { font-size-adjust: 0.5; } #c { font-size-adjust: unset; }")
            });
            var child = engine.Compute(doc.GetElementById("c"));
            Assert.That(child.Get("font-size-adjust"), Is.EqualTo("0.5"));
        }

        // ══════════════════════════════════════════════════════════════════
        // font-variant shorthand (registered, inherited=true, initial="normal")
        // CSS Fonts L4 §6 — only `normal` and `small-caps` are valid for
        // the shorthand (others require longhands per spec).
        // ══════════════════════════════════════════════════════════════════

        [Test]
        public void FontVariant_initial_value_is_normal() {
            var cs = Compute("");
            Assert.That(cs.Get("font-variant"), Is.EqualTo("normal"));
        }

        [Test]
        public void FontVariant_normal_round_trips() {
            var cs = Compute("#x { font-variant: normal; }");
            Assert.That(cs.Get("font-variant"), Is.EqualTo("normal"));
        }

        [Test]
        public void FontVariant_small_caps_round_trips() {
            var cs = Compute("#x { font-variant: small-caps; }");
            Assert.That(cs.Get("font-variant"), Is.EqualTo("small-caps"));
        }

        [Test]
        public void FontVariant_inherits_from_parent() {
            var child = ComputeInherited("font-variant: small-caps;");
            Assert.That(child.Get("font-variant"), Is.EqualTo("small-caps"));
        }

        [Test]
        public void FontVariant_important_wins_cascade() {
            var cs = Compute("#x { font-variant: small-caps !important; font-variant: normal; }");
            Assert.That(cs.Get("font-variant"), Is.EqualTo("small-caps"));
        }

        [Test]
        public void FontVariant_initial_keyword_resets_to_normal() {
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#p { font-variant: small-caps; } #c { font-variant: initial; }")
            });
            var child = engine.Compute(doc.GetElementById("c"));
            Assert.That(child.Get("font-variant"), Is.EqualTo("normal"));
        }

        [Test]
        public void FontVariant_inherit_keyword_pulls_parent_value() {
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#p { font-variant: small-caps; } #c { font-variant: inherit; }")
            });
            var child = engine.Compute(doc.GetElementById("c"));
            Assert.That(child.Get("font-variant"), Is.EqualTo("small-caps"));
        }

        [Test]
        public void FontVariant_unset_acts_as_inherit_on_inherited_property() {
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#p { font-variant: small-caps; } #c { font-variant: unset; }")
            });
            var child = engine.Compute(doc.GetElementById("c"));
            Assert.That(child.Get("font-variant"), Is.EqualTo("small-caps"));
        }

        // ══════════════════════════════════════════════════════════════════
        // font-variant-ligatures (NOT REGISTERED — round-trip via customProps)
        // CSS Fonts L4 §6.1 — inherited=yes (spec), but the engine does NOT
        // propagate customProps values through the inheritance bitmask path.
        // Spec-correct inheritance test is [Ignore]d — see CSS_OPEN_GAPS.md §A.
        // ══════════════════════════════════════════════════════════════════

        [Test]
        public void FontVariantLigatures_keyword_round_trips_via_customProps() {
            // CURRENT-BEHAVIOUR: unregistered property spills to customProps.
            // The author value is preserved for round-trip even though the
            // engine logs an "unknown property" warning.
            var cs = Compute("#x { font-variant-ligatures: common-ligatures; }");
            var v = cs.Get("font-variant-ligatures");
            Assert.That(v, Is.Not.Null, "unregistered font-variant-ligatures should spill to customProps and be readable");
            Assert.That(v, Does.Contain("common-ligatures"));
        }

        [Test]
        public void FontVariantLigatures_none_keyword_round_trips() {
            var cs = Compute("#x { font-variant-ligatures: none; }");
            Assert.That(cs.Get("font-variant-ligatures"), Is.EqualTo("none"));
        }

        [Test]
        public void FontVariantLigatures_no_common_ligatures_round_trips() {
            var cs = Compute("#x { font-variant-ligatures: no-common-ligatures; }");
            var v = cs.Get("font-variant-ligatures");
            Assert.That(v, Is.Not.Null);
            Assert.That(v, Does.Contain("no-common-ligatures"));
        }

        [Test]
        public void FontVariantLigatures_normal_keyword_round_trips() {
            var cs = Compute("#x { font-variant-ligatures: normal; }");
            Assert.That(cs.Get("font-variant-ligatures"), Is.EqualTo("normal"));
        }

        [Test]
        public void FontVariantLigatures_inherits_from_parent_SPEC() {
            // CSS Fonts L4 §6.1: inherited=yes. Gap A12 closed 2026-05-30.
            var child = ComputeInherited("font-variant-ligatures: common-ligatures;");
            Assert.That(child.Get("font-variant-ligatures"), Is.EqualTo("common-ligatures"));
        }

        // ══════════════════════════════════════════════════════════════════
        // font-variant-position (NOT REGISTERED)
        // CSS Fonts L4 §6.2 — normal | sub | super; inherited=yes
        // ══════════════════════════════════════════════════════════════════

        [Test]
        public void FontVariantPosition_sub_round_trips_via_customProps() {
            var cs = Compute("#x { font-variant-position: sub; }");
            Assert.That(cs.Get("font-variant-position"), Is.EqualTo("sub"));
        }

        [Test]
        public void FontVariantPosition_super_round_trips_via_customProps() {
            var cs = Compute("#x { font-variant-position: super; }");
            Assert.That(cs.Get("font-variant-position"), Is.EqualTo("super"));
        }

        [Test]
        public void FontVariantPosition_normal_round_trips_via_customProps() {
            var cs = Compute("#x { font-variant-position: normal; }");
            Assert.That(cs.Get("font-variant-position"), Is.EqualTo("normal"));
        }

        [Test]
        public void FontVariantPosition_inherits_from_parent_SPEC() {
            // CSS Fonts L4 §6.2: inherited=yes. Gap A12 closed 2026-05-30.
            var child = ComputeInherited("font-variant-position: sub;");
            Assert.That(child.Get("font-variant-position"), Is.EqualTo("sub"));
        }

        // ══════════════════════════════════════════════════════════════════
        // font-variant-caps (NOT REGISTERED)
        // CSS Fonts L4 §6.3 — normal | small-caps | all-small-caps |
        //   petite-caps | all-petite-caps | unicase | titling-caps; inherited=yes
        // ══════════════════════════════════════════════════════════════════

        [Test]
        public void FontVariantCaps_small_caps_round_trips_via_customProps() {
            var cs = Compute("#x { font-variant-caps: small-caps; }");
            Assert.That(cs.Get("font-variant-caps"), Is.EqualTo("small-caps"));
        }

        [Test]
        public void FontVariantCaps_all_small_caps_round_trips() {
            var cs = Compute("#x { font-variant-caps: all-small-caps; }");
            Assert.That(cs.Get("font-variant-caps"), Is.EqualTo("all-small-caps"));
        }

        [Test]
        public void FontVariantCaps_petite_caps_round_trips() {
            var cs = Compute("#x { font-variant-caps: petite-caps; }");
            Assert.That(cs.Get("font-variant-caps"), Is.EqualTo("petite-caps"));
        }

        [Test]
        public void FontVariantCaps_all_petite_caps_round_trips() {
            var cs = Compute("#x { font-variant-caps: all-petite-caps; }");
            Assert.That(cs.Get("font-variant-caps"), Is.EqualTo("all-petite-caps"));
        }

        [Test]
        public void FontVariantCaps_unicase_round_trips() {
            var cs = Compute("#x { font-variant-caps: unicase; }");
            Assert.That(cs.Get("font-variant-caps"), Is.EqualTo("unicase"));
        }

        [Test]
        public void FontVariantCaps_titling_caps_round_trips() {
            var cs = Compute("#x { font-variant-caps: titling-caps; }");
            Assert.That(cs.Get("font-variant-caps"), Is.EqualTo("titling-caps"));
        }

        [Test]
        public void FontVariantCaps_normal_round_trips() {
            var cs = Compute("#x { font-variant-caps: normal; }");
            Assert.That(cs.Get("font-variant-caps"), Is.EqualTo("normal"));
        }

        [Test]
        public void FontVariantCaps_inherits_from_parent_SPEC() {
            // CSS Fonts L4 §6.3: inherited=yes. Gap A12 closed 2026-05-30.
            var child = ComputeInherited("font-variant-caps: small-caps;");
            Assert.That(child.Get("font-variant-caps"), Is.EqualTo("small-caps"));
        }

        // ══════════════════════════════════════════════════════════════════
        // font-variant-alternates (NOT REGISTERED)
        // CSS Fonts L4 §6.5 — normal | historical-forms | ...; inherited=yes
        // (Function values like stylistic() omitted: parser doesn't handle
        //  at-rule context for @font-feature-values; keyword forms tested here)
        // ══════════════════════════════════════════════════════════════════

        [Test]
        public void FontVariantAlternates_normal_round_trips() {
            var cs = Compute("#x { font-variant-alternates: normal; }");
            Assert.That(cs.Get("font-variant-alternates"), Is.EqualTo("normal"));
        }

        [Test]
        public void FontVariantAlternates_historical_forms_round_trips() {
            var cs = Compute("#x { font-variant-alternates: historical-forms; }");
            Assert.That(cs.Get("font-variant-alternates"), Is.EqualTo("historical-forms"));
        }

        [Test]
        public void FontVariantAlternates_inherits_from_parent_SPEC() {
            // CSS Fonts L4 §6.5: inherited=yes. Gap A12 closed 2026-05-30.
            var child = ComputeInherited("font-variant-alternates: historical-forms;");
            Assert.That(child.Get("font-variant-alternates"), Is.EqualTo("historical-forms"));
        }

        // ══════════════════════════════════════════════════════════════════
        // font-variant-east-asian (NOT REGISTERED)
        // CSS Fonts L4 §6.6 — normal | ruby | <variant-values> | <width-values>; inherited=yes
        // ══════════════════════════════════════════════════════════════════

        [Test]
        public void FontVariantEastAsian_normal_round_trips() {
            var cs = Compute("#x { font-variant-east-asian: normal; }");
            Assert.That(cs.Get("font-variant-east-asian"), Is.EqualTo("normal"));
        }

        [Test]
        public void FontVariantEastAsian_ruby_round_trips() {
            var cs = Compute("#x { font-variant-east-asian: ruby; }");
            Assert.That(cs.Get("font-variant-east-asian"), Is.EqualTo("ruby"));
        }

        [Test]
        public void FontVariantEastAsian_jis78_round_trips() {
            var cs = Compute("#x { font-variant-east-asian: jis78; }");
            Assert.That(cs.Get("font-variant-east-asian"), Is.EqualTo("jis78"));
        }

        [Test]
        public void FontVariantEastAsian_proportional_width_round_trips() {
            var cs = Compute("#x { font-variant-east-asian: proportional-width; }");
            Assert.That(cs.Get("font-variant-east-asian"), Is.EqualTo("proportional-width"));
        }

        [Test]
        public void FontVariantEastAsian_inherits_from_parent_SPEC() {
            // CSS Fonts L4 §6.6: inherited=yes. Gap A12 closed 2026-05-30.
            var child = ComputeInherited("font-variant-east-asian: ruby;");
            Assert.That(child.Get("font-variant-east-asian"), Is.EqualTo("ruby"));
        }

        // ══════════════════════════════════════════════════════════════════
        // font-variant-emoji (NOT REGISTERED)
        // CSS Fonts L4 §6.12 — normal | text | emoji | unicode; inherited=yes
        // ══════════════════════════════════════════════════════════════════

        [Test]
        public void FontVariantEmoji_normal_round_trips() {
            var cs = Compute("#x { font-variant-emoji: normal; }");
            Assert.That(cs.Get("font-variant-emoji"), Is.EqualTo("normal"));
        }

        [Test]
        public void FontVariantEmoji_text_round_trips() {
            var cs = Compute("#x { font-variant-emoji: text; }");
            Assert.That(cs.Get("font-variant-emoji"), Is.EqualTo("text"));
        }

        [Test]
        public void FontVariantEmoji_emoji_round_trips() {
            var cs = Compute("#x { font-variant-emoji: emoji; }");
            Assert.That(cs.Get("font-variant-emoji"), Is.EqualTo("emoji"));
        }

        [Test]
        public void FontVariantEmoji_unicode_round_trips() {
            var cs = Compute("#x { font-variant-emoji: unicode; }");
            Assert.That(cs.Get("font-variant-emoji"), Is.EqualTo("unicode"));
        }

        [Test]
        public void FontVariantEmoji_inherits_from_parent_SPEC() {
            // CSS Fonts L4 §6.12: inherited=yes. Gap A12 closed 2026-05-30.
            var child = ComputeInherited("font-variant-emoji: emoji;");
            Assert.That(child.Get("font-variant-emoji"), Is.EqualTo("emoji"));
        }

        // ══════════════════════════════════════════════════════════════════
        // font-synthesis and longhands (registered, inherited=true)
        // CSS Fonts L4 §6.5
        // ══════════════════════════════════════════════════════════════════

        [Test]
        public void FontSynthesis_initial_value_contains_all_three() {
            // §6.5: initial="weight style small-caps" (all synthesis enabled).
            var cs = Compute("");
            var v = cs.Get("font-synthesis");
            Assert.That(v, Is.Not.Null);
            Assert.That(v, Does.Contain("weight"));
            Assert.That(v, Does.Contain("style"));
            Assert.That(v, Does.Contain("small-caps"));
        }

        [Test]
        public void FontSynthesis_none_round_trips() {
            var cs = Compute("#x { font-synthesis: none; }");
            Assert.That(cs.Get("font-synthesis"), Is.EqualTo("none"));
        }

        [Test]
        public void FontSynthesis_weight_only_round_trips() {
            var cs = Compute("#x { font-synthesis: weight; }");
            Assert.That(cs.Get("font-synthesis"), Is.EqualTo("weight"));
        }

        [Test]
        public void FontSynthesisWeight_auto_round_trips() {
            var cs = Compute("#x { font-synthesis-weight: auto; }");
            Assert.That(cs.Get("font-synthesis-weight"), Is.EqualTo("auto"));
        }

        [Test]
        public void FontSynthesisWeight_none_round_trips() {
            var cs = Compute("#x { font-synthesis-weight: none; }");
            Assert.That(cs.Get("font-synthesis-weight"), Is.EqualTo("none"));
        }

        [Test]
        public void FontSynthesisStyle_auto_round_trips() {
            var cs = Compute("#x { font-synthesis-style: auto; }");
            Assert.That(cs.Get("font-synthesis-style"), Is.EqualTo("auto"));
        }

        [Test]
        public void FontSynthesisStyle_none_round_trips() {
            var cs = Compute("#x { font-synthesis-style: none; }");
            Assert.That(cs.Get("font-synthesis-style"), Is.EqualTo("none"));
        }

        [Test]
        public void FontSynthesisSmallCaps_auto_round_trips() {
            var cs = Compute("#x { font-synthesis-small-caps: auto; }");
            Assert.That(cs.Get("font-synthesis-small-caps"), Is.EqualTo("auto"));
        }

        [Test]
        public void FontSynthesisSmallCaps_none_round_trips() {
            var cs = Compute("#x { font-synthesis-small-caps: none; }");
            Assert.That(cs.Get("font-synthesis-small-caps"), Is.EqualTo("none"));
        }

        [Test]
        public void FontSynthesisWeight_inherits_from_parent() {
            var child = ComputeInherited("font-synthesis-weight: none;");
            Assert.That(child.Get("font-synthesis-weight"), Is.EqualTo("none"));
        }

        [Test]
        public void FontSynthesisWeight_initial_keyword_resets_to_auto() {
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#p { font-synthesis-weight: none; } #c { font-synthesis-weight: initial; }")
            });
            var child = engine.Compute(doc.GetElementById("c"));
            Assert.That(child.Get("font-synthesis-weight"), Is.EqualTo("auto"));
        }
    }
}
