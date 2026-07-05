using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Transitions Module Level 2 — `transition-behavior` cascade coverage.
    //
    // ENGINE STATE (2026-05-30): `transition-behavior` is registered in CssProperties.
    // Gap A13 closed. The cascade correctly stores `normal` / `allow-discrete` and
    // the property has a valid id, initial value, and non-inherited flag.
    //
    // This file has two groups:
    //
    //   1. CURRENT-BEHAVIOUR tests (updated to reflect registered state).
    //
    //   2. SPEC-CONTRACT tests (un-ignored from [Ignore]'d) — now green.
    //
    // Spec: CSS Transitions L2 §3.1
    //   Name:       transition-behavior
    //   Value:      normal | allow-discrete  (comma list, §3.1)
    //   Initial:    normal
    //   Inherited:  no
    //   Animatable: no
    //
    // Spec: CSS Transitions L2 §2.1 also extends the set of discrete-animatable
    // properties to include `display` and `content-visibility` — but ONLY when
    // the matching transition-behavior entry is `allow-discrete`. Those
    // declarations round-trip through the cascade without `transition-behavior`;
    // the runtime gating is the unimplemented part.
    public class TransitionBehaviorTests {
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

        // ══════════════════════════════════════════════════════════════════════
        // Registration / round-trip tests (gap A13 closed 2026-05-30)
        // ══════════════════════════════════════════════════════════════════════

        [Test]
        public void Transition_behavior_is_registered() {
            // Gap A13 closed: transition-behavior is registered with a valid id.
            Assert.That(CssProperties.Get("transition-behavior"), Is.Not.Null,
                "transition-behavior must be registered in CssProperties (gap A13 closed)");
        }

        [Test]
        public void Transition_behavior_authored_value_round_trips() {
            // Authored value routes through the typed cascade slot.
            var cs = Compute("#x { transition-behavior: allow-discrete; }");
            Assert.That(cs.Get("transition-behavior"), Is.EqualTo("allow-discrete"),
                "authored transition-behavior: allow-discrete must survive cascade round-trip");
        }

        [Test]
        public void Display_and_content_visibility_round_trip_independently() {
            // CSS Transitions L2 §2.1 adds display/content-visibility to the
            // discrete-animatable set. These properties ARE registered and their
            // authored values MUST survive cascade regardless of transition-behavior.
            // This test verifies the cascade-side contract (not the runtime gating).
            var cs = Compute("#x { display: block; content-visibility: hidden; }");
            Assert.That(cs.Get("display"), Is.EqualTo("block"),
                "display: block must survive cascade round-trip");
            Assert.That(cs.Get("content-visibility"), Is.EqualTo("hidden"),
                "content-visibility: hidden must survive cascade round-trip");
        }

        [Test]
        public void Transition_longhand_with_display_property_name_round_trips() {
            // Authors who want to animate display must write:
            //   transition-property: display;
            //   transition-behavior: allow-discrete;
            // The transition-property longhand DOES survive cascade.
            // Verify the cascade-level round-trip for the supported longhands.
            var cs = Compute("#x { transition-property: display; transition-duration: 500ms; }");
            Assert.That(cs.Get("transition-property"), Is.EqualTo("display"),
                "transition-property: display must survive cascade round-trip");
        }

        // ══════════════════════════════════════════════════════════════════════
        // SPEC-CONTRACT tests — gap A13 closed 2026-05-30, now green.
        // ══════════════════════════════════════════════════════════════════════

        [Test]
        public void Transition_behavior_initial_is_normal() {
            // CSS Transitions L2 §3.1: initial value is `normal`.
            var cs = Compute("");
            Assert.That(cs.Get("transition-behavior"), Is.EqualTo("normal"),
                "CSS Transitions L2 §3.1: initial value must be 'normal'");
        }

        [Test]
        public void Transition_behavior_normal_round_trips() {
            // §3.1: `normal` — only smoothly-animatable properties transition.
            var cs = Compute("#x { transition-behavior: normal; }");
            Assert.That(cs.Get("transition-behavior"), Is.EqualTo("normal"),
                "transition-behavior: normal must survive cascade round-trip");
        }

        [Test]
        public void Transition_behavior_allow_discrete_round_trips() {
            // §3.1: `allow-discrete` — enables transitions for discrete-value
            // properties (display, content-visibility) in the matching entry.
            var cs = Compute("#x { transition-behavior: allow-discrete; }");
            Assert.That(cs.Get("transition-behavior"), Is.EqualTo("allow-discrete"),
                "transition-behavior: allow-discrete must survive cascade round-trip");
        }

        [Test]
        public void Transition_behavior_two_value_list_round_trips() {
            // §3.1: comma-separated list, one entry per transition-property entry.
            // `normal, allow-discrete` means the first transition uses normal,
            // the second uses allow-discrete.
            var cs = Compute("#x { transition-behavior: normal, allow-discrete; }");
            Assert.That(cs.Get("transition-behavior"), Is.EqualTo("normal, allow-discrete"),
                "Two-value transition-behavior list must round-trip");
        }

        [Test]
        public void Transition_behavior_repeated_allow_discrete_list_round_trips() {
            // Repeated `allow-discrete` values are legal — useful when multiple
            // transitions on the same element all animate discrete properties.
            var cs = Compute("#x { transition-behavior: allow-discrete, allow-discrete; }");
            Assert.That(cs.Get("transition-behavior"), Is.EqualTo("allow-discrete, allow-discrete"),
                "Repeated allow-discrete values in list must round-trip");
        }

        [Test]
        public void Transition_behavior_is_not_inherited() {
            // CSS Transitions L2 §3.1: Inherited: no. Parent value must not
            // propagate to children.
            var cs = ComputeChild("div { transition-behavior: allow-discrete; }");
            Assert.That(cs.Get("transition-behavior"), Is.EqualTo("normal"),
                "transition-behavior is non-inherited; child must see initial 'normal'");
        }

        [Test]
        public void Transition_behavior_non_inheritance_flag_is_false() {
            // Registration-level flag must match spec.
            Assert.That(CssProperties.IsInherited("transition-behavior"), Is.False,
                "CSS Transitions L2 §3.1 specifies 'Inherited: no'");
        }

        [Test]
        public void Transition_behavior_important_overrides_lower_specificity() {
            // CSS Cascade L5 §6.4: !important elevates above all normal declarations.
            var cs = Compute("* { transition-behavior: allow-discrete !important; } #x { transition-behavior: normal; }");
            Assert.That(cs.Get("transition-behavior"), Is.EqualTo("allow-discrete"),
                "!important must override higher-specificity normal declaration");
        }

        [Test]
        public void Transition_behavior_initial_keyword_resets_to_normal() {
            // CSS Cascade L5 §7.1: `initial` resolves to the property's initial value.
            var cs = Compute("#x { transition-behavior: allow-discrete; transition-behavior: initial; }");
            Assert.That(cs.Get("transition-behavior"), Is.EqualTo("normal"),
                "'initial' must reset transition-behavior to its initial value 'normal'");
        }

        [Test]
        public void Transition_behavior_unset_resolves_to_initial() {
            // CSS Cascade L5 §7.3: `unset` = `initial` for non-inherited properties.
            var cs = Compute("#x { transition-behavior: allow-discrete; transition-behavior: unset; }");
            Assert.That(cs.Get("transition-behavior"), Is.EqualTo("normal"),
                "'unset' on a non-inherited property must resolve to 'normal' (initial)");
        }

        // ══════════════════════════════════════════════════════════════════════
        // Discrete animation contract (cascade-level round-trip)
        // ══════════════════════════════════════════════════════════════════════

        [Test]
        public void Transition_display_with_allow_discrete_longhands_all_round_trip() {
            // CSS Transitions L2 §2.1: `display` animates discretely when the
            // matching transition-behavior entry is `allow-discrete`.
            // Cascade-level round-trip: all four longhands co-exist cleanly.
            // The runtime swap (display:none → display:block at t=1) is not
            // tested here — only that authored declarations survive the cascade.
            var cs = Compute(
                "#x { " +
                "  transition-property: display; " +
                "  transition-duration: 500ms; " +
                "  transition-timing-function: ease; " +
                "  transition-behavior: allow-discrete; " +
                "}");
            Assert.That(cs.Get("transition-property"), Is.EqualTo("display"),
                "transition-property: display must survive cascade");
            Assert.That(cs.Get("transition-duration"), Is.EqualTo("500ms"),
                "transition-duration: 500ms must survive cascade");
            Assert.That(cs.Get("transition-timing-function"), Is.EqualTo("ease"),
                "transition-timing-function: ease must survive cascade");
            Assert.That(cs.Get("transition-behavior"), Is.EqualTo("allow-discrete"),
                "transition-behavior: allow-discrete must survive cascade");
        }

        [Test]
        public void Transition_content_visibility_with_allow_discrete_round_trips() {
            // CSS Transitions L2 §2.1: `content-visibility` also participates
            // in discrete animation when `allow-discrete` is specified.
            var cs = Compute(
                "#x { " +
                "  transition-property: content-visibility; " +
                "  transition-duration: 300ms; " +
                "  transition-behavior: allow-discrete; " +
                "}");
            Assert.That(cs.Get("transition-property"), Is.EqualTo("content-visibility"),
                "transition-property: content-visibility must survive cascade");
            Assert.That(cs.Get("transition-behavior"), Is.EqualTo("allow-discrete"),
                "transition-behavior: allow-discrete must survive cascade");
        }
    }
}
