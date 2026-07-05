using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Weva.Css.Cascade.Shorthands;

namespace Weva.Tests.Css.Cascade.Shorthands {
    public class PlaceShorthandTests {
        static Dictionary<string, string> Expand(IShorthandExpander ex, string value) {
            return ex.Expand(value).ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        [Test]
        public void Place_items_one_value_applies_to_both() {
            var d = Expand(PlaceShorthandExpander.PlaceItems(), "center");
            Assert.That(d["align-items"], Is.EqualTo("center"));
            Assert.That(d["justify-items"], Is.EqualTo("center"));
        }

        [Test]
        public void Place_items_two_values_split_align_and_justify() {
            var d = Expand(PlaceShorthandExpander.PlaceItems(), "start end");
            Assert.That(d["align-items"], Is.EqualTo("start"));
            Assert.That(d["justify-items"], Is.EqualTo("end"));
        }

        [Test]
        public void Place_content_two_values() {
            var d = Expand(PlaceShorthandExpander.PlaceContent(), "space-between center");
            Assert.That(d["align-content"], Is.EqualTo("space-between"));
            Assert.That(d["justify-content"], Is.EqualTo("center"));
        }

        [Test]
        public void Place_self_one_value() {
            var d = Expand(PlaceShorthandExpander.PlaceSelf(), "stretch");
            Assert.That(d["align-self"], Is.EqualTo("stretch"));
            Assert.That(d["justify-self"], Is.EqualTo("stretch"));
        }

        [Test]
        public void Three_values_yield_nothing() {
            var d = Expand(PlaceShorthandExpander.PlaceItems(), "a b c");
            Assert.That(d.Count, Is.EqualTo(0));
        }

        [Test]
        public void Empty_yields_nothing() {
            var d = Expand(PlaceShorthandExpander.PlaceItems(), "");
            Assert.That(d.Count, Is.EqualTo(0));
        }

        [Test]
        public void Registry_resolves_all_three_shorthands() {
            Assert.That(ShorthandRegistry.IsShorthand("place-items"), Is.True);
            Assert.That(ShorthandRegistry.IsShorthand("place-content"), Is.True);
            Assert.That(ShorthandRegistry.IsShorthand("place-self"), Is.True);
        }
    }
}
