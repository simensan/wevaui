using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Weva.Css.Cascade.Shorthands;

namespace Weva.Tests.Css.Cascade.Shorthands {
    public class GapShorthandTests {
        static Dictionary<string, string> Expand(string value) {
            return new GapShorthandExpander().Expand(value).ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        [Test]
        public void One_value_sets_both_axes() {
            var d = Expand("8px");
            Assert.That(d["row-gap"], Is.EqualTo("8px"));
            Assert.That(d["column-gap"], Is.EqualTo("8px"));
        }

        [Test]
        public void Two_values_set_row_and_column_independently() {
            var d = Expand("4px 12px");
            Assert.That(d["row-gap"], Is.EqualTo("4px"));
            Assert.That(d["column-gap"], Is.EqualTo("12px"));
        }

        [Test]
        public void Normal_keyword_is_accepted() {
            var d = Expand("normal");
            Assert.That(d["row-gap"], Is.EqualTo("normal"));
            Assert.That(d["column-gap"], Is.EqualTo("normal"));
        }

        [Test]
        public void Three_values_yield_nothing() {
            var d = Expand("1px 2px 3px");
            Assert.That(d.Count, Is.EqualTo(0));
        }

        [Test]
        public void Garbage_yields_nothing() {
            var d = Expand("garbage");
            Assert.That(d.Count, Is.EqualTo(0));
        }

        [Test]
        public void Registry_resolves_gap() {
            Assert.That(ShorthandRegistry.IsShorthand("gap"), Is.True);
        }
    }
}
