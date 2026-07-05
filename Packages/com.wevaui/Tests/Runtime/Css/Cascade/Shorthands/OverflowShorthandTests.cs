using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Weva.Css.Cascade.Shorthands;

namespace Weva.Tests.Css.Cascade.Shorthands {
    public class OverflowShorthandTests {
        static Dictionary<string, string> Expand(string value) {
            return new OverflowShorthandExpander().Expand(value).ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        [Test]
        public void One_value_applies_to_both_axes() {
            var d = Expand("hidden");
            Assert.That(d["overflow-x"], Is.EqualTo("hidden"));
            Assert.That(d["overflow-y"], Is.EqualTo("hidden"));
        }

        [Test]
        public void Two_values_set_x_then_y() {
            var d = Expand("scroll auto");
            Assert.That(d["overflow-x"], Is.EqualTo("scroll"));
            Assert.That(d["overflow-y"], Is.EqualTo("auto"));
        }

        [Test]
        public void Garbage_yields_nothing() {
            var d = Expand("nope");
            Assert.That(d.Count, Is.EqualTo(0));
        }

        [Test]
        public void Three_values_yield_nothing() {
            var d = Expand("hidden auto scroll");
            Assert.That(d.Count, Is.EqualTo(0));
        }

        [Test]
        public void Registry_resolves_overflow() {
            Assert.That(ShorthandRegistry.IsShorthand("overflow"), Is.True);
        }
    }
}
