using System.Globalization;
using NUnit.Framework;
using Weva.Animation;
using Weva.Css;
using Weva.Css.Animation;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Events;

namespace Weva.Tests.Css.Animation {
    // Edge-case coverage for compound-property interpolation, easing variants,
    // multi-property transitions, and direction / iteration / fill-mode /
    // negative-delay combinations. Mirrors the helper conventions used by
    // CssAnimationRunnerKeyframesTests and CssAnimationRunnerTransitionTests
    // (Run / Tick / Set + Style(e, kv...)).
    public class AnimationEdgeCasesTests {
        const double Eps = 1e-3;

        static (CssAnimationRunner runner, FakeUIClock clock) MakeRunner(string css = null) {
            var clock = new FakeUIClock();
            var sheets = css == null
                ? System.Array.Empty<Stylesheet>()
                : new[] { CssParser.Parse(css) };
            var cascade = new CascadeEngine(System.Array.Empty<OriginatedStylesheet>());
            var runner = new CssAnimationRunner(cascade, sheets, clock);
            return (runner, clock);
        }

        static ComputedStyle Style(Element e, params (string, string)[] kv) {
            var s = new ComputedStyle(e);
            foreach (var pair in kv) s.Set(pair.Item1, pair.Item2);
            return s;
        }

        static double Parse(string s) {
            return double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        // Extracts the first numeric arg out of a transform fn invocation like
        // "translate(50px, 25px)" or "rotate(45deg)" — used by the compound-
        // transform tests that just want to read what one component lerped to.
        static double ExtractNumericArg(string fnText, int argIndex) {
            int open = fnText.IndexOf('(');
            int close = fnText.LastIndexOf(')');
            Assert.That(open, Is.GreaterThanOrEqualTo(0), $"missing '(' in '{fnText}'");
            Assert.That(close, Is.GreaterThan(open), $"missing ')' in '{fnText}'");
            string inner = fnText.Substring(open + 1, close - open - 1);
            string[] args = inner.Split(',');
            Assert.That(argIndex, Is.LessThan(args.Length), $"arg {argIndex} OOB in '{fnText}'");
            string raw = args[argIndex].Trim();
            // Strip unit suffix; we only care about the numeric scalar.
            int unitStart = raw.Length;
            for (int i = 0; i < raw.Length; i++) {
                char c = raw[i];
                if (!(char.IsDigit(c) || c == '.' || c == '-' || c == '+' || c == 'e' || c == 'E')) {
                    unitStart = i; break;
                }
            }
            return double.Parse(raw.Substring(0, unitStart), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        // -------- Compound transform interpolation --------

        // translate(0,0) -> translate(100px, 50px) at t=0.5 should yield each
        // axis interpolated independently — (50px, 25px). The transform path
        // matches function names + arg counts then lerps each numeric slot.
        [Test]
        public void Transition_transform_translate_to_translate_interpolates_each_axis() {
            var (runner, clock) = MakeRunner();
            var e = new Element("div");
            var prev = Style(e,
                ("transform", "translate(0px, 0px)"),
                ("transition", "transform 1s linear"));
            var next = Style(e,
                ("transform", "translate(100px, 50px)"),
                ("transition", "transform 1s linear"));
            runner.OnStyleChange(e, prev, next);
            clock.Set(0.5);
            runner.Tick(0.5);
            var c = runner.Compose(e, next);
            string t = c.Get("transform");
            Assert.That(t, Does.Contain("translate"));
            Assert.That(ExtractNumericArg(t, 0), Is.EqualTo(50).Within(0.5));
            Assert.That(ExtractNumericArg(t, 1), Is.EqualTo(25).Within(0.5));
        }

        // rotate(0deg) -> rotate(90deg) at t=0.5 should give 45deg. The runner
        // routes single-rotate transforms through TryInterpolateRotateTyped
        // for transitions too, but the result string still carries the angle.
        [Test]
        public void Transition_transform_rotate_to_rotate_interpolates_angle() {
            var (runner, clock) = MakeRunner();
            var e = new Element("div");
            var prev = Style(e,
                ("transform", "rotate(0deg)"),
                ("transition", "transform 1s linear"));
            var next = Style(e,
                ("transform", "rotate(90deg)"),
                ("transition", "transform 1s linear"));
            runner.OnStyleChange(e, prev, next);
            clock.Set(0.5);
            runner.Tick(0.5);
            var c = runner.Compose(e, next);
            string t = c.Get("transform");
            Assert.That(t, Does.Contain("rotate"));
            Assert.That(ExtractNumericArg(t, 0), Is.EqualTo(45).Within(0.5));
        }

        // Mismatched function lists (translate ↔ rotate) cannot be component-
        // wise interpolated. CSS Transforms L1 §17 specifies 2D matrix
        // decomposition for this case: both sides are collapsed to a 2D matrix,
        // decomposed into translate/rotate/scale/skew primitives, lerped, and
        // recomposed — the result is emitted as a matrix() function.
        // (Prior to the G9 matrix-decomposition fix this path produced discrete
        // swap-at-half semantics; the test name is kept for history but the
        // contract is now continuous matrix interpolation.)
        [Test]
        public void Transition_transform_translate_to_rotate_treats_as_discrete() {
            var (runner, clock) = MakeRunner();
            var e = new Element("div");
            var prev = Style(e,
                ("transform", "translate(100px, 0px)"),
                ("transition", "transform 1s linear"));
            var next = Style(e,
                ("transform", "rotate(90deg)"),
                ("transition", "transform 1s linear"));
            runner.OnStyleChange(e, prev, next);
            clock.Set(0.25);
            runner.Tick(0.25);
            // CSS Transforms L1 §17 matrix decomposition: mismatched shapes are
            // collapsed to 2D matrices and interpolated continuously. The output
            // is a matrix() function, not the raw from/to strings.
            // Snapshot the string immediately — Compose returns a shared
            // ComputedStyle instance that is mutated in-place by later calls.
            string matrixAt025 = runner.Compose(e, next).Get("transform");
            Assert.That(matrixAt025, Does.Contain("matrix"),
                "at t=0.25 result should be a matrix() from CSS Transforms L1 §17 decomposition");
            clock.Set(0.75);
            runner.Tick(0.75);
            string matrixAt075 = runner.Compose(e, next).Get("transform");
            Assert.That(matrixAt075, Does.Contain("matrix"),
                "at t=0.75 result should be a matrix() from CSS Transforms L1 §17 decomposition");
            // Verify the two mid-progress matrices are different (continuous
            // interpolation is happening, not a single discrete value).
            Assert.That(matrixAt075, Is.Not.EqualTo(matrixAt025),
                "matrix values at t=0.25 and t=0.75 must differ — continuous interpolation");
        }

        // Chained same-signature transform lists: each component is lerped on
        // its own. translate(10px) rotate(0deg) -> translate(20px) rotate(60deg)
        // at t=0.5 should give translate(15px) rotate(30deg).
        [Test]
        public void Transition_transform_chain_same_functions_interpolates_componentwise() {
            var (runner, clock) = MakeRunner();
            var e = new Element("div");
            var prev = Style(e,
                ("transform", "translate(10px) rotate(0deg)"),
                ("transition", "transform 1s linear"));
            var next = Style(e,
                ("transform", "translate(20px) rotate(60deg)"),
                ("transition", "transform 1s linear"));
            runner.OnStyleChange(e, prev, next);
            clock.Set(0.5);
            runner.Tick(0.5);
            var c = runner.Compose(e, next);
            string t = c.Get("transform");
            Assert.That(t, Does.Contain("translate"));
            Assert.That(t, Does.Contain("rotate"));
            // Split on the closing ')' between the two functions to isolate each.
            int splitAt = t.IndexOf(')') + 1;
            string left = t.Substring(0, splitAt);
            string right = t.Substring(splitAt);
            Assert.That(ExtractNumericArg(left, 0), Is.EqualTo(15).Within(0.5));
            Assert.That(ExtractNumericArg(right, 0), Is.EqualTo(30).Within(0.5));
        }

        // -------- Easing --------

        // ease-in (cubic-bezier(0.42, 0, 1, 1)) is slow at the start —
        // sampling at progress 0.25 must yield a value below the linear
        // diagonal.
        [Test]
        public void Transition_ease_in_starts_slow() {
            var (runner, clock) = MakeRunner();
            var e = new Element("div");
            var prev = Style(e, ("opacity", "0"), ("transition", "opacity 1s ease-in"));
            var next = Style(e, ("opacity", "1"), ("transition", "opacity 1s ease-in"));
            runner.OnStyleChange(e, prev, next);
            clock.Set(0.25);
            runner.Tick(0.25);
            var c = runner.Compose(e, next);
            Assert.That(Parse(c.Get("opacity")), Is.LessThan(0.25));
        }

        // ease-out (cubic-bezier(0, 0, 0.58, 1)) is fast at the start —
        // sampling at progress 0.25 must yield a value above the linear
        // diagonal.
        [Test]
        public void Transition_ease_out_starts_fast() {
            var (runner, clock) = MakeRunner();
            var e = new Element("div");
            var prev = Style(e, ("opacity", "0"), ("transition", "opacity 1s ease-out"));
            var next = Style(e, ("opacity", "1"), ("transition", "opacity 1s ease-out"));
            runner.OnStyleChange(e, prev, next);
            clock.Set(0.25);
            runner.Tick(0.25);
            var c = runner.Compose(e, next);
            Assert.That(Parse(c.Get("opacity")), Is.GreaterThan(0.25));
        }

        // steps(2, jump-end) is the CSS spec default for `steps(2)` — the
        // output progression should be 0 at t=0, 0.5 at t=0.5 (inside the
        // second sub-interval the eased output is 1/2), and 1 at t=1.
        [Test]
        public void Transition_steps_2_jump_end_snaps_to_steps() {
            var (runner, clock) = MakeRunner();
            var e = new Element("div");
            var prev = Style(e, ("opacity", "0"), ("transition", "opacity 1s steps(2, jump-end)"));
            var next = Style(e, ("opacity", "1"), ("transition", "opacity 1s steps(2, jump-end)"));
            runner.OnStyleChange(e, prev, next);
            // At t=0 (clamp): expect 'from' value.
            runner.Tick(0);
            Assert.That(runner.Compose(e, next).Get("opacity"), Is.EqualTo("0"));
            // At t=0.5: just inside the second step → output = 0.5.
            clock.Set(0.5);
            runner.Tick(0.5);
            Assert.That(Parse(runner.Compose(e, next).Get("opacity")), Is.EqualTo(0.5).Within(Eps));
            // At t=1.0: transition completes → exact 'to' value, record removed.
            clock.Set(1.0);
            runner.Tick(1.0);
            Assert.That(runner.Compose(e, next).Get("opacity"), Is.EqualTo("1"));
        }

        // steps(2, jump-start) outputs 1/2 immediately on t>0 and jumps to 1
        // at t>=0.5. StepsEasing for jump-start with N=2:
        //   t=0+ ε → currentStep=0, then +1 because t>0 → output=1 → 1/2.
        //   t=0.5  → currentStep=1, then +1 (clamped to Count=2) → 2/2 = 1.
        // So the visible progression is 0 (at t=0) → 0.5 (0<t<0.5) → 1 (t>=0.5).
        [Test]
        public void Transition_steps_2_jump_start_snaps_at_start() {
            var (runner, clock) = MakeRunner();
            var e = new Element("div");
            var prev = Style(e, ("opacity", "0"), ("transition", "opacity 1s steps(2, jump-start)"));
            var next = Style(e, ("opacity", "1"), ("transition", "opacity 1s steps(2, jump-start)"));
            runner.OnStyleChange(e, prev, next);
            // Just after start: jump-start sends us to the first step immediately (0.5).
            clock.Set(0.01);
            runner.Tick(0.01);
            Assert.That(Parse(runner.Compose(e, next).Get("opacity")), Is.EqualTo(0.5).Within(Eps));
            // At t=0.25: still in the first step sub-interval (0 < t*N < 1) → 0.5.
            clock.Set(0.25);
            runner.Tick(0.25);
            Assert.That(Parse(runner.Compose(e, next).Get("opacity")), Is.EqualTo(0.5).Within(Eps));
            // At t=1.0: transition completes → ToText.
            clock.Set(1.0);
            runner.Tick(1.0);
            Assert.That(runner.Compose(e, next).Get("opacity"), Is.EqualTo("1"));
        }

        // CubicBezierEasing.Evaluate clamps t<=0 → 0 and t>=1 → 1, regardless
        // of the control-point values. Verify that ease-in's underlying curve
        // (the EaseInEasing impl) honors those endpoints.
        [Test]
        public void Transition_cubic_bezier_matches_keyframe_at_0_and_1() {
            var bez = new CubicBezierEasing(0.42, 0, 1, 1);
            Assert.That(bez.Evaluate(0), Is.EqualTo(0).Within(Eps));
            Assert.That(bez.Evaluate(1), Is.EqualTo(1).Within(Eps));
        }

        // -------- Multi-property transitions --------

        // `transition: all 1s` should attach a transition to every changed
        // animatable property — both color and width here. After Tick at
        // t=0.5 (linear midpoint), both must reflect interpolated values
        // distinct from the 'to' state.
        [Test]
        public void Transition_property_all_applies_to_every_changed_property() {
            var (runner, clock) = MakeRunner();
            var e = new Element("div");
            var prev = Style(e,
                ("color", "rgb(0, 0, 0)"),
                ("width", "100px"),
                ("transition", "all 1s linear"));
            var next = Style(e,
                ("color", "rgb(255, 255, 255)"),
                ("width", "200px"),
                ("transition", "all 1s linear"));
            runner.OnStyleChange(e, prev, next);
            Assert.That(runner.RunningTransitionCount, Is.GreaterThanOrEqualTo(2));
            clock.Set(0.5);
            runner.Tick(0.5);
            var c = runner.Compose(e, next);
            // Color interpolated midway between black and white.
            Assert.That(c.Get("color"), Is.Not.EqualTo("rgb(0, 0, 0)"));
            Assert.That(c.Get("color"), Is.Not.EqualTo("rgb(255, 255, 255)"));
            // Width interpolated to ~150px.
            string w = c.Get("width");
            Assert.That(w, Does.EndWith("px"));
            Assert.That(Parse(w.Substring(0, w.Length - 2)), Is.EqualTo(150).Within(0.5));
        }

        // `transition: width 1s` should ONLY animate width — color must be
        // applied directly from the 'next' style (no transition record).
        // Note: ComputedStyle.Set on a Color-kinded property may normalize
        // the string ("rgb(255, 255, 255)" → "rgb(255,255,255)" or similar);
        // we compare via the next style's own Get to dodge round-trip noise.
        [Test]
        public void Transition_property_specific_only_applies_to_named_property() {
            var (runner, clock) = MakeRunner();
            var e = new Element("div");
            var prev = Style(e,
                ("color", "rgb(0, 0, 0)"),
                ("width", "100px"),
                ("transition", "width 1s linear"));
            var next = Style(e,
                ("color", "rgb(255, 255, 255)"),
                ("width", "200px"),
                ("transition", "width 1s linear"));
            runner.OnStyleChange(e, prev, next);
            Assert.That(runner.RunningTransitionCount, Is.EqualTo(1));
            clock.Set(0.5);
            runner.Tick(0.5);
            var c = runner.Compose(e, next);
            // Width is mid-flight.
            string w = c.Get("width");
            Assert.That(Parse(w.Substring(0, w.Length - 2)), Is.EqualTo(150).Within(0.5));
            // Color is NOT animated — equals the 'next' raw color.
            Assert.That(c.Get("color"), Is.EqualTo(next.Get("color")));
        }

        // -------- Keyframe iteration / direction --------

        // 1s × 2 iterations runs for a total of 2s. Sampling at t < 2s the
        // animation is still active (no sweep, record stays). At t > 2s
        // with fill-mode forwards the sample holds at the final keyframe
        // value (1). The record stays alive because forwards preserves it.
        [Test]
        public void Animation_iteration_count_2_completes_twice() {
            var (runner, clock) = MakeRunner("@keyframes a { from { opacity: 0; } to { opacity: 1; } }");
            var e = new Element("div");
            var s = Style(e,
                ("animation-name", "a"),
                ("animation-duration", "1s"),
                ("animation-iteration-count", "2"),
                ("animation-timing-function", "linear"),
                ("animation-fill-mode", "forwards"));
            runner.OnStyleChange(e, null, s);
            // Halfway through iteration 1 (t = 0.5s): opacity ~0.5.
            clock.Set(0.5);
            runner.Tick(0.5);
            Assert.That(Parse(runner.Compose(e, s).Get("opacity")), Is.EqualTo(0.5).Within(0.02));
            // Halfway through iteration 2 (t = 1.5s): opacity ~0.5 again.
            clock.Set(1.5);
            runner.Tick(1.5);
            Assert.That(Parse(runner.Compose(e, s).Get("opacity")), Is.EqualTo(0.5).Within(0.02));
            // After both iterations end (t = 2.5s): forwards holds final value (1).
            clock.Set(2.5);
            runner.Tick(2.5);
            Assert.That(Parse(runner.Compose(e, s).Get("opacity")), Is.EqualTo(1.0).Within(Eps));
        }

        // alternate flips direction on every odd iteration index. Iteration 0
        // runs forward (0→1). Iteration 1 runs reverse (1→0). Halfway into
        // iteration 1 (t = 1.5s) opacity should be ~0.5; quarter-way (t = 1.25s)
        // should be ~0.75 (because the reverse direction takes the eased
        // progress and maps to 1 - eased).
        [Test]
        public void Animation_direction_alternate_reverses_on_even_iterations() {
            var (runner, clock) = MakeRunner("@keyframes a { from { opacity: 0; } to { opacity: 1; } }");
            var e = new Element("div");
            var s = Style(e,
                ("animation-name", "a"),
                ("animation-duration", "1s"),
                ("animation-direction", "alternate"),
                ("animation-iteration-count", "2"),
                ("animation-timing-function", "linear"));
            runner.OnStyleChange(e, null, s);
            // Iteration 0, quarter-way: opacity ~0.25 (forward).
            clock.Set(0.25);
            runner.Tick(0.25);
            Assert.That(Parse(runner.Compose(e, s).Get("opacity")), Is.EqualTo(0.25).Within(0.02));
            // Iteration 1, quarter-way: opacity ~0.75 (reverse).
            clock.Set(1.25);
            runner.Tick(1.25);
            Assert.That(Parse(runner.Compose(e, s).Get("opacity")), Is.EqualTo(0.75).Within(0.02));
        }

        // animation-fill-mode: forwards holds the final keyframe value after
        // the animation completes. Sampling well past the end (t = 5s on a
        // 1s animation) should still return the 100% value (opacity 1).
        [Test]
        public void Animation_fill_mode_forwards_holds_final_value() {
            var (runner, clock) = MakeRunner("@keyframes a { from { opacity: 0; } to { opacity: 1; } }");
            var e = new Element("div");
            var s = Style(e,
                ("animation-name", "a"),
                ("animation-duration", "1s"),
                ("animation-timing-function", "linear"),
                ("animation-fill-mode", "forwards"));
            runner.OnStyleChange(e, null, s);
            clock.Set(5.0);
            runner.Tick(5.0);
            Assert.That(Parse(runner.Compose(e, s).Get("opacity")), Is.EqualTo(1.0).Within(Eps));
        }

        // animation-fill-mode: backwards applies the 0% keyframe value during
        // the delay window. With delay 1s and duration 1s, sampling at t=0.5
        // (still inside the delay) should return the 0% value.
        [Test]
        public void Animation_fill_mode_backwards_uses_first_keyframe_during_delay() {
            var (runner, clock) = MakeRunner("@keyframes a { from { opacity: 0.3; } to { opacity: 1; } }");
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
            Assert.That(Parse(runner.Compose(e, s).Get("opacity")), Is.EqualTo(0.3).Within(Eps));
        }

        // -------- Negative delay --------

        // animation-delay: -0.5s on a 1s animation has the effect of starting
        // the animation half-way through. At t=0 the activeTime = 0.5s,
        // localProgress = 0.5, linear easing → opacity 0.5.
        [Test]
        public void Animation_negative_delay_starts_mid_animation() {
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
            Assert.That(Parse(runner.Compose(e, s).Get("opacity")), Is.EqualTo(0.5).Within(0.02));
        }
    }
}
