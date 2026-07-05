using NUnit.Framework;
using Weva.Css;
using Weva.Css.Animation;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Events;

namespace Weva.Tests.Css.Animation {
    public class CssAnimationRunnerKeyframesTests {
        const double Eps = 1e-3;

        static (CssAnimationRunner runner, FakeUIClock clock) MakeRunner(string css) {
            var clock = new FakeUIClock();
            var sheet = CssParser.Parse(css);
            var cascade = new CascadeEngine(System.Array.Empty<OriginatedStylesheet>());
            var runner = new CssAnimationRunner(cascade, new[] { sheet }, clock);
            return (runner, clock);
        }

        static ComputedStyle Style(Element e, params (string, string)[] kv) {
            var s = new ComputedStyle(e);
            foreach (var pair in kv) s.Set(pair.Item1, pair.Item2);
            return s;
        }

        [Test]
        public void Animation_runs_when_name_matches() {
            var (runner, clock) = MakeRunner("@keyframes spin { from { opacity: 0; } to { opacity: 1; } }");
            var e = new Element("div");
            var s = Style(e,
                ("animation-name", "spin"),
                ("animation-duration", "1s"),
                ("animation-timing-function", "linear"));
            runner.OnStyleChange(e, null, s);
            clock.Set(0.5);
            runner.Tick(0.5);
            var c = runner.Compose(e, s);
            Assert.That(double.Parse(c.Get("opacity"), System.Globalization.CultureInfo.InvariantCulture),
                Is.EqualTo(0.5).Within(Eps));
        }

        [Test]
        public void Iteration_count_infinite_keeps_running() {
            var (runner, clock) = MakeRunner("@keyframes a { from { opacity: 0; } to { opacity: 1; } }");
            var e = new Element("div");
            var s = Style(e,
                ("animation-name", "a"),
                ("animation-duration", "1s"),
                ("animation-iteration-count", "infinite"),
                ("animation-timing-function", "linear"));
            runner.OnStyleChange(e, null, s);
            clock.Set(10.0);
            runner.Tick(10.0);
            // Still has a sample (at iteration boundary, modulo loops back).
            var c = runner.Compose(e, s);
            Assert.That(c.Get("opacity"), Is.Not.Null);
        }

        [Test]
        public void Iteration_count_two_plays_twice_then_stops() {
            var (runner, clock) = MakeRunner("@keyframes a { from { opacity: 0; } to { opacity: 1; } }");
            var e = new Element("div");
            var s = Style(e,
                ("animation-name", "a"),
                ("animation-duration", "1s"),
                ("animation-iteration-count", "2"),
                ("animation-timing-function", "linear"),
                ("animation-fill-mode", "none"));
            runner.OnStyleChange(e, null, s);
            clock.Set(2.5);
            runner.Tick(2.5);
            var c = runner.Compose(e, s);
            // Fill-mode none -> outside active window, sample is null and base style passes through.
            Assert.That(c.Get("opacity"), Is.Null.Or.EqualTo(s.Get("opacity")));
        }

        [Test]
        public void Direction_reverse_runs_from_one_to_zero() {
            var (runner, clock) = MakeRunner("@keyframes a { from { opacity: 0; } to { opacity: 1; } }");
            var e = new Element("div");
            var s = Style(e,
                ("animation-name", "a"),
                ("animation-duration", "1s"),
                ("animation-direction", "reverse"),
                ("animation-timing-function", "linear"));
            runner.OnStyleChange(e, null, s);
            clock.Set(0.0001);
            runner.Tick(0.0001);
            var c = runner.Compose(e, s);
            Assert.That(double.Parse(c.Get("opacity"), System.Globalization.CultureInfo.InvariantCulture),
                Is.GreaterThan(0.95));
        }

        [Test]
        public void Direction_alternate_flips_every_iteration() {
            var (runner, clock) = MakeRunner("@keyframes a { from { opacity: 0; } to { opacity: 1; } }");
            var e = new Element("div");
            var s = Style(e,
                ("animation-name", "a"),
                ("animation-duration", "1s"),
                ("animation-direction", "alternate"),
                ("animation-iteration-count", "2"),
                ("animation-timing-function", "linear"));
            runner.OnStyleChange(e, null, s);
            clock.Set(1.5);
            runner.Tick(1.5);
            var c = runner.Compose(e, s);
            // Halfway into reversed second iteration: ~0.5
            Assert.That(double.Parse(c.Get("opacity"), System.Globalization.CultureInfo.InvariantCulture),
                Is.EqualTo(0.5).Within(0.01));
        }

        [Test]
        public void Fill_mode_forwards_keeps_final_state() {
            var (runner, clock) = MakeRunner("@keyframes a { from { opacity: 0; } to { opacity: 1; } }");
            var e = new Element("div");
            var s = Style(e,
                ("animation-name", "a"),
                ("animation-duration", "1s"),
                ("animation-fill-mode", "forwards"),
                ("animation-timing-function", "linear"));
            runner.OnStyleChange(e, null, s);
            clock.Set(2.0);
            runner.Tick(2.0);
            var c = runner.Compose(e, s);
            Assert.That(double.Parse(c.Get("opacity"), System.Globalization.CultureInfo.InvariantCulture),
                Is.EqualTo(1).Within(Eps));
        }

        [Test]
        public void Fill_mode_backwards_applies_start_state_during_delay() {
            var (runner, clock) = MakeRunner("@keyframes a { from { opacity: 0.25; } to { opacity: 1; } }");
            var e = new Element("div");
            var s = Style(e,
                ("animation-name", "a"),
                ("animation-duration", "1s"),
                ("animation-delay", "1s"),
                ("animation-fill-mode", "backwards"),
                ("animation-timing-function", "linear"));
            runner.OnStyleChange(e, null, s);
            clock.Set(0.5);
            runner.Tick(0.5);
            var c = runner.Compose(e, s);
            Assert.That(double.Parse(c.Get("opacity"), System.Globalization.CultureInfo.InvariantCulture),
                Is.EqualTo(0.25).Within(Eps));
        }

        [Test]
        public void Fill_mode_both_combines_backwards_and_forwards() {
            var (runner, clock) = MakeRunner("@keyframes a { from { opacity: 0.25; } to { opacity: 0.75; } }");
            var e = new Element("div");
            var s = Style(e,
                ("animation-name", "a"),
                ("animation-duration", "1s"),
                ("animation-delay", "0.5s"),
                ("animation-fill-mode", "both"),
                ("animation-timing-function", "linear"));
            runner.OnStyleChange(e, null, s);
            clock.Set(0.0);
            runner.Tick(0.0);
            var c1 = runner.Compose(e, s);
            Assert.That(double.Parse(c1.Get("opacity"), System.Globalization.CultureInfo.InvariantCulture),
                Is.EqualTo(0.25).Within(Eps));
            clock.Set(5.0);
            runner.Tick(5.0);
            var c2 = runner.Compose(e, s);
            Assert.That(double.Parse(c2.Get("opacity"), System.Globalization.CultureInfo.InvariantCulture),
                Is.EqualTo(0.75).Within(Eps));
        }

        [Test]
        public void Play_state_paused_halts_at_current_time() {
            var (runner, clock) = MakeRunner("@keyframes a { from { opacity: 0; } to { opacity: 1; } }");
            var e = new Element("div");
            // First start running.
            var sRun = Style(e,
                ("animation-name", "a"),
                ("animation-duration", "1s"),
                ("animation-play-state", "paused"),
                ("animation-timing-function", "linear"));
            runner.OnStyleChange(e, null, sRun);
            clock.Set(0.5);
            runner.Tick(0.5);
            clock.Set(2.0);
            runner.Tick(2.0);
            var c = runner.Compose(e, sRun);
            // Paused at start -> opacity 0.
            Assert.That(double.Parse(c.Get("opacity"), System.Globalization.CultureInfo.InvariantCulture),
                Is.LessThan(0.1));
        }

        [Test]
        public void Multiple_animations_compose_with_later_winning() {
            var css = "@keyframes a { from { opacity: 0; } to { opacity: 0.25; } } " +
                      "@keyframes b { from { opacity: 0.5; } to { opacity: 1; } }";
            var (runner, clock) = MakeRunner(css);
            var e = new Element("div");
            var s = Style(e,
                ("animation-name", "a, b"),
                ("animation-duration", "1s, 1s"),
                ("animation-timing-function", "linear, linear"));
            runner.OnStyleChange(e, null, s);
            clock.Set(0.5);
            runner.Tick(0.5);
            var c = runner.Compose(e, s);
            // b wins; at t=0.5 with linear: 0.5 -> 1, halfway = 0.75.
            Assert.That(double.Parse(c.Get("opacity"), System.Globalization.CultureInfo.InvariantCulture),
                Is.EqualTo(0.75).Within(0.05));
        }

        [Test]
        public void Finished_both_animation_keeps_final_transform_while_sibling_infinite_animation_runs() {
            var css =
                "@keyframes star-pop { " +
                "0% { transform: translateY(18px) scale(0.20); opacity: 0; } " +
                "100% { transform: translateY(0px) scale(1); opacity: 1; } } " +
                "@keyframes star-twinkle { " +
                "0%, 100% { filter: brightness(1); } " +
                "50% { filter: brightness(1.35); } }";
            var (runner, clock) = MakeRunner(css);
            var e = new Element("div");
            var s = Style(e,
                ("animation-name", "star-pop, star-twinkle"),
                ("animation-duration", "1s, 2s"),
                ("animation-timing-function", "linear, linear"),
                ("animation-iteration-count", "1, infinite"),
                ("animation-fill-mode", "both, none"));
            runner.OnStyleChange(e, null, s);
            clock.Set(2.5);
            runner.Tick(2.5);

            var c1 = runner.Compose(e, s);
            Assert.That(c1.Get("transform"), Is.EqualTo("translatey(0px) scale(1)"));
            Assert.That(c1.Get("opacity"), Is.EqualTo("1"));
            Assert.That(c1.Get("filter"), Is.EqualTo("brightness(1.175)"));

            // A later cascade/style notification with the same computed
            // animation lists must update the specs without resetting the
            // finite animation's start time back to zero.
            runner.OnStyleChange(e, s, s);
            clock.Set(2.75);
            runner.Tick(2.75);
            var c2 = runner.Compose(e, s);
            Assert.That(c2.Get("transform"), Is.EqualTo("translatey(0px) scale(1)"));
            Assert.That(c2.Get("opacity"), Is.EqualTo("1"));
        }

        [Test]
        public void Sparse_keyframes_interpolate_each_property_from_frames_that_declare_it() {
            var css =
                "@keyframes confetti-fall { " +
                "0% { transform: translateY(-28px) rotate(0deg); opacity: 0; } " +
                "15% { opacity: 1; } " +
                "100% { transform: translateY(760px) rotate(260deg); opacity: 0; } }";
            var (runner, clock) = MakeRunner(css);
            var e = new Element("div");
            var s = Style(e,
                ("animation-name", "confetti-fall"),
                ("animation-duration", "1s"),
                ("animation-timing-function", "linear"));
            runner.OnStyleChange(e, null, s);

            clock.Set(0.5);
            runner.Tick(0.5);
            var c = runner.Compose(e, s);

            Assert.That(c.Get("transform"), Is.EqualTo("translatey(366px) rotate(130deg)"));
            Assert.That(double.Parse(c.Get("opacity"), System.Globalization.CultureInfo.InvariantCulture),
                Is.EqualTo(0.588).Within(0.01));
        }

        [Test]
        public void Sparse_keyframes_choose_interval_before_applying_easing() {
            var css =
                "@keyframes confetti-fall { " +
                "0% { transform: translateY(-28px) rotate(0deg); opacity: 0; } " +
                "15% { opacity: 1; } " +
                "100% { transform: translateY(760px) rotate(260deg); opacity: 0; } }";
            var (runner, clock) = MakeRunner(css);
            var e = new Element("div");
            var s = Style(e,
                ("animation-name", "confetti-fall"),
                ("animation-duration", "1s"),
                ("animation-timing-function", "ease-in"));
            runner.OnStyleChange(e, null, s);

            clock.Set(0.2);
            runner.Tick(0.2);
            var c = runner.Compose(e, s);

            Assert.That(double.Parse(c.Get("opacity"), System.Globalization.CultureInfo.InvariantCulture),
                Is.GreaterThan(0.9));
        }

        [Test]
        public void Animation_longhand_delay_overrides_shorthand_components() {
            var (runner, clock) = MakeRunner(
                "@keyframes fade { from { opacity: 0; } to { opacity: 1; } }");
            var e = new Element("div");
            var s = Style(e,
                ("animation", "fade 1s linear 0s both"),
                ("animation-name", "fade"),
                ("animation-duration", "1s"),
                ("animation-timing-function", "linear"),
                ("animation-delay", "1s"),
                ("animation-iteration-count", "1"),
                ("animation-direction", "normal"),
                ("animation-fill-mode", "both"),
                ("animation-play-state", "running"));
            runner.OnStyleChange(e, null, s);
            clock.Set(0.5);
            runner.Tick(0.5);

            var c = runner.Compose(e, s);
            Assert.That(double.Parse(c.Get("opacity"), System.Globalization.CultureInfo.InvariantCulture),
                Is.EqualTo(0).Within(Eps),
                "The computed animation-delay longhand must win over the original shorthand delay.");
        }

        // Confidence test for the demo's pulse pattern. At t=0.5s into a 2s linear pulse that goes
        // 0 -> 1 -> 0, the running sample at the 50% keyframe should be exactly 1.0 (the peak).
        // We sample at half a period (1s into 2s) which lands exactly on the 50% keyframe.
        [Test]
        public void Keyframes_at_50_percent_interpolates_correctly() {
            var (runner, clock) = MakeRunner(
                "@keyframes pulse { 0% { opacity: 0; } 50% { opacity: 1; } 100% { opacity: 0; } }");
            var e = new Element("div");
            var s = Style(e,
                ("animation-name", "pulse"),
                ("animation-duration", "2s"),
                ("animation-timing-function", "linear"),
                ("animation-iteration-count", "1"),
                ("animation-fill-mode", "forwards"));
            runner.OnStyleChange(e, null, s);
            // Sample exactly on the 50% keyframe.
            clock.Set(1.0);
            runner.Tick(1.0);
            var c = runner.Compose(e, s);
            Assert.That(double.Parse(c.Get("opacity"), System.Globalization.CultureInfo.InvariantCulture),
                Is.EqualTo(1.0).Within(Eps));

            // And quarter-way through (t=0.5s, halfway between 0% and 50%) should be 0.5.
            clock.Set(0.5);
            runner.Tick(0.5);
            var c2 = runner.Compose(e, s);
            Assert.That(double.Parse(c2.Get("opacity"), System.Globalization.CultureInfo.InvariantCulture),
                Is.EqualTo(0.5).Within(Eps));
        }

        // Regression: @keyframes values that reference custom properties
        // (e.g. `background-color: var(--red)`) were carried through to the
        // composed style as the literal string "var(--red)" — unparseable as a
        // color, so the animated box painted transparent and read as "just
        // black" against the page background (transform-anim.html .a-color).
        // var() in keyframe values must resolve against the element's computed
        // style exactly like an ordinary declaration.
        [Test]
        public void Keyframe_var_reference_resolves_against_element_custom_properties() {
            var (runner, clock) = MakeRunner(
                "@keyframes color-cycle { " +
                "0% { background-color: var(--red); } " +
                "100% { background-color: var(--green); } }");
            var e = new Element("div");
            var s = Style(e,
                ("--red", "#ef4444"),
                ("--green", "#22c55e"),
                ("animation-name", "color-cycle"),
                ("animation-duration", "4s"),
                ("animation-timing-function", "linear"),
                ("animation-iteration-count", "infinite"));
            runner.OnStyleChange(e, null, s);

            // At t=0 the 0% keyframe resolves --red to its concrete value.
            clock.Set(0.0);
            runner.Tick(0.0);
            var c0 = runner.Compose(e, s);
            var bg0 = c0.Get("background-color");
            Assert.That(bg0, Is.Not.Null.And.Not.Empty, "animated background-color must be set");
            Assert.That(bg0, Does.Not.Contain("var("),
                "var() in the keyframe value must be substituted, not carried through as a literal string");

            // Midway, the value is a blend of the two resolved colors — still a
            // concrete color, never a raw var() expression.
            clock.Set(2.0);
            runner.Tick(2.0);
            var cMid = runner.Compose(e, s);
            var bgMid = cMid.Get("background-color");
            Assert.That(bgMid, Is.Not.Null.And.Not.Empty);
            Assert.That(bgMid, Does.Not.Contain("var("));
        }

        [Test]
        public void Removing_animation_name_stops_animation() {
            var (runner, clock) = MakeRunner("@keyframes a { from { opacity: 0; } to { opacity: 1; } }");
            var e = new Element("div");
            var s1 = Style(e,
                ("animation-name", "a"),
                ("animation-duration", "1s"),
                ("animation-timing-function", "linear"));
            runner.OnStyleChange(e, null, s1);
            Assert.That(runner.RunningAnimationCount, Is.EqualTo(1));
            var s2 = Style(e,
                ("animation-name", "none"),
                ("animation-duration", "1s"),
                ("animation-timing-function", "linear"));
            runner.OnStyleChange(e, s1, s2);
            Assert.That(runner.RunningAnimationCount, Is.EqualTo(0));
        }

        // CSS Animations L1 §4.2: a negative `animation-delay` shifts the
        // start-of-animation backward in time, so at t=0 the animation appears
        // to have already been running for |delay| seconds.
        [Test]
        public void Negative_delay_samples_mid_animation_at_start() {
            var (runner, clock) = MakeRunner("@keyframes a { from { opacity: 0; } to { opacity: 1; } }");
            var e = new Element("div");
            var s = Style(e,
                ("animation-name", "a"),
                ("animation-duration", "1s"),
                ("animation-delay", "-0.5s"),
                ("animation-timing-function", "linear"),
                ("animation-fill-mode", "forwards"));
            runner.OnStyleChange(e, null, s);
            clock.Set(0);
            runner.Tick(0);
            var c = runner.Compose(e, s);
            Assert.That(double.Parse(c.Get("opacity"), System.Globalization.CultureInfo.InvariantCulture),
                Is.EqualTo(0.5).Within(0.05));
        }

        // When |delay| exceeds the total active duration, the animation is
        // already finished at t=0 — fill-mode dictates whether we hold the
        // final state or pass through (return null).
        [Test]
        public void Negative_delay_larger_than_duration_treats_animation_as_finished() {
            var (runner, clock) = MakeRunner("@keyframes a { from { opacity: 0; } to { opacity: 1; } }");
            var e = new Element("div");
            var s = Style(e,
                ("animation-name", "a"),
                ("animation-duration", "1s"),
                ("animation-delay", "-2s"),
                ("animation-timing-function", "linear"),
                ("animation-fill-mode", "forwards"));
            runner.OnStyleChange(e, null, s);
            clock.Set(0);
            runner.Tick(0);
            var c = runner.Compose(e, s);
            // Forwards fill -> hold final state (opacity 1).
            Assert.That(double.Parse(c.Get("opacity"), System.Globalization.CultureInfo.InvariantCulture),
                Is.EqualTo(1.0).Within(Eps));
        }

        // CSS Animations L1 §4.7: iteration-count accepts <number> including
        // fractional values. A 0.5-iteration animation runs through exactly
        // half of one cycle then stops.
        [Test]
        public void Fractional_iteration_count_runs_partial_iteration() {
            var (runner, clock) = MakeRunner("@keyframes a { from { opacity: 0; } to { opacity: 1; } }");
            var e = new Element("div");
            var s = Style(e,
                ("animation-name", "a"),
                ("animation-duration", "1s"),
                ("animation-iteration-count", "0.5"),
                ("animation-timing-function", "linear"),
                ("animation-fill-mode", "forwards"));
            runner.OnStyleChange(e, null, s);
            clock.Set(1.0); // past total active time (0.5 * 1s = 0.5s)
            runner.Tick(1.0);
            var c = runner.Compose(e, s);
            // Forwards fill -> hold final sample at half-progress = 0.5.
            Assert.That(double.Parse(c.Get("opacity"), System.Globalization.CultureInfo.InvariantCulture),
                Is.EqualTo(0.5).Within(0.02));
        }

        // alternate-reverse starts in the reverse direction on iteration 0,
        // then forward on iteration 1, opposite to plain `alternate`.
        [Test]
        public void Direction_alternate_reverse_starts_reversed() {
            var (runner, clock) = MakeRunner("@keyframes a { from { opacity: 0; } to { opacity: 1; } }");
            var e = new Element("div");
            var s = Style(e,
                ("animation-name", "a"),
                ("animation-duration", "1s"),
                ("animation-direction", "alternate-reverse"),
                ("animation-iteration-count", "2"),
                ("animation-timing-function", "linear"));
            runner.OnStyleChange(e, null, s);
            // Quarter into iteration 0 (which runs in reverse, 1->0): opacity ~0.75.
            clock.Set(0.25);
            runner.Tick(0.25);
            var c = runner.Compose(e, s);
            Assert.That(double.Parse(c.Get("opacity"), System.Globalization.CultureInfo.InvariantCulture),
                Is.EqualTo(0.75).Within(0.02));
        }

        // step-end timing function holds the start value until the very last
        // moment of the iteration. At any t in (0, 1) the sample matches the
        // `from` keyframe; at t=1 it jumps to `to`.
        [Test]
        public void Steps_end_holds_start_value_until_iteration_completes() {
            var (runner, clock) = MakeRunner("@keyframes a { from { opacity: 0; } to { opacity: 1; } }");
            var e = new Element("div");
            var s = Style(e,
                ("animation-name", "a"),
                ("animation-duration", "1s"),
                ("animation-timing-function", "step-end"),
                ("animation-fill-mode", "forwards"));
            runner.OnStyleChange(e, null, s);
            clock.Set(0.5);
            runner.Tick(0.5);
            var c = runner.Compose(e, s);
            // step-end at progress 0.5 -> sample at progress 0.
            Assert.That(double.Parse(c.Get("opacity"), System.Globalization.CultureInfo.InvariantCulture),
                Is.LessThan(0.05));
        }

        // step-start jumps to the end value immediately on iteration start.
        [Test]
        public void Steps_start_jumps_to_end_value_immediately() {
            var (runner, clock) = MakeRunner("@keyframes a { from { opacity: 0; } to { opacity: 1; } }");
            var e = new Element("div");
            var s = Style(e,
                ("animation-name", "a"),
                ("animation-duration", "1s"),
                ("animation-timing-function", "step-start"),
                ("animation-fill-mode", "forwards"));
            runner.OnStyleChange(e, null, s);
            clock.Set(0.001);
            runner.Tick(0.001);
            var c = runner.Compose(e, s);
            Assert.That(double.Parse(c.Get("opacity"), System.Globalization.CultureInfo.InvariantCulture),
                Is.GreaterThan(0.95));
        }

        // cubic-bezier(0.5, 0, 0.5, 1) is the standard "ease-in-out" curve.
        // At input progress 0.5 the curve y output should also be 0.5 by
        // symmetry of the control points.
        [Test]
        public void Cubic_bezier_symmetric_curve_passes_through_midpoint() {
            var (runner, clock) = MakeRunner("@keyframes a { from { opacity: 0; } to { opacity: 1; } }");
            var e = new Element("div");
            var s = Style(e,
                ("animation-name", "a"),
                ("animation-duration", "1s"),
                ("animation-timing-function", "cubic-bezier(0.5, 0, 0.5, 1)"));
            runner.OnStyleChange(e, null, s);
            clock.Set(0.5);
            runner.Tick(0.5);
            var c = runner.Compose(e, s);
            Assert.That(double.Parse(c.Get("opacity"), System.Globalization.CultureInfo.InvariantCulture),
                Is.EqualTo(0.5).Within(0.05));
        }

        // alternate with an odd iteration count finishes mid-flip: the final
        // sampled iteration runs in reverse. With iteration-count 3 the
        // sequence is forward / reverse / forward, so the last frame samples
        // the forward end (opacity = 1) under fill-mode forwards.
        [Test]
        public void Alternate_odd_iterations_ends_in_forward_direction() {
            var (runner, clock) = MakeRunner("@keyframes a { from { opacity: 0; } to { opacity: 1; } }");
            var e = new Element("div");
            var s = Style(e,
                ("animation-name", "a"),
                ("animation-duration", "1s"),
                ("animation-direction", "alternate"),
                ("animation-iteration-count", "3"),
                ("animation-timing-function", "linear"),
                ("animation-fill-mode", "forwards"));
            runner.OnStyleChange(e, null, s);
            clock.Set(5.0); // past completion
            runner.Tick(5.0);
            var c = runner.Compose(e, s);
            Assert.That(double.Parse(c.Get("opacity"), System.Globalization.CultureInfo.InvariantCulture),
                Is.EqualTo(1.0).Within(Eps));
        }
    }
}
