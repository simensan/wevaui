using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Weva.Css.Cascade.Shorthands;

namespace Weva.Tests.Css.Cascade.Shorthands {
    // TG28 — per-grammar unit tests for OverscrollBehaviorShorthandExpander.
    // CSS Overscroll Behavior 1 §2: `overscroll-behavior: <value>{1,2}` —
    // one value applies to both axes; two values map x then y. Only the
    // three keywords `auto | contain | none` are valid.
    public class OverscrollBehaviorShorthandTests {
        static Dictionary<string, string> Expand(string value) {
            return new OverscrollBehaviorShorthandExpander().Expand(value).ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        [Test]
        public void Single_value_applies_to_both_axes() {
            var d = Expand("contain");
            Assert.That(d["overscroll-behavior-x"], Is.EqualTo("contain"));
            Assert.That(d["overscroll-behavior-y"], Is.EqualTo("contain"));
        }

        [Test]
        public void Two_values_map_x_then_y() {
            var d = Expand("contain auto");
            Assert.That(d["overscroll-behavior-x"], Is.EqualTo("contain"));
            Assert.That(d["overscroll-behavior-y"], Is.EqualTo("auto"));
        }

        [Test]
        public void None_keyword_is_accepted_on_both_axes() {
            var d = Expand("none none");
            Assert.That(d["overscroll-behavior-x"], Is.EqualTo("none"));
            Assert.That(d["overscroll-behavior-y"], Is.EqualTo("none"));
        }

        [Test]
        public void Garbage_token_drops_the_declaration() {
            var d = Expand("scroll");
            Assert.That(d.Count, Is.EqualTo(0));
        }

        [Test]
        public void Three_values_drop_the_declaration() {
            var d = Expand("auto contain none");
            Assert.That(d.Count, Is.EqualTo(0));
        }

        [Test]
        public void Empty_value_emits_no_longhands() {
            var d = Expand("");
            Assert.That(d.Count, Is.EqualTo(0));
        }

        [Test]
        public void Registry_resolves_overscroll_behavior_to_expander() {
            Assert.That(ShorthandRegistry.IsShorthand("overscroll-behavior"), Is.True);
            Assert.That(ShorthandRegistry.TryGet("overscroll-behavior", out var ex), Is.True);
            Assert.That(ex.ShorthandName, Is.EqualTo("overscroll-behavior"));
        }
    }
}
