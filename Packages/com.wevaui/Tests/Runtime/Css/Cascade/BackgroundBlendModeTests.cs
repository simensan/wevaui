using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Compositing and Blending Level 1 §6.3 — `background-blend-mode` cascade
    // coverage.
    //
    // REGISTRATION STATE (as of 2026-05-30):
    //   `background-blend-mode` IS registered in CssProperties.cs (gap A11 closed).
    //   CssProperties.GetId("background-blend-mode") returns a valid id >= 0.
    //   ComputedStyle.Get("background-blend-mode") returns "normal" (initial) when
    //   no rule is authored, and the authored value when a rule is present.
    //
    // Spec: CSS Compositing and Blending L1 §6.3
    //   <https://www.w3.org/TR/compositing/#background-blend-mode>
    //   Property:   background-blend-mode
    //   Initial:    normal
    //   Applies to: all elements
    //   Inherited:  NO
    //   Animatable: discrete
    //   Value:      <blend-mode>#
    //     <blend-mode> = normal | multiply | screen | overlay | darken |
    //                    lighten | color-dodge | color-burn | hard-light |
    //                    soft-light | difference | exclusion | hue |
    //                    saturation | color | luminosity | plus-darker |
    //                    plus-lighter
    public class BackgroundBlendModeTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static Stylesheet Css(string s) => CssParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(Css(s));

        static ComputedStyle Compute(string css) {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("x"));
        }

        static ComputedStyle ComputeChild(string css) {
            var doc = Html("<div><span id=\"child\"></span></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            engine.Compute(doc.GetElementById("div"));
            return engine.Compute(doc.GetElementById("child"));
        }

        // ══════════════════════════════════════════════════════════════════
        // Registration state — verify the property is correctly registered.
        // ══════════════════════════════════════════════════════════════════

        [Test]
        public void Background_blend_mode_is_registered_in_CssProperties() {
            // Gap A11 closed 2026-05-30: background-blend-mode is now registered.
            int id = CssProperties.GetId("background-blend-mode");
            Assert.That(id, Is.GreaterThanOrEqualTo(0),
                "background-blend-mode must be registered with a valid non-negative id");
        }

        [Test]
        public void Background_blend_mode_authored_value_round_trips() {
            // Authored value routes through the typed cascade slot (not customProps).
            var cs = Compute("#x { background-blend-mode: multiply; }");
            Assert.That(cs.Get("background-blend-mode"), Is.EqualTo("multiply"),
                "authored background-blend-mode must survive cascade round-trip");
        }

        [Test]
        public void Background_blend_mode_absent_declaration_returns_initial_normal() {
            // With registration in place, FillInitials inserts the spec initial
            // value "normal" for any element with no authored declaration.
            var cs = Compute("");
            Assert.That(cs.Get("background-blend-mode"), Is.EqualTo("normal"),
                "registered property with no authored rule returns the initial value 'normal'");
        }

        [Test]
        public void Mix_blend_mode_is_not_disturbed_by_background_blend_mode_authoring() {
            // Writing background-blend-mode must not accidentally write through
            // to mix-blend-mode (they share the word "blend-mode" but are
            // distinct properties with separate registrations).
            var cs = Compute("#x { background-blend-mode: multiply; mix-blend-mode: screen; }");
            Assert.That(cs.Get("mix-blend-mode"), Is.EqualTo("screen"),
                "mix-blend-mode must retain its own authored value");
        }

        // ══════════════════════════════════════════════════════════════════
        // Spec-correct behaviour — gap A11 closed 2026-05-30.
        // ══════════════════════════════════════════════════════════════════

        // ── Initial value ─────────────────────────────────────────────────

        [Test]
        public void Background_blend_mode_initial_is_normal() {
            // CSS Compositing L1 §6.3: initial value is `normal`.
            // With no rule, the computed value must equal "normal".
            var cs = Compute("");
            Assert.That(cs.Get("background-blend-mode"), Is.EqualTo("normal"));
        }

        // ── Single-keyword round-trips (each <blend-mode> value) ─────────

        [Test]
        public void Background_blend_mode_normal_round_trips() {
            var cs = Compute("#x { background-blend-mode: normal; }");
            Assert.That(cs.Get("background-blend-mode"), Is.EqualTo("normal"));
        }

        [Test]
        public void Background_blend_mode_multiply_round_trips() {
            var cs = Compute("#x { background-blend-mode: multiply; }");
            Assert.That(cs.Get("background-blend-mode"), Is.EqualTo("multiply"));
        }

        [Test]
        public void Background_blend_mode_screen_round_trips() {
            var cs = Compute("#x { background-blend-mode: screen; }");
            Assert.That(cs.Get("background-blend-mode"), Is.EqualTo("screen"));
        }

        [Test]
        public void Background_blend_mode_overlay_round_trips() {
            var cs = Compute("#x { background-blend-mode: overlay; }");
            Assert.That(cs.Get("background-blend-mode"), Is.EqualTo("overlay"));
        }

        [Test]
        public void Background_blend_mode_darken_round_trips() {
            var cs = Compute("#x { background-blend-mode: darken; }");
            Assert.That(cs.Get("background-blend-mode"), Is.EqualTo("darken"));
        }

        [Test]
        public void Background_blend_mode_lighten_round_trips() {
            var cs = Compute("#x { background-blend-mode: lighten; }");
            Assert.That(cs.Get("background-blend-mode"), Is.EqualTo("lighten"));
        }

        [Test]
        public void Background_blend_mode_color_dodge_round_trips() {
            var cs = Compute("#x { background-blend-mode: color-dodge; }");
            Assert.That(cs.Get("background-blend-mode"), Is.EqualTo("color-dodge"));
        }

        [Test]
        public void Background_blend_mode_color_burn_round_trips() {
            var cs = Compute("#x { background-blend-mode: color-burn; }");
            Assert.That(cs.Get("background-blend-mode"), Is.EqualTo("color-burn"));
        }

        [Test]
        public void Background_blend_mode_hard_light_round_trips() {
            var cs = Compute("#x { background-blend-mode: hard-light; }");
            Assert.That(cs.Get("background-blend-mode"), Is.EqualTo("hard-light"));
        }

        [Test]
        public void Background_blend_mode_soft_light_round_trips() {
            var cs = Compute("#x { background-blend-mode: soft-light; }");
            Assert.That(cs.Get("background-blend-mode"), Is.EqualTo("soft-light"));
        }

        [Test]
        public void Background_blend_mode_difference_round_trips() {
            var cs = Compute("#x { background-blend-mode: difference; }");
            Assert.That(cs.Get("background-blend-mode"), Is.EqualTo("difference"));
        }

        [Test]
        public void Background_blend_mode_exclusion_round_trips() {
            var cs = Compute("#x { background-blend-mode: exclusion; }");
            Assert.That(cs.Get("background-blend-mode"), Is.EqualTo("exclusion"));
        }

        [Test]
        public void Background_blend_mode_hue_round_trips() {
            // CSS Compositing L1 §6.3 separable-mode: hue applies the
            // hue of the source to the luminosity + chroma of the backdrop.
            var cs = Compute("#x { background-blend-mode: hue; }");
            Assert.That(cs.Get("background-blend-mode"), Is.EqualTo("hue"));
        }

        [Test]
        public void Background_blend_mode_saturation_round_trips() {
            var cs = Compute("#x { background-blend-mode: saturation; }");
            Assert.That(cs.Get("background-blend-mode"), Is.EqualTo("saturation"));
        }

        [Test]
        public void Background_blend_mode_color_round_trips() {
            // `color` is a valid <blend-mode> keyword despite sharing the name
            // with a CSS type. The parser must not confuse them.
            var cs = Compute("#x { background-blend-mode: color; }");
            Assert.That(cs.Get("background-blend-mode"), Is.EqualTo("color"));
        }

        [Test]
        public void Background_blend_mode_luminosity_round_trips() {
            var cs = Compute("#x { background-blend-mode: luminosity; }");
            Assert.That(cs.Get("background-blend-mode"), Is.EqualTo("luminosity"));
        }

        [Test]
        public void Background_blend_mode_plus_darker_round_trips() {
            // `plus-darker` is not in CSS Compositing L1 official list but is
            // a widely-supported extension; the engine must carry it verbatim.
            var cs = Compute("#x { background-blend-mode: plus-darker; }");
            Assert.That(cs.Get("background-blend-mode"), Is.EqualTo("plus-darker"));
        }

        [Test]
        public void Background_blend_mode_plus_lighter_round_trips() {
            var cs = Compute("#x { background-blend-mode: plus-lighter; }");
            Assert.That(cs.Get("background-blend-mode"), Is.EqualTo("plus-lighter"));
        }

        // ── Multi-layer comma list ─────────────────────────────────────────

        [Test]
        public void Background_blend_mode_two_layer_list_round_trips() {
            // CSS Compositing L1 §6.3: `background-blend-mode` accepts a
            // comma-separated list — one value per background layer, matching
            // the order of `background-image` layers.
            var cs = Compute("#x { background-blend-mode: multiply, screen; }");
            var v = cs.Get("background-blend-mode");
            Assert.That(v, Does.Contain("multiply"),
                "first layer blend mode must be present");
            Assert.That(v, Does.Contain("screen"),
                "second layer blend mode must be present");
        }

        [Test]
        public void Background_blend_mode_three_layer_list_round_trips() {
            // Three layers: normal (layer 3, bottommost) + overlay + multiply.
            var cs = Compute("#x { background-blend-mode: multiply, overlay, normal; }");
            var v = cs.Get("background-blend-mode");
            Assert.That(v, Does.Contain("multiply"));
            Assert.That(v, Does.Contain("overlay"));
            Assert.That(v, Does.Contain("normal"));
        }

        [Test]
        public void Background_blend_mode_single_value_applies_to_all_layers() {
            // Spec §6.3: if there are fewer values than layers the list is
            // repeated — a single `multiply` applies to every layer.
            // The cascade must carry the authored single value, not expand it.
            var cs = Compute("#x { background-blend-mode: multiply; }");
            Assert.That(cs.Get("background-blend-mode"), Is.EqualTo("multiply"));
        }

        // ── Inheritance flag (must NOT inherit) ────────────────────────────

        [Test]
        public void Background_blend_mode_does_not_inherit() {
            // CSS Compositing L1 §6.3: Inherited: NO.
            // A child without its own rule sees the initial value `normal`,
            // not the parent's authored blend mode.
            var cs = ComputeChild("div { background-blend-mode: multiply; }");
            Assert.That(cs.Get("background-blend-mode"), Is.EqualTo("normal"),
                "background-blend-mode is non-inherited; child must see initial 'normal'");
        }

        // ── CSS-wide keywords ──────────────────────────────────────────────

        [Test]
        public void Background_blend_mode_initial_keyword_resolves_to_normal() {
            // CSS Cascade L5 §7.1: `initial` resolves to the property's
            // spec initial value — for background-blend-mode that is `normal`.
            var cs = Compute("#x { background-blend-mode: multiply; } " +
                             "#x { background-blend-mode: initial; }");
            Assert.That(cs.Get("background-blend-mode"), Is.EqualTo("normal"),
                "initial keyword must resolve to the spec initial value 'normal'");
        }

        [Test]
        public void Background_blend_mode_inherit_keyword_copies_parent_value() {
            // CSS Cascade L5 §7.2: `inherit` forces inheritance of the parent's
            // computed value even for non-inherited properties.
            var doc = Html("<div id=\"parent\"><span id=\"child\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#parent { background-blend-mode: screen; } " +
                       "#child  { background-blend-mode: inherit; }")
            });
            engine.Compute(doc.GetElementById("parent"));
            var cs = engine.Compute(doc.GetElementById("child"));
            Assert.That(cs.Get("background-blend-mode"), Is.EqualTo("screen"),
                "inherit keyword must propagate parent's computed screen value");
        }

        [Test]
        public void Background_blend_mode_unset_on_non_inherited_resolves_to_initial() {
            // CSS Cascade L5 §7.3: `unset` behaves as `initial` for
            // non-inherited properties.
            var cs = Compute("#x { background-blend-mode: overlay; } " +
                             "#x { background-blend-mode: unset; }");
            Assert.That(cs.Get("background-blend-mode"), Is.EqualTo("normal"),
                "unset on non-inherited must resolve to initial 'normal'");
        }

        // ── !important ─────────────────────────────────────────────────────

        [Test]
        public void Background_blend_mode_important_wins_over_higher_specificity() {
            // A lower-specificity rule with !important must defeat a
            // higher-specificity rule without it (CSS Cascade L5 §6.2).
            var doc = Html("<div id=\"x\" class=\"a\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { background-blend-mode: screen !important; } " +
                       "#x.a { background-blend-mode: darken; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("background-blend-mode"), Is.EqualTo("screen"),
                "!important on low-specificity rule must win over higher-specificity normal rule");
        }

        // ── Invalid token recovery ─────────────────────────────────────────

        [Test]
        public void Background_blend_mode_invalid_value_falls_back_to_prior_rule() {
            // CSS Cascade L5 §3: an invalid declaration is ignored; the
            // property retains the previous computed value — here the initial
            // 'normal', since no valid prior rule applies.
            // Per-property keyword validation (CssPropertyKeywordValidator)
            // now enforces this at cascade time for background-blend-mode.
            var cs = Compute("#x { background-blend-mode: not-a-blend-mode; }");
            Assert.That(cs.Get("background-blend-mode"), Is.EqualTo("normal"),
                "invalid blend-mode keyword must be discarded; property falls back to initial");
        }

        [Test]
        public void Background_blend_mode_specificity_winner_overrides_loser() {
            // Higher-specificity #x overrides lower-specificity div.
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { background-blend-mode: overlay; } " +
                       "#x  { background-blend-mode: luminosity; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("background-blend-mode"), Is.EqualTo("luminosity"),
                "#x (id specificity) must beat div (type specificity)");
        }
    }
}
