using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Animation;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Events;

namespace Weva.Tests.Css.Animation {
    // Coverage for CSS Animations L2 §5 / §10 `animation-composition`.
    // Tracker: H2b in CSS_COMPLIANCE_ISSUES.md. v1 supports:
    //   • `replace` (default, overwrite)
    //   • `add` for bare-number / matching-unit length / single-function
    //     transform shapes (number+number, px+px, translateX(px)+translateX(px),
    //     etc.). Mismatched units / multi-function transforms / colors fall
    //     back to replace.
    //   • `accumulate` is treated as `add` for v1. Strict spec accumulation
    //     across iteration counts (per §5.4 transform/color accumulation) is
    //     deferred.
    public class AnimationCompositionTests {
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
        public void Composition_add_sums_animation_translate_with_underlying_transform_H2b() {
            // CSS Animations L2 §5: `add` sums the animation value with the
            // underlying value. Underlying transform is translateX(5px); the
            // keyframe interpolates translateX(10px) → translateX(20px). At
            // t = 0.5 (linear easing) the sample is translateX(15px); add
            // composes onto the 5px base for 5 + 15 = 20px.
            var (runner, clock) = MakeRunner(
                "@keyframes slide { " +
                "from { transform: translateX(10px); } " +
                "to { transform: translateX(20px); } }");
            var e = new Element("div");
            var s = Style(e,
                // translateX kept lowercase here because the underlying value flows
                // through to the composed output verbatim — TryComposeAdd preserves
                // the underlying name (not the animation-sample name) when summing.
                ("transform", "translatex(5px)"),
                ("animation-name", "slide"),
                ("animation-duration", "1s"),
                ("animation-timing-function", "linear"),
                ("animation-composition", "add"));
            runner.OnStyleChange(e, null, s);
            clock.Set(0.5);
            runner.Tick(0.5);
            var composed = runner.Compose(e, s);
            // 5px (underlying) + 15px (sample at t=0.5) = 20px.
            Assert.That(composed.Get("transform"), Is.EqualTo("translatex(20px)"));
        }

        [Test]
        public void Composition_replace_overrides_underlying_transform_H2b() {
            // Same setup as the `add` test but with the default `replace`
            // composition: the animation's effective value REPLACES the
            // underlying 5px translate, so the composed transform at t=0.5
            // is just the keyframe sample (15px), not the 20px sum.
            var (runner, clock) = MakeRunner(
                "@keyframes slide { " +
                "from { transform: translateX(10px); } " +
                "to { transform: translateX(20px); } }");
            var e = new Element("div");
            var s = Style(e,
                // translateX kept lowercase here because the underlying value flows
                // through to the composed output verbatim — TryComposeAdd preserves
                // the underlying name (not the animation-sample name) when summing.
                ("transform", "translatex(5px)"),
                ("animation-name", "slide"),
                ("animation-duration", "1s"),
                ("animation-timing-function", "linear"),
                ("animation-composition", "replace"));
            runner.OnStyleChange(e, null, s);
            clock.Set(0.5);
            runner.Tick(0.5);
            var composed = runner.Compose(e, s);
            Assert.That(composed.Get("transform"), Is.EqualTo("translatex(15px)"));
        }

        [Test]
        public void Shorthand_animation_add_token_sets_composition_add_H2b() {
            // CSS Animations L2 §10: the `animation:` shorthand now
            // recognises `replace` / `add` / `accumulate`. `animation: spin
            // 2s linear add` should set composition to Add, alongside the
            // existing name / duration / easing components.
            var specs = AnimationShorthandParser.Parse("spin 2s linear add");
            Assert.That(specs.Count, Is.EqualTo(1));
            Assert.That(specs[0].Name, Is.EqualTo("spin"));
            Assert.That(specs[0].DurationSeconds, Is.EqualTo(2).Within(Eps));
            Assert.That(specs[0].Composition, Is.EqualTo(AnimationCompositionMode.Add));

            // Sanity-check `accumulate` and `replace` round-trip too.
            var acc = AnimationShorthandParser.Parse("foo 1s accumulate");
            Assert.That(acc[0].Composition, Is.EqualTo(AnimationCompositionMode.Accumulate));
            var rep = AnimationShorthandParser.Parse("bar 1s replace");
            Assert.That(rep[0].Composition, Is.EqualTo(AnimationCompositionMode.Replace));
            // Default (no token) is Replace, matching the property's initial value.
            var def = AnimationShorthandParser.Parse("baz 1s");
            Assert.That(def[0].Composition, Is.EqualTo(AnimationCompositionMode.Replace));
        }

        [Test]
        public void Composition_accumulate_with_multi_iteration_is_treated_as_add_v1_H2b() {
            // v1 behaviour: `accumulate` is composed the same way as `add`
            // — the animation value is summed with the underlying value
            // each Tick. Strict spec accumulation across iteration counts
            // (per §5.4: the previous iteration's final value becomes the
            // baseline for the next iteration's `add`) is DEFERRED. This
            // test pins the v1 behaviour; if/when strict accumulate lands,
            // it should be replaced with a Chrome-parity check that
            // observes the iteration-N baseline shift.
            var (runner, clock) = MakeRunner(
                "@keyframes bump { " +
                "from { opacity: 0; } " +
                "to { opacity: 0.25; } }");
            var e = new Element("div");
            var s = Style(e,
                ("opacity", "0.5"),
                ("animation-name", "bump"),
                ("animation-duration", "1s"),
                ("animation-timing-function", "linear"),
                ("animation-iteration-count", "3"),
                ("animation-composition", "accumulate"));
            runner.OnStyleChange(e, null, s);

            // First iteration mid-point: underlying 0.5 + sample 0.125 = 0.625.
            clock.Set(0.5);
            runner.Tick(0.5);
            var c1 = runner.Compose(e, s);
            double op1 = double.Parse(c1.Get("opacity"), System.Globalization.CultureInfo.InvariantCulture);
            Assert.That(op1, Is.EqualTo(0.625).Within(Eps));

            // Mid-point of the third iteration: under strict §5.4
            // accumulate this would be 0.5 + 0.5 + 0.125 (iteration-2
            // accumulated baseline of 0.5 from two full sweeps of 0.25,
            // plus the in-flight 0.125). v1 treats accumulate as add, so
            // it stays at 0.5 + 0.125 = 0.625 — pinned below to document
            // the gap.
            clock.Set(2.5);
            runner.Tick(2.5);
            var c2 = runner.Compose(e, s);
            double op2 = double.Parse(c2.Get("opacity"), System.Globalization.CultureInfo.InvariantCulture);
            Assert.That(op2, Is.EqualTo(0.625).Within(Eps));
        }
    }
}
