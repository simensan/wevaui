using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Compositing and Blending Level 1 §8 — `mix-blend-mode` cascade.
    //
    // `mix-blend-mode` controls how an element's rendering is composited with
    // the content behind it (its backdrop in the stacking context).
    //
    // Spec: https://www.w3.org/TR/compositing/#mix-blend-mode
    //   Initial:    normal
    //   Applies to: all elements
    //   Inherited:  NO
    //   Animatable: discrete
    //   Value:      <blend-mode>
    //     <blend-mode> = normal | multiply | screen | overlay | darken |
    //                    lighten | color-dodge | color-burn | hard-light |
    //                    soft-light | difference | exclusion |
    //                    hue | saturation | color | luminosity
    //
    // This file pins the CASCADE layer only (parse → cascade → Get string
    // round-trip). Renderer-level tests (UIBatcher packing, HLSL dispatch)
    // live in Tests/Runtime/Rendering/MixBlendModeTests.cs and require the
    // WEVA_URP define — these tests are always-on headless NUnit.
    public class MixBlendModeCascadeTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static Stylesheet Css(string s) => CssParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(Css(s));

        static ComputedStyle Compute(string css) {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("x"));
        }

        // Parent → child tree; used to verify non-inheritance.
        static ComputedStyle ComputeChild(string parentCss) {
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] { Author(parentCss) });
            engine.Compute(doc.GetElementById("p"));
            return engine.Compute(doc.GetElementById("c"));
        }

        // ── Registration ───────────────────────────────────────────────────

        [Test]
        public void Mix_blend_mode_is_registered() {
            // Property id must be non-negative for all cascade paths to fire.
            Assert.That(CssProperties.GetId("mix-blend-mode"), Is.GreaterThanOrEqualTo(0));
        }

        // ── Initial value ──────────────────────────────────────────────────

        [Test]
        public void Mix_blend_mode_initial_is_normal() {
            // CSS Compositing L1 §8: initial value is `normal` (no compositing effect).
            var cs = Compute("");
            Assert.That(cs.Get("mix-blend-mode"), Is.EqualTo("normal"));
        }

        // ── All 16 <blend-mode> keyword round-trips ────────────────────────
        // CSS Compositing L1 §6.1 lists 16 keywords. Each must survive the
        // full tokenise → cascade → Get path unchanged.

        [Test]
        public void Mix_blend_mode_normal_round_trips() {
            var cs = Compute("#x { mix-blend-mode: normal; }");
            Assert.That(cs.Get("mix-blend-mode"), Is.EqualTo("normal"));
        }

        [Test]
        public void Mix_blend_mode_multiply_round_trips() {
            var cs = Compute("#x { mix-blend-mode: multiply; }");
            Assert.That(cs.Get("mix-blend-mode"), Is.EqualTo("multiply"));
        }

        [Test]
        public void Mix_blend_mode_screen_round_trips() {
            var cs = Compute("#x { mix-blend-mode: screen; }");
            Assert.That(cs.Get("mix-blend-mode"), Is.EqualTo("screen"));
        }

        [Test]
        public void Mix_blend_mode_overlay_round_trips() {
            var cs = Compute("#x { mix-blend-mode: overlay; }");
            Assert.That(cs.Get("mix-blend-mode"), Is.EqualTo("overlay"));
        }

        [Test]
        public void Mix_blend_mode_darken_round_trips() {
            var cs = Compute("#x { mix-blend-mode: darken; }");
            Assert.That(cs.Get("mix-blend-mode"), Is.EqualTo("darken"));
        }

        [Test]
        public void Mix_blend_mode_lighten_round_trips() {
            var cs = Compute("#x { mix-blend-mode: lighten; }");
            Assert.That(cs.Get("mix-blend-mode"), Is.EqualTo("lighten"));
        }

        [Test]
        public void Mix_blend_mode_color_dodge_round_trips() {
            var cs = Compute("#x { mix-blend-mode: color-dodge; }");
            Assert.That(cs.Get("mix-blend-mode"), Is.EqualTo("color-dodge"));
        }

        [Test]
        public void Mix_blend_mode_color_burn_round_trips() {
            var cs = Compute("#x { mix-blend-mode: color-burn; }");
            Assert.That(cs.Get("mix-blend-mode"), Is.EqualTo("color-burn"));
        }

        [Test]
        public void Mix_blend_mode_hard_light_round_trips() {
            var cs = Compute("#x { mix-blend-mode: hard-light; }");
            Assert.That(cs.Get("mix-blend-mode"), Is.EqualTo("hard-light"));
        }

        [Test]
        public void Mix_blend_mode_soft_light_round_trips() {
            var cs = Compute("#x { mix-blend-mode: soft-light; }");
            Assert.That(cs.Get("mix-blend-mode"), Is.EqualTo("soft-light"));
        }

        [Test]
        public void Mix_blend_mode_difference_round_trips() {
            var cs = Compute("#x { mix-blend-mode: difference; }");
            Assert.That(cs.Get("mix-blend-mode"), Is.EqualTo("difference"));
        }

        [Test]
        public void Mix_blend_mode_exclusion_round_trips() {
            var cs = Compute("#x { mix-blend-mode: exclusion; }");
            Assert.That(cs.Get("mix-blend-mode"), Is.EqualTo("exclusion"));
        }

        // HSL-based composite modes — CSS Compositing L1 §11.5..§11.8.

        [Test]
        public void Mix_blend_mode_hue_round_trips() {
            var cs = Compute("#x { mix-blend-mode: hue; }");
            Assert.That(cs.Get("mix-blend-mode"), Is.EqualTo("hue"));
        }

        [Test]
        public void Mix_blend_mode_saturation_round_trips() {
            var cs = Compute("#x { mix-blend-mode: saturation; }");
            Assert.That(cs.Get("mix-blend-mode"), Is.EqualTo("saturation"));
        }

        [Test]
        public void Mix_blend_mode_color_round_trips() {
            // `color` is a valid blend-mode keyword; must not be confused with
            // the CSS color data type or the `color` property name.
            var cs = Compute("#x { mix-blend-mode: color; }");
            Assert.That(cs.Get("mix-blend-mode"), Is.EqualTo("color"));
        }

        [Test]
        public void Mix_blend_mode_luminosity_round_trips() {
            var cs = Compute("#x { mix-blend-mode: luminosity; }");
            Assert.That(cs.Get("mix-blend-mode"), Is.EqualTo("luminosity"));
        }

        // ── Non-inheritance ────────────────────────────────────────────────

        [Test]
        public void Mix_blend_mode_does_not_inherit() {
            // CSS Compositing L1 §8: Inherited: no. A child element with no
            // authored declaration sees the initial value `normal`, not the
            // parent's blend mode.
            var cs = ComputeChild("#p { mix-blend-mode: multiply; }");
            Assert.That(cs.Get("mix-blend-mode"), Is.EqualTo("normal"),
                "mix-blend-mode is non-inherited; child must see initial 'normal'");
        }

        // ── CSS-wide keywords ──────────────────────────────────────────────

        [Test]
        public void Mix_blend_mode_initial_keyword_resolves_to_normal() {
            // `initial` resolves to the property's spec initial for any property.
            var cs = Compute("#x { mix-blend-mode: multiply; } " +
                             "#x { mix-blend-mode: initial; }");
            Assert.That(cs.Get("mix-blend-mode"), Is.EqualTo("normal"),
                "initial keyword must resolve to spec initial 'normal'");
        }

        [Test]
        public void Mix_blend_mode_unset_on_non_inherited_resolves_to_initial() {
            // `unset` on a non-inherited property behaves as `initial`.
            var cs = Compute("#x { mix-blend-mode: screen; } " +
                             "#x { mix-blend-mode: unset; }");
            Assert.That(cs.Get("mix-blend-mode"), Is.EqualTo("normal"),
                "unset on non-inherited property must yield initial 'normal'");
        }

        [Test]
        public void Mix_blend_mode_inherit_keyword_copies_parent_value() {
            // `inherit` forces inheritance of the parent's computed value even
            // for non-inherited properties (CSS Cascade L5 §7.2).
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#p { mix-blend-mode: overlay; } " +
                       "#c { mix-blend-mode: inherit; }")
            });
            engine.Compute(doc.GetElementById("p"));
            var cs = engine.Compute(doc.GetElementById("c"));
            Assert.That(cs.Get("mix-blend-mode"), Is.EqualTo("overlay"),
                "inherit keyword must propagate parent's 'overlay' value");
        }

        // ── Specificity and source order ───────────────────────────────────

        [Test]
        public void Mix_blend_mode_higher_specificity_wins() {
            var doc = Html("<div id=\"x\" class=\"a\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { mix-blend-mode: screen; } " +
                       "#x.a { mix-blend-mode: darken; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("mix-blend-mode"), Is.EqualTo("darken"),
                "#x.a (id+class) specificity must beat div (type)");
        }

        [Test]
        public void Mix_blend_mode_important_overrides_higher_specificity() {
            // !important on a lower-specificity rule beats a higher-specificity
            // normal-priority rule (CSS Cascade L5 §6.2).
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { mix-blend-mode: screen !important; } " +
                       "#x  { mix-blend-mode: darken; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("mix-blend-mode"), Is.EqualTo("screen"),
                "!important on low-specificity rule must win over higher-specificity normal rule");
        }

        [Test]
        public void Mix_blend_mode_later_source_order_wins_at_same_specificity() {
            var cs = Compute("div { mix-blend-mode: multiply; } " +
                             "div { mix-blend-mode: exclusion; }");
            Assert.That(cs.Get("mix-blend-mode"), Is.EqualTo("exclusion"),
                "second rule at same specificity and origin wins by source order");
        }

        // ── Independence from background-blend-mode ────────────────────────

        [Test]
        public void Mix_blend_mode_independent_from_background_blend_mode() {
            // The two blend-mode properties are entirely separate cascade slots.
            var cs = Compute("#x { background-blend-mode: multiply; mix-blend-mode: screen; }");
            Assert.That(cs.Get("mix-blend-mode"), Is.EqualTo("screen"),
                "mix-blend-mode must not be disturbed by background-blend-mode");
            Assert.That(cs.Get("background-blend-mode"), Is.EqualTo("multiply"),
                "background-blend-mode must not be disturbed by mix-blend-mode");
        }
    }
}
