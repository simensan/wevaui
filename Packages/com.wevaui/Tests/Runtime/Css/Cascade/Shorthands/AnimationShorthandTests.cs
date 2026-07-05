using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Weva.Css.Cascade.Shorthands;

namespace Weva.Tests.Css.Cascade.Shorthands {
    public class AnimationShorthandTests {
        static Dictionary<string, string> Expand(string value) {
            return new AnimationShorthandExpander().Expand(value).ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        [Test]
        public void Name_and_duration() {
            var d = Expand("fade 1s");
            Assert.That(d["animation-name"], Is.EqualTo("fade"));
            Assert.That(d["animation-duration"], Is.EqualTo("1s"));
            Assert.That(d["animation-timing-function"], Is.EqualTo("ease"));
            Assert.That(d["animation-delay"], Is.EqualTo("0s"));
            Assert.That(d["animation-iteration-count"], Is.EqualTo("1"));
            Assert.That(d["animation-direction"], Is.EqualTo("normal"));
            Assert.That(d["animation-fill-mode"], Is.EqualTo("none"));
            Assert.That(d["animation-play-state"], Is.EqualTo("running"));
        }

        [Test]
        public void Two_time_tokens_set_duration_then_delay() {
            var d = Expand("fade 1s 0.5s");
            Assert.That(d["animation-duration"], Is.EqualTo("1s"));
            Assert.That(d["animation-delay"], Is.EqualTo("0.5s"));
        }

        [Test]
        public void Iteration_count_infinite() {
            var d = Expand("spin 2s linear infinite");
            Assert.That(d["animation-name"], Is.EqualTo("spin"));
            Assert.That(d["animation-duration"], Is.EqualTo("2s"));
            Assert.That(d["animation-timing-function"], Is.EqualTo("linear"));
            Assert.That(d["animation-iteration-count"], Is.EqualTo("infinite"));
        }

        [Test]
        public void Direction_alternate_and_fill_both() {
            var d = Expand("bounce 1s alternate both");
            Assert.That(d["animation-direction"], Is.EqualTo("alternate"));
            Assert.That(d["animation-fill-mode"], Is.EqualTo("both"));
        }

        [Test]
        public void Comma_list_produces_comma_joined_longhands() {
            var d = Expand("fade 1s, spin 2s linear infinite");
            Assert.That(d["animation-name"], Is.EqualTo("fade, spin"));
            Assert.That(d["animation-duration"], Is.EqualTo("1s, 2s"));
            Assert.That(d["animation-timing-function"], Is.EqualTo("ease, linear"));
            Assert.That(d["animation-iteration-count"], Is.EqualTo("1, infinite"));
        }

        // H1b: animation shorthand must route `linear(...)` to the timing slot
        // the same way it routes `cubic-bezier(...)` and `steps(...)`. Before
        // the fix, `IsTimingFunction` rejected the function-call form because
        // the whitelist only contained `cubic-bezier(` and `steps(`.
        [Test]
        public void Linear_function_timing_function_on_animation_shorthand_is_preserved() {
            var d = Expand("spin 2s linear(0, 0.5, 1)");
            Assert.That(d["animation-name"], Is.EqualTo("spin"));
            Assert.That(d["animation-duration"], Is.EqualTo("2s"));
            Assert.That(d["animation-timing-function"], Is.EqualTo("linear(0, 0.5, 1)"));
            var resolved = Weva.Animation.EasingParser.Parse(d["animation-timing-function"]);
            Assert.That(resolved, Is.InstanceOf<Weva.Animation.PiecewiseLinearEasing>());
        }

        [Test]
        public void Demo_star_animation_shorthand_preserves_fill_mode_and_second_timing() {
            var d = Expand("star-pop 1.4s cubic-bezier(0.18, 0.90, 0.24, 1.10) both, star-twinkle 2.4s ease-in-out infinite");
            Assert.That(d["animation-name"], Is.EqualTo("star-pop, star-twinkle"));
            Assert.That(d["animation-duration"], Is.EqualTo("1.4s, 2.4s"));
            Assert.That(d["animation-timing-function"], Is.EqualTo("cubic-bezier(0.18, 0.90, 0.24, 1.10), ease-in-out"));
            Assert.That(d["animation-iteration-count"], Is.EqualTo("1, infinite"));
            Assert.That(d["animation-fill-mode"], Is.EqualTo("both, none"));
        }

        [Test]
        public void Paused_play_state() {
            var d = Expand("fade 1s paused");
            Assert.That(d["animation-play-state"], Is.EqualTo("paused"));
        }

        [Test]
        public void None_at_start_is_treated_as_animation_name() {
            var d = Expand("none 1s");
            Assert.That(d["animation-name"], Is.EqualTo("none"));
            Assert.That(d["animation-duration"], Is.EqualTo("1s"));
        }

        [Test]
        public void Empty_yields_nothing() {
            var d = Expand("");
            Assert.That(d.Count, Is.EqualTo(0));
        }

        [Test]
        public void Registry_resolves_animation() {
            Assert.That(ShorthandRegistry.IsShorthand("animation"), Is.True);
        }

        // Confidence test for the demo-style declaration `animation: pulse 2s ease-in-out infinite;`.
        // Verifies the shorthand expands into all four primary longhands with the expected values
        // so the cascade can hand them to the runner.
        [Test]
        public void Animation_shorthand_expands_to_longhands() {
            var d = Expand("pulse 2s ease-in-out infinite");
            Assert.That(d["animation-name"], Is.EqualTo("pulse"));
            Assert.That(d["animation-duration"], Is.EqualTo("2s"));
            Assert.That(d["animation-timing-function"], Is.EqualTo("ease-in-out"));
            Assert.That(d["animation-iteration-count"], Is.EqualTo("infinite"));
        }
    }
}
