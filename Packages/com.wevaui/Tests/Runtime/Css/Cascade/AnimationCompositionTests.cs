using NUnit.Framework;
using Weva.Css;
using Weva.Css.Animation;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Animations Module Level 2 §3.6 — `animation-composition` cascade
    // coverage.
    //
    // CssProperties.cs registers `animation-composition` with:
    //   inherited: false   initial: "replace"
    //
    // Value set: replace | add | accumulate  (list, comma-separated — one
    // per animation in the list per §3.6)
    //
    // Runtime semantics (CssAnimationRunner.Compose) are exercised in
    // AnimationCompositionRuntimeTests; this file covers the cascade
    // boundary: initial value, all three keyword round-trips, multi-value
    // list, non-inheritance, !important interaction, CSS-wide keywords, and
    // invalid-keyword rejection.
    public class AnimationCompositionTests {
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
            return engine.Compute(doc.GetElementById("child"));
        }

        // ── Registration ─────────────────────────────────────────────────────

        [Test]
        public void Animation_composition_is_registered() {
            // Property must exist in the registry (id >= 0) for the cascade
            // to carry authored values to computed style.
            Assert.That(CssProperties.Get("animation-composition"), Is.Not.Null,
                "'animation-composition' must be registered in CssProperties");
        }

        // ── Initial value per CSS Animations L2 §3.6 ─────────────────────────

        [Test]
        public void Animation_composition_initial_is_replace() {
            // CSS Animations L2 §3.6: initial value is `replace`. No
            // `animation-composition` declaration → computed value = `replace`.
            var cs = Compute("");
            Assert.That(cs.Get("animation-composition"), Is.EqualTo("replace"),
                "CSS Animations L2 §3.6: initial value must be 'replace'");
        }

        // ── Keyword round-trips ───────────────────────────────────────────────

        [Test]
        public void Animation_composition_replace_round_trips() {
            // §3.6: `replace` — the effect value replaces the underlying value.
            // Explicit authoring of the initial value should survive the cascade.
            var cs = Compute("#x { animation-composition: replace; }");
            Assert.That(cs.Get("animation-composition"), Is.EqualTo("replace"),
                "animation-composition: replace must survive cascade round-trip");
        }

        [Test]
        public void Animation_composition_add_round_trips() {
            // §3.6: `add` — the effect value is added to the underlying value
            // (e.g. numeric opacity summed, transforms concatenated).
            var cs = Compute("#x { animation-composition: add; }");
            Assert.That(cs.Get("animation-composition"), Is.EqualTo("add"),
                "animation-composition: add must survive cascade round-trip");
        }

        [Test]
        public void Animation_composition_accumulate_round_trips() {
            // §3.6: `accumulate` — like `add` but uses accumulation semantics
            // for iteration counts. Engine maps to `add` for v1 per H2b notes.
            var cs = Compute("#x { animation-composition: accumulate; }");
            Assert.That(cs.Get("animation-composition"), Is.EqualTo("accumulate"),
                "animation-composition: accumulate must survive cascade round-trip");
        }

        // ── Multi-value list (<single-animation-composition>#) ────────────────

        [Test]
        public void Animation_composition_two_value_list_round_trips() {
            // §3.6: The property accepts a comma-separated list; one value per
            // animation in the animation-name list. e.g. `replace, add` means
            // the first animation uses `replace`, the second uses `add`.
            var cs = Compute("#x { animation-composition: replace, add; }");
            Assert.That(cs.Get("animation-composition"), Is.EqualTo("replace, add"),
                "Two-value animation-composition list must round-trip");
        }

        [Test]
        public void Animation_composition_three_value_list_round_trips() {
            // §3.6: Full three-value list pinning all three keywords in list form.
            var cs = Compute("#x { animation-composition: replace, add, accumulate; }");
            Assert.That(cs.Get("animation-composition"), Is.EqualTo("replace, add, accumulate"),
                "Three-value animation-composition list must round-trip");
        }

        [Test]
        public void Animation_composition_repeated_values_round_trip() {
            // §3.6: Repeated keywords are legal (matches the pattern for all
            // animation-* comma lists). Two `accumulate` entries is valid.
            var cs = Compute("#x { animation-composition: accumulate, accumulate; }");
            Assert.That(cs.Get("animation-composition"), Is.EqualTo("accumulate, accumulate"),
                "Repeated keywords in animation-composition list must round-trip");
        }

        // ── Non-inheritance ───────────────────────────────────────────────────

        [Test]
        public void Animation_composition_is_not_inherited() {
            // CSS Animations L2 §3.6: Inherited: no. The parent's authored
            // composition mode must not propagate to children.
            var cs = ComputeChild("div { animation-composition: add; }");
            Assert.That(cs.Get("animation-composition"), Is.EqualTo("replace"),
                "animation-composition is non-inherited; child must see initial 'replace'");
        }

        [Test]
        public void Animation_composition_non_inheritance_flag_is_false() {
            // Verify the registration-level inheritance flag matches spec.
            Assert.That(CssProperties.IsInherited("animation-composition"), Is.False,
                "CSS Animations L2 §3.6 specifies 'Inherited: no'");
        }

        // ── !important interaction ────────────────────────────────────────────

        [Test]
        public void Animation_composition_important_overrides_lower_specificity() {
            // CSS Cascade L5 §6.4: !important elevates a declaration above all
            // normal-origin declarations regardless of specificity. A wildcard
            // selector's !important must beat an id-selector's normal declaration.
            var cs = Compute("* { animation-composition: accumulate !important; } #x { animation-composition: add; }");
            Assert.That(cs.Get("animation-composition"), Is.EqualTo("accumulate"),
                "!important must override higher-specificity normal declaration");
        }

        [Test]
        public void Animation_composition_important_beats_source_order() {
            // A later normal declaration must not displace an earlier !important.
            var cs = Compute("#x { animation-composition: add !important; animation-composition: replace; }");
            Assert.That(cs.Get("animation-composition"), Is.EqualTo("add"),
                "!important must not be displaced by a later normal declaration");
        }

        // ── CSS-wide keywords ─────────────────────────────────────────────────

        [Test]
        public void Animation_composition_initial_keyword_resets_to_replace() {
            // CSS Cascade L5 §7.1: `initial` always resolves to the property's
            // registered initial value.
            var cs = Compute("#x { animation-composition: add; animation-composition: initial; }");
            Assert.That(cs.Get("animation-composition"), Is.EqualTo("replace"),
                "'initial' must reset animation-composition to its initial value 'replace'");
        }

        [Test]
        public void Animation_composition_unset_on_non_inherited_resolves_to_initial() {
            // CSS Cascade L5 §7.3: `unset` = `initial` for non-inherited props.
            var cs = Compute("#x { animation-composition: add; animation-composition: unset; }");
            Assert.That(cs.Get("animation-composition"), Is.EqualTo("replace"),
                "'unset' on a non-inherited property must resolve to 'replace' (initial)");
        }

        [Test]
        public void Animation_composition_inherit_keyword_pulls_parent_value() {
            // CSS Cascade L5 §7.2: `inherit` forces parent's computed value,
            // even on non-inherited properties.
            var doc = Html("<div><span id=\"child\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { animation-composition: add; } #child { animation-composition: inherit; }")
            });
            var cs = engine.Compute(doc.GetElementById("child"));
            Assert.That(cs.Get("animation-composition"), Is.EqualTo("add"),
                "'inherit' on a non-inherited property must pull parent computed value");
        }

        // ── Invalid keyword handling ──────────────────────────────────────────

        [Test]
        public void Animation_composition_invalid_keyword_falls_back_to_initial_per_spec() {
            // CSS Values L4 §2.1 / CSS Cascade L5 §3: an unrecognised keyword
            // is an invalid declaration; it is treated as if not specified and
            // the cascade falls through to the initial value `replace`.
            // Per-property keyword validation (CssPropertyKeywordValidator) is
            // enforced at cascade time for known keyword-only properties.
            var cs = Compute("#x { animation-composition: foo-bar; }");
            Assert.That(cs.Get("animation-composition"), Is.EqualTo("replace"),
                "Spec: invalid keyword must be dropped; initial 'replace' applies");
        }

        [Test]
        public void Animation_composition_is_not_animatable_itself() {
            // CSS Animations L2 §3.6: 'Animatable: no'. The property controls
            // how animations compose but is itself not an animated property.
            // PropertyKindRegistry.IsAnimatable returns true only for properties
            // with an explicit PropertyKind entry (i.e. known animatable types).
            // animation-composition must have no such entry.
            Assert.That(PropertyKindRegistry.IsAnimatable("animation-composition"), Is.False,
                "animation-composition must not be animatable per CSS Animations L2 §3.6");
        }
    }
}
