using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Weva.Css.Cascade.Shorthands;

namespace Weva.Tests.Css.Cascade.Shorthands {
    public class FlexFlowShorthandTests {
        static Dictionary<string, string> Expand(string value) {
            return new FlexFlowShorthandExpander().Expand(value).ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        [Test]
        public void Direction_only_resets_wrap_to_nowrap() {
            var d = Expand("row-reverse");
            Assert.That(d["flex-direction"], Is.EqualTo("row-reverse"));
            Assert.That(d["flex-wrap"], Is.EqualTo("nowrap"));
        }

        [Test]
        public void Wrap_only_resets_direction_to_row() {
            var d = Expand("wrap");
            Assert.That(d["flex-direction"], Is.EqualTo("row"));
            Assert.That(d["flex-wrap"], Is.EqualTo("wrap"));
        }

        [Test]
        public void Direction_then_wrap() {
            var d = Expand("column wrap-reverse");
            Assert.That(d["flex-direction"], Is.EqualTo("column"));
            Assert.That(d["flex-wrap"], Is.EqualTo("wrap-reverse"));
        }

        [Test]
        public void Wrap_then_direction_works_in_either_order() {
            var d = Expand("wrap column");
            Assert.That(d["flex-direction"], Is.EqualTo("column"));
            Assert.That(d["flex-wrap"], Is.EqualTo("wrap"));
        }

        [Test]
        public void Three_tokens_yield_nothing() {
            var d = Expand("row wrap nowrap");
            Assert.That(d.Count, Is.EqualTo(0));
        }

        [Test]
        public void Registry_resolves_flex_flow() {
            Assert.That(ShorthandRegistry.IsShorthand("flex-flow"), Is.True);
        }
    }
}
