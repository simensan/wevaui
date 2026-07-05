using System;
using NUnit.Framework;
using Weva.Animation;
using Weva.Css;
using Weva.Css.Animation;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Dom;
using Weva.Events;

namespace Weva.Tests.Css.Animation {
    public class CssAnimationRunnerTransitionTests {
        const double Eps = 1e-3;

        static (CssAnimationRunner runner, FakeUIClock clock) MakeRunner() {
            var clock = new FakeUIClock();
            var cascade = new CascadeEngine(System.Array.Empty<OriginatedStylesheet>());
            var runner = new CssAnimationRunner(cascade, System.Array.Empty<Stylesheet>(), clock);
            return (runner, clock);
        }

        static ComputedStyle Style(Element e, params (string, string)[] kv) {
            var s = new ComputedStyle(e);
            foreach (var pair in kv) s.Set(pair.Item1, pair.Item2);
            return s;
        }

        [Test]
        public void No_transition_yields_instant_change() {
            var (runner, clock) = MakeRunner();
            var e = new Element("div");
            var prev = Style(e, ("background-color", "red"));
            var next = Style(e, ("background-color", "blue"));
            runner.OnStyleChange(e, prev, next);
            runner.Tick(clock.NowSeconds);
            Assert.That(runner.HasRunningAnimations(e), Is.False);
            var composed = runner.Compose(e, next);
            Assert.That(composed.Get("background-color"), Is.EqualTo("blue"));
        }

        [Test]
        public void Transition_on_color_change_creates_running_record() {
            var (runner, clock) = MakeRunner();
            var e = new Element("div");
            var prev = Style(e,
                ("background-color", "rgb(0, 0, 0)"),
                ("transition", "background-color 0.2s linear"));
            var next = Style(e,
                ("background-color", "rgb(255, 255, 255)"),
                ("transition", "background-color 0.2s linear"));
            runner.OnStyleChange(e, prev, next);
            Assert.That(runner.HasRunningAnimations(e), Is.True);
        }

        [Test]
        public void At_t_zero_effective_value_equals_from() {
            var (runner, clock) = MakeRunner();
            var e = new Element("div");
            var prev = Style(e, ("opacity", "0"), ("transition", "opacity 1s linear"));
            var next = Style(e, ("opacity", "1"), ("transition", "opacity 1s linear"));
            runner.OnStyleChange(e, prev, next);
            runner.Tick(0);
            var composed = runner.Compose(e, next);
            Assert.That(composed.Get("opacity"), Is.EqualTo("0"));
        }

        [Test]
        public void At_half_duration_with_linear_easing_is_midpoint() {
            var (runner, clock) = MakeRunner();
            var e = new Element("div");
            var prev = Style(e, ("opacity", "0"), ("transition", "opacity 1s linear"));
            var next = Style(e, ("opacity", "1"), ("transition", "opacity 1s linear"));
            runner.OnStyleChange(e, prev, next);
            clock.Set(0.5);
            runner.Tick(0.5);
            var composed = runner.Compose(e, next);
            Assert.That(double.Parse(composed.Get("opacity"), System.Globalization.CultureInfo.InvariantCulture),
                Is.EqualTo(0.5).Within(Eps));
        }

        [Test]
        public void At_full_duration_effective_value_equals_to_and_record_is_removed() {
            var (runner, clock) = MakeRunner();
            var e = new Element("div");
            var prev = Style(e, ("opacity", "0"), ("transition", "opacity 1s linear"));
            var next = Style(e, ("opacity", "1"), ("transition", "opacity 1s linear"));
            runner.OnStyleChange(e, prev, next);
            clock.Set(1.5);
            runner.Tick(1.5);
            Assert.That(runner.HasRunningAnimations(e), Is.False);
            var composed = runner.Compose(e, next);
            Assert.That(composed.Get("opacity"), Is.EqualTo("1"));
        }

        [Test]
        public void Mid_transition_style_change_restarts_from_current() {
            var (runner, clock) = MakeRunner();
            var e = new Element("div");
            var s0 = Style(e, ("opacity", "0"), ("transition", "opacity 1s linear"));
            var s1 = Style(e, ("opacity", "1"), ("transition", "opacity 1s linear"));
            runner.OnStyleChange(e, s0, s1);
            clock.Set(0.5);
            runner.Tick(0.5);
            var firstComposed = runner.Compose(e, s1);
            Assert.That(double.Parse(firstComposed.Get("opacity"), System.Globalization.CultureInfo.InvariantCulture),
                Is.EqualTo(0.5).Within(Eps));

            // Re-target back to 0 mid-transition.
            var s2 = Style(e, ("opacity", "0"), ("transition", "opacity 1s linear"));
            runner.OnStyleChange(e, s1, s2);
            // CSS Transitions L1 §3: reversing to the original `from` shortens
            // the new transition to oldDuration * (1 - reverse_progress) =
            // 1s * (1 - 0.5) = 0.5s. At t=1.0 (0.5s into the new transition)
            // the value has arrived at the target 0.
            clock.Set(1.0);
            runner.Tick(1.0);
            var c2 = runner.Compose(e, s2);
            Assert.That(c2.Get("opacity"), Is.EqualTo("0"));
        }

        [Test]
        public void Delay_holds_at_from_value_until_delay_elapses() {
            var (runner, clock) = MakeRunner();
            var e = new Element("div");
            var prev = Style(e, ("opacity", "0"), ("transition", "opacity 1s linear 0.5s"));
            var next = Style(e, ("opacity", "1"), ("transition", "opacity 1s linear 0.5s"));
            runner.OnStyleChange(e, prev, next);
            clock.Set(0.25);
            runner.Tick(0.25);
            var c = runner.Compose(e, next);
            Assert.That(c.Get("opacity"), Is.EqualTo("0"));
        }

        [Test]
        public void Transition_all_applies_to_every_changed_property() {
            var (runner, clock) = MakeRunner();
            var e = new Element("div");
            var prev = Style(e,
                ("opacity", "0"),
                ("color", "rgb(0, 0, 0)"),
                ("transition", "all 100ms linear"));
            var next = Style(e,
                ("opacity", "1"),
                ("color", "rgb(255, 255, 255)"),
                ("transition", "all 100ms linear"));
            runner.OnStyleChange(e, prev, next);
            // Two transitions started: opacity and color.
            Assert.That(runner.RunningTransitionCount, Is.GreaterThanOrEqualTo(2));
        }

        [Test]
        public void Transition_none_disables_all() {
            var (runner, clock) = MakeRunner();
            var e = new Element("div");
            var prev = Style(e, ("opacity", "0"), ("transition", "none"));
            var next = Style(e, ("opacity", "1"), ("transition", "none"));
            runner.OnStyleChange(e, prev, next);
            Assert.That(runner.HasRunningAnimations(e), Is.False);
        }

        [Test]
        public void Per_property_durations_apply() {
            var (runner, clock) = MakeRunner();
            var e = new Element("div");
            var prev = Style(e,
                ("opacity", "0"),
                ("color", "rgb(0, 0, 0)"),
                ("transition", "opacity 100ms linear, color 200ms linear"));
            var next = Style(e,
                ("opacity", "1"),
                ("color", "rgb(255, 255, 255)"),
                ("transition", "opacity 100ms linear, color 200ms linear"));
            runner.OnStyleChange(e, prev, next);
            clock.Set(0.15);
            runner.Tick(0.15);
            var c = runner.Compose(e, next);
            // opacity finishes by t=0.1; color is mid-flight.
            Assert.That(c.Get("opacity"), Is.EqualTo("1"));
            // Color is still running.
            bool hasColor = false;
            foreach (var k in System.Linq.Enumerable.Range(0, runner.RunningTransitionCount)) {
                hasColor = true; break;
            }
            Assert.That(hasColor, Is.True);
        }

        [Test]
        public void Discrete_property_in_transition_list_switches_at_half() {
            var (runner, clock) = MakeRunner();
            var e = new Element("div");
            // CSS Transitions L2 §3.1: allow-discrete enables the step-at-50% behaviour for
            // discrete properties. Without it, no transition record would be created.
            var prev = Style(e,
                ("display", "block"),
                ("transition", "display 1s linear"),
                ("transition-behavior", "allow-discrete"));
            var next = Style(e,
                ("display", "inline"),
                ("transition", "display 1s linear"),
                ("transition-behavior", "allow-discrete"));
            runner.OnStyleChange(e, prev, next);
            clock.Set(0.4);
            runner.Tick(0.4);
            var c = runner.Compose(e, next);
            Assert.That(c.Get("display"), Is.EqualTo("block"));
            clock.Set(0.6);
            runner.Tick(0.6);
            var c2 = runner.Compose(e, next);
            Assert.That(c2.Get("display"), Is.EqualTo("inline"));
        }

        [Test]
        public void Stop_cancels_for_one_element() {
            var (runner, clock) = MakeRunner();
            var e1 = new Element("div");
            var e2 = new Element("div");
            var p1 = Style(e1, ("opacity", "0"), ("transition", "opacity 1s linear"));
            var n1 = Style(e1, ("opacity", "1"), ("transition", "opacity 1s linear"));
            runner.OnStyleChange(e1, p1, n1);
            var p2 = Style(e2, ("opacity", "0"), ("transition", "opacity 1s linear"));
            var n2 = Style(e2, ("opacity", "1"), ("transition", "opacity 1s linear"));
            runner.OnStyleChange(e2, p2, n2);
            runner.Stop(e1);
            Assert.That(runner.HasRunningAnimations(e1), Is.False);
            Assert.That(runner.HasRunningAnimations(e2), Is.True);
        }

        [Test]
        public void StopAll_cancels_everything() {
            var (runner, clock) = MakeRunner();
            var e1 = new Element("div");
            var e2 = new Element("div");
            var p1 = Style(e1, ("opacity", "0"), ("transition", "opacity 1s linear"));
            var n1 = Style(e1, ("opacity", "1"), ("transition", "opacity 1s linear"));
            runner.OnStyleChange(e1, p1, n1);
            var p2 = Style(e2, ("color", "rgb(0,0,0)"), ("transition", "color 1s linear"));
            var n2 = Style(e2, ("color", "rgb(255,255,255)"), ("transition", "color 1s linear"));
            runner.OnStyleChange(e2, p2, n2);
            runner.StopAll();
            Assert.That(runner.RunningTransitionCount, Is.EqualTo(0));
        }

        [Test]
        public void Element_removed_via_Stop_cleans_up_records() {
            var (runner, clock) = MakeRunner();
            var e = new Element("div");
            var p = Style(e, ("opacity", "0"), ("transition", "opacity 1s linear"));
            var n = Style(e, ("opacity", "1"), ("transition", "opacity 1s linear"));
            runner.OnStyleChange(e, p, n);
            Assert.That(runner.RunningTransitionCount, Is.EqualTo(1));
            runner.Stop(e);
            Assert.That(runner.RunningTransitionCount, Is.EqualTo(0));
        }

        [Test]
        public void Compose_returns_same_instance_when_nothing_running() {
            var (runner, clock) = MakeRunner();
            var e = new Element("div");
            var s = Style(e, ("opacity", "1"));
            var composed = runner.Compose(e, s);
            Assert.That(composed, Is.SameAs(s));
        }

        // Regression: a pseudo-class flip (e.g. :hover) drives CascadeEngine to
        // re-resolve the element's ComputedStyle and call OnStyleChange. The
        // runner must start a transition and produce an interpolated bg-color
        // mid-flight rather than snap to the new value. This guards the path
        // InteractionStateProvider.SetFlag -> state.Version bump -> cascade miss
        // -> OnStyleChange -> StartTransitionFor.
        [Test]
        public void Hover_style_change_animates_background_color_over_duration() {
            var (runner, clock) = MakeRunner();
            var e = new Element("div");
            var rest = Style(e,
                ("background-color", "rgb(0, 0, 0)"),
                ("transition", "background-color 200ms linear"));
            var hover = Style(e,
                ("background-color", "rgb(200, 200, 200)"),
                ("transition", "background-color 200ms linear"));
            // Simulate the cascade firing on a hover-driven re-resolve.
            runner.OnStyleChange(e, rest, hover);
            Assert.That(runner.HasRunningAnimations(e), Is.True);

            clock.Set(0.1);
            runner.Tick(0.1);
            var midComposed = runner.Compose(e, hover);
            // At t = 100ms / 200ms = 0.5, linear interp -> rgb(100, 100, 100).
            // Verify it's not snapping to the target value.
            Assert.That(midComposed.Get("background-color"), Is.Not.EqualTo("rgb(200, 200, 200)"));
            Assert.That(midComposed.Get("background-color"), Is.Not.EqualTo("rgb(0, 0, 0)"));
        }

        // Regression: hover-then-unhover within the duration should reverse
        // smoothly from the current interpolated value (effective-from rule),
        // not jump back to the original 'from'. This guards
        // StartTransitionFor's existing.CurrentText re-target branch.
        [Test]
        public void Mid_flight_reverse_uses_current_interpolated_value_as_new_from() {
            var (runner, clock) = MakeRunner();
            var e = new Element("div");
            var rest = Style(e, ("opacity", "0"), ("transition", "opacity 1s linear"));
            var hover = Style(e, ("opacity", "1"), ("transition", "opacity 1s linear"));
            // Hover.
            runner.OnStyleChange(e, rest, hover);
            clock.Set(0.25);
            runner.Tick(0.25);
            var atQuarter = runner.Compose(e, hover);
            Assert.That(double.Parse(atQuarter.Get("opacity"), System.Globalization.CultureInfo.InvariantCulture),
                Is.EqualTo(0.25).Within(Eps));

            // Un-hover at t=0.25: re-target to 0. New 'from' should be 0.25, not 1.
            runner.OnStyleChange(e, hover, rest);
            // Reversing-shortening (CSS Transitions L1 §3) cuts the new
            // duration to 1s * (1 - 0.25) = 0.75s. Halfway through the
            // SHORTENED transition is t = 0.25 + 0.375 = 0.625; value
            // 0.25 -> 0 at half eased = 0.125.
            clock.Set(0.625);
            runner.Tick(0.625);
            var atReversed = runner.Compose(e, rest);
            Assert.That(double.Parse(atReversed.Get("opacity"), System.Globalization.CultureInfo.InvariantCulture),
                Is.EqualTo(0.125).Within(Eps));
        }

        // CSS Transitions L1 §3 "Faster reversing of interrupted transitions":
        // retargeting back to the prior transition's original `from` shortens
        // the new transition's duration to oldDuration * (1 - reverse_progress)
        // so the visible position arrives at the original `from` at the same
        // wall-clock time the original would have re-occupied that point.
        [Test]
        public void Interrupted_reverse_to_original_from_shortens_duration_proportionally() {
            var (runner, clock) = MakeRunner();
            var e = new Element("div");
            var rest = Style(e, ("opacity", "0"), ("transition", "opacity 1s linear"));
            var hover = Style(e, ("opacity", "1"), ("transition", "opacity 1s linear"));
            runner.OnStyleChange(e, rest, hover);
            clock.Set(0.25);
            runner.Tick(0.25);
            var atQuarter = runner.Compose(e, hover);
            Assert.That(double.Parse(atQuarter.Get("opacity"), System.Globalization.CultureInfo.InvariantCulture),
                Is.EqualTo(0.25).Within(Eps));

            // Reverse back to the original from (0) at t=0.25.
            runner.OnStyleChange(e, hover, rest);

            // New duration must be 0.75s (1s * (1 - 0.25)). Probe just before
            // and at the shortened endpoint: at t=1.00 (0.75s into the new
            // transition) the value must be exactly at the target 0.
            clock.Set(1.0);
            runner.Tick(1.0);
            var atEnd = runner.Compose(e, rest);
            Assert.That(atEnd.Get("opacity"), Is.EqualTo("0"),
                "shortened transition must complete at t=1.0 (0.75s after retarget)");
            Assert.That(runner.RunningTransitionCount, Is.EqualTo(0),
                "completed transition should have been swept");

            // Midpoint check: halfway through the SHORTENED transition is at
            // t = 0.25 + 0.75/2 = 0.625; value should be 0.25 * 0.5 = 0.125.
            // Repeat the scenario on a fresh element so the prior assert's
            // tick-to-1.0 doesn't poison the timeline.
            var (runner2, clock2) = MakeRunner();
            var e2 = new Element("div");
            runner2.OnStyleChange(e2, rest, hover);
            clock2.Set(0.25);
            runner2.Tick(0.25);
            runner2.OnStyleChange(e2, hover, rest);
            clock2.Set(0.625);
            runner2.Tick(0.625);
            var atMid = runner2.Compose(e2, rest);
            Assert.That(double.Parse(atMid.Get("opacity"), System.Globalization.CultureInfo.InvariantCulture),
                Is.EqualTo(0.125).Within(Eps),
                "halfway through 0.75s shortened transition: 0.25 -> 0, expect 0.125");
        }

        // Regression guard: retargeting to a NEW value (not the original
        // `from`) must NOT trigger reversing-shortening; the new transition
        // keeps its author-specified full duration.
        [Test]
        public void Interrupted_retarget_to_new_value_keeps_full_duration() {
            var (runner, clock) = MakeRunner();
            var e = new Element("div");
            var s0 = Style(e, ("opacity", "0"), ("transition", "opacity 1s linear"));
            var s1 = Style(e, ("opacity", "1"), ("transition", "opacity 1s linear"));
            runner.OnStyleChange(e, s0, s1);
            clock.Set(0.25);
            runner.Tick(0.25);

            // Retarget to a NEW value (0.5) — not the original `from` (0).
            var s2 = Style(e, ("opacity", "0.5"), ("transition", "opacity 1s linear"));
            runner.OnStyleChange(e, s1, s2);

            // If the duration was wrongly shortened to 0.75s, the transition
            // would have completed by t=1.0. Confirm it's still mid-flight at
            // t=1.0 (0.75s in / 1s full duration -> 0.25 -> 0.5 progressed 0.75
            // -> value = 0.25 + 0.75 * (0.5 - 0.25) = 0.4375).
            clock.Set(1.0);
            runner.Tick(1.0);
            var mid = runner.Compose(e, s2);
            Assert.That(runner.RunningTransitionCount, Is.EqualTo(1),
                "transition to a new value must keep full duration and still be running at t=1.0");
            Assert.That(double.Parse(mid.Get("opacity"), System.Globalization.CultureInfo.InvariantCulture),
                Is.EqualTo(0.4375).Within(Eps),
                "no shortening when target differs from original from");
        }

        // CSS Transitions L2 §3.1: with the default transition-behavior: normal,
        // discrete properties (display, visibility, etc.) snap immediately — no
        // transition record is created. Only allow-discrete opts into the
        // step-at-50% animation path.
        [Test]
        public void Transition_behavior_normal_skips_discrete_properties() {
            var (runner, clock) = MakeRunner();
            var e = new Element("div");
            // Default behavior is 'normal' -> discrete props should snap, not animate.
            var prev = Style(e,
                ("display", "block"),
                ("transition", "display 1s linear"),
                ("transition-behavior", "normal"));
            var next = Style(e,
                ("display", "inline"),
                ("transition", "display 1s linear"),
                ("transition-behavior", "normal"));
            runner.OnStyleChange(e, prev, next);
            // No transition record should exist for a discrete prop with normal behavior.
            Assert.That(runner.RunningTransitionCount, Is.EqualTo(0));
        }

        // CSS Transitions L2 §3.1: omitting transition-behavior is equivalent to
        // "normal" — discrete properties must snap even when transition-behavior is
        // not explicitly set in the style.
        [Test]
        public void Transition_behavior_default_omitted_also_skips_discrete_properties() {
            var (runner, clock) = MakeRunner();
            var e = new Element("div");
            // No transition-behavior set — the initial value "normal" must apply.
            var prev = Style(e,
                ("visibility", "visible"),
                ("transition-property", "visibility"),
                ("transition-duration", "1s"));
            var next = Style(e,
                ("visibility", "hidden"),
                ("transition-property", "visibility"),
                ("transition-duration", "1s"));
            runner.OnStyleChange(e, prev, next);
            Assert.That(runner.RunningTransitionCount, Is.EqualTo(0),
                "visibility is discrete; without allow-discrete it must not produce a transition");
        }

        // CSS Transitions L2 §3.1: allow-discrete enables the transition for a
        // discrete property. display: none->block should run for the full duration
        // and the value should remain from-value (none) before t=0.5 on a linear curve.
        [Test]
        public void Transition_behavior_allow_discrete_animates_display_before_half() {
            var (runner, clock) = MakeRunner();
            var e = new Element("div");
            var prev = Style(e,
                ("display", "none"),
                ("transition-property", "display"),
                ("transition-duration", "1s"),
                ("transition-timing-function", "linear"),
                ("transition-behavior", "allow-discrete"));
            var next = Style(e,
                ("display", "block"),
                ("transition-property", "display"),
                ("transition-duration", "1s"),
                ("transition-timing-function", "linear"),
                ("transition-behavior", "allow-discrete"));
            runner.OnStyleChange(e, prev, next);
            Assert.That(runner.RunningTransitionCount, Is.EqualTo(1),
                "allow-discrete must create a transition record for a discrete property");
            clock.Set(0.4);
            runner.Tick(0.4);
            var composed = runner.Compose(e, next);
            Assert.That(composed.Get("display"), Is.EqualTo("none"),
                "before t=0.5 the from-value (none) must hold");
        }

        // CSS Transitions L2 §3.1: allow-discrete step-at-50% — after the
        // midpoint the to-value applies (linear easing so t=progress exactly).
        [Test]
        public void Transition_behavior_allow_discrete_animates_display_after_half() {
            var (runner, clock) = MakeRunner();
            var e = new Element("div");
            var prev = Style(e,
                ("display", "none"),
                ("transition-property", "display"),
                ("transition-duration", "1s"),
                ("transition-timing-function", "linear"),
                ("transition-behavior", "allow-discrete"));
            var next = Style(e,
                ("display", "block"),
                ("transition-property", "display"),
                ("transition-duration", "1s"),
                ("transition-timing-function", "linear"),
                ("transition-behavior", "allow-discrete"));
            runner.OnStyleChange(e, prev, next);
            clock.Set(0.6);
            runner.Tick(0.6);
            var composed = runner.Compose(e, next);
            Assert.That(composed.Get("display"), Is.EqualTo("block"),
                "at t=0.6 (past 50%) the to-value (block) must apply");
        }

        // CSS Transitions L2 §3.1: per-property comma list — mix one "allow-discrete"
        // entry with one "normal" entry. The discrete property (display) gets a
        // transition; the other discrete property (visibility) with normal does not.
        [Test]
        public void Transition_behavior_per_property_comma_list_allow_discrete_and_normal() {
            var (runner, clock) = MakeRunner();
            var e = new Element("div");
            var prev = Style(e,
                ("display", "block"),
                ("visibility", "visible"),
                ("transition-property", "display, visibility"),
                ("transition-duration", "1s, 1s"),
                ("transition-behavior", "allow-discrete, normal"));
            var next = Style(e,
                ("display", "inline"),
                ("visibility", "hidden"),
                ("transition-property", "display, visibility"),
                ("transition-duration", "1s, 1s"),
                ("transition-behavior", "allow-discrete, normal"));
            runner.OnStyleChange(e, prev, next);
            // display has allow-discrete -> one transition record
            // visibility has normal -> no record
            Assert.That(runner.RunningTransitionCount, Is.EqualTo(1),
                "only the allow-discrete property (display) must have a transition record");
        }

        // Regression: non-discrete properties must not be affected by transition-behavior.
        // opacity is Number kind; it should transition regardless of transition-behavior.
        [Test]
        public void Transition_behavior_normal_does_not_block_non_discrete_properties() {
            var (runner, clock) = MakeRunner();
            var e = new Element("div");
            var prev = Style(e,
                ("opacity", "0"),
                ("transition-property", "opacity"),
                ("transition-duration", "1s"),
                ("transition-behavior", "normal"));
            var next = Style(e,
                ("opacity", "1"),
                ("transition-property", "opacity"),
                ("transition-duration", "1s"),
                ("transition-behavior", "normal"));
            runner.OnStyleChange(e, prev, next);
            Assert.That(runner.RunningTransitionCount, Is.EqualTo(1),
                "transition-behavior: normal must not suppress transitions for non-discrete (Number) properties");
        }

        // Regression guard for M4: ParseTransitionSpecsFor short-circuits when
        // the `transition` shorthand equals the literal initial "all 0s ease 0s".
        // If a cosmetic variant slips past the literal compare it must still
        // produce no running transition via the regular parser path — i.e. the
        // fast-path is a pure optimization, not a correctness gate.
        [Test]
        public void Initial_transition_shorthand_starts_no_animation_canonical_and_cosmetic_variants() {
            string[] variants = {
                "all 0s ease 0s",
                "  all 0s ease 0s  ",
                "All 0s Ease 0s",
                "all 0ms ease 0ms",
                "all 0s ease",
            };
            foreach (var variant in variants) {
                var (runner, clock) = MakeRunner();
                var e = new Element("div");
                var prev = Style(e, ("opacity", "0"), ("transition", variant));
                var next = Style(e, ("opacity", "1"), ("transition", variant));
                runner.OnStyleChange(e, prev, next);
                runner.Tick(clock.NowSeconds);
                Assert.That(runner.HasRunningAnimations(e), Is.False,
                    $"transition shorthand '{variant}' must not start any running animation");
                var composed = runner.Compose(e, next);
                Assert.That(composed.Get("opacity"), Is.EqualTo("1"),
                    $"transition shorthand '{variant}' must produce an instant change");
            }
        }

        // H16: CSS Easing L1 §2.1 says an invalid easing falls back to the
        // property's initial value, which is `ease` for both
        // transition-timing-function and animation-timing-function. The
        // longhand transition path previously substituted `linear` on parse
        // failure (CssAnimationRunner.BuildFromLonghands). Pin the fix: a
        // nonsense longhand easing must produce the `ease` curve, not linear.
        [Test]
        public void Transition_with_invalid_longhand_easing_falls_back_to_ease_not_linear() {
            var (runner, clock) = MakeRunner();
            var e = new Element("div");
            // Set the easing longhand to a nonsense value so the runner's
            // BuildFromLonghands path is reached (hasLonghand becomes true
            // because the easing longhand differs from the initial "ease").
            var prev = Style(e,
                ("opacity", "0"),
                ("transition-property", "opacity"),
                ("transition-duration", "1s"),
                ("transition-timing-function", "nonsense-easing-name"));
            var next = Style(e,
                ("opacity", "1"),
                ("transition-property", "opacity"),
                ("transition-duration", "1s"),
                ("transition-timing-function", "nonsense-easing-name"));
            runner.OnStyleChange(e, prev, next);
            Assert.That(runner.HasRunningAnimations(e), Is.True,
                "transition must start even when its easing longhand is invalid");

            clock.Set(0.5);
            runner.Tick(0.5);
            var composed = runner.Compose(e, next);
            double observed = double.Parse(composed.Get("opacity"),
                System.Globalization.CultureInfo.InvariantCulture);
            // The CSS `ease` curve = cubic-bezier(0.25, 0.1, 0.25, 1.0); use it
            // directly so the test never disagrees with the engine's own
            // bezier implementation. y(0.5) is well above 0.5 (~0.802), so
            // observing a value close to 0.5 would mean the runner had
            // silently fallen back to linear.
            double expectedAtHalf = EaseEasing.Instance.Evaluate(0.5);
            Assert.That(observed, Is.EqualTo(expectedAtHalf).Within(Eps),
                "invalid transition easing must fall back to `ease`, not linear");
            Assert.That(observed, Is.GreaterThan(0.6),
                "sanity guard: linear would land at 0.5 — fallback must be `ease`");
        }

        // Pins the existing animation-path behaviour: longhand animation-timing-function
        // already falls back to ease (CssAnimationRunner.BuildAnimSpecsFromLonghands).
        // Keeps the guard in place so a future refactor can't regress it back to linear.
        [Test]
        public void Animation_with_invalid_longhand_easing_falls_back_to_ease() {
            var clock = new FakeUIClock();
            var sheet = CssParser.Parse(
                "@keyframes h16fade { from { opacity: 0; } to { opacity: 1; } }");
            var cascade = new CascadeEngine(System.Array.Empty<OriginatedStylesheet>());
            var runner = new CssAnimationRunner(cascade, new[] { sheet }, clock);

            var e = new Element("div");
            var s = Style(e,
                ("animation-name", "h16fade"),
                ("animation-duration", "1s"),
                ("animation-timing-function", "nonsense-easing-name"));
            runner.OnStyleChange(e, null, s);

            clock.Set(0.5);
            runner.Tick(0.5);
            var composed = runner.Compose(e, s);
            double observed = double.Parse(composed.Get("opacity"),
                System.Globalization.CultureInfo.InvariantCulture);
            double expectedAtHalf = EaseEasing.Instance.Evaluate(0.5);
            Assert.That(observed, Is.EqualTo(expectedAtHalf).Within(Eps),
                "invalid animation easing must fall back to `ease`");
        }

        // Consistency guard: the two longhand paths must substitute the SAME
        // easing for the same invalid input. Pre-H16 the transition path
        // produced linear (~0.5) while the animation path produced ease
        // (~0.802), a 60% divergence at t=0.5 for identical authored CSS.
        [Test]
        public void Invalid_easing_resolves_identically_for_transitions_and_animations() {
            // --- Transition arm: longhand easing = nonsense ---
            var (tRunner, tClock) = MakeRunner();
            var te = new Element("div");
            var tPrev = Style(te,
                ("opacity", "0"),
                ("transition-property", "opacity"),
                ("transition-duration", "1s"),
                ("transition-timing-function", "nonsense-easing-name"));
            var tNext = Style(te,
                ("opacity", "1"),
                ("transition-property", "opacity"),
                ("transition-duration", "1s"),
                ("transition-timing-function", "nonsense-easing-name"));
            tRunner.OnStyleChange(te, tPrev, tNext);
            tClock.Set(0.5);
            tRunner.Tick(0.5);
            double transitionMid = double.Parse(
                tRunner.Compose(te, tNext).Get("opacity"),
                System.Globalization.CultureInfo.InvariantCulture);

            // --- Animation arm: same nonsense easing, same 0->1 shape ---
            var aClock = new FakeUIClock();
            var aSheet = CssParser.Parse(
                "@keyframes h16cmp { from { opacity: 0; } to { opacity: 1; } }");
            var aCascade = new CascadeEngine(System.Array.Empty<OriginatedStylesheet>());
            var aRunner = new CssAnimationRunner(aCascade, new[] { aSheet }, aClock);
            var ae = new Element("div");
            var aStyle = Style(ae,
                ("animation-name", "h16cmp"),
                ("animation-duration", "1s"),
                ("animation-timing-function", "nonsense-easing-name"));
            aRunner.OnStyleChange(ae, null, aStyle);
            aClock.Set(0.5);
            aRunner.Tick(0.5);
            double animationMid = double.Parse(
                aRunner.Compose(ae, aStyle).Get("opacity"),
                System.Globalization.CultureInfo.InvariantCulture);

            Assert.That(transitionMid, Is.EqualTo(animationMid).Within(Eps),
                "transition and animation must resolve the same fallback easing for invalid input");
        }
    }
}
