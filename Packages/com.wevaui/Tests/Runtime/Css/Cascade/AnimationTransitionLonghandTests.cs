using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Animations Module L1 §3 + CSS Transitions Module L1 §2 — longhand
    // cascade coverage.
    //
    // CssProperties registers all eight `animation-*` longhands and all four
    // `transition-*` longhands with their spec initial values
    // (CssProperties.cs:917-924, 911-914). Both shorthands have expander
    // coverage (TransitionShorthandTests + the cascade integration tests in
    // CascadeShorthandIntegrationTests) but the longhands themselves had
    // no direct test pinning:
    //   - the spec initial value when no rule applies
    //   - keyword round-trip on the spec-defined non-default values
    //   - non-inheritance (all 12 are non-inherited per spec)
    //
    // CssAnimationRunnerTransitionTests exercises the runtime side
    // (animation play, easing, fill modes) but its setup uses concrete
    // values, not the initial-state contract.
    public class AnimationTransitionLonghandTests {
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

        // ══════════════════════════════════════════════════════════════════
        // CSS Animations L1 §3 — animation-* longhands
        // ══════════════════════════════════════════════════════════════════

        // ── initial values per §3 ────────────────────────────────────────

        [Test]
        public void Animation_name_initial_is_none() {
            // §3.1: initial = `none`. No animation runs without an
            // explicit name binding to a keyframes ruleset.
            var cs = Compute("");
            Assert.That(cs.Get("animation-name"), Is.EqualTo("none"));
        }

        [Test]
        public void Animation_duration_initial_is_zero_seconds() {
            // §3.2: initial = `0s`. A zero-duration animation immediately
            // jumps to the from-value (or to-value depending on fill-mode).
            var cs = Compute("");
            Assert.That(cs.Get("animation-duration"), Is.EqualTo("0s"));
        }

        [Test]
        public void Animation_timing_function_initial_is_ease() {
            // §3.3: initial = `ease`. Same default as CSS Transitions.
            var cs = Compute("");
            Assert.That(cs.Get("animation-timing-function"), Is.EqualTo("ease"));
        }

        [Test]
        public void Animation_delay_initial_is_zero_seconds() {
            // §3.4: initial = `0s`. Negative values are valid per spec
            // (start the animation mid-cycle).
            var cs = Compute("");
            Assert.That(cs.Get("animation-delay"), Is.EqualTo("0s"));
        }

        [Test]
        public void Animation_iteration_count_initial_is_one() {
            // §3.5: initial = `1`. `infinite` is the loop keyword.
            var cs = Compute("");
            Assert.That(cs.Get("animation-iteration-count"), Is.EqualTo("1"));
        }

        [Test]
        public void Animation_direction_initial_is_normal() {
            // §3.6: initial = `normal`. Other values: reverse, alternate,
            // alternate-reverse.
            var cs = Compute("");
            Assert.That(cs.Get("animation-direction"), Is.EqualTo("normal"));
        }

        [Test]
        public void Animation_fill_mode_initial_is_none() {
            // §3.7: initial = `none`. Other values: forwards, backwards,
            // both. Defines whether the animation's effects extend
            // outside its active period.
            var cs = Compute("");
            Assert.That(cs.Get("animation-fill-mode"), Is.EqualTo("none"));
        }

        [Test]
        public void Animation_play_state_initial_is_running() {
            // §3.8: initial = `running`. The other value is `paused`.
            var cs = Compute("");
            Assert.That(cs.Get("animation-play-state"), Is.EqualTo("running"));
        }

        // ── keyword / value round-trips ──────────────────────────────────

        [Test]
        public void Animation_iteration_count_infinite_round_trips() {
            var cs = Compute("#x { animation-iteration-count: infinite; }");
            Assert.That(cs.Get("animation-iteration-count"), Is.EqualTo("infinite"));
        }

        [Test]
        public void Animation_direction_alternate_reverse_round_trips() {
            // CSS Animations L1 §3.6 — the four-way enum.
            var cs = Compute("#x { animation-direction: alternate-reverse; }");
            Assert.That(cs.Get("animation-direction"), Is.EqualTo("alternate-reverse"));
        }

        [Test]
        public void Animation_fill_mode_forwards_round_trips() {
            var cs = Compute("#x { animation-fill-mode: forwards; }");
            Assert.That(cs.Get("animation-fill-mode"), Is.EqualTo("forwards"));
        }

        [Test]
        public void Animation_fill_mode_both_round_trips() {
            var cs = Compute("#x { animation-fill-mode: both; }");
            Assert.That(cs.Get("animation-fill-mode"), Is.EqualTo("both"));
        }

        [Test]
        public void Animation_play_state_paused_round_trips() {
            var cs = Compute("#x { animation-play-state: paused; }");
            Assert.That(cs.Get("animation-play-state"), Is.EqualTo("paused"));
        }

        [Test]
        public void Animation_delay_negative_value_round_trips() {
            // §3.4: negative delays are valid (start mid-cycle).
            var cs = Compute("#x { animation-delay: -500ms; }");
            Assert.That(cs.Get("animation-delay"), Is.EqualTo("-500ms"));
        }

        // ── non-inheritance ──────────────────────────────────────────────

        [Test]
        public void Animation_name_does_not_inherit() {
            // CSS Animations L1 §3.1: animation-name is non-inherited.
            var cs = ComputeChild("div { animation-name: pulse; }");
            Assert.That(cs.Get("animation-name"), Is.EqualTo("none"),
                "animation-name is non-inherited; child must see initial `none`");
        }

        [Test]
        public void Animation_duration_does_not_inherit() {
            var cs = ComputeChild("div { animation-duration: 1s; }");
            Assert.That(cs.Get("animation-duration"), Is.EqualTo("0s"),
                "animation-duration is non-inherited; child must see initial `0s`");
        }

        [Test]
        public void Animation_play_state_does_not_inherit() {
            // Important: pausing a parent's animation must not also pause
            // unrelated child animations.
            var cs = ComputeChild("div { animation-play-state: paused; }");
            Assert.That(cs.Get("animation-play-state"), Is.EqualTo("running"),
                "animation-play-state is non-inherited; pausing parent must not pause unrelated child animations");
        }

        // ══════════════════════════════════════════════════════════════════
        // CSS Transitions L1 §2 — transition-* longhands
        // ══════════════════════════════════════════════════════════════════

        // ── initial values per §2 ────────────────────────────────────────

        [Test]
        public void Transition_property_initial_is_all() {
            // §2.1: initial = `all`. Every animatable property gets the
            // default transition unless overridden.
            var cs = Compute("");
            Assert.That(cs.Get("transition-property"), Is.EqualTo("all"));
        }

        [Test]
        public void Transition_duration_initial_is_zero_seconds() {
            // §2.2: initial = `0s`. No visible transition by default.
            var cs = Compute("");
            Assert.That(cs.Get("transition-duration"), Is.EqualTo("0s"));
        }

        [Test]
        public void Transition_timing_function_initial_is_ease() {
            // §2.3: initial = `ease`.
            var cs = Compute("");
            Assert.That(cs.Get("transition-timing-function"), Is.EqualTo("ease"));
        }

        [Test]
        public void Transition_delay_initial_is_zero_seconds() {
            // §2.4: initial = `0s`. Negative delays are valid here too.
            var cs = Compute("");
            Assert.That(cs.Get("transition-delay"), Is.EqualTo("0s"));
        }

        // ── round-trips ──────────────────────────────────────────────────

        [Test]
        public void Transition_property_none_round_trips() {
            // §2.1: `none` disables all transitions on the element.
            var cs = Compute("#x { transition-property: none; }");
            Assert.That(cs.Get("transition-property"), Is.EqualTo("none"));
        }

        [Test]
        public void Transition_property_specific_round_trips() {
            // §2.1: comma-separated list of property names is allowed.
            var cs = Compute("#x { transition-property: opacity, transform; }");
            Assert.That(cs.Get("transition-property"), Is.EqualTo("opacity, transform"));
        }

        [Test]
        public void Transition_timing_function_cubic_bezier_round_trips() {
            var cs = Compute("#x { transition-timing-function: cubic-bezier(0.4, 0, 0.2, 1); }");
            Assert.That(cs.Get("transition-timing-function"), Is.EqualTo("cubic-bezier(0.4, 0, 0.2, 1)"));
        }

        [Test]
        public void Transition_timing_function_steps_round_trips() {
            // CSS Easing L1 §3.2: steps(n[, position]) for stair-stepping.
            var cs = Compute("#x { transition-timing-function: steps(4, end); }");
            Assert.That(cs.Get("transition-timing-function"), Is.EqualTo("steps(4, end)"));
        }

        // ── non-inheritance ──────────────────────────────────────────────

        [Test]
        public void Transition_property_does_not_inherit() {
            var cs = ComputeChild("div { transition-property: opacity; }");
            Assert.That(cs.Get("transition-property"), Is.EqualTo("all"),
                "transition-property is non-inherited; child must see initial `all`");
        }

        [Test]
        public void Transition_duration_does_not_inherit() {
            var cs = ComputeChild("div { transition-duration: 300ms; }");
            Assert.That(cs.Get("transition-duration"), Is.EqualTo("0s"),
                "transition-duration is non-inherited; child must see initial `0s`");
        }
    }
}
