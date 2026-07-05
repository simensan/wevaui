using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Weva.Css.Cascade.Shorthands;

namespace Weva.Tests.Css.Cascade.Shorthands {
    public class TransitionShorthandTests {
        static Dictionary<string, string> Expand(string value) {
            return new TransitionShorthandExpander().Expand(value).ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        [Test]
        public void Single_property_and_duration() {
            var d = Expand("opacity 200ms");
            Assert.That(d["transition-property"], Is.EqualTo("opacity"));
            Assert.That(d["transition-duration"], Is.EqualTo("200ms"));
            Assert.That(d["transition-timing-function"], Is.EqualTo("ease"));
            Assert.That(d["transition-delay"], Is.EqualTo("0s"));
        }

        [Test]
        public void Property_duration_and_timing_function() {
            var d = Expand("opacity 200ms ease-in");
            Assert.That(d["transition-property"], Is.EqualTo("opacity"));
            Assert.That(d["transition-duration"], Is.EqualTo("200ms"));
            Assert.That(d["transition-timing-function"], Is.EqualTo("ease-in"));
        }

        [Test]
        public void Two_time_tokens_set_duration_then_delay() {
            var d = Expand("opacity 1s 0.5s");
            Assert.That(d["transition-duration"], Is.EqualTo("1s"));
            Assert.That(d["transition-delay"], Is.EqualTo("0.5s"));
        }

        [Test]
        public void Default_property_is_all() {
            var d = Expand("200ms");
            Assert.That(d["transition-property"], Is.EqualTo("all"));
            Assert.That(d["transition-duration"], Is.EqualTo("200ms"));
        }

        [Test]
        public void Comma_list_produces_comma_joined_longhands() {
            var d = Expand("opacity 200ms, transform 500ms ease-in");
            Assert.That(d["transition-property"], Is.EqualTo("opacity, transform"));
            Assert.That(d["transition-duration"], Is.EqualTo("200ms, 500ms"));
            Assert.That(d["transition-timing-function"], Is.EqualTo("ease, ease-in"));
            Assert.That(d["transition-delay"], Is.EqualTo("0s, 0s"));
        }

        [Test]
        public void Cubic_bezier_timing_function_works() {
            var d = Expand("opacity 1s cubic-bezier(0.1, 0.7, 1, 0.1)");
            Assert.That(d["transition-timing-function"], Is.EqualTo("cubic-bezier(0.1, 0.7, 1, 0.1)"));
        }

        [Test]
        public void Steps_timing_function_works() {
            var d = Expand("opacity 1s steps(4, end)");
            Assert.That(d["transition-timing-function"], Is.EqualTo("steps(4, end)"));
        }

        // H1b: the cascade-side shorthand expander previously whitelisted only
        // `cubic-bezier(` and `steps(` in IsTimingFunction, so a `linear(...)`
        // token was silently dropped (the layer either failed to parse or the
        // function-call token slipped into another slot). Adding `linear(`
        // routes it to the timing slot so the longhand parser
        // (EasingParser.ParseLinear) can produce a PiecewiseLinearEasing.
        [Test]
        public void Linear_function_timing_function_with_double_position_shorthand_is_preserved() {
            var d = Expand("opacity 1s linear(0, 0.5 50%, 1)");
            Assert.That(d["transition-property"], Is.EqualTo("opacity"));
            Assert.That(d["transition-duration"], Is.EqualTo("1s"));
            Assert.That(d["transition-timing-function"], Is.EqualTo("linear(0, 0.5 50%, 1)"));
            Assert.That(d["transition-delay"], Is.EqualTo("0s"));

            // Cross-check: the resolved easing matches what the runner-side
            // longhand parser (EasingParser) produces for the same string, so
            // the shorthand path no longer silently drops the function.
            var resolved = Weva.Animation.EasingParser.Parse(d["transition-timing-function"]);
            Assert.That(resolved, Is.InstanceOf<Weva.Animation.PiecewiseLinearEasing>());
        }

        [Test]
        public void Simplest_two_point_linear_function_timing_function_is_preserved() {
            var d = Expand("opacity 1s linear(0, 1)");
            Assert.That(d["transition-timing-function"], Is.EqualTo("linear(0, 1)"));
            var resolved = Weva.Animation.EasingParser.Parse(d["transition-timing-function"]);
            Assert.That(resolved, Is.InstanceOf<Weva.Animation.PiecewiseLinearEasing>());
        }

        // H1b negative regression: a function-call token that isn't on the
        // easing whitelist must NOT be routed to the timing slot just because
        // we added `linear(`. The layer fails to parse (the token has parens
        // so it can't slip into the property slot either), matching the
        // existing `Garbage_layer_yields_nothing` behavior — and matching H16
        // in spirit (unknown easing must not produce a real timing function).
        [Test]
        public void Unknown_function_easing_does_not_match_timing_function() {
            var d = Expand("opacity 1s nonsense(0)");
            Assert.That(d.Count, Is.EqualTo(0),
                "unknown function token must not be silently accepted as a timing function");
        }

        [Test]
        public void All_keyword_property_works() {
            var d = Expand("all 200ms");
            Assert.That(d["transition-property"], Is.EqualTo("all"));
        }

        [Test]
        public void Garbage_layer_yields_nothing() {
            var d = Expand("opacity 200ms, !!!badd");
            Assert.That(d.Count, Is.EqualTo(0));
        }

        [Test]
        public void Empty_yields_nothing() {
            var d = Expand("");
            Assert.That(d.Count, Is.EqualTo(0));
        }

        [Test]
        public void Registry_resolves_transition() {
            Assert.That(ShorthandRegistry.IsShorthand("transition"), Is.True);
        }
    }
}
